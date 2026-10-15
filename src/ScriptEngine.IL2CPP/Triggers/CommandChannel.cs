using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScriptEngineIl2Cpp.Triggers;

/// <summary>
/// 基于文件的命令通道，给脚本 / AI 代理用，不开任何网络端口。
///
/// 往 <c>BepInEx/ScriptEngine.IL2CPP/commands/</c> 里放一个文件，文件名 <c>&lt;id&gt;.&lt;命令&gt;</c>，内容随意：
/// <list type="bullet">
/// <item><c>42.reload</c> —— 强制重载全部脚本</item>
/// <item><c>42.unload</c> —— 卸载全部脚本</item>
/// <item><c>42.status</c> —— 只回报当前状态</item>
/// </list>
/// 引擎取走（删除）命令文件，执行，然后写出 <c>&lt;id&gt;.result.json</c>（先写临时文件再改名，读到的一定是完整 JSON）。
/// 另外每次重载/卸载后都会刷新 <c>BepInEx/ScriptEngine.IL2CPP/status.json</c>。
/// 没人取走的陈旧结果文件（超过 5 分钟）会被顺手清掉。
/// </summary>
internal sealed class CommandChannel
{
    private const long PollIntervalMs = 250;
    private const long CleanupIntervalMs = 30_000;
    private static readonly TimeSpan StaleResultAge = TimeSpan.FromMinutes(5);

    private readonly string _commandDirectory;
    private readonly string _statusPath;
    private readonly Action<string> _warn;
    private long _nextPoll;
    private long _nextCleanup;

    public CommandChannel(string workDirectory, Action<string> warn)
    {
        _commandDirectory = Path.Combine(workDirectory, "commands");
        _statusPath = Path.Combine(workDirectory, "status.json");
        _warn = warn;
        Directory.CreateDirectory(_commandDirectory);
    }

    public string CommandDirectory => _commandDirectory;

    /// <summary>取下一条待处理命令；没有则返回 null。</summary>
    public PendingCommand? Poll()
    {
        var now = Environment.TickCount64;
        if (now < _nextPoll) return null;
        _nextPoll = now + PollIntervalMs;

        string[] files;
        try
        {
            Directory.CreateDirectory(_commandDirectory); // 被人删了就重建
            files = Directory.GetFiles(_commandDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // 先把结果文件 / 临时文件筛掉，再对剩下的命令文件按创建时间排序：
        // 不然每 250 ms 都要对残留的 .result.json 逐个查一遍磁盘元数据。
        var commandFiles = new List<string>();
        var leftovers = new List<string>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".result.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                leftovers.Add(path);
            else
                commandFiles.Add(path);
        }

        if (now >= _nextCleanup)
        {
            _nextCleanup = now + CleanupIntervalMs;
            DeleteStale(leftovers);
        }

        foreach (var path in commandFiles.OrderBy(File.GetCreationTimeUtc))
        {
            var name = Path.GetFileName(path);
            var command = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
            var id = Path.GetFileNameWithoutExtension(name);

            try { File.Delete(path); }
            catch (IOException) { continue; } // 对方还没写完/还握着句柄，下个轮询再来
            catch (UnauthorizedAccessException) { continue; }

            return new PendingCommand(id, command);
        }

        return null;
    }

    public void WriteResult(PendingCommand command, string json)
        => WriteAtomically(Path.Combine(_commandDirectory, command.Id + ".result.json"), json);

    public void WriteStatus(string json) => WriteAtomically(_statusPath, json);

    private static void DeleteStale(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > StaleResultAge) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 下次再清
            }
        }
    }

    private void WriteAtomically(string path, string contents)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _warn($"Could not write {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}

internal readonly record struct PendingCommand(string Id, string Command);
