using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using ScriptEngineIl2Cpp.Cleanup;
using ScriptEngineIl2Cpp.Rewriting;

namespace ScriptEngineIl2Cpp;

/// <summary>
/// 引擎的核心：把脚本目录里的 BasePlugin DLL 装进来，卸掉旧的，再装新的。
///
/// 一次重载 = 快照(读字节) → 准备(Cecil 改写 + 发现插件) → <b>以上任何一步失败，旧代次原封不动</b> →
/// 卸旧(Unload / 清残留 / 清缓存 / 放掉加载上下文) → 装新(新的可回收上下文 + 实例化)。
/// 只在 Unity 主线程调用。
/// </summary>
internal sealed class ScriptHost
{
    private const string EngineAssemblyName = "ScriptEngine.IL2CPP";

    private static readonly PropertyInfo PluginInstanceProperty =
        typeof(PluginInfo).GetProperty(nameof(PluginInfo.Instance), BindingFlags.Public | BindingFlags.Instance)!;

    private readonly ManualLogSource _log;
    private readonly EngineSettings _settings;
    private readonly ScriptRewriter _rewriter;
    private readonly List<UnloadWatch> _watches = new();
    private readonly List<(int Generation, string State)> _contextHistory = new();

    private ScriptGeneration? _current;
    private int _historyVersion;
    private int _generationCounter;
    private string? _fingerprint;

    public ScriptHost(ManualLogSource log, EngineSettings settings)
    {
        _log = log;
        _settings = settings;
        _rewriter = new ScriptRewriter(SharedCecilResolver.Instance, IL2CPPChainloader.ToPluginInfo);
    }

    public ReloadReport? LastReport { get; private set; }

    /// <summary>已经分配过的最大代次（失败的准备阶段不占用代次）。</summary>
    public int Generation => _generationCounter;

    public IReadOnlyList<(int Generation, string State)> ContextHistory => _contextHistory;

    /// <summary>上下文回收状态变化时递增，泵据此重写 status.json。</summary>
    public int HistoryVersion => _historyVersion;

    public IReadOnlyList<PluginRecord> LoadedPlugins
        => _current is null
            ? Array.Empty<PluginRecord>()
            : _current.Plugins.Select(p => Describe(p.Info)).ToList();

    // ================================================================== 对外入口

    /// <param name="trigger">startup / hotkey / file / command，仅用于日志和状态。</param>
    /// <param name="force">true = 即使脚本目录内容没变也重载。</param>
    public ReloadReport Reload(string trigger, bool force)
    {
        var report = new ReloadReport(_log, trigger);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            ReloadCore(report, force);
        }
        catch (Exception ex)
        {
            report.Error($"Reload failed unexpectedly: {ex}");
        }

        stopwatch.Stop();
        report.DurationMs = stopwatch.ElapsedMilliseconds;
        LastReport = report;

        if (report.Generation == 0 && !report.Ok)
        {
            _log.LogMessage($"Reload ({trigger}) aborted before anything was changed — the previous scripts stay loaded ({report.Errors.Count} error(s), {report.DurationMs} ms)");
        }
        else if (!report.Deferred && !report.Skipped)
        {
            var status = report.Ok ? "done" : "finished with errors";
            _log.LogMessage($"Reload ({trigger}) {status}: generation {report.Generation}, " +
                            $"{report.Loaded.Count} plugin(s) loaded, {report.Unloaded.Count} unloaded, " +
                            $"{report.Errors.Count} error(s), {report.DurationMs} ms (prepare {report.PrepareMs}, unload {report.UnloadMs}, load {report.LoadMs})");
        }

