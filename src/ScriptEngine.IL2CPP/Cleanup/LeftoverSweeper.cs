using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ScriptEngineIl2Cpp.Cleanup;

/// <summary>
/// 插件 <c>Unload()</c> 之后，把旧代次注入的 Unity 对象（MonoBehaviour / ScriptableObject）里
/// 还活着的实例销毁掉。
///
/// 为什么需要：旧代次的 <c>Update()</c> 还挂在 Unity 的对象上，不销毁就会和新代次同时跑 ——
/// 并且 native 对象会持有指向旧托管对象的 GCHandle。
/// 这是兜底：写了完整 <c>Unload()</c> 的插件这里什么也找不到；没写的（<c>Unload()</c> 默认返回 false）也不至于翻车。
///
/// 只清理<b>注入类型的实例</b>；插件自己 new 出来的 GameObject、贴图、Canvas 等仍然要靠插件自己的 <c>Unload()</c>。
///
/// 实现上只做「每个基类一次全表扫描」：<c>Resources.FindObjectsOfTypeAll</c> 每次都要遍历整个 Unity 对象表，
/// 在 EFT 这种体量下一次约 200 ms，所以不能每个注入类型扫一遍；
/// 改成按基类（MonoBehaviour / ScriptableObject）各扫一次，再用原生 class 指针比对挑出我们的类型。
/// （用 FindObjectsOfTypeAll 而不是 FindObjectsOfType：后者看不到 HideAndDontSave 的对象，
/// 而 BepInEx 的 <c>BepInEx_Manager</c> 恰好就是。）
/// </summary>
internal static class LeftoverSweeper
{
    /// <returns>销毁的对象数；<paramref name="details"/> 里是 “类型名 ×数量”。</returns>
    public static int Sweep(IEnumerable<Assembly> assemblies, List<string> details)
    {
        // 原生 class 指针 → 托管类型
        var targets = new Dictionary<IntPtr, Type>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in GetTypesSafe(assembly))
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(Object).IsAssignableFrom(type)) continue;

                try
                {
                    // 注意：这两个调用会触发 Il2CppClassPointerStore<T> 的静态构造，进而跑 T 自己的静态构造函数。
                    // 单个类型的静态构造抛异常，不能让整个清扫作废。
                    if (!ClassInjector.IsTypeRegisteredInIl2Cpp(type)) continue; // 没注册进 IL2CPP 的类型不可能有实例

                    var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
                    if (classPointer != IntPtr.Zero) targets[classPointer] = type;
                }
                catch
                {
                    // 尽力而为
                }
            }
        }

        if (targets.Count == 0) return 0;

        var counts = new Dictionary<Type, int>();
        var total = 0;

        foreach (var group in targets.Values.GroupBy(ScanBase))
        {
            var scanType = group.Key;
            var wanted = targets.Where(t => group.Contains(t.Value)).ToDictionary(t => t.Key, t => t.Value);
            total += ScanAndDestroy(Il2CppType.From(scanType), wanted, counts);
        }

        foreach (var (type, count) in counts.OrderBy(c => c.Key.FullName))
            details.Add($"{type.FullName} x{count}");

        return total;
    }

    /// <summary>用哪个原生基类做全表扫描：越窄越快。</summary>
    private static Type ScanBase(Type type)
    {
        if (typeof(MonoBehaviour).IsAssignableFrom(type)) return typeof(MonoBehaviour);
        if (typeof(ScriptableObject).IsAssignableFrom(type)) return typeof(ScriptableObject);
        return type; // 其它 Unity 对象（罕见）：只能按自己的类型扫
    }

    private static int ScanAndDestroy(Il2CppSystem.Type scanType, Dictionary<IntPtr, Type> wanted, Dictionary<Type, int> counts)
    {
        var all = Resources.FindObjectsOfTypeAll(scanType);

        // 先收集再销毁：销毁过程中 OnDestroy 可能改动对象表
        var victims = new List<(IntPtr Pointer, Type Type)>();
        var arrayPointer = all.Pointer;
        var elementOffset = 4 * IntPtr.Size; // Il2CppArray 头：klass, monitor, bounds, max_length
        var length = all.Length;

        for (var i = 0; i < length; i++)
        {
            var objectPointer = Marshal.ReadIntPtr(arrayPointer, elementOffset + i * IntPtr.Size);
            if (objectPointer == IntPtr.Zero) continue;

            var classPointer = Marshal.ReadIntPtr(objectPointer); // Il2CppObject.klass
            if (wanted.TryGetValue(classPointer, out var type))
                victims.Add((objectPointer, type));
        }

        // 先把数组钉住直到销毁结束：victims 里只有裸指针，数组一旦被回收，里面的对象在 IL2CPP GC 眼里就可能不可达
        var destroyed = 0;
        foreach (var (pointer, type) in victims)
        {
            try
            {
                var instance = new Object(pointer);
                if (instance == null) continue; // Unity 的“已销毁”判空

                try
                {
                    // DestroyImmediate：旧代次的 OnDestroy 必须在新代次 Load() 之前跑完，
                    // 否则 “旧 OnDestroy 清理全局状态” 会和 “新 Load 刚建好的状态” 打架。
                    Object.DestroyImmediate(instance);
                }
                catch
                {
                    Object.Destroy(instance);
                }

                destroyed++;
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
            catch
            {
                // 尽力而为
            }
        }

        GC.KeepAlive(all);
        return destroyed;
    }

    internal static IEnumerable<Type> GetTypesSafe(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var list = new List<Type>();
            foreach (var type in ex.Types)
                if (type is not null) list.Add(type);
            return list;
        }
    }
}
