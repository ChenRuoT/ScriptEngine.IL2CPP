using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace BehaviourScript;

/// <summary>
/// 一个注入的 MonoBehaviour：每 120 帧打一行日志，OnDestroy 也打日志。
/// 每次重载引擎都会给它的命名空间加上 .__g{代次}，所以 ClassInjector 不会因为重名而报错。
/// </summary>
public sealed class TickBehaviour : MonoBehaviour
{
    public TickBehaviour(IntPtr pointer) : base(pointer) { }

    internal static BepInEx.Logging.ManualLogSource? Log;
    internal string Owner = "?";
    private int _frames;

    private void Update()
    {
        if (++_frames % 120 == 0)
            Log?.LogInfo($"[tick] {Owner}: frame {_frames} (type = {GetType().FullName})");
    }

    private void OnDestroy() => Log?.LogInfo($"[tick] {Owner}: OnDestroy after {_frames} frames");
}

/// <summary>守规矩的插件：自己保存组件引用，在 Unload() 里销毁。</summary>
[BepInPlugin("com.shiunaya.samples.behaviour.tidy", "Tidy Behaviour", "1.0.0")]
public sealed class TidyPlugin : BasePlugin
{
    private TickBehaviour? _behaviour;
    private SharedLib.SharedBehaviour? _shared;

    public override void Load()
    {
        TickBehaviour.Log = Log;
        _behaviour = AddComponent<TickBehaviour>();
        _behaviour.Owner = "tidy";
        SharedLib.SharedBehaviour.Message += OnShared;
        _shared = AddComponent<SharedLib.SharedBehaviour>();
        Log.LogInfo("[tidy] Load: component added");
    }

    private void OnShared(string text) => Log.LogInfo($"[tidy] shared: {text}");

    public override bool Unload()
    {
        SharedLib.SharedBehaviour.Message -= OnShared;
        if (_behaviour != null)
            UnityEngine.Object.Destroy(_behaviour);
        if (_shared != null)
            UnityEngine.Object.Destroy(_shared);
        _shared = null;
        _behaviour = null;
        Log.LogInfo("[tidy] Unload: components destroyed");
        return true;
    }
}

/// <summary>
/// 不守规矩的插件：没写 Unload()。引擎的残留清理应该替它把组件销毁掉。
/// 它的注入类型是<b>嵌套在插件类里</b>的（社区插件常见写法）：引擎改的是最外层 LeakyPlugin 的命名空间，
/// 所以这个插件自己的类型名每一代都会变（BehaviourScript.__g{N}.LeakyPlugin），实例化时必须用改写后的名字。
/// </summary>
[BepInPlugin("com.shiunaya.samples.behaviour.leaky", "Leaky Behaviour", "1.0.0")]
public sealed class LeakyPlugin : BasePlugin
{
    public sealed class LeakyTick : MonoBehaviour
    {
        public LeakyTick(IntPtr pointer) : base(pointer) { }

        private int _frames;

        private void Update()
        {
            if (++_frames % 240 == 0)
                TickBehaviour.Log?.LogInfo($"[leaky] nested tick: frame {_frames} (type = {GetType().FullName})");
        }
    }

    public override void Load()
    {
        AddComponent<LeakyTick>();
        Log.LogInfo("[leaky] Load: nested component added (and never cleaned up by the plugin)");
    }
}
