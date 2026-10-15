using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ScriptEngineIl2Cpp.Rewriting;

/// <summary>
/// 装载前对脚本程序集做的 Cecil 改写。纯函数式：字节进，字节出，不碰磁盘、不碰游戏状态。
///
/// 做三件事：
/// <list type="number">
/// <item>
/// <b>注入类型按代次改名。</b>Il2CppInterop 的 <c>ClassInjector</c> 按 <c>Type.FullName</c> 去重，
/// 第二次加载同名的 MonoBehaviour 会抛 "already injected"。所以凡是继承自 <c>Il2CppObjectBase</c>
/// 的类（或内含这种嵌套类的顶层类），把它的<b>命名空间</b>加上 <c>.__g{代次}</c> 后缀。
/// 类名保持不变（Unity 里看到的组件名不变）。跨脚本程序集的类型引用同步改写。
/// </item>
/// <item>
/// <b>Assembly.Location 垫片。</b>字节流加载的程序集 <c>Location</c> 为空，把调用点替换成
/// <see cref="ScriptRuntime.GetAssemblyLocation"/>，老插件用 <c>GetExecutingAssembly().Location</c> 找资源照常工作。
/// </item>
/// <item>
/// <b>PDB。</b>重写后的程序集配一份新的 portable PDB，异常堆栈仍有文件/行号。
/// </item>
/// </list>
/// 没有任何改动需求的程序集原样透传（字节和 PDB 都不动）。
/// </summary>
internal sealed class ScriptRewriter
{
    internal const string Il2CppObjectBaseFullName = "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase";
    internal const string GenerationNamespaceMarker = "__g";

