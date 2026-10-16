using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Mono.Cecil;
using ScriptEngineIl2Cpp;
using ScriptEngineIl2Cpp.Cleanup;
using ScriptEngineIl2Cpp.Rewriting;
using ScriptEngineIl2Cpp.Triggers;

namespace ScriptEngine.Tests;

/// <summary>独立评审（docs/review-fixes.md）提出的问题的回归测试。每一条都是「没修之前会失败」的场景。</summary>
internal static partial class Program
{
    private static void ReviewRegressions()
    {
        CecilResolverUsesTheSharedCache();
        HostAssembliesAreRejectedBeforeAnalysis();
        RenameKeysIgnoreAssemblyNameCase();
        PluginDiscoveryFollowsRenamedBaseClasses();
        LoadOrderFollowsDependencies();
        PurgerTargetsAreRealTypes();
        PurgerMatchesDeadAssemblies();
        PdbMatching();
        ScriptFileReaderBehaviour();
        FileTriggerBehaviour();
        CommandChannelBehaviour();
        HarmonyPatchLifecycle();
        HarmonyPinReport();
        HarmonyHostPatchLifecycle();
    }

    // ------------------------------------------------------------------ 评审 #1

    /// <summary>统计两个 Resolve 重载各被调了几次的解析器。</summary>
    private sealed class CountingResolver : IAssemblyResolver
    {
        private readonly DefaultAssemblyResolver _inner;

        public CountingResolver(DefaultAssemblyResolver inner) => _inner = inner;

        public int SingleArgument { get; private set; }
        public int WithParameters { get; private set; }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            SingleArgument++;
            return _inner.Resolve(name);
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            WithParameters++;
            return _inner.Resolve(name, parameters);
        }

