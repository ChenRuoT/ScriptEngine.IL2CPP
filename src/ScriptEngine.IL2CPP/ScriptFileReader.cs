using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
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

    /// <summary>不是完整程序集的 DLL，在最后一次写入后多久之内仍当作「还在写」（超过就当作垃圾文件跳过）。</summary>
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
                    debug($"跳过非托管 DLL: {Path.GetFileName(path)}");
                    continue;
                case DllKind.Broken:
                    // 刚写过 → 多半是编译器/复制还没写完，稍后重试；很久没动过还是坏的 → 垃圾文件（0 字节、文本、LFS 指针……），
                    // 跳过并警告，不能让一个坏文件永远卡住所有重载。
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < SettleTime)
                        throw new NotReadyException($"{Path.GetFileName(path)} is not a complete .NET assembly yet (still being written?)");

                    warn($"{path}: not a valid .NET assembly — ignored");
                    continue;
            }

            var pdbPath = Path.ChangeExtension(path, ".pdb");
            var pdb = File.Exists(pdbPath) ? ReadBytes(pdbPath) : null;

            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
            hash.AppendData(dll);
            if (pdb is not null) hash.AppendData(pdb);

            snapshot.Files.Add(new ScriptFile(path, dll, pdb));
        }

        snapshot.Fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        return snapshot;
    }

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
            throw new NotReadyException($"{Path.GetFileName(path)} 暂时读不了: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new NotReadyException($"{Path.GetFileName(path)} 暂时读不了: {ex.Message}", ex);
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
}
