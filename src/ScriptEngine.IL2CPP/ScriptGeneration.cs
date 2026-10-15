using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ScriptEngineIl2Cpp;

internal sealed class LoadedPlugin
{
    public LoadedPlugin(PluginInfo info, BasePlugin instance, string assemblyName)
    {
        Info = info;
        Instance = instance;
        AssemblyName = assemblyName;
    }

    public PluginInfo Info { get; }
    public BasePlugin Instance { get; }
    public string AssemblyName { get; }
}

/// <summary>
/// 一次重载装进来的那一批东西：加载上下文、程序集、插件实例。
/// 卸载时先 <see cref="Release"/> 把所有强引用断开，之后只留一个 <see cref="WeakReference"/> 观察上下文是否被回收。
/// </summary>
internal sealed class ScriptGeneration
{
    public ScriptGeneration(int id) => Id = id;

    public int Id { get; }
    public ScriptLoadContext? Context { get; set; }

    /// <summary>本代程序集字节数合计，用来估算钉住时泄漏的量级。</summary>
    public long AssemblyBytes { get; set; }
    public List<Assembly> Assemblies { get; } = new();
    public List<LoadedPlugin> Plugins { get; } = new();

    /// <summary>
    /// <c>Load()</c> 抛异常的插件的日志源。它没有进 <see cref="Plugins"/>，但可能已经挂上了在跑的组件，
    /// 日志源要留到本代次卸载时才和别的一起摘。
    /// </summary>
    public List<BepInEx.Logging.ManualLogSource> ExtraLogSources { get; } = new();

    /// <summary>断开对加载上下文、程序集和插件实例的全部强引用。返回上下文的弱引用。</summary>
    public WeakReference Release()
    {
        var weak = new WeakReference(Context, trackResurrection: false);
        Plugins.Clear();
        Assemblies.Clear();
        ExtraLogSources.Clear();
        Context = null;
        return weak;
    }
}

/// <summary>被卸载的上下文的回收观察记录。</summary>
internal sealed class UnloadWatch
{
    public UnloadWatch(int generation, WeakReference context, List<BepInEx.Logging.ManualLogSource> logSources)
    {
        Generation = generation;
        Context = context;
        LogSources = logSources;
        CreatedTicks = Environment.TickCount64;
        NextCheckTicks = CreatedTicks + 2000;
    }

    public int Generation { get; }
    public WeakReference Context { get; }

    /// <summary>旧代次插件的日志源，上下文被回收后才从 BepInEx 的日志源列表里摘掉。</summary>
    public List<BepInEx.Logging.ManualLogSource> LogSources { get; }
    public long CreatedTicks { get; }
    public long NextCheckTicks { get; set; }
    public int Checks { get; set; }
    public bool Collected => !Context.IsAlive;

    /// <summary>这一代注入过类型：Il2CppInterop 对注入类型的原生 trampoline 持有永久 GCHandle，上下文预期回收不了。</summary>
    public bool ExpectPinned { get; init; }

    public long RetainedBytes { get; init; }

    /// <summary>
    /// 旧代次的程序集（弱引用）。回收不了的时候，<see cref="Cleanup.PinFinder"/> 要靠它们才知道在找谁；
    /// 能回收的话它们自己就消失了。
    /// </summary>
    public List<WeakReference<System.Reflection.Assembly>> Assemblies { get; init; } = new();
}
