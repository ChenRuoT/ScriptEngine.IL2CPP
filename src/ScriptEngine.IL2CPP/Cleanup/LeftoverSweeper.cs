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
/// 在 EFT 这种体量下一次约 20 ms，所以不能每个注入类型扫一遍；
/// 改成按基类（MonoBehaviour / ScriptableObject）各扫一次，再用原生 class 指针比对挑出我们的类型。
/// （用 FindObjectsOfTypeAll 而不是 FindObjectsOfType：后者看不到 HideAndDontSave 的对象，
/// 而 BepInEx 的 <c>BepInEx_Manager</c> 恰好就是。）
/// </summary>
internal static class LeftoverSweeper
{
    /// <summary>
    /// <c>ClassInjector.IsManagedTypeInjected(Type)</c>（internal）：只查 <c>InjectedTypes</c> 里有没有这个类型的全名，没有任何副作用。
    /// 不用 public 的 <c>IsTypeRegisteredInIl2Cpp</c>：它第一行就调 <c>Il2CppClassPointerStore.GetNativeClassPointer(type)</c>，
    /// 会触发这个类型的静态构造函数，并把「运行期从未注入过」的类型也塞进 <c>ourPointerReaders</c>（强引用 Type → 钉住旧上下文）。
    /// </summary>
    private static readonly Func<Type, bool>? IsInjectedByName = CreateInjectedCheck();

    private static Func<Type, bool>? CreateInjectedCheck()
    {
        try
        {
            var method = typeof(ClassInjector).GetMethod(
                "IsManagedTypeInjected", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
            return method is null ? null : (Func<Type, bool>)Delegate.CreateDelegate(typeof(Func<Type, bool>), method);
        }
        catch
        {
            return null; // Il2CppInterop 版本变了：退回 public API（有副作用，但能用）
        }
    }

    /// <returns>销毁的对象数；<paramref name="details"/> 里是 “类型名 ×数量”。</returns>
    public static int Sweep(IEnumerable<Assembly> assemblies, List<string> details)
    {
        var targets = CollectTargets(assemblies);
        if (targets.Count == 0) return 0;

        var counts = new Dictionary<Type, int>();
        var total = 0;

        foreach (var group in targets.Values.GroupBy(ScanBase))
        {
            var scanType = group.Key;
            var wanted = targets.Where(t => group.Contains(t.Value)).ToDictionary(t => t.Key, t => t.Value);
            total += ScanAndDestroy(Il2CppType.From(scanType), wanted, counts);
        }

        AppendDetails(counts, details);
        return total;
    }

    /// <summary>
    /// 给 <paramref name="holder"/>（BepInEx 的管理对象，所有插件的 <c>AddComponent&lt;T&gt;()</c> 都挂在它上面）上的组件拍一张快照，
    /// 只记原生指针，不创建任何包装对象。插件 <c>Load()</c> 之前拍，<c>Load()</c> 抛异常之后用 <see cref="SweepNewComponents"/> 对照。
    /// </summary>
    public static HashSet<IntPtr> SnapshotComponents(GameObject? holder)
    {
        var pointers = new HashSet<IntPtr>();
        if (holder == null) return pointers;

        var components = holder.GetComponents<Component>();
        var offset = 4 * IntPtr.Size; // Il2CppArray 头：klass, monitor, bounds, max_length
        for (var i = 0; i < components.Length; i++)
        {
            var pointer = Marshal.ReadIntPtr(components.Pointer, offset + i * IntPtr.Size);
            if (pointer != IntPtr.Zero) pointers.Add(pointer);
        }

        GC.KeepAlive(components);
        return pointers;
    }

    /// <summary>
    /// 插件 <c>Load()</c> 抛异常后，销毁它在崩溃前挂到管理对象上的注入组件：
    /// 管理对象上「<paramref name="before"/> 快照里没有、现在有、并且是 <paramref name="assemblies"/> 里注入类型的实例」。
    /// 比按程序集整体清扫安全：同一个程序集里别的、已经成功加载的插件的组件不会被误杀。
    /// </summary>
    public static int SweepNewComponents(GameObject? holder, HashSet<IntPtr> before, IEnumerable<Assembly> assemblies, List<string> details)
    {
        if (holder == null) return 0;

        var targets = CollectTargets(assemblies);
        if (targets.Count == 0) return 0;

        var counts = new Dictionary<Type, int>();
        var total = 0;

        foreach (var pointer in SnapshotComponents(holder).Where(p => !before.Contains(p)).ToList())
        {
            var classPointer = Marshal.ReadIntPtr(pointer); // Il2CppObject.klass
            if (!targets.TryGetValue(classPointer, out var type)) continue;

            if (Destroy(pointer))
            {
                total++;
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
        }

        AppendDetails(counts, details);
        return total;
    }

    // ------------------------------------------------------------------ 内部

    /// <summary>
    /// 给定程序集里<b>实际被注入过</b>的 Unity 对象类型的个数（只做按名检查，没有任何副作用）。
    /// 非零 = Il2CppInterop 会永久钉住这一代的加载上下文，回收不了。
    /// </summary>
    public static int CountInjectedTypes(IEnumerable<Assembly> assemblies)
        => assemblies.SelectMany(GetTypesSafe).Count(IsInjectedUnityObjectType);

    /// <summary>原生 class 指针 → 托管类型：给定程序集里<b>确实被注入过</b>的 Unity 对象类型。</summary>
    internal static Dictionary<IntPtr, Type> CollectTargets(IEnumerable<Assembly> assemblies)
    {
        var targets = new Dictionary<IntPtr, Type>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in GetTypesSafe(assembly))
            {
                try
                {
                    // 先用无副作用的按名检查把「没注入过」的类型筛掉，再去读原生指针
                    // （读指针会触发 Il2CppClassPointerStore<T> 的静态构造，进而跑 T 自己的静态构造函数；单个类型出错不能让整个清扫作废）
                    if (!IsInjectedUnityObjectType(type)) continue;

                    var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(type);
                    if (classPointer != IntPtr.Zero) targets[classPointer] = type;
                }
                catch
                {
                    // 尽力而为
                }
            }
        }

        return targets;
    }

    private static bool IsInjectedUnityObjectType(Type type)
    {
        if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(Object).IsAssignableFrom(type)) return false;

        try
        {
            return IsInjectedByName?.Invoke(type) ?? ClassInjector.IsTypeRegisteredInIl2Cpp(type);
        }
        catch
        {
            return false;
        }
    }

    private static void AppendDetails(Dictionary<Type, int> counts, List<string> details)
    {
        foreach (var (type, count) in counts.OrderBy(c => c.Key.FullName))
            details.Add($"{type.FullName} x{count}");
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

        // 数组要钉住直到销毁结束：victims 里只有裸指针，数组一旦被回收，里面的对象在 IL2CPP GC 眼里就可能不可达
        var destroyed = 0;
        foreach (var (pointer, type) in victims)
        {
            if (!Destroy(pointer)) continue;

            destroyed++;
            counts[type] = counts.GetValueOrDefault(type) + 1;
        }

        GC.KeepAlive(all);
        return destroyed;
    }

    /// <summary>销毁一个原生 Unity 对象。已经被销毁（Unity 的“假 null”）算没销毁。</summary>
    private static bool Destroy(IntPtr pointer)
    {
        try
        {
            var instance = new Object(pointer);
            if (instance == null) return false;

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

            return true;
        }
        catch
        {
            return false; // 尽力而为
        }
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
