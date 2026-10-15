using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace FaultyScript;

/// <summary>Load() 一定抛异常的插件。引擎应当：记录带行号的错误、清掉它的 Plugins 登记、调用它的 Unload() 收拾、不影响其它脚本。</summary>
[BepInPlugin("com.shiunaya.samples.faulty", "Faulty Script", "1.0.0")]
public sealed class FaultyPlugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo("[faulty] Load: about to fail on purpose");
        Boom();
    }

    public override bool Unload()
    {
        Log.LogInfo("[faulty] Unload: cleanup after a failed Load()");
        return true;
    }

    private static void Boom() => throw new InvalidOperationException("boom (expected: this sample always fails)");
}
