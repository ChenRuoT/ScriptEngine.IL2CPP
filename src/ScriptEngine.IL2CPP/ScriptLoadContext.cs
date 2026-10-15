using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using ScriptEngineIl2Cpp.Rewriting;

namespace ScriptEngineIl2Cpp;

/// <summary>
/// 一次重载对应一个（可回收的）加载上下文：这一批脚本 DLL 都装在里面，彼此按简单名互相解析；
/// 其余程序集（BepInEx / Il2CppInterop / interop 代理 / BCL）回落到默认上下文，保证
/// <c>BasePlugin</c>、<c>MonoBehaviour</c> 等类型与引擎、游戏看到的是同一份。
///
/// ⚠️ 本类<b>不能</b>持有自己装进来的 <see cref="Assembly"/> 的强引用（比如缓存进字段）：
/// 上下文对象 → 程序集 → 加载器分配器 构成的环会让 CLR 判定这个可回收上下文「仍然可达」，
/// <see cref="AssemblyLoadContext.Unload"/> 之后永远回收不了（离线测试里实测过）。
/// 需要「是否已装载」就查 <see cref="AssemblyLoadContext.Assemblies"/>。
/// </summary>
internal sealed class ScriptLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, ScriptImage> _images;
    private readonly object _gate = new();

    public ScriptLoadContext(int generation, bool collectible, IEnumerable<ScriptImage> images)
        : base($"ScriptEngine.IL2CPP/g{generation}", collectible)
    {
        Generation = generation;
        _images = new Dictionary<string, ScriptImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images) _images.TryAdd(image.AssemblyName, image);
    }

    public int Generation { get; }

    /// <summary>装载本批全部脚本程序集（用字节流，磁盘上的 DLL 不会被锁）。调用方用完要及时放掉返回的列表。</summary>
    public List<Assembly> LoadAll()
    {
        var result = new List<Assembly>(_images.Count);
        foreach (var image in _images.Values)
            result.Add(LoadScript(image));
        return result;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null) return null;

        if (_images.TryGetValue(assemblyName.Name, out var image))
            return LoadScript(image);

        // 显式回落到默认上下文里「已经装好」的同名程序集：
        // BepInEx 的链加载器用 Assembly.LoadFrom 装插件，interop 程序集也是预装的，按简单名找最稳。
        foreach (var candidate in Default.Assemblies)
        {
            if (string.Equals(candidate.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null; // 交给运行时的默认解析（TPA 里的 System.* 等）
    }

    private Assembly LoadScript(ScriptImage image)
    {
        lock (_gate)
        {
            var existing = Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, image.AssemblyName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            using var dll = new MemoryStream(image.Dll, writable: false);
            Assembly assembly;
            if (image.Pdb is null)
            {
                assembly = LoadFromStream(dll);
            }
            else
            {
                using var pdb = new MemoryStream(image.Pdb, writable: false);
                assembly = LoadFromStream(dll, pdb);
            }

            ScriptRuntime.RegisterLocation(assembly, image.Path);
            return assembly;
        }
    }
}