        return report;
    }

    /// <summary>卸载全部脚本，不再装新的（直到下一次重载）。</summary>
    public ReloadReport UnloadAll(string trigger)
    {
        var report = new ReloadReport(_log, trigger);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (_current is null)
            {
                report.Info("Nothing to unload.");
            }
            else
            {
                UnloadCurrent(report);
                _fingerprint = null;
                _log.LogMessage($"Unload ({trigger}) done: {report.Unloaded.Count} plugin(s) unloaded, {report.Errors.Count} error(s)");
            }
        }
        catch (Exception ex)
        {
            report.Error($"Unload failed unexpectedly: {ex}");
        }

        stopwatch.Stop();
        report.DurationMs = stopwatch.ElapsedMilliseconds;
        LastReport = report;
        return report;
    }

    /// <summary>每帧调用：观察旧上下文是否被回收。</summary>
    public void Tick()
    {
        if (_watches.Count > 0) CheckWatches();
    }

    // ================================================================== 重载流程

    private void ReloadCore(ReloadReport report, bool force)
    {
        var directory = _settings.ScriptsDirectory;
        Directory.CreateDirectory(directory);

        // ---- 1. 快照 ----
        ScriptSnapshot snapshot;
        try
        {
            snapshot = ScriptFileReader.Read(directory, message => _log.LogDebug(message), report.Warn);
        }
        catch (ScriptFileReader.NotReadyException ex)
        {
            report.Deferred = true;
            report.Info($"Scripts not ready yet, will retry shortly: {ex.Message}");
            return;
        }

        if (!force && _current is not null && snapshot.Fingerprint == _fingerprint)
        {
            report.Skipped = true;
            _log.LogInfo("Scripts folder content unchanged — nothing to reload.");
            return;
        }

        // ---- 2. 准备（失败则什么都没动） ----
        var phase = Stopwatch.StartNew();
        var generation = _generationCounter + 1;
        var images = Prepare(snapshot, generation, report);
        report.PrepareMs = phase.ElapsedMilliseconds;
        if (images is null) return;

        // ---- 3. 换代：从这里开始不可回头 ----
        _generationCounter = generation;
        report.Generation = generation;
        ScriptRuntime.Generation = generation;

        phase.Restart();
        UnloadCurrent(report);
        report.UnloadMs = phase.ElapsedMilliseconds;

        _fingerprint = snapshot.Fingerprint;
        phase.Restart();
        LoadGeneration(generation, images, report);
        report.LoadMs = phase.ElapsedMilliseconds;
    }

    private List<ScriptImage>? Prepare(ScriptSnapshot snapshot, int generation, ReloadReport report)
    {
        List<ScriptImage> images;
        try
        {
            images = _rewriter.Rewrite(snapshot.Files, generation, report.Warn);
        }
        catch (Exception ex)
        {
            report.Error("Could not prepare the scripts — nothing was changed, the previous scripts stay loaded.\n" + ex);
            return null;
        }

        // 脚本目录里混进了宿主已有的程序集（BepInEx、Harmony、Il2CppInterop、引擎自己，或同一个插件还留在 plugins/ 里）：
        // 在脚本上下文里再装一份会造成「同名两份类型」，直接跳过，让脚本解析到宿主那份。
        var hostNames = new HashSet<string>(
            AssemblyLoadContext.Default.Assemblies.Select(a => a.GetName().Name ?? ""),
            StringComparer.OrdinalIgnoreCase);

        images.RemoveAll(image =>
        {
            if (!hostNames.Contains(image.AssemblyName)) return false;

            report.Warn(image.AssemblyName.Equals(EngineAssemblyName, StringComparison.OrdinalIgnoreCase)
                ? $"{Path.GetFileName(image.Path)}: that is the script engine itself — remove it from the scripts folder. Skipped."
                : $"{Path.GetFileName(image.Path)}: an assembly named '{image.AssemblyName}' is already loaded by the game/BepInEx " +
                  "(is the same plugin also in BepInEx/plugins?). Skipped — scripts will use the host's copy.");
            return true;
        });

        return images;
    }

    // ------------------------------------------------------------------ 装载

    private void LoadGeneration(int id, IReadOnlyList<ScriptImage> images, ReloadReport report)
    {
        var generation = new ScriptGeneration(id);
        _current = generation; // 先登记：装到一半失败，下次重载也能把已经起来的插件卸掉

        var context = new ScriptLoadContext(id, _settings.CollectibleContexts.Value, images);
        generation.Context = context;
        generation.InjectedTypeCount = images.Sum(i => i.RenamedTypes.Count);
        generation.AssemblyBytes = images.Sum(i => (long)i.Dll.Length);

        try
        {
            generation.Assemblies.AddRange(context.LoadAll());
        }
        catch (Exception ex)
        {
            report.Error($"Could not load the script assemblies into the load context: {ex}");
            return;
        }

        var candidates = new List<(PluginInfo Info, Assembly Assembly, ScriptImage Image)>();
        foreach (var image in images)
        {
            if (image.Plugins.Count == 0)
            {
                _log.LogDebug($"{Path.GetFileName(image.Path)}: no BasePlugin found (treated as a library)");
                continue;
            }

            var assembly = generation.Assemblies.First(a => string.Equals(a.GetName().Name, image.AssemblyName, StringComparison.OrdinalIgnoreCase));
            candidates.AddRange(image.Plugins.Select(info => (info, assembly, image)));
        }

        foreach (var (info, assembly, image) in OrderByDependencies(candidates, report))
            LoadPlugin(generation, info, assembly, image, report);

        if (candidates.Count == 0)
            report.Info(images.Count == 0
                ? $"No script DLLs found in {_settings.ScriptsDirectory}"
                : "No BasePlugin classes found in the script DLLs.");
    }

    private static List<(PluginInfo Info, Assembly Assembly, ScriptImage Image)> OrderByDependencies(
        List<(PluginInfo Info, Assembly Assembly, ScriptImage Image)> candidates, ReloadReport report)
    {
        // 同 GUID 只留版本最高的一份
        var byGuid = new Dictionary<string, (PluginInfo Info, Assembly Assembly, ScriptImage Image)>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Info.Metadata.Version))
        {
            if (byGuid.ContainsKey(candidate.Info.Metadata.GUID))
                report.Warn($"Skipping [{candidate.Info}] ({Path.GetFileName(candidate.Image.Path)}): another script already provides {candidate.Info.Metadata.GUID}");
            else
                byGuid[candidate.Info.Metadata.GUID] = candidate;
        }

        IEnumerable<string> guids = byGuid.Keys;
        try
        {
            guids = Utility.TopologicalSort(
                byGuid.Keys.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
                guid => byGuid[guid].Info.Dependencies.Select(d => d.DependencyGUID).Where(byGuid.ContainsKey)).ToList();
        }
        catch (Exception ex)
        {
            report.Error($"Plugin dependency order could not be resolved, loading in arbitrary order: {ex.Message}");
        }

        return guids.Select(g => byGuid[g]).ToList();
    }

    private void LoadPlugin(ScriptGeneration generation, PluginInfo info, Assembly assembly, ScriptImage image, ReloadReport report)
    {
        var chainloader = IL2CPPChainloader.Instance;
        var guid = info.Metadata.GUID;
        var processes = info.Processes.ToList();

        if (processes.Count > 0 && processes.All(p => !string.Equals(p.ProcessName.Replace(".exe", ""), Paths.ProcessName, StringComparison.InvariantCultureIgnoreCase)))
        {
            _log.LogInfo($"Skipping [{info}] because of its process filter");
            return;
        }

        if (chainloader.Plugins.TryGetValue(guid, out var existing))
        {
            report.Error($"Cannot load [{info}]: a plugin with the same GUID ({existing}) is already loaded by BepInEx (is it also in BepInEx/plugins?)");
            return;
        }

        foreach (var dependency in info.Dependencies)
        {
            if ((dependency.Flags & BepInDependency.DependencyFlags.HardDependency) == 0) continue;

            if (!chainloader.Plugins.TryGetValue(dependency.DependencyGUID, out var dependencyInfo))
            {
                report.Error($"Cannot load [{info}]: missing dependency {dependency.DependencyGUID}");
                return;
            }

            if (dependency.VersionRange is not null && !dependency.VersionRange.IsSatisfied(dependencyInfo.Metadata.Version))
            {
                report.Error($"Cannot load [{info}]: dependency {dependency.DependencyGUID} is version {dependencyInfo.Metadata.Version}, needs {dependency.VersionRange}");
                return;
            }
        }

        BasePlugin? created = null;
        void Capture(PluginInfo _, Assembly __, BasePlugin plugin) => created = plugin;

        _log.LogInfo($"Loading script [{info}]");
        chainloader.PluginLoad += Capture;
        try
        {
            chainloader.Plugins[guid] = info;
            RunModuleConstructor(info, assembly);

            var instance = chainloader.LoadPlugin(info, assembly);
            PluginInstanceProperty.GetSetMethod(nonPublic: true)!.Invoke(info, new object[] { instance });

            generation.Plugins.Add(new LoadedPlugin(info, instance, image.AssemblyName));
            report.Loaded.Add(Describe(info));
        }
        catch (Exception ex)
        {
            chainloader.Plugins.Remove(guid);
            report.Error($"Error loading [{info}]: {Unwrap(ex)}");

            if (created is not null)
            {
                // Load() 抛出时实例已经创建、可能已经注册了一半东西：尽力让它自己收拾
                try { created.Unload(); } catch { /* 已经出错了，别再添乱 */ }
                RemoveLogSource(created.Log);
            }
        }
        finally
        {
            chainloader.PluginLoad -= Capture;
        }
    }

    private void RunModuleConstructor(PluginInfo info, Assembly assembly)
    {
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.GetType(info.TypeName)!.Module.ModuleHandle);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Couldn't run module constructor for {assembly.FullName}::{info.TypeName}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 卸载

    private void UnloadCurrent(ReloadReport report)
    {
        var generation = _current;
        if (generation is null) return;
        _current = null;

        var id = generation.Id;
        var watch = UnloadGeneration(generation, report);
        generation = null;

        _watches.Add(watch);

        // 放在单独的方法里：保证上面所有指向旧代次的局部变量都已经出了栈帧，GC 才收得走
        CollectAndCheck(watch, report, includeIl2Cpp: true);
        if (watch.Collected)
            RecordCollected(watch, report);
        else if (!watch.ExpectPinned)
            report.Info($"Previous generation {id} is unloaded but its load context is not collected yet — will keep checking in the background.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private UnloadWatch UnloadGeneration(ScriptGeneration generation, ReloadReport report)
    {
        // 1. 插件自己的 Unload()（后装先卸）
        var logSources = new List<ManualLogSource>();
        var allUnloadsClean = true;
        for (var i = generation.Plugins.Count - 1; i >= 0; i--)
        {
            var loaded = generation.Plugins[i];
            var record = Describe(loaded.Info);
            report.Unloaded.Add(record);

            try
            {
                record.UnloadReturned = loaded.Instance.Unload();
                if (record.UnloadReturned != true)
                {
                    allUnloadsClean = false;
                    report.Warn($"[{loaded.Info}] Unload() returned false: the plugin does not clean up after itself, " +
                                "so patches / event handlers / GameObjects it created may still be alive after the reload.");
                }
            }
            catch (Exception ex)
            {
                allUnloadsClean = false;
                record.UnloadReturned = false;
                report.Error($"[{loaded.Info}] Unload() threw: {Unwrap(ex)}");
            }

            try { loaded.Instance.Config.SaveOnConfigSet = false; } // 旧实例不许再回写 .cfg
            catch { /* ignore */ }

            logSources.Add(loaded.Instance.Log);
            if (IL2CPPChainloader.Instance.Plugins.TryGetValue(loaded.Info.Metadata.GUID, out var registered)
                && ReferenceEquals(registered, loaded.Info))
            {
                IL2CPPChainloader.Instance.Plugins.Remove(loaded.Info.Metadata.GUID);
            }

            // PluginInfo 还被别处引用着（外部插件、我们自己的报告），不清掉 Instance 会钉住整个旧上下文
            try { PluginInstanceProperty.GetSetMethod(nonPublic: true)!.Invoke(loaded.Info, new object?[] { null }); }
            catch { /* ignore */ }
        }

        // 2. 清掉插件没销毁的注入对象
        if (_settings.SweepLeftovers.Value)
        {
            try
            {
                var sweepWatch = Stopwatch.StartNew();
                var details = new List<string>();
                report.SweptObjects += LeftoverSweeper.Sweep(generation.Assemblies, details);
                if (details.Count > 0 || sweepWatch.ElapsedMilliseconds > 250)
                    report.Info($"Leftover sweep took {sweepWatch.ElapsedMilliseconds} ms");

                foreach (var detail in details)
                {
                    // Unload() 都返回 true 时，剩下的多半是插件刚 Destroy() 过、要等到帧尾才真正销毁的对象：只提示，不告警
                    if (allUnloadsClean)
                        report.Info($"Swept Unity object(s) of the old generation that were still pending destruction: {detail}");
                    else
                        report.Warn($"Swept leftover Unity object(s) from the old generation: {detail} (the plugin's Unload() should destroy these itself)");
                }
            }
            catch (Exception ex)
            {
                report.Warn($"Sweeping leftover Unity objects failed: {ex.Message}");
            }
        }

        // 3. BepInEx / Il2CppInterop 的静态缓存里别留着旧类型
        try
        {
            var purged = StaticCachePurger.Purge(generation.Assemblies);
            if (purged.Count > 0)
                _log.LogDebug("Purged static caches: " + string.Join(", ", purged.Select(p => $"{p.Key} x{p.Value}")));
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Static cache purge failed: {ex.Message}");
        }

        // 4. 断开引用并卸载上下文
        var context = generation.Context;
        var id = generation.Id;
        var injectedTypes = generation.InjectedTypeCount;
        var assemblyBytes = generation.AssemblyBytes;
        var weak = generation.Release();

        try
        {
            if (context is { IsCollectible: true })
                context.Unload();
            else if (context is not null)
                report.Warn($"Generation {id} used a non-collectible load context — its assemblies stay in memory.");
        }
        catch (Exception ex)
        {
            report.Warn($"AssemblyLoadContext.Unload() failed: {ex.Message}");
        }

        // 旧日志源先不摘：被 Destroy 的组件的 OnDestroy、甚至没清干净的旧代次代码还会继续打日志，
        // 摘掉就等于把「旧代码还活着」这个最有用的线索吞了。等上下文真被回收了再摘。
        return new UnloadWatch(id, weak, logSources) { ExpectPinned = injectedTypes > 0, RetainedBytes = assemblyBytes };
    }

    private static void RemoveLogSource(ManualLogSource source)
    {
        try { Logger.Sources.Remove(source); }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------------ 回收观察

    private void CheckWatches()
    {
        var now = Environment.TickCount64;

        for (var i = _watches.Count - 1; i >= 0; i--)
        {
            var watch = _watches[i];

            if (watch.Collected)
            {
                _watches.RemoveAt(i);
                RecordCollected(watch, null);
                continue;
            }

            if (now < watch.NextCheckTicks) continue;

            watch.Checks++;
            watch.NextCheckTicks = now + 2000;
            CollectAndCheck(watch, null, includeIl2Cpp: watch.Checks <= 2);

            if (watch.Collected)
            {
                _watches.RemoveAt(i);
                RecordCollected(watch, null);
            }
            else if (watch.Checks >= (watch.ExpectPinned ? 2 : 8))
            {
                _watches.RemoveAt(i);
                _historyVersion++;
                if (watch.ExpectPinned)
                {
                    // 预期内的「留在内存里」：旧代码已被中和，这时再摘日志源，免得每次重载都白白留一批在 Logger.Sources 里。
                    // （意外 pinned 的代次反而保留日志源——它的残余代码还在跑的话，日志是唯一线索。）
                    watch.LogSources.ForEach(RemoveLogSource);
                    _contextHistory.Add((watch.Generation, "retained"));
                    _log.LogInfo($"Generation {watch.Generation}: its assemblies stay in memory (~{watch.RetainedBytes / 1024} KB) — it injected Unity types, " +
                                 "and Il2CppInterop never releases those. Harmless: the old code is neutralised, only the bytes remain.");
                }
                else
                {
                    _contextHistory.Add((watch.Generation, "pinned"));
                    _log.LogWarning($"Generation {watch.Generation}: the old load context is still referenced after {watch.Checks} GC passes — " +
                                    "something (a Harmony patch, an event handler, a static cache, a live Unity object) still points at the old code. " +
                                    "Its assemblies stay in memory until the game exits. Make sure the plugin's Unload() undoes everything it set up.");
                }
            }
        }
    }

    private void RecordCollected(UnloadWatch watch, ReloadReport? report)
    {
        var elapsed = Environment.TickCount64 - watch.CreatedTicks;
        _watches.Remove(watch);
        _contextHistory.Add((watch.Generation, "collected"));
        _historyVersion++;
        watch.LogSources.ForEach(RemoveLogSource);

        var message = $"Generation {watch.Generation}: old load context collected ({elapsed} ms after unload) — the old code is gone from memory.";
        if (report is not null) report.Info(message);
        else _log.LogInfo(message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectAndCheck(UnloadWatch watch, ReloadReport? report, bool includeIl2Cpp)
    {
        if (includeIl2Cpp && _settings.Il2CppGcAfterUnload.Value)
        {
            try { Il2CppSystem.GC.Collect(); }
            catch (Exception ex) { _log.LogDebug($"Il2Cpp GC failed: {ex.Message}"); }
        }

        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
    }

    // ================================================================== 小工具

    private static PluginRecord Describe(PluginInfo info) => new()
    {
        Guid = info.Metadata.GUID,
        Name = info.Metadata.Name,
        Version = info.Metadata.Version.ToString(),
        TypeName = info.TypeName,
        File = info.Location,
    };

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex;
    }
}
