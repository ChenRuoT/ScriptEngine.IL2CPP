using System;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace DepMainScript;

/// <summary>主模组暴露给副模组的注入组件（引擎会给它的命名空间加上 .__g{代次}）。</summary>
public sealed class MainBehaviour : MonoBehaviour
{
    public MainBehaviour(IntPtr pointer) : base(pointer) { }
}

/// <summary>全批共用的装载序号：谁的 Load() 先跑，谁拿到小的号。</summary>
public static class LoadOrder
{
    private static int _next;
    public static int Next() => Interlocked.Increment(ref _next);
}

// GUID 故意排在副模组（…aconsumer）之后：没有 BepInDependency 时，按 GUID 字母序副模组会先装
[BepInPlugin(Guid, "Dep Main", "1.0.0")]
public sealed class DepMainPlugin : BasePlugin
{
    public const string Guid = "com.shiunaya.samples.dep.zmain";

    /// <summary>0 = 主模组的 Load() 还没跑过。</summary>
    public static int LoadedOrder { get; private set; }

    public string Greeting => "hello from DepMainPlugin";

    private MainBehaviour? _behaviour;

    public override void Load()
    {
        LoadedOrder = LoadOrder.Next();
        _behaviour = AddComponent<MainBehaviour>();
        Log.LogInfo($"[dep-main] Load: order={LoadedOrder}, assembly={typeof(DepMainPlugin).Assembly.GetName().Name}, behaviour type={typeof(MainBehaviour).FullName}");
    }

    public override bool Unload()
    {
        if (_behaviour != null) UnityEngine.Object.Destroy(_behaviour);
        _behaviour = null;
        LoadedOrder = 0;
        Log.LogInfo("[dep-main] Unload");
        return true;
    }
}
