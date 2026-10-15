using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PatchScript;

/// <summary>被打补丁的目标：原本返回 1。</summary>
public static class Target
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Compute() => 1;
}

[HarmonyPatch(typeof(Target), nameof(Target.Compute))]
public static class ComputePatch
{
    private static bool Prefix(ref int __result)
    {
        __result = 42;
        return false; // 跳过原方法
    }
}

/// <summary>
/// 守规矩的 Harmony 用法：Load() 里 PatchAll，Unload() 里 UnpatchSelf()。
/// 把 [Test] UnpatchOnUnload 改成 false（BepInEx/config/com.shiunaya.samples.patch.cfg）可以看到不撤销的后果。
/// </summary>
[BepInPlugin("com.shiunaya.samples.patch", "Patch Script", "1.0.0")]
public sealed class PatchPlugin : BasePlugin
{
    private Harmony? _harmony;
    private bool _unpatchOnUnload;

    public override void Load()
    {
        _unpatchOnUnload = Config.Bind("Test", "UnpatchOnUnload", true, "Call Harmony.UnpatchSelf() in Unload().").Value;

        _harmony = new Harmony("com.shiunaya.samples.patch");
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        Log.LogInfo($"[patch] Load: Target.Compute() = {Target.Compute()} (42 = patched), unpatchOnUnload={_unpatchOnUnload}");
    }

    public override bool Unload()
    {
        if (_unpatchOnUnload) _harmony?.UnpatchSelf();
        _harmony = null;
        Log.LogInfo($"[patch] Unload: Target.Compute() = {Target.Compute()} (1 = unpatched)");
        return true;
    }
}
