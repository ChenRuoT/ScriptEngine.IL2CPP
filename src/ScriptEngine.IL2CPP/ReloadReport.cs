using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace ScriptEngineIl2Cpp;

/// <summary>一次重载的结果。同时负责写日志，并把消息留一份给 status.json / 命令通道。</summary>
internal sealed class ReloadReport
{
    private readonly ManualLogSource _log;

    public ReloadReport(ManualLogSource log, string trigger)
    {
        _log = log;
        Trigger = trigger;
        StartedUtc = DateTime.UtcNow;
    }

    public string Trigger { get; }
    public DateTime StartedUtc { get; }
    public long DurationMs { get; set; }

    /// <summary>分段耗时：快照+Cecil 改写 / 卸旧（含 GC） / 装新（含各插件的 Load()）。</summary>
    public long PrepareMs { get; set; }
    public long UnloadMs { get; set; }
    public long LoadMs { get; set; }

    /// <summary>本次分配到的代次；没有走到装载阶段则为 0。</summary>
    public int Generation { get; set; }

    /// <summary>成功 = 没有错误。跳过（没有变化）也算成功。</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>文件还不稳定（编译器正在写、文件被占用），稍后重试；此时旧代次原封不动。</summary>
    public bool Deferred { get; set; }

    /// <summary>内容和上次完全一致，没有重载。</summary>
    public bool Skipped { get; set; }

    public List<string> Infos { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();

    public List<PluginRecord> Loaded { get; } = new();
    public List<PluginRecord> Unloaded { get; } = new();

    /// <summary>引擎替插件清掉的残留 Unity 对象数（Unload 没销毁干净的组件）。</summary>
    public int SweptObjects { get; set; }

    public void Info(string message)
    {
        Infos.Add(message);
        _log.LogInfo(message);
    }

    public void Warn(string message)
    {
        Warnings.Add(message);
        _log.LogWarning(message);
    }

    public void Error(string message)
    {
        Errors.Add(message);
        _log.LogError(message);
    }
}

internal sealed class PluginRecord
{
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string File { get; init; } = "";

    /// <summary>仅卸载记录：<c>Unload()</c> 的返回值（插件承诺自己清理干净了吗）。</summary>
    public bool? UnloadReturned { get; set; }
}
