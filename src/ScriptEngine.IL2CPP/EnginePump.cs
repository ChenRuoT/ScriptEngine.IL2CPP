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

    private readonly ManualLogSource _log;
    private readonly EngineSettings _settings;
    private readonly ScriptHost _host;
    private readonly FileTrigger _files = new();
    private readonly CommandChannel? _commands;

    private bool _started;
    private bool _hotkeyBroken;
    private int _deferredRetries;
    private string? _retryTrigger;
    private bool _retryForce;
    private long _nextHealthCheck;
    private int _publishedHistoryVersion;

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
                report = RunReload("command", force: true);
                ok = report.Ok && !report.Deferred;
                break;
            case "unload":
                report = _host.UnloadAll("command");
                ok = report.Ok;
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
                _deferredRetries = 0;
                _retryTrigger = null;
                _retryForce = false;
                report.Error($"Gave up after {MaxDeferredRetries} retries: the script files are still not readable.");
            }
        }
        else
        {
            _deferredRetries = 0;
            _retryTrigger = null;
            _retryForce = false;
        }

        PublishStatus();
        return report;
    }

    /// <summary>脚本目录被删掉重建、监视器报错后失效时，隔几秒自动重新挂上。</summary>
    private void EnsureWatcherHealthy()
    {
        if (!_settings.AutoReload.Value) return;

        var now = Environment.TickCount64;
        if (now < _nextHealthCheck) return;
        _nextHealthCheck = now + 5000;

        _files.EnsureHealthy(_settings.ScriptsDirectory, message => _log.LogWarning(message));
    }

    private void PublishStatus()
    {
        _publishedHistoryVersion = _host.HistoryVersion;
        _commands?.WriteStatus(StatusJson.Status(_host, _settings.ScriptsDirectory));
    }

    public void Dispose() => _files.Dispose();
}
