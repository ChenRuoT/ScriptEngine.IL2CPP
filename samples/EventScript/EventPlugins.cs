using System;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Comfort.Common;
using EFT.Ballistics;
using UnityEngine;

namespace EventScript;

/// <summary>两个插件共用的探针：读事件委托的调用列表长度，不触发任何游戏事件。</summary>
internal static class Probe
{
    internal static string Context(object owner)
        => AssemblyLoadContext.GetLoadContext(owner.GetType().Assembly)?.Name ?? "?";

    /// <summary>GlobalEventDispatcher.OnBeingHit 当前有几个订阅者（主菜单里就有这个单例）。</summary>
    internal static int BeingHitSubscribers(GlobalEventDispatcher? dispatcher)
    {
        var chain = dispatcher?.OnBeingHitField;
        return chain == null ? 0 : chain.GetInvocationList().Length;
    }
}

/// <summary>守规矩：Unload() 里 -= 退订。重载 N 次，订阅者数应该始终回到基线，不会越攒越多。</summary>
[BepInPlugin("com.shiunaya.samples.events.tidy", "Tidy Event Script", "1.0.0")]
public sealed class TidyEventPlugin : BasePlugin
{
    // 委托必须自己持有：Il2CppInterop 按「同一个托管委托 → 同一个原生委托」缓存转换结果，-= 时要传同一个对象
    private Action<DamageInfo, EFT.Player>? _onBeingHit;
    private Action<string, string, LogType>? _onLog;
    private GlobalEventDispatcher? _dispatcher;
    private int _probeHits;
    private bool _useDispatcherEvent;
    private bool _useLogEvent;

    public override void Load()
    {
        // 两个开关用来二分「哪条订阅路径会让旧上下文回收不了」：改 BepInEx/config/com.shiunaya.samples.events.tidy.cfg 再重载
        _useDispatcherEvent = Config.Bind("Test", "DispatcherEvent", true, "Subscribe to GlobalEventDispatcher.OnBeingHit.").Value;
        _useLogEvent = Config.Bind("Test", "LogEvent", true, "Subscribe to Application.logMessageReceived.").Value;

        _dispatcher = Singleton<GlobalEventDispatcher>.Instance;
        _onBeingHit = OnBeingHit;
        _onLog = OnLog;

        var before = Probe.BeingHitSubscribers(_dispatcher);
        if (_useDispatcherEvent && _dispatcher != null) _dispatcher.OnBeingHit += _onBeingHit;
        Log.LogInfo($"[events] tidy ({Probe.Context(this)}): OnBeingHit subscribers {before} -> {Probe.BeingHitSubscribers(_dispatcher)} (dispatcher {(_dispatcher != null ? "present" : "missing")}, use={_useDispatcherEvent}/{_useLogEvent})");

        if (_useLogEvent) Application.logMessageReceived += _onLog;
        Debug.Log("[events-probe] ping from tidy"); // 同步触发 Unity 的日志事件：所有还订阅着的处理器都会跑一遍
        Log.LogInfo($"[events] tidy ({Probe.Context(this)}): my own log handler ran {_probeHits} time(s) for the probe");
    }

    public override bool Unload()
    {
        if (_useDispatcherEvent && _dispatcher != null && _onBeingHit != null) _dispatcher.OnBeingHit -= _onBeingHit;
        if (_useLogEvent && _onLog != null) Application.logMessageReceived -= _onLog;

        Log.LogInfo($"[events] tidy ({Probe.Context(this)}): unsubscribed, OnBeingHit subscribers now {Probe.BeingHitSubscribers(_dispatcher)}");
        _onBeingHit = null;
        _onLog = null;
        _dispatcher = null;
        return true;
    }

    private void OnBeingHit(DamageInfo info, EFT.Player player) { }

    private void OnLog(string condition, string stackTrace, LogType type)
    {
        if (!condition.StartsWith("[events-probe]", StringComparison.Ordinal)) return;

        _probeHits++;
        // 旧代次的处理器如果还订阅着，会在这里打出一行 ctx 是旧代次的日志
        Log.LogInfo($"[events] tidy handler of {Probe.Context(this)} saw: {condition}");
    }
}

/// <summary>
/// 反面教材：订阅了但 Unload() 里不退订。重载 N 次，订阅者数会一路涨，旧代次的处理器还在被调用，旧上下文也回收不了。
/// 默认不启用：在 BepInEx/config/com.shiunaya.samples.events.forgetful.cfg 里写 [Test] Enable = true 之后再重载。
/// </summary>
[BepInPlugin("com.shiunaya.samples.events.forgetful", "Forgetful Event Script", "1.0.0")]
public sealed class ForgetfulEventPlugin : BasePlugin
{
    private Action<DamageInfo, EFT.Player>? _onBeingHit;
    private Action<string, string, LogType>? _onLog;

