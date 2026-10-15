using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace HostPatchFixture;

/// <summary>补丁目标按名字指向测试宿主里的 ScriptEngine.Tests.Program.HostTarget（脚本不引用宿主程序集）。</summary>
[HarmonyPatch("ScriptEngine.Tests.Program", "HostTarget")]
public static class HostTargetPatch
{
    private static bool Prefix(ref int __result)
    {
        __result = 42;
        return false;
    }
}

[BepInPlugin("com.shiunaya.fixtures.hostpatch", "Host Patch Fixture", "1.0.0")]
public sealed class HostPatchPlugin : BasePlugin
{
    private Harmony? _harmony;

    public override void Load()
    {
        _harmony = new Harmony("com.shiunaya.fixtures.hostpatch");
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        return true;
    }
}
