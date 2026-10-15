using System;
using BepInEx.Logging;
using ScriptEngineIl2Cpp.Triggers;
using UnityEngine;

namespace ScriptEngineIl2Cpp;

/// <summary>
/// 引擎的「每帧大脑」：热键、文件变化、命令通道都在这里汇总成对 <see cref="ScriptHost"/> 的调用。
/// 由唯一的注入组件 <see cref="EngineBehaviour"/> 在 Unity 主线程上每帧驱动。
/// </summary>
internal sealed class EnginePump : IDisposable
{
    /// <summary>文件没准备好时的最大重试次数（每次间隔一个安静期）。</summary>
    private const int MaxDeferredRetries = 30;

    /// <summary>检查「监视器是否还健康 / 配置有没有改」的间隔。</summary>
    private const long HealthCheckIntervalMs = 5000;

    private readonly ManualLogSource _log;
    private readonly EngineSettings _settings;
    private readonly ScriptHost _host;
    private readonly FileTrigger _files = new();
    private readonly CommandChannel? _commands;

    private bool _started;
    private bool _hotkeyBroken;
    private long _nextHealthCheck;
    private int _publishedHistoryVersion;

    // 被延后的重载（文件正被写入 / 占用）：带着原来的触发源和 force 重试，直到成功或放弃
    private int _deferredRetries;
    private string? _retryTrigger;
    private bool _retryForce;

    // 触发了被延后的重载的那条命令：结果要等重试有了结局再写，而不是立刻回一个「失败」
    private PendingCommand? _deferredCommand;

    public EnginePump(ManualLogSource log, EngineSettings settings, ScriptHost host)
    {
        _log = log;
        _settings = settings;
        _host = host;

        if (_settings.CommandChannel.Value)
            _commands = new CommandChannel(EngineSettings.WorkDirectory, message => _log.LogWarning(message));
    }

    public void Tick()
    {
        _host.Tick();

        if (!_started)
        {
            _started = true;
            Start();
        }

        EnsureWatcherHealthy();
        PollHotkey();
        PollFiles();
        PollCommands();

        if (_host.HistoryVersion != _publishedHistoryVersion) PublishStatus();
    }

    private void Start()
    {
        if (_settings.AutoReload.Value)
            _files.Start(_settings.ScriptsDirectory, message => _log.LogWarning(message));

        _log.LogInfo($"Scripts folder: {_settings.ScriptsDirectory}");
        _log.LogInfo($"Reload: hotkey={_settings.ReloadKey.Value}, auto-reload={(_files.IsWatching ? "on" : "off")}, " +
                     $"command channel={(_commands is null ? "off" : _commands.CommandDirectory)}");

        if (_settings.LoadOnStart.Value)
            RunReload("startup", force: true);
        else
            PublishStatus();
    }

    /// <summary>
    /// 让文件监视跟上运行时的配置变化：<c>AutoReload</c> 关掉就停止监视，打开就重新挂上；
    /// <c>ScriptsDirectory</c> 改了就切到新目录；脚本目录被删掉重建、监视器报错失效后也在这里自愈。
    /// </summary>
    private void EnsureWatcherHealthy()
    {
        var now = Environment.TickCount64;
        if (now < _nextHealthCheck) return;
        _nextHealthCheck = now + HealthCheckIntervalMs;

        if (_settings.AutoReload.Value)
            _files.EnsureHealthy(_settings.ScriptsDirectory, message => _log.LogWarning(message));
        else if (_files.IsWatching)
            _files.Stop();
    }

    private void PollHotkey()
    {
        var key = _settings.ReloadKey.Value;
        if (_hotkeyBroken || key == KeyCode.None) return;

        bool pressed;
        try
        {
            pressed = Input.GetKeyDown(key);
        }
        catch (Exception ex)
        {
            // 旧输入后端不可用时别每帧刷异常
            _hotkeyBroken = true;
            _log.LogWarning($"Reload hotkey disabled — reading the keyboard failed: {ex.Message}");
            return;
        }

        if (pressed) RunReload("hotkey", force: true);
    }

