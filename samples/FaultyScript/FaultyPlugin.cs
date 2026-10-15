using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace FaultyScript;

/// <summary>每 120 帧打一行日志的注入组件。同一个类型被正常插件和故障插件共用，用来验证引擎只清掉故障插件挂的那一个。</summary>
public sealed class SharedTick : MonoBehaviour
{
    public SharedTick(IntPtr pointer) : base(pointer) { }

    internal static BepInEx.Logging.ManualLogSource? Log;
    internal string Owner = "?";
    private int _frames;

    private void Update()
    {
        if (++_frames % 120 == 0)
            Log?.LogInfo($"[tick] {Owner}: frame {_frames}");
    }
}

/// <summary>
/// 先挂上一个组件，然后 Load() 抛异常。引擎应当：记录带行号的错误、调用它的 Unload()、
/// 把它半路挂上的 SharedTick 销毁掉，而同一程序集里别的插件（Companion）挂的 SharedTick 不受影响。
/// </summary>
[BepInPlugin("com.shiunaya.samples.faulty", "Faulty Script", "1.0.0")]
public sealed class FaultyPlugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo("[faulty] Load: adds a component, then fails on purpose");
        var tick = AddComponent<SharedTick>();
        tick.Owner = "faulty";
        Boom();
    }

    public override bool Unload()
    {
        Log.LogInfo("[faulty] Unload: cleanup after a failed Load()");
        return true;
    }

    private static void Boom() => throw new InvalidOperationException("boom (expected: this sample always fails)");
}

/// <summary>正常的插件，GUID 排在 Faulty 前面，所以先装好；它挂的 SharedTick 必须在 Faulty 失败之后仍然活着。</summary>
[BepInPlugin("com.shiunaya.samples.companion", "Companion Script", "1.0.0")]
public sealed class CompanionPlugin : BasePlugin
{
    private SharedTick? _tick;

    public override void Load()
    {
        SharedTick.Log = Log;
        _tick = AddComponent<SharedTick>();
        _tick.Owner = "companion";
        Log.LogInfo("[companion] Load: component added");
    }

    public override bool Unload()
    {
        if (_tick != null) UnityEngine.Object.Destroy(_tick);
        _tick = null;
        Log.LogInfo("[companion] Unload");
        return true;
    }
}

/// <summary>声明和 Companion 不兼容。Companion 已经装好了，所以引擎应当拒绝装这个。</summary>
[BepInPlugin("com.shiunaya.samples.incompatible", "Incompatible Script", "1.0.0")]
[BepInIncompatibility("com.shiunaya.samples.companion")]
public sealed class IncompatiblePlugin : BasePlugin
{
    public override void Load() => Log.LogError("[incompatible] Load() ran — it should have been refused!");
}
