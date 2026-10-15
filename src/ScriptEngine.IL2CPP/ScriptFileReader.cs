using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using ScriptEngineIl2Cpp.Rewriting;

namespace ScriptEngineIl2Cpp;

/// <summary>一次脚本目录快照。</summary>
internal sealed class ScriptSnapshot
{
    public List<ScriptFile> Files { get; } = new();

    /// <summary>所有 DLL+PDB 内容的摘要，用来判断「是否真的变了」。</summary>
    public string Fingerprint { get; set; } = "";
}

/// <summary>
/// 把脚本目录里的 DLL/PDB 读成字节快照。
/// 以 <c>FileShare.ReadWrite | Delete</c> 打开并立刻读完关闭 —— 引擎从不长期持有文件句柄，
/// 所以游戏运行时 <c>dotnet build</c> 也能覆盖这些 DLL。
/// </summary>
internal static class ScriptFileReader
{
    /// <summary>文件现在读不了（正在被编译器写、被占用、写到一半）。调用方应稍后重试。</summary>
    public sealed class NotReadyException : Exception
    {
        public NotReadyException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>最后一次写入在这么久之内的文件，认为可能还在写。</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    public static ScriptSnapshot Read(string directory, Action<string> debug, Action<string> warn)
    {
        var snapshot = new ScriptSnapshot();
        if (!Directory.Exists(directory)) return snapshot;

        List<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 构建过程中子目录被创建/删除
            throw new NotReadyException($"cannot list '{directory}': {ex.Message}", ex);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var path in paths)
        {
            var dll = ReadBytes(path);

            switch (Classify(dll))
            {
                case DllKind.Native:
                    debug($"Skipping unmanaged DLL: {Path.GetFileName(path)}");
                    continue;
                case DllKind.Broken:
                    // 刚写过 → 多半是编译器/复制还没写完，稍后重试；很久没动过还是坏的 → 垃圾文件（0 字节、文本、LFS 指针……），
                    // 跳过并警告，不能让一个坏文件永远卡住所有重载。
                    if (IsRecent(path))
                        throw new NotReadyException($"{Path.GetFileName(path)} is not a complete .NET assembly yet (still being written?)");

                    warn($"{path}: not a valid .NET assembly — ignored");
                    continue;
            }

            var pdb = ReadMatchingPdb(path, dll, warn);

            // 每一段前面都写上长度：DLL 和 PDB 的字节是变长的，直接拼接会让不同的 (dll, pdb) 切分得到同一个摘要
            AppendFramed(hash, Encoding.UTF8.GetBytes(path));
            AppendFramed(hash, dll);
            AppendFramed(hash, pdb);

            snapshot.Files.Add(new ScriptFile(path, dll, pdb));
        }

        snapshot.Fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        return snapshot;
    }

    private static void AppendFramed(IncrementalHash hash, byte[]? data)
    {
        // -1 表示「没有这一段」（没有旁挂 PDB），和长度为 0 的空段区分开
        hash.AppendData(BitConverter.GetBytes(data?.LongLength ?? -1L));
        if (data is not null) hash.AppendData(data);
    }

    /// <summary>
    /// 读旁挂的 PDB，并确认它真的是这份 DLL 的：DLL 的 CodeView 调试目录里有 PDB 的 GUID，portable PDB 的头里也有。
    /// 编译器/复制是先写 DLL 再写 PDB，中间这几十毫秒里磁盘上是「新 DLL + 上一次的旧 PDB」，
    /// 带着旧 PDB 加载会让堆栈里的行号悄悄指错地方。
    /// </summary>
    private static byte[]? ReadMatchingPdb(string dllPath, byte[] dll, Action<string> warn)
    {
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (!File.Exists(pdbPath)) return null;

        var pdb = ReadBytes(pdbPath);

        switch (CheckPdb(dll, pdb))
        {
            case PdbMatch.Mismatch:
            case PdbMatch.Truncated:
                if (IsRecent(pdbPath) || IsRecent(dllPath))
                    throw new NotReadyException($"{Path.GetFileName(pdbPath)} does not match {Path.GetFileName(dllPath)} yet (still being written?)");

                warn($"{pdbPath}: does not belong to {Path.GetFileName(dllPath)} (stale or damaged) — loading without debug symbols");
                return null;

            default:
                return pdb;
        }
    }

    private static bool IsRecent(string path) => DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < SettleTime;

    private static byte[] ReadBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (IOException ex)
        {
            throw new NotReadyException($"{Path.GetFileName(path)} cannot be read right now: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new NotReadyException($"{Path.GetFileName(path)} cannot be read right now: {ex.Message}", ex);
        }
    }

    private enum DllKind { Managed, Native, Broken }

    private static DllKind Classify(byte[] bytes)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata) return DllKind.Native;
            return pe.GetMetadataReader().IsAssembly ? DllKind.Managed : DllKind.Native;
        }
        catch (BadImageFormatException)
        {
            return DllKind.Broken;
        }
        catch (InvalidOperationException)
        {
            return DllKind.Broken;
        }
    }

    internal enum PdbMatch
    {
        /// <summary>GUID 对得上。</summary>
        Matches,

        /// <summary>验证不了（Windows 原生 PDB、DLL 没有 CodeView 目录……）：照旧交给 Cecil。</summary>
        Unknown,

        /// <summary>是 portable PDB，但它的 GUID 不是这份 DLL 的。</summary>
        Mismatch,

        /// <summary>开头是 portable PDB 的魔数却解析失败：写了一半。</summary>
        Truncated,
    }

    internal static PdbMatch CheckPdb(byte[] dll, byte[] pdb)
    {
        Guid? expected = null;
        try
        {
            using var pe = new PEReader(new MemoryStream(dll, writable: false));
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.CodeView) continue;

                expected = pe.ReadCodeViewDebugDirectoryData(entry).Guid;
                break;
            }
        }
        catch (BadImageFormatException)
        {
            return PdbMatch.Unknown;
        }

        if (expected is null) return PdbMatch.Unknown;

        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb, writable: false));
            var header = provider.GetMetadataReader().DebugMetadataHeader;
            if (header is null) return PdbMatch.Unknown;

            // DebugMetadataHeader.Id 是 20 字节：前 16 字节是 GUID，后 4 字节是时间戳
            if (header.Id.Length < 16) return PdbMatch.Unknown;
            var actual = new Guid(header.Id.AsSpan(0, 16));
            return actual == expected.Value ? PdbMatch.Matches : PdbMatch.Mismatch;
        }
        catch (BadImageFormatException)
        {
            // portable PDB 以 "BSJB" 开头；不是这个开头的是 Windows 原生 PDB（验证不了，交给 Cecil）
            return StartsWithPortableMagic(pdb) ? PdbMatch.Truncated : PdbMatch.Unknown;
        }
    }

    private static bool StartsWithPortableMagic(byte[] pdb)
        => pdb.Length >= 4 && pdb[0] == (byte)'B' && pdb[1] == (byte)'S' && pdb[2] == (byte)'J' && pdb[3] == (byte)'B';
}