    private static readonly HashSet<string> BclAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Runtime", "System.Private.CoreLib", "mscorlib", "netstandard",
    };

    // PluginInfo.TypeName 的 setter 是 internal
    private static readonly MethodInfo PluginTypeNameSetter =
        typeof(PluginInfo).GetProperty(nameof(PluginInfo.TypeName))!.GetSetMethod(nonPublic: true)!;

    private readonly IAssemblyResolver _sharedResolver;
    private readonly Func<TypeDefinition, string, PluginInfo?> _pluginSelector;

    /// <param name="sharedResolver">解析 BepInEx / interop / 运行时程序集。</param>
    /// <param name="pluginSelector">判断顶层类型是不是 BasePlugin（生产环境传 <c>IL2CPPChainloader.ToPluginInfo</c>）。</param>
    public ScriptRewriter(IAssemblyResolver sharedResolver, Func<TypeDefinition, string, PluginInfo?> pluginSelector)
    {
        _sharedResolver = sharedResolver;
        _pluginSelector = pluginSelector;
    }

    /// <param name="files">本批全部脚本 DLL。</param>
    /// <param name="generation">本次重载的代次（用来给注入类改名）。</param>
    /// <param name="warn">非致命问题（例如符号读不出来）。</param>
    /// <param name="rejectAssembly">
    /// 按程序集简单名判断「这份不该当脚本装」，返回警告原因（null = 接受）。生产环境用来挡掉与宿主已加载程序集同名的 DLL
    /// （BepInEx、Harmony、Il2CppInterop、引擎自己，或同一个插件还留在 plugins/ 里）。
    /// 必须在分析之前挡：否则这份程序集里的注入类型会进改名表，其它脚本对它的类型引用被改成 .__g{N}，
    /// 而运行时又回落到宿主那份没改名的 —— TypeLoadException。
    /// </param>
    public List<ScriptImage> Rewrite(
        IReadOnlyList<ScriptFile> files, int generation, Action<string> warn, Func<string, string?>? rejectAssembly = null)
    {
        var resolver = new GenerationResolver(_sharedResolver);
        var loaded = new List<(ScriptFile File, AssemblyDefinition Assembly)>(files.Count);

        try
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var assembly = Read(file, resolver, warn);

                // 卫星资源程序集（de/X.resources.dll 之类）没有代码，也没有装载价值
                if (assembly.Name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    assembly.Dispose();
                    continue;
                }

                if (rejectAssembly?.Invoke(assembly.Name.Name) is { } reason)
                {
                    warn($"{file.Path}: {reason}");
                    assembly.Dispose();
                    continue;
                }

                // 同名程序集只留第一份（调用方按路径排序，结果是确定的）。不挡掉的话，后面按简单名建字典会抛异常，
                // 而那时旧代次已经卸掉了 —— 违反「准备阶段失败不伤旧代次」。
                if (!seenNames.Add(assembly.Name.Name))
                {
                    warn($"{file.Path}: another script DLL named '{assembly.Name.Name}' was already taken — skipped " +
                         "(the same dependency in two sub-folders, or the same plugin twice?)");
                    assembly.Dispose();
                    continue;
                }

                resolver.Register(assembly);
                loaded.Add((file, assembly));
            }

            // ---- 1. 分析（只读，类型解析都用原始名字） ----
            var renamePlan = new Dictionary<TypeDefinition, string>();                     // 顶层类型 → 新命名空间
            var renameKeys = new Dictionary<(string Assembly, string FullName), string>(RenameKeyComparer.Instance); // 跨程序集引用用
            foreach (var (_, assembly) in loaded)
            {
                foreach (var type in assembly.MainModule.Types)
                {
                    if (type.Name == "<Module>" || !NeedsRename(type)) continue;

                    var newNamespace = MakeNamespace(type.Namespace, generation);
                    renamePlan[type] = newNamespace;
                    renameKeys[(assembly.Name.Name, type.FullName)] = newNamespace;
                }
            }

            // ---- 2. 发现 BasePlugin ----
            // 必须在改名之前：Cecil 的 ModuleDefinition 按「读入时的名字」缓存类型，改名后跨程序集的 Resolve()
            // 可能找不到被改名的基类。改名之后只需要把 TypeName 补成新名字（改名只动顶层类型的命名空间，是确定的）。
            var discovered = new List<(int Index, TypeDefinition Type, PluginInfo Info)>();
            for (var i = 0; i < loaded.Count; i++)
            {
                var module = loaded[i].Assembly.MainModule;
                if (!MayContainPlugins(module)) continue;

                foreach (var type in module.Types)
                {
                    var info = _pluginSelector(type, loaded[i].File.Path);
                    if (info is not null) discovered.Add((i, type, info));
                }
            }

            // ---- 3. 改写 ----
            var images = new List<ScriptImage>(loaded.Count);
            foreach (var (file, assembly) in loaded)
            {
                var image = new ScriptImage(file.Path, assembly.Name.Name, file.Dll, file.Pdb);
                var module = assembly.MainModule;

                foreach (var type in module.Types)
                {
                    if (!renamePlan.TryGetValue(type, out var newNamespace)) continue;
                    var oldFullName = type.FullName;
                    type.Namespace = newNamespace;
                    image.RenamedTypes.Add(new KeyValuePair<string, string>(oldFullName, type.FullName));
                }

                image.TypeReferencesRewritten = RewriteTypeReferences(module, renameKeys);
                image.LocationCallsShimmed = ShimAssemblyLocation(module);
                // 只改了类型引用的程序集（自己没有注入类型、也没用 Assembly.Location）同样必须重新写出，
                // 否则它装载的还是旧名字，运行时 TypeLoadException。
                image.Rewritten = image.RenamedTypes.Count > 0 || image.TypeReferencesRewritten > 0 || image.LocationCallsShimmed > 0;
                images.Add(image);
            }

            foreach (var (index, type, info) in discovered)
            {
                // type.FullName 此时已经是改名后的
                if (info.TypeName != type.FullName) PluginTypeNameSetter.Invoke(info, new object[] { type.FullName });
                images[index].Plugins.Add(info);
            }

            // ---- 4. 输出 ----
            for (var i = 0; i < loaded.Count; i++)
            {
                if (!images[i].Rewritten) continue;
                WriteBack(loaded[i].Assembly, images[i], warn);
            }

            return images;
        }
        finally
        {
            foreach (var (_, assembly) in loaded) assembly.Dispose();
        }
    }

    // ------------------------------------------------------------------ 读取

    private static AssemblyDefinition Read(ScriptFile file, IAssemblyResolver resolver, Action<string> warn)
    {
        var readerParameters = new ReaderParameters(ReadingMode.Immediate)
        {
            AssemblyResolver = resolver,
            InMemory = true,
        };

        if (file.Pdb is not null)
        {
            readerParameters.ReadSymbols = true;
            readerParameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false);
            readerParameters.SymbolStream = new MemoryStream(file.Pdb, writable: false);
        }
        else
        {
            // 没有旁挂 PDB 时仍然尝试嵌入式 portable PDB
            readerParameters.ReadSymbols = true;
            readerParameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false);
        }

        try
        {
            return AssemblyDefinition.ReadAssembly(new MemoryStream(file.Dll, writable: false), readerParameters);
        }
        catch (Exception ex) when (readerParameters.ReadSymbols && ex is not BadImageFormatException)
        {
            warn($"{System.IO.Path.GetFileName(file.Path)}: 调试符号读取失败（{ex.GetType().Name}: {ex.Message}），本次不带符号加载");
            readerParameters.ReadSymbols = false;
            readerParameters.SymbolStream = null;
            readerParameters.SymbolReaderProvider = null;
            return AssemblyDefinition.ReadAssembly(new MemoryStream(file.Dll, writable: false), readerParameters);
        }
    }

    // ------------------------------------------------------------------ 分析

    /// <summary>(程序集名, 类型全名) 的比较器：程序集名忽略大小写（Windows 文件系统上程序集引用的大小写不一定和程序集自己的名字一致），类型全名区分大小写。</summary>
    private sealed class RenameKeyComparer : IEqualityComparer<(string Assembly, string FullName)>
    {
        public static readonly RenameKeyComparer Instance = new();

        public bool Equals((string Assembly, string FullName) x, (string Assembly, string FullName) y)
            => string.Equals(x.Assembly, y.Assembly, StringComparison.OrdinalIgnoreCase) && string.Equals(x.FullName, y.FullName, StringComparison.Ordinal);

        public int GetHashCode((string Assembly, string FullName) key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Assembly), StringComparer.Ordinal.GetHashCode(key.FullName));
    }

    internal static string MakeNamespace(string? original, int generation)
    {
        var marker = GenerationNamespaceMarker + generation;
        return string.IsNullOrEmpty(original) ? marker : original + "." + marker;
    }

    /// <summary>顶层类型本身或它的任意嵌套类型是不是「注入候选」。</summary>
    private static bool NeedsRename(TypeDefinition type)
        => IsInjectionCandidate(type) || type.NestedTypes.Any(NeedsRename);

    /// <summary>类继承链上有 <c>Il2CppObjectBase</c>：这种类要么注入 IL2CPP，要么没法用。</summary>
    internal static bool IsInjectionCandidate(TypeDefinition type)
    {
        if (type.IsInterface || type.IsValueType) return false;

        TypeReference? current = type.BaseType;
        for (var guard = 0; current is not null && guard < 64; guard++)
        {
            if (current.FullName == Il2CppObjectBaseFullName) return true;
            if (current.Scope is AssemblyNameReference scope && BclAssemblyNames.Contains(scope.Name)) return false;

            TypeDefinition? definition;
            try { definition = current.Resolve(); }
            catch (AssemblyResolutionException) { return false; }

            if (definition is null) return false;
            current = definition.BaseType;
        }

        return false;
    }

    /// <summary>
    /// 和 BepInEx 自己的 <c>HasBepinPlugins</c> 一样：程序集引用了 BepInEx.Core 并且用到了 <c>BepInPlugin</c> 特性，才值得逐个类型去查是不是 BasePlugin。
    /// （不能要求它直接引用 BepInEx.Unity.IL2CPP：继承另一个脚本程序集里的抽象插件基类的插件，只会引用那个基类所在的程序集。）
    /// </summary>
    private static bool MayContainPlugins(ModuleDefinition module)
        => module.AssemblyReferences.Any(r => r.Name == "BepInEx.Core")
           && module.GetTypeReferences().Any(r => r.FullName == "BepInEx.BepInPlugin");

    // ------------------------------------------------------------------ 改写

    /// <summary>别的脚本程序集里对被改名类型的引用，跟着改。</summary>
    private static int RewriteTypeReferences(
        ModuleDefinition module,
        Dictionary<(string Assembly, string FullName), string> renameKeys)
    {
        if (renameKeys.Count == 0) return 0;

        var pending = new List<(TypeReference Reference, string NewNamespace)>();
        foreach (var reference in module.GetTypeReferences())
        {
            if (reference.DeclaringType is not null) continue; // 嵌套类型跟着外层走
            if (reference.Scope is not AssemblyNameReference scope) continue;
            if (renameKeys.TryGetValue((scope.Name, reference.FullName), out var newNamespace))
                pending.Add((reference, newNamespace));
        }

        foreach (var (reference, newNamespace) in pending)
            reference.Namespace = newNamespace;

        return pending.Count;
    }

    /// <summary>把 <c>Assembly.get_Location()</c> 的调用点换成 <see cref="ScriptRuntime.GetAssemblyLocation"/>。</summary>
    private static int ShimAssemblyLocation(ModuleDefinition module)
    {
        MethodReference? shim = null;
        var count = 0;

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Call) continue;
                    if (instruction.Operand is not MethodReference callee) continue;
                    if (callee.Name != "get_Location" || callee.DeclaringType.FullName != "System.Reflection.Assembly") continue;

                    // 实例 getter 与静态 shim 的栈形状一致：[Assembly] -> [string]
                    shim ??= module.ImportReference(
                        typeof(ScriptRuntime).GetMethod(nameof(ScriptRuntime.GetAssemblyLocation), new[] { typeof(Assembly) })!);
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = shim;
                    count++;
                }
            }
        }

        return count;
    }

    private static void WriteBack(AssemblyDefinition assembly, ScriptImage image, Action<string> warn)
    {
        var module = assembly.MainModule;
        var dllOut = new MemoryStream();
        var writerParameters = new WriterParameters();
        MemoryStream? pdbOut = null;

        if (module.HasSymbols)
        {
            pdbOut = new MemoryStream();
            writerParameters.WriteSymbols = true;
            writerParameters.SymbolWriterProvider = new PortablePdbWriterProvider();
            writerParameters.SymbolStream = pdbOut;
        }

        assembly.Write(dllOut, writerParameters);

        // 原 PDB 与新 DLL 对不上，必须一起换掉（没有符号就置空）
        image.Dll = dllOut.ToArray();
        image.Pdb = pdbOut?.ToArray();
    }
}
