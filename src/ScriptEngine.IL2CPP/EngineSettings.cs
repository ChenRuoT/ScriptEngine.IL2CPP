using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace ScriptEngineIl2Cpp;

/// <summary>引擎配置（BepInEx/config/com.shiunaya.bepinex.scriptengine.il2cpp.cfg）。</summary>
internal sealed class EngineSettings
{
    private readonly ConfigEntry<string> _scriptsDirectory;

    public EngineSettings(ConfigFile config)
    {
        _scriptsDirectory = config.Bind("General", "ScriptsDirectory", "",
            "Folder that is watched for script DLLs (BasePlugin plugins). Empty = BepInEx/scripts. " +
            "A relative path is resolved against the game folder. Sub-folders are scanned too.");

        ReloadKey = config.Bind("General", "ReloadKey", KeyCode.F6,
            "Press this key in game to reload all scripts. Set to None to disable the hotkey.");

        LoadOnStart = config.Bind("General", "LoadOnStart", true,
            "Load the scripts once the game is up (first frame after BepInEx finished loading its plugins).");

        AutoReload = config.Bind("General", "AutoReload", true,
            "Reload automatically when a DLL in the scripts folder is created, changed or deleted " +
            "(e.g. after 'dotnet build' wrote a new one). Script DLLs are read into memory, so the game never locks them.");

        AutoReloadDelay = config.Bind("General", "AutoReloadDelay", 1.0f,
            new ConfigDescription(
                "Quiet period in seconds that must pass after the last file change before reloading " +
                "(a build writes several files; this waits for it to finish).",
                new AcceptableValueRange<float>(0.2f, 30f)));

        CommandChannel = config.Bind("Automation", "CommandChannel", true,
            "Poll BepInEx/ScriptEngine.IL2CPP/commands for '<id>.reload' / '<id>.unload' / '<id>.status' files and answer with " +
            "'<id>.result.json'. Also keeps BepInEx/ScriptEngine.IL2CPP/status.json up to date. Lets scripts and AI agents drive reloads.");

        CollectibleContexts = config.Bind("Advanced", "CollectibleContexts", true,
            "Load every generation into a collectible AssemblyLoadContext so old code can be garbage-collected. " +
            "Turn off only if injecting types from a collectible context misbehaves (old generations then leak).");

        SweepLeftovers = config.Bind("Advanced", "SweepLeftovers", true,
            "After a plugin's Unload(), destroy the Unity objects (MonoBehaviour / ScriptableObject) of its injected types that are still alive.");

        Il2CppGcAfterUnload = config.Bind("Advanced", "Il2CppGcAfterUnload", true,
            "Run an IL2CPP + managed GC after unloading, so the old AssemblyLoadContext can actually be collected. May cause a short hitch.");
    }

    public ConfigEntry<KeyCode> ReloadKey { get; }
    public ConfigEntry<bool> LoadOnStart { get; }
    public ConfigEntry<bool> AutoReload { get; }
    public ConfigEntry<float> AutoReloadDelay { get; }
    public ConfigEntry<bool> CommandChannel { get; }
    public ConfigEntry<bool> CollectibleContexts { get; }
    public ConfigEntry<bool> SweepLeftovers { get; }
    public ConfigEntry<bool> Il2CppGcAfterUnload { get; }

    /// <summary>脚本目录的绝对路径。</summary>
    public string ScriptsDirectory
    {
        get
        {
            var configured = _scriptsDirectory.Value;
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(Paths.BepInExRootPath, "scripts");

            return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(Paths.GameRootPath, configured));
        }
    }

    /// <summary>引擎自己的工作目录（命令、状态文件）。</summary>
    public static string WorkDirectory => Path.Combine(Paths.BepInExRootPath, "ScriptEngine.IL2CPP");
}
