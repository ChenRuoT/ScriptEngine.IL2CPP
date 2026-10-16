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
using UnityEngine;

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

    /// <summary>插件的 Load() 超过这么久就在报告里点名。</summary>
    private const long SlowLoadMilliseconds = 250;

    /// <summary>没有注入类型的代次，卸载后最多再做这么多轮后台 GC 检查（每轮间隔 2 秒）。</summary>
    private const int MaxWatchChecks = 8;

    /// <summary>「会留在内存里」的代次，其旧日志源在卸载后多久才摘（等被 Destroy 的组件跑完 OnDestroy）。</summary>
    private const long SourceGraceMilliseconds = 3000;

    /// <summary>卸载的第二步（清缓存、放掉加载上下文）比第一步（Unload() 等）晚多久。要跨过至少一个帧尾，见 <see cref="UnloadCurrent"/>。</summary>
    private const long RetireDelayMilliseconds = 500;

    private static readonly PropertyInfo PluginInstanceProperty =
        typeof(PluginInfo).GetProperty(nameof(PluginInfo.Instance), BindingFlags.Public | BindingFlags.Instance)!;

    private readonly ManualLogSource _log;
    private readonly EngineSettings _settings;
    private readonly ScriptRewriter _rewriter;
    private readonly List<UnloadWatch> _watches = new();
    private readonly List<(long DueTicks, List<ManualLogSource> Sources)> _sourceRemovals = new();
    private readonly List<RetiringGeneration> _retiring = new();
    private readonly HashSet<string> _staleReported = new();
    private bool _pinDiagnosed;
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

    /// <summary>
    /// BepInEx 的管理对象（所有插件的 <c>AddComponent&lt;T&gt;()</c> 都挂在它上面，引擎自己的组件也在上面）。
    /// 插件 <c>Load()</c> 抛异常时，用它前后的组件快照找出该插件半路挂上去的组件。
    /// </summary>
    public GameObject? ManagerObject { get; set; }

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

        // 「内容没变所以跳过」不是一次真正的重载：别拿它覆盖掉上一次真正重载的报告（status.json 的 lastReload）
        if (!report.Skipped || LastReport is null) LastReport = report;

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
        if (_retiring.Count > 0) FinishRetirements();
        if (_watches.Count > 0) CheckWatches();
        if (_sourceRemovals.Count > 0) RunSourceRemovals();
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
        // 脚本目录里混进了宿主已有的程序集（BepInEx、Harmony、Il2CppInterop、引擎自己，或同一个插件还留在 plugins/ 里）：
        // 在脚本上下文里再装一份会造成「同名两份类型」，所以由改写器在分析之前就挡掉，让脚本解析到宿主那份。
        var hostNames = new HashSet<string>(
            AssemblyLoadContext.Default.Assemblies.Select(a => a.GetName().Name ?? ""),
            StringComparer.OrdinalIgnoreCase);

        string? RejectHostAssembly(string assemblyName)
        {
            if (!hostNames.Contains(assemblyName)) return null;

            return assemblyName.Equals(EngineAssemblyName, StringComparison.OrdinalIgnoreCase)
                ? "that is the script engine itself — remove it from the scripts folder. Skipped."
                : $"an assembly named '{assemblyName}' is already loaded by the game/BepInEx " +
                  "(is the same plugin also in BepInEx/plugins?). Skipped — scripts will use the host's copy.";
        }

        try
        {
            return _rewriter.Rewrite(snapshot.Files, generation, report.Warn, RejectHostAssembly);
        }
        catch (ScriptFileReader.NotReadyException ex)
        {
            // 符号文件还没写完之类：和「文件读不了」一样延后重试，旧代次不动
            report.Deferred = true;
            report.Info($"Scripts not ready yet, will retry shortly: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            report.Error("Could not prepare the scripts — nothing was changed, the previous scripts stay loaded.\n" + ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ 装载

    private void LoadGeneration(int id, IReadOnlyList<ScriptImage> images, ReloadReport report)
    {
        var generation = new ScriptGeneration(id);
        _current = generation; // 先登记：装到一半失败，下次重载也能把已经起来的插件卸掉

        var context = new ScriptLoadContext(id, _settings.CollectibleContexts.Value, images);
        generation.Context = context;
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

        foreach (var (info, assembly, image) in OrderByDependencies(candidates, images, report))
            LoadPlugin(generation, info, assembly, image, report);

        if (candidates.Count == 0)
            report.Info(images.Count == 0
                ? $"No script DLLs found in {_settings.ScriptsDirectory}"
                : "No BasePlugin classes found in the script DLLs.");
    }

    internal static List<(PluginInfo Info, Assembly Assembly, ScriptImage Image)> OrderByDependencies(
        List<(PluginInfo Info, Assembly Assembly, ScriptImage Image)> candidates, IReadOnlyList<ScriptImage> images, ReloadReport report)
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

        var sorted = new List<string>(byGuid.Keys.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));

        // 依赖边 = [BepInDependency] + 程序集引用（副模组没写 BepInDependency、只是引用了主模组的程序集，也要让主模组先装）。
        // 程序集引用可能成环（A、B 互相引用），成环就退回只按 [BepInDependency]：那是插件作者明确声明的，环了就是真的有问题。
        var assemblyEdges = AssemblyDependencies(byGuid, images);
        IEnumerable<string> Edges(string guid, bool withAssemblies)
        {
            var edges = byGuid[guid].Info.Dependencies.Select(d => d.DependencyGUID).Where(byGuid.ContainsKey);
            return withAssemblies ? edges.Concat(assemblyEdges[guid]) : edges;
        }

        try
        {
            return Utility.TopologicalSort(sorted, g => Edges(g, withAssemblies: true)).Select(g => byGuid[g]).ToList();
        }
        catch (Exception)
        {
            // 退回只按 [BepInDependency]
        }

        try
        {
            return Utility.TopologicalSort(sorted, g => Edges(g, withAssemblies: false)).Select(g => byGuid[g]).ToList();
        }
        catch (Exception ex)
        {
            report.Error($"Plugin dependency order could not be resolved, loading in arbitrary order: {ex.Message}");
            return sorted.Select(g => byGuid[g]).ToList();
        }
    }

    /// <summary>每个插件所依赖的、其它脚本程序集里的插件 GUID（沿着程序集引用传递：副模组 → 库 → 主模组也算）。</summary>
    private static Dictionary<string, List<string>> AssemblyDependencies(
        Dictionary<string, (PluginInfo Info, Assembly Assembly, ScriptImage Image)> byGuid, IReadOnlyList<ScriptImage> images)
    {
        var referencesByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images) referencesByName[image.AssemblyName] = image.ReferencedAssemblies;

        var result = new Dictionary<string, List<string>>();
        foreach (var (guid, candidate) in byGuid)
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(candidate.Image.ReferencedAssemblies);
            while (pending.Count > 0)
            {
                var name = pending.Pop();
                if (!referencesByName.TryGetValue(name, out var next) || !reachable.Add(name)) continue; // 只走本批脚本
                foreach (var reference in next) pending.Push(reference);
            }

            result[guid] = byGuid
                .Where(other => other.Key != guid
                                && !string.Equals(other.Value.Image.AssemblyName, candidate.Image.AssemblyName, StringComparison.OrdinalIgnoreCase)
                                && reachable.Contains(other.Value.Image.AssemblyName))
                .Select(other => other.Key)
                .ToList();
        }

        return result;
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

        // [BepInIncompatibility]：和 BepInEx 自己一样，我声明了不兼容的插件已经在 → 不装；
        // 反过来，已经装好的插件声明了和我不兼容 → 也不装（脚本是后来者，让路的应该是它）
        foreach (var incompatibility in info.Incompatibilities)
        {
            if (!chainloader.Plugins.ContainsKey(incompatibility.IncompatibilityGUID)) continue;

            report.Error($"Cannot load [{info}]: it is incompatible with {incompatibility.IncompatibilityGUID}, which is already loaded");
            return;
        }

        foreach (var other in chainloader.Plugins.Values)
        {
            if (!other.Incompatibilities.Any(i => string.Equals(i.IncompatibilityGUID, guid, StringComparison.Ordinal))) continue;

            report.Error($"Cannot load [{info}]: the already loaded plugin [{other}] declares itself incompatible with {guid}");
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

        // Load() 抛异常时，用前后的组件快照找出它半路挂上去的注入组件（只记原生指针，开销可以忽略）
        HashSet<IntPtr>? componentsBefore = null;
        if (_settings.SweepLeftovers.Value)
        {
            try { componentsBefore = LeftoverSweeper.SnapshotComponents(ManagerObject); }
            catch (Exception snapshotError) { _log.LogDebug($"Component snapshot failed: {snapshotError.Message}"); }
        }

        _log.LogInfo($"Loading script [{info}]");
        chainloader.PluginLoad += Capture;
        try
        {
            chainloader.Plugins[guid] = info;
            RunModuleConstructor(info, assembly);

            var loadWatch = Stopwatch.StartNew();
            var instance = chainloader.LoadPlugin(info, assembly);
            loadWatch.Stop();
            PluginInstanceProperty.GetSetMethod(nonPublic: true)!.Invoke(info, new object[] { instance });

            generation.Plugins.Add(new LoadedPlugin(info, instance, image.AssemblyName));
            var record = Describe(info);
            record.LoadMs = loadWatch.ElapsedMilliseconds;
            report.Loaded.Add(record);

            // 重载的大头通常是插件自己的 Load()（注入一个大的 MonoBehaviour 类就要上千毫秒）：慢的点名，免得误以为是引擎慢
            if (record.LoadMs >= SlowLoadMilliseconds)
                report.Info($"[{info}] Load() took {record.LoadMs} ms (most of a reload is usually the plugin's own Load(), e.g. Il2CppInterop injecting a big MonoBehaviour)");
        }
        catch (Exception ex)
        {
            chainloader.Plugins.Remove(guid);
            report.Error($"Error loading [{info}]: {Unwrap(ex)}");

            if (created is not null)
            {
                // Load() 抛出时实例已经创建、可能已经注册了一半东西：尽力让它自己收拾
                try { created.Unload(); } catch { /* 已经出错了，别再添乱 */ }

                try { created.Config.SaveOnConfigSet = false; } // 半途夭折的实例不许再回写 .cfg
                catch { /* ignore */ }

                // 日志源不立刻摘：它半路挂上的组件（下面会清，但清不掉的、或之后的 OnDestroy）还要打日志。随本代次卸载时统一摘。
                generation.ExtraLogSources.Add(created.Log);
            }

            SweepAfterFailedLoad(generation, info, componentsBefore, report);
        }
        finally
        {
            chainloader.PluginLoad -= Capture;
        }
    }

    /// <summary>
    /// 插件 <c>Load()</c> 抛异常后，把它崩溃前挂到 BepInEx 管理对象上的注入组件销毁掉，免得半成品插件的 Update() 在本代次里一直跑。
    /// 只清「Load() 之前没有、现在有」的注入组件，同一个程序集里别的、已经成功加载的插件的组件不会被误杀。
    /// </summary>
    private void SweepAfterFailedLoad(ScriptGeneration generation, PluginInfo info, HashSet<IntPtr>? componentsBefore, ReloadReport report)
    {
        if (componentsBefore is null) return;

        try
        {
            var details = new List<string>();
            report.SweptObjects += LeftoverSweeper.SweepNewComponents(ManagerObject, componentsBefore, generation.Assemblies, details);
            foreach (var detail in details)
                report.Warn($"Destroyed component(s) that [{info}] had attached before its Load() failed: {detail}");
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Sweeping after a failed Load() failed: {ex.Message}");
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
    //
    // 分两步：
    //   第一步（立刻）：插件自己的 Unload()、清掉没销毁的注入对象、摘掉它在 BepInEx 里的登记。新代次紧接着就能装。
    //   第二步（延后 RetireDelayMilliseconds，由 Tick() 驱动）：抹静态缓存、把旧代次交给游戏的事件处理器换成空操作、放掉加载上下文、看它能不能被回收。
    //   第二步要晚几帧：插件在 Unload() 里 Destroy() 掉的组件要到帧尾才真正销毁，它们的 OnDestroy 里常常要 -= 退订事件，
    //   而退订得在 Il2CppInterop 的转换缓存里找到当初订阅用的<b>同一个</b> IL2CPP 委托 —— 缓存提前清掉，-= 就会造出一个新委托，删不掉旧的。

    private void UnloadCurrent(ReloadReport report)
    {
        var generation = _current;
        if (generation is null) return;
        _current = null;

        var logSources = RetirePlugins(generation, report);
        _retiring.Add(new RetiringGeneration(generation, logSources, Environment.TickCount64 + RetireDelayMilliseconds));
    }

    /// <summary>第一步。返回旧代次所有插件的日志源（留到第二步之后再摘）。</summary>
    private List<ManualLogSource> RetirePlugins(ScriptGeneration generation, ReloadReport report)
    {
        // 1. 插件自己的 Unload()（后装先卸）
        var logSources = new List<ManualLogSource>(generation.ExtraLogSources); // 含 Load() 失败的插件留下的
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

        generation.Plugins.Clear(); // 插件实例到此为止，别再被这一代的记录引用

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

        // 3. 旧代次交给游戏的事件处理器 / 回调，马上换成空操作：忘了退订的处理器不能在新代次已经跑起来之后继续执行旧代码。
        //    这里只收集、不清缓存 —— 缓存要等到第二步，让被 Destroy 的组件的 OnDestroy 里的 -= 还能找到同一个 IL2CPP 委托。
        try
        {
            var dead = new HashSet<Assembly>(generation.Assemblies);
            var scan = StaticCachePurger.Purge(generation.Assemblies, StaticCachePurger.PurgeMode.ScanDelegates);
            if (scan.DeadDelegateWrappers.Count > 0)
            {
                var neutralized = StaleDelegateNeutralizer.Neutralize(scan.DeadDelegateWrappers, dead, OnStaleHandlerInvoked);
                if (neutralized.Neutralized > 0)
                    report.Info($"Neutralised {neutralized.Neutralized} event handler(s)/callback(s) the old generation handed to the game " +
                                "(if one fires after all, it is ignored and reported once)");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Neutralising stale handlers failed: {ex.Message}");
        }

        return logSources;
    }

    private void FinishRetirements()
    {
        var now = Environment.TickCount64;
        while (true)
        {
            // 取出并放掉旧代次要放在单独的方法里：保证所有指向旧代次的局部变量都已经出了栈帧，下面的 GC 才收得走
            var watch = ReleaseNextDue(now);
            if (watch is null) return;

            if (watch.ExpectPinned)
            {
                // 真正注入过 Unity 类型的代次：Il2CppInterop 对注入类型的原生 trampoline 持有永久 GCHandle，上下文注定回收不了 → 当场定论。
                // 连 GC 都不用跑：全量 IL2CPP + 托管 GC 在 EFT 主菜单里要 ~250 ms，白白卡一下；也不放进 _watches 去后台反复跑。
                RecordRetained(watch);
                continue;
            }

            CollectAndCheck(watch, includeIl2Cpp: true);

            if (watch.Collected)
            {
                RecordCollected(watch);
            }
            else
            {
                _watches.Add(watch);
                _log.LogInfo($"Previous generation {watch.Generation} is unloaded but its load context is not collected yet — will keep checking in the background.");
            }
        }
    }

    /// <summary>第二步：取出最早到期的旧代次，清缓存、把它交给游戏的处理器换成空操作、放掉并卸载加载上下文。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private UnloadWatch? ReleaseNextDue(long now)
    {
        var index = _retiring.FindIndex(r => r.DueTicks <= now);
        if (index < 0) return null;

        var retiring = _retiring[index];
        _retiring.RemoveAt(index);

        var generation = retiring.TakeGeneration();
        var id = generation.Id;

        // 3. BepInEx / Il2CppInterop 的静态缓存里别留着旧类型，旧代次交给游戏的事件处理器换成空操作
        var dead = new HashSet<Assembly>(generation.Assemblies);
        try
        {
            var purged = StaticCachePurger.Purge(generation.Assemblies);
            if (purged.Removed.Count > 0)
                _log.LogInfo($"Generation {id}: purged static-cache entries that pointed at the old code: " + string.Join(", ", purged.Removed.Select(p => $"{p.Key} x{p.Value}")));
            if (purged.ElapsedMs > 100)
                _log.LogInfo($"Generation {id}: static cache purge scanned {purged.Scanned} entries in {purged.ElapsedMs} ms");

            if (purged.DeadDelegateWrappers.Count > 0)
            {
                // 第一步已经处理过的会被跳过；这里补的是第一步之后（比如被延后的 OnDestroy 里）才新造出来的转换
                var neutralized = StaleDelegateNeutralizer.Neutralize(purged.DeadDelegateWrappers, dead, OnStaleHandlerInvoked);
                if (neutralized.Neutralized > 0)
                    _log.LogInfo($"Generation {id}: neutralised {neutralized.Neutralized} more handler(s)/callback(s) created after Unload()");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Static cache purge failed: {ex.Message}");
        }

        // 4. 断开引用并卸载上下文
        var context = generation.Context;
        var injectedTypes = LeftoverSweeper.CountInjectedTypes(generation.Assemblies); // 实际注入过的（不是改写器认定的「候选」）
        var assemblyBytes = generation.AssemblyBytes;
        var weakAssemblies = generation.Assemblies.Select(a => new WeakReference<Assembly>(a)).ToList();
        var weak = generation.Release();

        try
        {
            if (context is { IsCollectible: true })
                context.Unload();
            else if (context is not null)
                _log.LogWarning($"Generation {id} used a non-collectible load context — its assemblies stay in memory.");
        }
        catch (Exception ex)
        {
            _log.LogWarning($"AssemblyLoadContext.Unload() failed: {ex.Message}");
        }

        // 旧日志源先不摘：没清干净的旧代次代码还会继续打日志，摘掉就等于把「旧代码还活着」这个最有用的线索吞了。等上下文真被回收了再摘。
        return new UnloadWatch(id, weak, retiring.LogSources) { ExpectPinned = injectedTypes > 0, RetainedBytes = assemblyBytes, Assemblies = weakAssemblies };
    }

    /// <summary>被换成空操作的旧代次处理器被触发了：说明插件没有在 Unload() 里退订它（每个处理器只报一次）。</summary>
    private void OnStaleHandlerInvoked(string description)
    {
        if (!_staleReported.Add(description)) return;

        _log.LogWarning($"A handler from an unloaded script generation was invoked and ignored: {description}. " +
                        "The plugin did not unsubscribe it in Unload() — make sure every event it subscribed to is unsubscribed there.");
    }

    private static void RemoveLogSource(ManualLogSource source)
    {
        try { BepInEx.Logging.Logger.Sources.Remove(source); }
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
                RecordCollected(watch);
                continue;
            }

            if (now < watch.NextCheckTicks) continue;

            watch.Checks++;
            watch.NextCheckTicks = now + 2000;
            CollectAndCheck(watch, includeIl2Cpp: watch.Checks <= 4);

            if (watch.Collected)
            {
                _watches.RemoveAt(i);
                RecordCollected(watch);
            }
            else if (watch.Checks >= MaxWatchChecks)
            {
                // 没有注入类型却回收不了：有东西（Harmony 补丁、事件订阅、静态缓存、活着的 Unity 对象……）还指着旧代码。
                // 日志源故意留着 —— 残余的旧代码要是还在跑，日志是唯一线索。
                _watches.RemoveAt(i);
                _historyVersion++;
                _contextHistory.Add((watch.Generation, "pinned"));
                _log.LogWarning($"Generation {watch.Generation}: the old load context is still referenced after {watch.Checks} GC passes — " +
                                "something (a Harmony patch on a method of the script itself, a Unity object, a static cache, a thread, a native callback…) still points at the old code. " +
                                "Its assemblies stay in memory until the game exits. Make sure the plugin's Unload() undoes everything it set up.");
                DiagnosePinnedGeneration(watch);
            }
        }
    }

    /// <summary>回收不了的代次：找出静态状态里是谁还引用着旧代码，打出引用链。每个游戏会话只做一次（它要遍历一大片对象图，可能卡一秒左右）。</summary>
    private void DiagnosePinnedGeneration(UnloadWatch watch)
    {
        if (!_settings.DiagnosePinned.Value || _pinDiagnosed) return;
        _pinDiagnosed = true;

        try
        {
            var assemblies = new List<Assembly>();
            foreach (var weak in watch.Assemblies)
                if (weak.TryGetTarget(out var assembly)) assemblies.Add(assembly);
            if (assemblies.Count == 0) return;

            var result = PinFinder.Find(assemblies, maxPaths: 6, maxObjects: 300_000, timeBudgetMs: 1500);
            if (result.Paths.Count == 0)
            {
                _log.LogInfo($"Generation {watch.Generation}: no static reference to the old code was found ({result.VisitedObjects} objects searched in {result.ElapsedMs} ms" +
                             $"{(result.Truncated ? ", search truncated" : "")}) — a GCHandle (native callback, Il2CppInterop trampoline, running thread) probably holds it.");
                return;
            }

            _log.LogWarning($"Generation {watch.Generation}: what still references the old code ({result.VisitedObjects} objects searched in {result.ElapsedMs} ms):\n  " +
                            string.Join("\n  ", result.Paths));
        }
        catch (Exception ex)
        {
            _log.LogDebug($"Pin diagnosis failed: {ex.Message}");
        }
    }

    private void RecordCollected(UnloadWatch watch)
    {
        var elapsed = Environment.TickCount64 - watch.CreatedTicks;
        _watches.Remove(watch);
        _contextHistory.Add((watch.Generation, "collected"));
        _historyVersion++;
        watch.LogSources.ForEach(RemoveLogSource);

        _log.LogInfo($"Generation {watch.Generation}: old load context collected ({elapsed} ms after unload) — the old code is gone from memory.");
    }

    private void RecordRetained(UnloadWatch watch)
    {
        _contextHistory.Add((watch.Generation, "retained"));
        _historyVersion++;

        // 旧日志源晚几秒再摘：被 Destroy 的组件的 OnDestroy 要等到帧尾才跑，它们的日志还得打得出来。
        // 不摘的话每次重载都会在 Logger.Sources 里白白留下一批。
        _sourceRemovals.Add((Environment.TickCount64 + SourceGraceMilliseconds, watch.LogSources));

        _log.LogInfo($"Generation {watch.Generation}: its assemblies stay in memory (~{watch.RetainedBytes / 1024} KB) — it injected Unity types, " +
                     "and Il2CppInterop never releases those. Harmless: the old code is neutralised, only the bytes remain.");
    }

    private void RunSourceRemovals()
    {
        var now = Environment.TickCount64;
        for (var i = _sourceRemovals.Count - 1; i >= 0; i--)
        {
            if (_sourceRemovals[i].DueTicks > now) continue;

            _sourceRemovals[i].Sources.ForEach(RemoveLogSource);
            _sourceRemovals.RemoveAt(i);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectAndCheck(UnloadWatch watch, bool includeIl2Cpp)
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

    /// <summary>已经执行完第一步（Unload() 等）、等着第二步（清缓存、放掉上下文）的旧代次。</summary>
    private sealed class RetiringGeneration
    {
        private ScriptGeneration? _generation;

        public RetiringGeneration(ScriptGeneration generation, List<ManualLogSource> logSources, long dueTicks)
        {
            _generation = generation;
            LogSources = logSources;
            DueTicks = dueTicks;
        }

        public long DueTicks { get; }
        public List<ManualLogSource> LogSources { get; }

        /// <summary>把旧代次交出去，本对象不再引用它。</summary>
        public ScriptGeneration TakeGeneration()
        {
            var generation = _generation ?? throw new InvalidOperationException("already taken");
            _generation = null;
            return generation;
        }
    }

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