    public override void Load()
    {
        if (!Config.Bind("Test", "Enable", false, "Subscribe to events and never unsubscribe (demonstrates what NOT to do).").Value)
        {
            Log.LogInfo("[events] forgetful: disabled (set [Test] Enable = true in its .cfg to try it)");
            return;
        }

        var dispatcher = Singleton<GlobalEventDispatcher>.Instance;
        _onBeingHit = (_, _) => { };
        _onLog = OnLog;

        var before = Probe.BeingHitSubscribers(dispatcher);
        if (dispatcher != null) dispatcher.OnBeingHit += _onBeingHit;
        Log.LogInfo($"[events] forgetful ({Probe.Context(this)}): OnBeingHit subscribers {before} -> {Probe.BeingHitSubscribers(dispatcher)}");

        Application.logMessageReceived += _onLog;
    }

    public override bool Unload()
    {
        Log.LogInfo($"[events] forgetful ({Probe.Context(this)}): Unload() returns true but forgets to unsubscribe");
        return true;
    }

    private void OnLog(string condition, string stackTrace, LogType type)
    {
        if (condition.StartsWith("[events-probe]", StringComparison.Ordinal))
            Log.LogInfo($"[events] forgetful handler of {Probe.Context(this)} saw: {condition}");
    }
}

/// <summary>
/// 在组件自己的 OnDestroy 里退订事件。插件的 Unload() 只是 Destroy() 它 —— 真正的销毁要等到帧尾，
/// 所以 OnDestroy（和里面的 -=）发生在引擎第一步卸载<b>之后</b>。引擎必须把清转换缓存推迟到那之后，
/// 否则 -= 会拿到一个新造的 IL2CPP 委托，删不掉当初订阅的那个。
/// 默认不启用：在 BepInEx/config/com.shiunaya.samples.events.ondestroy.cfg 里写 [Test] Enable = true 之后再重载。
/// </summary>
[BepInPlugin("com.shiunaya.samples.events.ondestroy", "OnDestroy Event Script", "1.0.0")]
public sealed class OnDestroyEventPlugin : BasePlugin
{
    private DeferredUnsubscribeBehaviour? _behaviour;

    public override void Load()
    {
        if (!Config.Bind("Test", "Enable", false, "Subscribe in a component's Awake and unsubscribe in its OnDestroy.").Value)
        {
            Log.LogInfo("[events] ondestroy: disabled (set [Test] Enable = true in its .cfg to try it)");
            return;
        }

        DeferredUnsubscribeBehaviour.Log = Log;
        _behaviour = AddComponent<DeferredUnsubscribeBehaviour>();
    }

    public override bool Unload()
    {
        if (_behaviour != null) UnityEngine.Object.Destroy(_behaviour); // 延后到帧尾才真正销毁 → OnDestroy 里的 -= 晚于引擎的第一步卸载
        _behaviour = null;
        return true;
    }
}

public sealed class DeferredUnsubscribeBehaviour : MonoBehaviour
{
    public DeferredUnsubscribeBehaviour(IntPtr pointer) : base(pointer) { }

    internal static BepInEx.Logging.ManualLogSource? Log;

    private Action<DamageInfo, EFT.Player>? _handler;
    private GlobalEventDispatcher? _dispatcher;

    private void Awake()
    {
        _dispatcher = Singleton<GlobalEventDispatcher>.Instance;
        _handler = OnBeingHit;

        var before = Probe.BeingHitSubscribers(_dispatcher);
        if (_dispatcher != null) _dispatcher.OnBeingHit += _handler;
        Log?.LogInfo($"[events] ondestroy ({Probe.Context(this)}): Awake subscribed, OnBeingHit subscribers {before} -> {Probe.BeingHitSubscribers(_dispatcher)}");
    }

    private void OnDestroy()
    {
        if (_dispatcher != null && _handler != null) _dispatcher.OnBeingHit -= _handler;
        Log?.LogInfo($"[events] ondestroy ({Probe.Context(this)}): OnDestroy unsubscribed, OnBeingHit subscribers now {Probe.BeingHitSubscribers(_dispatcher)}");
        _handler = null;
        _dispatcher = null;
    }

    private void OnBeingHit(DamageInfo info, EFT.Player player) { }
}
