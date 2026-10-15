using System;
using System.IO;

namespace ScriptEngineIl2Cpp.Triggers;

/// <summary>
/// 监视脚本目录里的 DLL/PDB 变化，带防抖。
///
/// FileSystemWatcher 的回调在线程池线程上：这里只记「有变化」和「最后一次变化的时间」，
/// 真正的重载由主线程的泵在安静期过后来取（<see cref="TryConsume"/>）。
/// 两个字段的读写都在同一把锁里，所以「清掉某个时刻之前的变化」（<see cref="ClearPendingBefore"/>）不会误清掉并发到来的新变化。
/// </summary>
internal sealed class FileTrigger : IDisposable
{
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;
    private long _lastEventTicks;
    private bool _pending;
    private volatile bool _broken;
    private bool _warnedStartFailure;

    public bool IsWatching => _watcher is not null && !_broken;

    public void Start(string directory, Action<string> warn)
    {
        Stop();

        try
        {
            var fullPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(fullPath);
            var watcher = new FileSystemWatcher(fullPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Changed += (_, e) => OnFileEvent(e.FullPath);
            watcher.Created += (_, e) => OnFileEvent(e.FullPath);
            watcher.Deleted += (_, e) => OnFileEvent(e.FullPath);
            watcher.Renamed += (_, e) => { OnFileEvent(e.OldFullPath); OnFileEvent(e.FullPath); };
            watcher.Error += (_, _) =>
            {
                // 缓冲区溢出（不知道漏了什么，保险起见重载一次），或者被监视的目录被删了：监视器已经失效，等泵来重建
                _broken = true;
                Poke();
            };
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _watchedDirectory = fullPath;
            _broken = false;
            _warnedStartFailure = false;
        }
        catch (Exception ex)
        {
            if (_warnedStartFailure) return;
            _warnedStartFailure = true;
            warn($"Could not watch '{directory}' for changes (auto-reload disabled, the hotkey still works): {ex.Message}");
        }
    }

    /// <summary>停止监视（不影响已经攒下的「有变化」标记）。</summary>
    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchedDirectory = null;
    }

    /// <summary>
    /// 监视器没起来、已失效（例如脚本目录被删掉重建）或者要监视的目录变了（运行时改了 ScriptsDirectory）时重新挂上，
    /// 并补一次重载，免得漏掉空窗期的变化。
    /// </summary>
    public void EnsureHealthy(string directory, Action<string> warn)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }

        if (_watcher is not null && !_broken && string.Equals(_watchedDirectory, fullPath, StringComparison.OrdinalIgnoreCase)) return;

        Start(fullPath, warn);
        if (_watcher is not null) Poke();
    }

    /// <summary>标记「有变化」（或需要稍后重试），安静期从现在重新计时。</summary>
    public void Poke()
    {
        lock (_gate)
        {
            _lastEventTicks = Environment.TickCount64;
            _pending = true;
        }
    }

    /// <summary>有未处理的变化，并且距离最后一次变化已超过 <paramref name="quietMilliseconds"/> 时返回 true 并清除标记。</summary>
    public bool TryConsume(long quietMilliseconds)
    {
        lock (_gate)
        {
            if (!_pending) return false;
            if (Environment.TickCount64 - _lastEventTicks < quietMilliseconds) return false;

            _pending = false;
            return true;
        }
    }

    /// <summary>
    /// 丢掉「最后一次变化发生在 <paramref name="ticks"/> 或更早」的待处理标记。
    /// 一次重载（快照时刻 ≥ <paramref name="ticks"/>）已经读到了磁盘上最新的内容，在它之前攒下的变化事件不该在防抖期过后再触发一次空重载；
    /// 而重载期间新到的变化（时间戳更晚）会保留下来。
    /// </summary>
    public void ClearPendingBefore(long ticks)
    {
        lock (_gate)
        {
            if (_pending && _lastEventTicks <= ticks) _pending = false;
        }
    }

    private void OnFileEvent(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0 // 目录被移动/删除
            || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            Poke();
        }
    }

    public void Dispose() => Stop();
}