    private void PollFiles()
    {
        // 自动重载关着（并且没有被延后的重载在等重试）：别囤着文件事件，开关重新打开时会自己补一次
        if (!_settings.AutoReload.Value && _retryTrigger is null)
        {
            _files.ClearPendingBefore(long.MaxValue);
            return;
        }

        var quietMs = (long)(_settings.AutoReloadDelay.Value * 1000f);
        if (!_files.TryConsume(quietMs)) return;

        // 被延后的重载（热键 / 命令触发的强制重载也算）带着原来的触发源和 force 重试：
        // 否则瞬时的文件占用会把「强制重载」悄悄降级成「内容没变 → 跳过」。
        var trigger = _retryTrigger ?? "file";
        var force = _retryForce;
        _retryTrigger = null;
        _retryForce = false;
        RunReload(trigger, force);
    }

    private void PollCommands()
    {
        if (_commands is null) return;
        if (_commands.Poll() is not { } command) return;

        ReloadReport? report = null;
        var ok = true;

        switch (command.Command)
        {
            case "reload":
                SupersedeDeferredCommand(command);
                report = RunReload("command", force: true);

                if (report.Deferred && _retryTrigger is not null)
                {
                    // 文件正在写：引擎会自己重试。命令的结果等重试有了结局再写，别让客户端拿到一个马上就会被推翻的失败
                    _deferredCommand = command;
                    return;
                }

                ok = report.Ok && !report.Deferred;
                break;

            case "unload":
                SupersedeDeferredCommand(command);
                report = _host.UnloadAll("command");
                ok = report.Ok;

                // 卸载是明确的意图：别让防抖期里攒下的文件事件、或者还在重试的重载在一秒后把脚本又装回来
                ResetRetry();
                _files.ClearPendingBefore(Environment.TickCount64);
                PublishStatus();
                break;

            case "status":
                break;

            default:
                ok = false;
                _log.LogWarning($"Unknown command '{command.Command}' (id {command.Id}); expected .reload / .unload / .status");
                break;
        }

        _commands.WriteResult(command, StatusJson.CommandResult(command.Id, command.Command, ok, report, _host, _settings.ScriptsDirectory));
    }

    private ReloadReport RunReload(string trigger, bool force)
    {
        var startedTicks = Environment.TickCount64;
        var report = _host.Reload(trigger, force);

        if (report.Deferred)
        {
            if (++_deferredRetries <= MaxDeferredRetries)
            {
                _retryTrigger = trigger;
                _retryForce |= force;
                _files.Poke(); // 安静期过后自动再试
            }
            else
            {
                ResetRetry();
                report.Error($"Gave up after {MaxDeferredRetries} retries: the script files are still not readable.");
                CompleteDeferredCommand(report);
            }
        }
        else
        {
            ResetRetry();

            // 这次重载读到的是 startedTicks 之后磁盘上的内容：之前攒下的文件事件已经被它覆盖，
            // 不该在防抖期过后再触发一次「内容没变」的空重载。
            // 重载期间新到的变化时间戳更晚，会保留下来。
            _files.ClearPendingBefore(startedTicks);
            CompleteDeferredCommand(report);
        }

        PublishStatus();
        return report;
    }

    private void ResetRetry()
    {
        _deferredRetries = 0;
        _retryTrigger = null;
        _retryForce = false;
    }

    /// <summary>被延后的 reload 命令有了结局（重载成功，或者放弃了）：把结果写出去。</summary>
    private void CompleteDeferredCommand(ReloadReport report)
    {
        if (_deferredCommand is not { } command || _commands is null) return;
        _deferredCommand = null;

        var ok = report.Ok && !report.Deferred;
        _commands.WriteResult(command, StatusJson.CommandResult(command.Id, command.Command, ok, report, _host, _settings.ScriptsDirectory));
    }

    /// <summary>又来了一条会改变脚本状态的命令：悬着的那条 reload 命令就此了结（它的重载已经被新命令接替）。</summary>
    private void SupersedeDeferredCommand(PendingCommand successor)
    {
        if (_deferredCommand is not { } older || _commands is null) return;
        _deferredCommand = null;

        _commands.WriteResult(older, StatusJson.CommandResult(
            older.Id, older.Command, ok: false, report: null, _host, _settings.ScriptsDirectory,
            message: $"superseded by command '{successor.Id}.{successor.Command}' before the script files became readable"));
    }

    private void PublishStatus()
    {
        _publishedHistoryVersion = _host.HistoryVersion;
        _commands?.WriteStatus(StatusJson.Status(_host, _settings.ScriptsDirectory));
    }

    public void Dispose() => _files.Dispose();
}