        public void Dispose() { }
    }

    private static void CecilResolverUsesTheSharedCache()
    {
        Section("Review #1: Cecil type resolution goes through the shared resolver's caching overload");

        var counting = new CountingResolver((DefaultAssemblyResolver)BuildResolver());
        var files = new[] { "BehaviourScript", "SharedLib" }.Select(n => ReadFile(ScriptPath(n))).ToList();
        new ScriptRewriter(counting, IL2CPPChainloader.ToPluginInfo).Rewrite(files, 11, _ => { });

        // DefaultAssemblyResolver 只在单参数重载里查/写缓存；双参数重载直接走基类，
        // 每次都重新打开磁盘上的 interop DLL，返回的 AssemblyDefinition 还没人 Dispose
        Check(counting.SingleArgument > 0, $"type resolution happened ({counting.SingleArgument} single-argument calls)");
        Check(counting.WithParameters == 0, $"the uncached two-argument overload is never used (got {counting.WithParameters})");
    }

    // ------------------------------------------------------------------ 评审 #2

    private static void HostAssembliesAreRejectedBeforeAnalysis()
    {
        Section("Review #2: assemblies the host already has are skipped *before* they can poison cross-script references");

        var warnings = new List<string>();
        var files = new[] { "ConsumerFixture", "SharedLib" }.Select(n => ReadFile(ScriptPath(n))).ToList();
        var images = new ScriptRewriter(BuildResolver(), IL2CPPChainloader.ToPluginInfo).Rewrite(
            files, 9, warnings.Add, name => name == "SharedLib" ? "already loaded by the host" : null);

        Check(images.Count == 1 && images[0].AssemblyName == "ConsumerFixture", "only the consumer remains");
        Check(images[0].TypeReferencesRewritten == 0 && !images[0].Rewritten,
            "its reference to the host-provided SharedBehaviour keeps the original name (the host copy is not renamed)");
        Check(warnings.Any(w => w.Contains("SharedLib.dll") && w.Contains("already loaded by the host")), "a warning names the rejected file");
    }

    private static void RenameKeysIgnoreAssemblyNameCase()
    {
        Section("Review #2: cross-assembly rename lookup ignores the case of the assembly name");

        // 把消费者里对 SharedLib 的引用改成全小写 "sharedlib"（Windows 上程序集引用的大小写不一定和程序集自己的名字一致）
        var consumer = ReadFile(ScriptPath("ConsumerFixture"));
        byte[] patched;
        using (var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(consumer.Dll)))
        {
            foreach (var reference in assembly.MainModule.AssemblyReferences.Where(r => r.Name == "SharedLib"))
                reference.Name = "sharedlib";

            using var output = new MemoryStream();
            assembly.Write(output);
            patched = output.ToArray();
        }

        var files = new[] { new ScriptFile(consumer.Path, patched, null), ReadFile(ScriptPath("SharedLib")) };
        var images = new ScriptRewriter(BuildResolver(), IL2CPPChainloader.ToPluginInfo).Rewrite(files, 5, _ => { });
        var rewritten = images.Single(i => i.AssemblyName == "ConsumerFixture");

        Check(rewritten.TypeReferencesRewritten >= 1, $"the reference scoped to 'sharedlib' still got renamed (got {rewritten.TypeReferencesRewritten})");
    }

    private static void PluginDiscoveryFollowsRenamedBaseClasses()
    {
        Section("Review (unsure b): a plugin whose abstract base lives in another script DLL (and gets renamed) is still found");

        var images = Rewrite(6, "DerivedPluginFixture", "PluginBaseFixture");
        var derived = images.Single(i => i.AssemblyName == "DerivedPluginFixture");
        var baseImage = images.Single(i => i.AssemblyName == "PluginBaseFixture");

        Check(baseImage.RenamedTypes.Any(r => r.Value == "PluginBaseFixture.__g6.PluginBase"), "the base class was renamed (it contains a nested injected class)");
        Check(derived.Plugins.Count == 1 && derived.Plugins[0].TypeName == "DerivedPluginFixture.DerivedPlugin", "the derived plugin is discovered with its own name");

        // 真正装载：派生类型要能顺着改名后的基类解析
        var context = new ScriptLoadContext(6, collectible: true, images);
        var assemblies = context.LoadAll();
        var derivedType = assemblies.Single(a => a.GetName().Name == "DerivedPluginFixture").GetType("DerivedPluginFixture.DerivedPlugin")!;
        Check(derivedType.BaseType?.FullName == "PluginBaseFixture.__g6.PluginBase", $"its base type is the renamed one (got {derivedType.BaseType?.FullName})");
    }

    // ------------------------------------------------------------------ 主模组 + 副模组都在 scripts/

    private static void LoadOrderFollowsDependencies()
    {
        Section("Load order: a mod that depends on another script comes after it — by [BepInDependency] or just by assembly reference");

        // 副模组的 GUID（…aconsumer）字母序在主模组（…zmain）之前，所以只有真的按依赖排序才会让主模组先装
        List<string> Order(params string[] names)
        {
            var images = Rewrite(20, names);
            var candidates = images.SelectMany(i => i.Plugins.Select(p => (p, (Assembly)null!, i))).ToList();
            var report = new ReloadReport(BepInEx.Logging.Logger.CreateLogSource("order-test"), "test");
            var ordered = ScriptHost.OrderByDependencies(candidates, images, report).Select(c => c.Info.Metadata.GUID).ToList();
            Check(report.Errors.Count == 0, "no ordering error reported");
            return ordered;
        }

        const string main = "com.shiunaya.samples.dep.zmain";
        Check(Order("DepConsumerScript", "DepMainScript").SequenceEqual(new[] { main, "com.shiunaya.samples.dep.aconsumer" }), "[BepInDependency]: main first");
        Check(Order("DepConsumerFixture", "DepMainScript").SequenceEqual(new[] { main, "com.shiunaya.fixtures.dep.aconsumer" }), "assembly reference only (no [BepInDependency]): main first");
        Check(Order("DepMainScript", "DepConsumerFixture").First() == main, "...regardless of the order the files were given in");

        // 程序集引用成环：退回只按 [BepInDependency]，不报错、不丢插件
        var cyclic = Rewrite(21, "DepConsumerScript", "DepMainScript");
        cyclic.Single(i => i.AssemblyName == "DepMainScript").ReferencedAssemblies.Add("DepConsumerScript");
        var candidatesCyclic = cyclic.SelectMany(i => i.Plugins.Select(p => (p, (Assembly)null!, i))).ToList();
        var cyclicReport = new ReloadReport(BepInEx.Logging.Logger.CreateLogSource("order-test"), "test");
        var cyclicOrder = ScriptHost.OrderByDependencies(candidatesCyclic, cyclic, cyclicReport).Select(c => c.Info.Metadata.GUID).ToList();
        Check(cyclicReport.Errors.Count == 0 && cyclicOrder.Count == 2 && cyclicOrder[0] == main, "a reference cycle falls back to the declared [BepInDependency] order");
    }

    // ------------------------------------------------------------------ 评审 #3

    private static void PurgerTargetsAreRealTypes()
    {
        Section("Review #3: every StaticCachePurger target resolves to a real type");

        foreach (var (type, assembly) in StaticCachePurger.Targets)
            Check(Type.GetType($"{type}, {assembly}", throwOnError: false) is not null, $"{type} ({assembly})");
    }

    private static void PurgerMatchesDeadAssemblies()
    {
        Section("Review #3: the purger recognises assemblies / types / delegates / tuples that belong to a dead assembly");

        var images = Rewrite(12, "HelloScript");
        var context = new ScriptLoadContext(12, collectible: true, images);
        var assembly = context.LoadAll().Single();
        var type = assembly.GetType("HelloScript.HelloPlugin")!;
        var dead = new HashSet<Assembly> { assembly };

        Check(StaticCachePurger.Belongs(assembly, dead, 0), "an Assembly value (Il2CppWrapperTypes.ourAssemblies)");
        Check(StaticCachePurger.Belongs(type, dead, 0), "a Type key");
        Check(StaticCachePurger.Belongs((type, 7), dead, 0), "a (Type, x) tuple key (ClassInjector._injectedFieldTypes / DelegateSupport.ConvertedStatic)");
        Check(StaticCachePurger.Belongs(type.GetMethod("Load")!, dead, 0), "a MethodInfo key");
        Check(!StaticCachePurger.Belongs(typeof(string), dead, 0), "an unrelated Type is left alone");
        Check(!StaticCachePurger.Belongs(typeof(Program).Assembly, dead, 0), "an unrelated Assembly is left alone");
        Check(!StaticCachePurger.Belongs((IntPtr)12345, dead, 0), "pointer keys are skipped cheaply");

        // ConditionalWeakTable（DelegateSupport.ConvertedByTarget 之类）：key 属于已死程序集的条目要被摘掉，别的不动
        var instance = (BasePlugin)Activator.CreateInstance(type)!;
        BepInEx.Logging.Logger.Sources.Remove(instance.Log);
        var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        var unrelatedKey = new object();
        table.Add(instance, new object());
        table.Add(unrelatedKey, new object());

        var removed = StaticCachePurger.PurgeWeakTable(table, dead, out var scanned);
        Check(removed == 1 && scanned == 2, $"a ConditionalWeakTable entry whose key is an instance of a dead type is removed, the other one is scanned and kept (removed {removed}, scanned {scanned})");
        Check(!table.TryGetValue(instance, out _) && table.TryGetValue(unrelatedKey, out _), "the table now only holds the unrelated entry");

        GC.KeepAlive(context);
        GC.KeepAlive(instance);
        GC.KeepAlive(unrelatedKey);
    }

    // ------------------------------------------------------------------ 评审 #7（PDB / 指纹 / 读取）

    private static void PdbMatching()
    {
        Section("Review #7: a sidecar PDB is checked against the DLL's CodeView GUID");

        var hello = ReadFile(ScriptPath("HelloScript"));
        var other = ReadFile(ScriptPath("BehaviourScript"));

        Check(ScriptFileReader.CheckPdb(hello.Dll, hello.Pdb!) == ScriptFileReader.PdbMatch.Matches, "the DLL's own PDB matches");
        Check(ScriptFileReader.CheckPdb(hello.Dll, other.Pdb!) == ScriptFileReader.PdbMatch.Mismatch, "another assembly's PDB is a mismatch (the stale-PDB window of a build)");
        Check(ScriptFileReader.CheckPdb(hello.Dll, hello.Pdb!.Take(hello.Pdb!.Length / 2).ToArray()) == ScriptFileReader.PdbMatch.Truncated, "a half-written portable PDB is detected");
        Check(ScriptFileReader.CheckPdb(hello.Dll, new byte[] { 1, 2, 3, 4, 5 }) == ScriptFileReader.PdbMatch.Unknown, "something that is not a portable PDB cannot be judged (left to Cecil)");
    }

    private static void ScriptFileReaderBehaviour()
    {
        Section("Review #7: ScriptFileReader — settled garbage is ignored, fresh garbage defers, PDB mismatch handling, fingerprint");

        var temp = Path.Combine(Path.GetTempPath(), "ScriptEngine.Tests.reader." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var hello = ScriptPath("HelloScript");
            var behaviourPdb = Path.ChangeExtension(ScriptPath("BehaviourScript"), ".pdb");
            File.Copy(hello, Path.Combine(temp, "HelloScript.dll"));
            File.Copy(Path.ChangeExtension(hello, ".pdb"), Path.Combine(temp, "HelloScript.pdb"));

            var warnings = new List<string>();
            var first = ScriptFileReader.Read(temp, _ => { }, warnings.Add);
            var second = ScriptFileReader.Read(temp, _ => { }, warnings.Add);
            Check(first.Files.Count == 1 && first.Files[0].Pdb is not null, "a DLL with its own PDB is read with symbols");
            Check(first.Fingerprint == second.Fingerprint && first.Fingerprint.Length > 0, "the fingerprint is stable for identical content");

            // 垃圾 .dll：刚写过 → 还在写，延后；很久没动 → 忽略并警告
            var garbage = Path.Combine(temp, "Garbage.dll");
            File.WriteAllText(garbage, "this is not an assembly");
            Check(Throws<ScriptFileReader.NotReadyException>(() => ScriptFileReader.Read(temp, _ => { }, warnings.Add)),
                "a fresh invalid DLL defers the reload (it may still be being written)");

            File.SetLastWriteTimeUtc(garbage, DateTime.UtcNow.AddMinutes(-10));
            warnings.Clear();
            var settled = ScriptFileReader.Read(temp, _ => { }, warnings.Add);
            Check(settled.Files.Count == 1 && warnings.Any(w => w.Contains("Garbage.dll")), "a settled invalid DLL is ignored with a warning instead of blocking every reload");
            Check(settled.Fingerprint == first.Fingerprint, "ignored files do not affect the fingerprint");
            File.Delete(garbage);

            // PDB 和 DLL 对不上：刚写过 → 延后；稳定了 → 丢掉符号并警告，指纹跟着变
            File.Copy(behaviourPdb, Path.Combine(temp, "HelloScript.pdb"), overwrite: true);
            File.SetLastWriteTimeUtc(Path.Combine(temp, "HelloScript.pdb"), DateTime.UtcNow); // File.Copy 会带上源文件的旧时间戳，这里要的是「刚写完」
            Check(Throws<ScriptFileReader.NotReadyException>(() => ScriptFileReader.Read(temp, _ => { }, warnings.Add)),
                "a fresh mismatching PDB defers the reload");

            File.SetLastWriteTimeUtc(Path.Combine(temp, "HelloScript.pdb"), DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(Path.Combine(temp, "HelloScript.dll"), DateTime.UtcNow.AddMinutes(-10));
            warnings.Clear();
            var stale = ScriptFileReader.Read(temp, _ => { }, warnings.Add);
            Check(stale.Files.Count == 1 && stale.Files[0].Pdb is null && warnings.Any(w => w.Contains("HelloScript.pdb")),
                "a settled mismatching PDB is dropped with a warning (no wrong line numbers)");
            Check(stale.Fingerprint != first.Fingerprint, "dropping the PDB changes the fingerprint");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------ 评审 #4（防抖 / 目录切换）

    private static void FileTriggerBehaviour()
    {
        Section("Review #4: FileTrigger — clearing is time-scoped, the watch follows the directory");

        using var trigger = new FileTrigger();

        trigger.Poke();
        var pokedAt = Environment.TickCount64;
        Thread.Sleep(40);
        trigger.ClearPendingBefore(pokedAt - 1);
        Check(trigger.TryConsume(0), "an event newer than the cut-off survives ClearPendingBefore");

        trigger.Poke();
        Thread.Sleep(40);
        trigger.ClearPendingBefore(Environment.TickCount64);
        Check(!trigger.TryConsume(0), "an event older than the cut-off is dropped (a reload already covered it)");

        var root = Path.Combine(Path.GetTempPath(), "ScriptEngine.Tests.watch." + Guid.NewGuid().ToString("N"));
        var dirA = Path.Combine(root, "a");
        var dirB = Path.Combine(root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            var warnings = new List<string>();
            trigger.ClearPendingBefore(long.MaxValue);
            trigger.Start(dirA, warnings.Add);
            Check(trigger.IsWatching, "watching folder A");

            trigger.EnsureHealthy(dirB, warnings.Add); // 运行时改了 ScriptsDirectory
            trigger.ClearPendingBefore(long.MaxValue);
            Check(trigger.IsWatching, "still watching after switching to folder B");

            File.WriteAllText(Path.Combine(dirA, "ignored.dll"), "x");
            File.WriteAllText(Path.Combine(dirB, "seen.dll"), "x");
            Check(WaitUntil(() => trigger.TryConsume(0), 3000), "a change in the new folder B is noticed");

            File.Delete(Path.Combine(dirA, "ignored.dll"));
            Thread.Sleep(300);
            trigger.ClearPendingBefore(long.MaxValue);
            File.WriteAllText(Path.Combine(dirA, "later.dll"), "x");
            Thread.Sleep(300);
            Check(!trigger.TryConsume(0), "changes in the old folder A are no longer watched");

            // 被监视的目录被删掉再重建：监视器应该自愈
            Directory.Delete(dirB, recursive: true);
            Thread.Sleep(500);
            Directory.CreateDirectory(dirB);
            trigger.EnsureHealthy(dirB, warnings.Add);
            trigger.ClearPendingBefore(long.MaxValue);
            Thread.Sleep(200);
            File.WriteAllText(Path.Combine(dirB, "again.dll"), "x");
            Check(WaitUntil(() => trigger.TryConsume(0), 3000), "after deleting and re-creating the watched folder, changes are noticed again");
        }
        finally
        {
            trigger.Stop();
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------ 评审 #7（命令通道）

    private static void CommandChannelBehaviour()
    {
        Section("Review #7: CommandChannel — result files are filtered before any metadata lookups, stale ones are swept");

        var work = Path.Combine(Path.GetTempPath(), "ScriptEngine.Tests.commands." + Guid.NewGuid().ToString("N"));
        try
        {
            var channel = new CommandChannel(work, _ => { });
            var commands = Path.Combine(work, "commands");

            var stale = Path.Combine(commands, "old.result.json");
            var fresh = Path.Combine(commands, "new.result.json");
            File.WriteAllText(stale, "{}");
            File.WriteAllText(fresh, "{}");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-30));
            File.WriteAllText(Path.Combine(commands, "7.reload"), "");

            var command = channel.Poll();
            Check(command is { Id: "7", Command: "reload" }, $"the command file is picked up and the result files are not mistaken for commands (got {command})");
            Check(!File.Exists(Path.Combine(commands, "7.reload")), "the command file is consumed");
            Check(!File.Exists(stale), "a result file nobody picked up for half an hour is swept");
            Check(File.Exists(fresh), "a fresh result file is left alone");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------ Harmony

    private static void HarmonyPatchLifecycle()
    {
        Section("Harmony: UnpatchSelf() in Unload() really undoes the patch (here the target is a method of the script itself)");

        var good = RunPatchGeneration(21, unpatchOnUnload: true, out var good1, out var good2, out var good3);
        Check(good1 == 1 && good2 == 42 && good3 == 1, $"Target.Compute(): original {good1} -> patched {good2} -> after Unload() {good3}");
        CollectAll(good);
        // 目标是脚本自己的方法时，Harmony / MonoMod 的内部表（PatchManager、DetourManager……）会一直留着这个方法 → 旧上下文收不掉。
        // 真实插件打补丁的目标是游戏方法，不受影响（见下一个用例）；这里只记录事实。
        Console.WriteLine($"    (info) patched method inside the script itself: context collected after UnpatchSelf() = {!good.IsAlive} (Harmony keeps bookkeeping for the method)");

        // 反面：不撤销补丁。只记录观察到的事实（README 的「插件作者契约」里写着要 UnpatchSelf）
        var bad = RunPatchGeneration(22, unpatchOnUnload: false, out _, out _, out var bad3);
        CollectAll(bad);
        Console.WriteLine($"    (info) without UnpatchSelf(): Target.Compute() after Unload() = {bad3} (42 = the old patch is still live); context collected = {!bad.IsAlive}");
        Check(bad3 == 42, "without UnpatchSelf() the old patch stays live — which is why the contract demands an unpatch in Unload()");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RunPatchGeneration(int generation, bool unpatchOnUnload, out int original, out int patched, out int afterUnload)
    {
        File.WriteAllText(Path.Combine(Paths.ConfigPath, "com.shiunaya.samples.patch.cfg"), $"[Test]\n\nUnpatchOnUnload = {(unpatchOnUnload ? "true" : "false")}\n");

        var images = Rewrite(generation, "PatchScript");
        var context = new ScriptLoadContext(generation, collectible: true, images);
        var assembly = context.LoadAll().Single();

        var compute = assembly.GetType("PatchScript.Target")!.GetMethod("Compute")!;
        original = (int)compute.Invoke(null, null)!;

        var plugin = (BasePlugin)Activator.CreateInstance(assembly.GetType("PatchScript.PatchPlugin")!)!;
        plugin.Load();
        patched = (int)compute.Invoke(null, null)!;

        plugin.Unload();
        afterUnload = (int)compute.Invoke(null, null)!;

        BepInEx.Logging.Logger.Sources.Remove(plugin.Log);
        plugin.Config.SaveOnConfigSet = false;
        StaticCachePurger.Purge(new[] { assembly });

        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    /// <summary>诊断：UnpatchSelf() 之后，Harmony / MonoMod 的静态状态里还有谁引用着脚本程序集？</summary>
    private static void HarmonyPinReport()
    {
        Section("Diagnostics: who still references a script assembly after Harmony.UnpatchSelf()?");

        File.WriteAllText(Path.Combine(Paths.ConfigPath, "com.shiunaya.samples.patch.cfg"), "[Test]\n\nUnpatchOnUnload = true\n");
        var images = Rewrite(30, "PatchScript");
        var context = new ScriptLoadContext(30, collectible: true, images);
        var assembly = context.LoadAll().Single();

        var plugin = (BasePlugin)Activator.CreateInstance(assembly.GetType("PatchScript.PatchPlugin")!)!;
        plugin.Load();
        plugin.Unload();
        BepInEx.Logging.Logger.Sources.Remove(plugin.Log);
        plugin.Config.SaveOnConfigSet = false;

        var result = PinFinder.Find(new[] { assembly }, maxPaths: 60);
        Console.WriteLine($"    (info) PinFinder: {result.Paths.Count} path(s), visited {result.VisitedObjects} objects in {result.ElapsedMs} ms{(result.Truncated ? " (truncated)" : "")}");
        foreach (var path in result.Paths) Console.WriteLine("      " + path);
        Check(result.Paths.Any(p => p.Contains("PatchManager")), "PinFinder names Harmony's PatchManager as one of the holders");

        GC.KeepAlive(plugin);
        GC.KeepAlive(context);
    }

    /// <summary>被 HostPatchFixture 打补丁的宿主方法（按名字找到，脚本不引用本程序集）。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static int HostTarget() => 1;

    private static void HarmonyHostPatchLifecycle()
    {
        Section("Harmony: the realistic shape — patch targets live in the host, the patch methods in the script");

        var weak = RunHostPatchGeneration(31, out var before, out var patched, out var after);
        Check(before == 1 && patched == 42 && after == 1, $"HostTarget(): {before} -> patched {patched} -> after UnpatchSelf() {after}");
        CollectAll(weak);
        Check(!weak.IsAlive, "the unloaded AssemblyLoadContext is garbage-collected after UnpatchSelf()");

        if (weak.IsAlive)
        {
            // 没收掉：看看是谁
            Console.WriteLine("    (info) still alive — PinFinder cannot be used here because the assembly is already gone from our hands");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RunHostPatchGeneration(int generation, out int before, out int patched, out int after)
    {
        var images = Rewrite(generation, "HostPatchFixture");
        var context = new ScriptLoadContext(generation, collectible: true, images);
        var assembly = context.LoadAll().Single();

        before = HostTarget();
        var plugin = (BasePlugin)Activator.CreateInstance(assembly.GetType("HostPatchFixture.HostPatchPlugin")!)!;
        plugin.Load();
        patched = HostTarget();
        plugin.Unload();
        after = HostTarget();

        BepInEx.Logging.Logger.Sources.Remove(plugin.Log);
        plugin.Config.SaveOnConfigSet = false;
        StaticCachePurger.Purge(new[] { assembly });

        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    private static void CollectAll(WeakReference weak)
    {
        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // ------------------------------------------------------------------ 小工具

    private static bool Throws<TException>
(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }

        return condition();
    }
}
