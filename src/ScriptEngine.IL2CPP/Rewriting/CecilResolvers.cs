using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using Mono.Cecil;

namespace ScriptEngineIl2Cpp.Rewriting;

/// <summary>
/// 长寿命的 Cecil 解析器：负责 BepInEx / interop / 运行时程序集。
/// 跨重载复用，免得每次重载都重新读一遍 UnityEngine.CoreModule、Il2Cppmscorlib 这些大文件。
/// </summary>
internal static class SharedCecilResolver
{
    private static DefaultAssemblyResolver? _instance;

    public static IAssemblyResolver Instance => _instance ??= Create();

    private static DefaultAssemblyResolver Create()
    {
        var resolver = new DefaultAssemblyResolver();

        void Add(string? dir)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                resolver.AddSearchDirectory(dir);
        }

        Add(Paths.BepInExAssemblyDirectory);                          // BepInEx/core
        Add(Path.Combine(Paths.BepInExRootPath, "interop"));          // IL2CPP 代理程序集
        Add(RuntimeEnvironment.GetRuntimeDirectory());                // System.* 等运行时程序集
        Add(Paths.PluginPath);
        try
        {
            // 插件常把依赖放在自己的子目录里（plugins/sptushonka/…），整棵树都加进来
            foreach (var dir in Directory.GetDirectories(Paths.PluginPath, "*", SearchOption.AllDirectories))
                Add(dir);
        }
        catch (IOException) { /* 目录遍历失败不致命 */ }

        return resolver;
    }
}

/// <summary>
/// 单次重载用的解析器：先在本批脚本里找（互相引用），找不到再交给共享解析器。
/// </summary>
internal sealed class GenerationResolver : IAssemblyResolver
{
    private readonly IAssemblyResolver _shared;
    private readonly Dictionary<string, AssemblyDefinition> _scripts = new(StringComparer.OrdinalIgnoreCase);

    public GenerationResolver(IAssemblyResolver shared) => _shared = shared;

    public void Register(AssemblyDefinition assembly) => _scripts[assembly.Name.Name] = assembly;

    // Cecil 解析 TypeReference 时调用的是单参数重载（MetadataResolver → IAssemblyResolver.Resolve(name)）。
    // 这里必须原样转给共享解析器的单参数重载：DefaultAssemblyResolver 只在这个重载里查/写自己的缓存，
    // 双参数重载直接走基类 BaseAssemblyResolver —— 每次都重新打开磁盘上的 interop DLL，
    // 返回的 AssemblyDefinition 又没人 Dispose（文件句柄泄漏，重载越来越慢）。
    public AssemblyDefinition Resolve(AssemblyNameReference name)
        => _scripts.TryGetValue(name.Name, out var script) ? script : _shared.Resolve(name);

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        => _scripts.TryGetValue(name.Name, out var script) ? script : _shared.Resolve(name, parameters);

    // 共享解析器不归这里管
    public void Dispose() { }
}
