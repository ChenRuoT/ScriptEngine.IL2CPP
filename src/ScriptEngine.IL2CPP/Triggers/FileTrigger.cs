using System;
using System.IO;
using System.Threading;

namespace ScriptEngineIl2Cpp.Triggers;

/// <summary>
/// 监视脚本目录里的 DLL/PDB 变化，带防抖。
///
/// FileSystemWatcher 的回调在线程池线程上：这里只记「有变化」和「最后一次变化的时间」两个原子量，
/// 真正的重载由主线程的泵在安静期过后来取（<see cref="TryConsume"/>）。
/// </summary>
internal sealed class FileTrigger : IDisposable
{
    private FileSystemWatcher? _watcher;
    private long _lastEventTicks;
    private int _pending;
    private volatile bool _broken;
    private bool _warnedStartFailure;

    public bool IsWatching => _watcher is not null && !_broken;

    public void Start(string directory, Action<string> warn)
    {
        Dispose();

        try
        {
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory)
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

    /// <summary>监视器没起来或已失效（例如脚本目录被删掉重建）时重新挂上，并补一次重载，免得漏掉失效期间的变化。</summary>
    public void EnsureHealthy(string directory, Action<string> warn)
    {
        if (_watcher is not null && !_broken) return;

        Start(directory, warn);
        if (_watcher is not null) Poke();
    }

    /// <summary>标记「有变化」（或需要稍后重试），安静期从现在重新计时。</summary>
    public void Poke()
    {
        Volatile.Write(ref _lastEventTicks, Environment.TickCount64);
        Volatile.Write(ref _pending, 1);
    }

    /// <summary>有未处理的变化，并且距离最后一次变化已超过 <paramref name="quietMilliseconds"/> 时返回 true 并清除标记。</summary>
    public bool TryConsume(long quietMilliseconds)
    {
        if (Volatile.Read(ref _pending) == 0) return false;
        if (Environment.TickCount64 - Volatile.Read(ref _lastEventTicks) < quietMilliseconds) return false;

        Volatile.Write(ref _pending, 0);
        return true;
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
