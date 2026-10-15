using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using UnityEngine;

namespace HotkeyTestScript;

/// <summary>
/// 热键验证示例：按下配置的热键（默认 F5）打印一行消息，消息里带上「哪个代次处理的」。
/// 用来验证两件事：
///   1. 脚本里注入的 MonoBehaviour 能收到 Unity 的 Update / Input（引擎注入的类型每代会被改名成 __g{N}）；
///   2. 热重载之后，按键由新代次处理（日志里的命名空间会从 __g1 变成 __g2 …）。
/// 注意：热键可以改，见 BepInEx/config/com.shiunaya.scriptengine.hotkeytest.cfg —— 有些键会被别的软件
/// 注册成全局热键（本机实测 F7 被占用：RegisterHotKey(F7) 返回 1409），那样按键根本到不了游戏，
/// 表现为「按键没反应」而不是插件的 bug。
/// </summary>
[BepInPlugin("com.shiunaya.scriptengine.hotkeytest", "ScriptEngine Hotkey Test", "1.0.0")]
public sealed class HotkeyPlugin : BasePlugin
{
    private HotkeyBehaviour? _behaviour;

    public override void Load()
    {
        var hotkey = Config.Bind(
            "General",
            "Hotkey",
            KeyCode.F5,
            "按下这个键会打印一条 HOTKEY_TEST_MESSAGE（默认 F5；换键之后可以立刻用 F6 或文件监视热重载）");

        _behaviour = AddComponent<HotkeyBehaviour>();
        _behaviour.Init(Log, hotkey);

        Log.LogInfo($"Hotkey test loaded — press {hotkey.Value} to print the current message.");
    }

    public override bool Unload()
    {
        if (_behaviour != null)
        {
            UnityEngine.Object.Destroy(_behaviour);
            _behaviour = null;
        }

        return true;
    }
}

public sealed class HotkeyBehaviour : MonoBehaviour
{
    private ManualLogSource? _logger;
    private ConfigEntry<KeyCode>? _hotkey;
    private int _frames;
    private bool _inputErrorLogged;
    private bool _lastFocused;

    public HotkeyBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    public void Init(ManualLogSource logger, ConfigEntry<KeyCode> hotkey)
    {
        _logger = logger;
        _hotkey = hotkey;
        _logger.LogInfo($"[probe] behaviour {GetType().Namespace} is up (hotkey = {hotkey.Value})");
    }

    public void Update()
    {
        _frames++;

        bool focused;
        bool anyDown;

        try
        {
            focused = Application.isFocused;
            anyDown = Input.anyKeyDown;
        }
        catch (Exception ex)
        {
            if (!_inputErrorLogged)
            {
                _inputErrorLogged = true;
                _logger?.LogError($"[probe] Input 抛异常：{ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        // 焦点变化：判断「窗口有没有拿到焦点」最可靠的信号（没焦点时按键根本不会来）
        if (focused != _lastFocused)
        {
            _lastFocused = focused;
            _logger?.LogInfo($"[probe] focus -> {focused} (frame {_frames})");
        }

        // 有按键就认一下是哪个功能键：排查「按键被别的软件吃掉」时靠这行
        if (anyDown)
        {
            var keys = FKeyScanner();
            if (keys.Length > 0)
                _logger?.LogInfo($"[probe] keys=[{keys}] frame={_frames}");
        }

        if (_hotkey != null && Input.GetKeyDown(_hotkey.Value))
        {
            // 命名空间里带着代次（__g1 / __g2 …），所以这一行本身就证明「哪个代次的代码在响应按键」
            _logger?.LogInfo($"HOTKEY_TEST_MESSAGE — handled by {GetType().Namespace} on frame {_frames}");
        }
    }

    /// <summary>扫一遍 F1–F12，返回这一帧里按下的那些键（只在 anyKeyDown 时才扫，开销可忽略）。</summary>
    private static string FKeyScanner()
    {
        var sb = new System.Text.StringBuilder();

        for (int i = 1; i <= 12; i++)
        {
            var key = (KeyCode)((int)KeyCode.F1 + i - 1);
            if (Input.GetKeyDown(key))
                sb.Append(key).Append(' ');
        }

        return sb.ToString().Trim();
    }
}
