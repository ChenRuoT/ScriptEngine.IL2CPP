using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Mono.Cecil;
using ScriptEngineIl2Cpp;
using ScriptEngineIl2Cpp.Cleanup;
using ScriptEngineIl2Cpp.Rewriting;

namespace ScriptEngine.Tests;

/// <summary>
/// 离线测试（不启动游戏）。对 samples/ 编出的脚本 DLL 验证引擎的纯托管部分：
/// Cecil 改写、可回收上下文装载、BasePlugin 实例化与卸载、上下文回收。
/// 退出码 0 = 全部通过。
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static string _gameDir = "";
    private static string _repoRoot = "";

    private static int Main()
    {
        _gameDir = Meta("GameDir");
        _repoRoot = Meta("RepoRoot");

        // 先挂解析器，再去碰任何 BepInEx / Cecil 类型：这些程序集只在游戏目录里
        var searchDirs = new[]
        {
            Path.Combine(_gameDir, "BepInEx", "core"),
            Path.Combine(_gameDir, "BepInEx", "interop"),
        };
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var dir in searchDirs)
            {
                var candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }

            return null;
        };

        Run();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? $"ALL PASSED ({_checks} checks)" : $"{_failures} FAILED of {_checks} checks");
        return _failures == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        // BasePlugin 的构造函数要用 Paths / Logger / ConfigFile：把它们指到临时目录，别碰真实游戏的 BepInEx 配置
        var temp = Path.Combine(Path.GetTempPath(), "ScriptEngine.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Paths.SetExecutablePath(Path.Combine(_gameDir, "EscapeFromTarkov.exe"), Path.Combine(temp, "BepInEx"));
        Directory.CreateDirectory(Paths.ConfigPath);

        try
        {
            RewriterRenamesInjectedTypes();
            RewriterShimsAssemblyLocation();
            CrossAssemblyReferencesFollowRename();
            ReferenceOnlyAssemblyIsRewritten();
            DuplicateAssemblyNamesAreSkipped();
            HelloScriptLifecycleAndCollection();
            GenerationsDoNotCollide();
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------ 测试用例

    private static void RewriterRenamesInjectedTypes()
    {
        Section("Rewriter: injected types are renamed per generation");

        var images = Rewrite(7, "BehaviourScript", "SharedLib");
        var behaviour = images.Single(i => i.AssemblyName == "BehaviourScript");

        Check(behaviour.Rewritten, "BehaviourScript was rewritten");
        Check(behaviour.RenamedTypes.Count == 2, $"two top-level types renamed: TickBehaviour and LeakyPlugin (which only *contains* a nested injected class) (got {behaviour.RenamedTypes.Count})");
        Check(behaviour.RenamedTypes.Any(r => r.Key == "BehaviourScript.TickBehaviour" && r.Value == "BehaviourScript.__g7.TickBehaviour"),
            "TickBehaviour -> BehaviourScript.__g7.TickBehaviour (namespace changed, class name kept)");
        Check(behaviour.Plugins.Select(p => p.TypeName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "BehaviourScript.TidyPlugin", "BehaviourScript.__g7.LeakyPlugin" }),
            "both BasePlugin classes found; LeakyPlugin (nested injected class) is reported under its rewritten name");
        Check(behaviour.Pdb is { Length: > 0 }, "a fresh portable PDB was written");

        var shared = images.Single(i => i.AssemblyName == "SharedLib");
        Check(shared.RenamedTypes.Any(r => r.Value == "SharedLib.__g7.SharedBehaviour"), "SharedLib.SharedBehaviour renamed too");
        Check(shared.Plugins.Count == 0, "SharedLib has no plugins (library)");
    }

    private static void ReferenceOnlyAssemblyIsRewritten()
    {
        Section("Rewriter: an assembly that only *references* a renamed type is re-emitted too");

        var images = Rewrite(4, "ConsumerFixture", "SharedLib");
        var consumer = images.Single(i => i.AssemblyName == "ConsumerFixture");

        Check(consumer.RenamedTypes.Count == 0 && consumer.LocationCallsShimmed == 0, "the fixture has no injected types and no Assembly.Location calls of its own");
        Check(consumer.TypeReferencesRewritten >= 1, $"its type references were rewritten (got {consumer.TypeReferencesRewritten})");
        Check(consumer.Rewritten, "so it must be re-emitted (Rewritten == true)");

        using (var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(consumer.Dll)))
        {
            var stale = assembly.MainModule.GetTypeReferences().Any(r => r.FullName == "SharedLib.SharedBehaviour");
            Check(!stale, "the emitted bytes no longer mention SharedLib.SharedBehaviour");
        }

        // 装进上下文并强制 JIT Load()：类型引用解析不了会在这里抛 TypeLoadException
        var context = new ScriptLoadContext(4, collectible: true, images);
        var assemblies = context.LoadAll();
        var consumerAssembly = assemblies.Single(a => a.GetName().Name == "ConsumerFixture");
        var load = consumerAssembly.GetType("ConsumerFixture.ConsumerPlugin")!.GetMethod("Load")!;
        try
        {
            RuntimeHelpers.PrepareMethod(load.MethodHandle);
            Check(true, "ConsumerPlugin.Load() JIT-compiles against the renamed SharedLib type");
        }
        catch (Exception ex)
        {
            Check(false, $"ConsumerPlugin.Load() failed to JIT: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DuplicateAssemblyNamesAreSkipped()
    {
        Section("Rewriter: duplicate assembly names are dropped before anything is unloaded");

        var warnings = new List<string>();
        var files = new[] { ReadFile(ScriptPath("HelloScript")), ReadFile(ScriptPath("HelloScript")) };
        var images = new ScriptRewriter(BuildResolver(), IL2CPPChainloader.ToPluginInfo).Rewrite(files, 1, warnings.Add);

        Check(images.Count == 1, $"only one of the two identically named assemblies survives (got {images.Count})");
        Check(warnings.Any(w => w.Contains("already taken")), "a warning explains the skipped duplicate");
        Check(new ScriptLoadContext(1, collectible: true, images).LoadAll().Count == 1, "the load context can still be built");
    }

    private static void RewriterShimsAssemblyLocation()
    {
        Section("Rewriter: Assembly.get_Location calls are shimmed");

        var hello = Rewrite(1, "HelloScript").Single();
        Check(hello.LocationCallsShimmed >= 1, $"get_Location call sites replaced (got {hello.LocationCallsShimmed})");
        Check(hello.RenamedTypes.Count == 0, "HelloScript has no injected types, nothing renamed");
        Check(hello.Pdb is { Length: > 0 }, "PDB present");

        using var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(hello.Dll));
        var stillCalls = assembly.MainModule.GetTypes()
            .SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Count(i => i.Operand is MethodReference { Name: "get_Location" } r && r.DeclaringType.FullName == "System.Reflection.Assembly");
        Check(stillCalls == 0, "no direct Assembly.get_Location call remains in the rewritten IL");
    }

    private static void CrossAssemblyReferencesFollowRename()
    {
        Section("Rewriter: cross-assembly type references follow the rename");

        var images = Rewrite(3, "BehaviourScript", "SharedLib");
        var behaviour = images.Single(i => i.AssemblyName == "BehaviourScript");

        using (var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(behaviour.Dll)))
        {
            var refs = assembly.MainModule.GetTypeReferences()
                .Where(r => r.Scope is AssemblyNameReference { Name: "SharedLib" }).Select(r => r.FullName).ToList();
            Check(refs.Contains("SharedLib.__g3.SharedBehaviour"), $"BehaviourScript references SharedLib.__g3.SharedBehaviour (refs: {string.Join(", ", refs)})");
            Check(!refs.Contains("SharedLib.SharedBehaviour"), "no stale reference to the old name");
        }

        // 真正装进一个上下文：引用必须能解析到改名后的类型
        var context = new ScriptLoadContext(3, collectible: true, images);
        var assemblies = context.LoadAll();
        var behaviourAssembly = assemblies.Single(a => a.GetName().Name == "BehaviourScript");
        var types = LeftoverSweeper.GetTypesSafe(behaviourAssembly).Select(t => t.FullName).ToList();
        Check(types.Contains("BehaviourScript.__g3.TickBehaviour"), "TickBehaviour is visible under its new name at runtime");
        Check(!types.Contains("BehaviourScript.TickBehaviour"), "old name is gone at runtime");
        var sharedAssembly = assemblies.Single(a => a.GetName().Name == "SharedLib");
        Check(sharedAssembly.GetType("SharedLib.__g3.SharedBehaviour") is not null, "SharedLib type visible under its new name");
        Check(behaviourAssembly.GetType("BehaviourScript.__g3.LeakyPlugin+LeakyTick") is not null, "nested injected class is reachable through the renamed outer type");
        var leakyInfo = behaviour.Plugins.Single(p => p.TypeName.EndsWith("LeakyPlugin"));
        Check(behaviourAssembly.GetType(leakyInfo.TypeName) is { } leakyType && Activator.CreateInstance(leakyType) is BasePlugin, "the plugin class can be instantiated by the name PluginInfo reports");
        Check(Equals(AssemblyLoadContext.GetLoadContext(behaviourAssembly), context), "assemblies live in the script load context");
    }

    private static void HelloScriptLifecycleAndCollection()
    {
        Section("HelloScript: load -> instantiate -> Load() -> Unload() -> context is collected");

        var weak = RunHelloGeneration(5, out var locationOk, out var unloadReturned, out var contextName);
        Check(locationOk, "Assembly.Location inside the script returns the real DLL path (shim)");
        Check(unloadReturned, "plugin Unload() returned true");
        Check(contextName.StartsWith("ScriptEngine.IL2CPP/g5"), $"script ran in its own load context ({contextName})");

        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Check(!weak.IsAlive, "the unloaded AssemblyLoadContext was garbage-collected (no leak)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunHelloGeneration(int generation, out bool locationOk, out bool unloadReturned, out string contextName)
    {
        var images = Rewrite(generation, "HelloScript");
        var context = new ScriptLoadContext(generation, collectible: true, images);
        var assembly = context.LoadAll().Single();

        var info = images[0].Plugins.Single();
        var type = assembly.GetType(info.TypeName)!;
        var plugin = (BasePlugin)Activator.CreateInstance(type)!;

        plugin.Load();

        var location = ScriptRuntime.GetAssemblyLocation(assembly);
        locationOk = string.Equals(location, images[0].Path, StringComparison.OrdinalIgnoreCase) && File.Exists(location);
        contextName = AssemblyLoadContext.GetLoadContext(assembly)?.Name ?? "";

        unloadReturned = plugin.Unload();
        BepInEx.Logging.Logger.Sources.Remove(plugin.Log);
        plugin.Config.SaveOnConfigSet = false;

        var purged = StaticCachePurger.Purge(new[] { assembly });
        Console.WriteLine($"    (static caches purged: {(purged.Count == 0 ? "none" : string.Join(", ", purged.Select(p => p.Key + " x" + p.Value)))})");

        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    private static void GenerationsDoNotCollide()
    {
        Section("Two generations of the same scripts coexist (distinct names, distinct contexts)");

        var first = Rewrite(1, "BehaviourScript", "SharedLib");
        var second = Rewrite(2, "BehaviourScript", "SharedLib");

        var contextA = new ScriptLoadContext(1, collectible: true, first);
        var contextB = new ScriptLoadContext(2, collectible: true, second);
        var a = contextA.LoadAll().Single(x => x.GetName().Name == "BehaviourScript");
        var b = contextB.LoadAll().Single(x => x.GetName().Name == "BehaviourScript");

        var fullNamesA = LeftoverSweeper.GetTypesSafe(a).Select(t => t.FullName).ToHashSet();
        var fullNamesB = LeftoverSweeper.GetTypesSafe(b).Select(t => t.FullName).ToHashSet();
        Check(fullNamesA.Contains("BehaviourScript.__g1.TickBehaviour") && fullNamesB.Contains("BehaviourScript.__g2.TickBehaviour"),
            "TickBehaviour has a different FullName in each generation (ClassInjector dedupes by FullName)");
        Check(!ReferenceEquals(a, b), "different Assembly instances");
    }

    // ------------------------------------------------------------------ 工具

    private static List<ScriptImage> Rewrite(int generation, params string[] names)
    {
        var files = names.Select(n => ReadFile(ScriptPath(n))).ToList();
        var rewriter = new ScriptRewriter(BuildResolver(), IL2CPPChainloader.ToPluginInfo);
        return rewriter.Rewrite(files, generation, message => Console.WriteLine("    warn: " + message));
    }

    private static IAssemblyResolver BuildResolver()
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(_gameDir, "BepInEx", "core"));
        resolver.AddSearchDirectory(Path.Combine(_gameDir, "BepInEx", "interop"));
        resolver.AddSearchDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        return resolver;
    }

    private static string ScriptPath(string name)
    {
        foreach (var group in new[] { "samples", Path.Combine("tests", "fixtures") })
        {
            var dir = Path.Combine(_repoRoot, group, name, "bin");
            if (!Directory.Exists(dir)) continue;

            var candidate = Directory.EnumerateFiles(dir, name + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (candidate is not null) return candidate;
        }

        throw new FileNotFoundException($"{name}.dll not built under samples/ or tests/fixtures/");
    }

    private static ScriptFile ReadFile(string dllPath)
    {
        var pdb = Path.ChangeExtension(dllPath, ".pdb");
        return new ScriptFile(dllPath, File.ReadAllBytes(dllPath), File.Exists(pdb) ? File.ReadAllBytes(pdb) : null);
    }

    private static string Meta(string key)
        => typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title);
    }

    private static void Check(bool condition, string what)
    {
        _checks++;
        if (!condition) _failures++;
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {what}");
    }
}

