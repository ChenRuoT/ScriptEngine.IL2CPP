using System;
using UnityEngine;

namespace ScriptEngineIl2Cpp;

/// <summary>
/// 引擎唯一注入的 MonoBehaviour，只负责每帧驱动 <see cref="EnginePump"/>。
/// 引擎本身是常驻插件、永不重载，所以这个类型只会被注入一次，不受 "already injected" 影响。
/// </summary>
internal sealed class EngineBehaviour : MonoBehaviour
{
    internal static EnginePump? Pump { get; set; }

    private float _lastErrorTime = -999f;

    public EngineBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Update()
    {
        var pump = Pump;
        if (pump is null) return;

        try
        {
            pump.Tick();
        }
        catch (Exception ex)
        {
            // 泵出任何异常都不该让 Unity 每帧刷一整页堆栈：限流到 5 秒一次
            var now = Time.unscaledTime;
            if (now - _lastErrorTime < 5f) return;
            _lastErrorTime = now;
            ScriptEnginePlugin.Logger.LogError($"Engine update failed (repeats suppressed for 5 s): {ex}");
        }
    }
}
