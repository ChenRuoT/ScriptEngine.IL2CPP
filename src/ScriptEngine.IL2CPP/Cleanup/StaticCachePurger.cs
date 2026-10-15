using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;

namespace ScriptEngineIl2Cpp.Cleanup;

/// <summary>
/// 从 BepInEx / Il2CppInterop 的静态缓存里抹掉指向旧代次程序集的条目。
///
/// 这些库的缓存用 <see cref="Type"/> 当 key（<c>ClassInjector.ourDerivedFields</c>、
/// <c>TomlTypeConverter.TypeConverters</c> 等）。旧脚本里的类型一旦被登记进去，
/// 这些静态字典就会强引用它，旧的 <see cref="System.Runtime.Loader.AssemblyLoadContext"/> 永远卸不掉。
/// 只扫一份白名单类型，避免为了「看看有没有缓存」去触发无关类型的静态构造。
/// </summary>
internal static class StaticCachePurger
{
    internal static readonly (string Type, string Assembly)[] Targets =
    {
        // 注意 Il2CppObjectPool / Il2CppWrapperTypes 在 Il2CppInterop.Runtime.Runtime 命名空间里（两个 Runtime）
        ("Il2CppInterop.Runtime.Injection.ClassInjector", "Il2CppInterop.Runtime"),
        ("Il2CppInterop.Runtime.Injection.InjectorHelpers", "Il2CppInterop.Runtime"),
        ("Il2CppInterop.Runtime.DelegateSupport", "Il2CppInterop.Runtime"),
        ("Il2CppInterop.Runtime.Il2CppClassPointerStore", "Il2CppInterop.Runtime"),   // ourPointerReaders：Type → 读指针的委托
        ("Il2CppInterop.Runtime.Runtime.Il2CppObjectPool", "Il2CppInterop.Runtime"),  // s_cache：指针 → WeakReference<包装对象>
        ("Il2CppInterop.Runtime.Runtime.Il2CppWrapperTypes", "Il2CppInterop.Runtime"), // ourTypes / ourAnyTypes / ourInitializers / ourAssemblies
        ("BepInEx.Unity.IL2CPP.Utils.Collections.Il2CppManagedEnumerator", "BepInEx.Unity.IL2CPP"), // boxers：Type → 装箱委托
        ("BepInEx.Configuration.TomlTypeConverter", "BepInEx.Core"),
    };

    private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>一次清理的结果。</summary>
    internal sealed class PurgeResult
    {
        /// <summary>字段名 → 抹掉的条目数（只含非零项）。</summary>
        public Dictionary<string, int> Removed { get; } = new();

        /// <summary>扫描过的条目总数。</summary>
        public int Scanned { get; set; }

        public long ElapsedMs { get; set; }

        /// <summary>
        /// 从 <c>DelegateSupport</c> 的转换缓存里摘下来的、属于已死程序集的 IL2CPP 委托包装对象。
        /// 交给 <see cref="StaleDelegateNeutralizer"/> 去把它们背后的托管处理器换成空操作。
        /// </summary>
        public List<Il2CppObjectBase> DeadDelegateWrappers { get; } = new();
    }

    /// <summary>
    /// <see cref="Remove"/>：抹掉所有指向已死程序集的条目。
    /// <see cref="ScanDelegates"/>：只看 <c>DelegateSupport</c> 的转换缓存，把已死程序集的 IL2CPP 委托包装对象收集起来，<b>不</b>删任何条目
    /// （插件 Unload() 里 Destroy 掉的组件，要到帧尾它的 OnDestroy 才会 -= 退订，退订要靠这些缓存条目找到同一个 IL2CPP 委托）。
    /// </summary>
    internal enum PurgeMode
    {
        Remove,
        ScanDelegates,
    }

    public static PurgeResult Purge(IReadOnlyCollection<Assembly> deadAssemblies, PurgeMode mode = PurgeMode.Remove)
    {
        var result = new PurgeResult();
        if (deadAssemblies.Count == 0) return result;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var dead = new HashSet<Assembly>(deadAssemblies);

        foreach (var (typeName, assemblyName) in Targets)
        {
            Type? type;
            try { type = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false); }
            catch { type = null; }
            if (type is null) continue;
            if (mode == PurgeMode.ScanDelegates && type.Name != "DelegateSupport") continue;

            foreach (var field in type.GetFields(AllStatic))
            {
                var isDictionary = typeof(IDictionary).IsAssignableFrom(field.FieldType);
                var isWeakTable = IsConditionalWeakTable(field.FieldType);
                if (!isDictionary && !isWeakTable) continue;

                try
                {
                    var value = field.GetValue(null);
                    if (value is null) continue;

                    // 只有 DelegateSupport 的转换缓存里存的是「IL2CPP 委托包装对象」，只从那里收集
                    var wrappers = type.Name == "DelegateSupport" ? result.DeadDelegateWrappers : null;
                    var remove = mode == PurgeMode.Remove;
                    var removed = isDictionary
                        ? PurgeDictionary((IDictionary)value, dead, out var scanned, wrappers, remove)
                        : PurgeWeakTable(value, dead, out scanned, wrappers, remove);
                    result.Scanned += scanned;
                    if (removed > 0 && remove) result.Removed[$"{type.Name}.{field.Name}"] = removed;
                }
                catch
                {
                    // 缓存清理是尽力而为：版本不匹配、字段被改名都不该影响重载
                }
            }
        }

        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static int PurgeDictionary(IDictionary dictionary, HashSet<Assembly> dead, out int scanned, List<Il2CppObjectBase>? wrappers = null, bool remove = true)
    {
        List<(object Key, object? Value)>? doomed = null;
        scanned = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            scanned++;
            if (!Belongs(entry.Key, dead, 0) && !Belongs(entry.Value, dead, 0)) continue;
            (doomed ??= new List<(object, object?)>()).Add((entry.Key, entry.Value));
        }

        if (doomed is null) return 0;
        foreach (var (key, value) in doomed)
        {
            if (remove) dictionary.Remove(key);
            if (wrappers is not null) CollectWrappers(value, wrappers, 0);
        }

        return doomed.Count;
    }

    /// <summary>从缓存条目的值里取出 IL2CPP 包装对象（值可能是包装对象本身，也可能是「(方法, 类型) → 包装对象」之类的字典）。</summary>
    private static void CollectWrappers(object? value, List<Il2CppObjectBase> into, int depth)
    {
        switch (value)
        {
            case Il2CppObjectBase wrapper:
                into.Add(wrapper);
                break;
            case IDictionary dictionary when depth < 2:
                foreach (DictionaryEntry entry in dictionary)
                    CollectWrappers(entry.Value, into, depth + 1);
                break;
        }
    }

    private static bool IsConditionalWeakTable(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>);

    /// <summary>
    /// <c>DelegateSupport.ConvertedByTarget</c> / <c>ConvertedDelegates</c> 是 <see cref="ConditionalWeakTable{TKey,TValue}"/>：
    /// key（订阅事件用的处理器的 Target，即插件实例）是弱引用，看上去不会钉住谁。
    /// 但 value 里缓存的 IL2CPP 委托包装对象 → 原生委托 → 注入类 Il2CppToMonoDelegateReference → 一个永久的 CoreCLR GCHandle →
    /// 托管委托 → Target（就是那个 key）：跨两个 GC 的环，谁也收不掉，哪怕事件早就 -= 退订了。
    /// 把条目从表里摘掉，才有机会让包装对象被终结 → IL2CPP 侧委托被回收 → 它的终结器释放 GCHandle → 旧上下文能被收走。
    /// </summary>
    internal static int PurgeWeakTable(object table, HashSet<Assembly> dead, out int scanned, List<Il2CppObjectBase>? wrappers = null, bool remove = true)
    {
        scanned = 0;
        PropertyInfo? keyProperty = null;
        PropertyInfo? valueProperty = null;
        List<(object Key, object? Value)>? doomed = null;

        foreach (var entry in (IEnumerable)table) // 装箱的 KeyValuePair<TKey, TValue>
        {
            scanned++;
            keyProperty ??= entry.GetType().GetProperty("Key");
            valueProperty ??= entry.GetType().GetProperty("Value");

            var key = keyProperty?.GetValue(entry);
            if (key is null) continue;

            var value = valueProperty?.GetValue(entry);
            if (!Belongs(key, dead, 0) && !Belongs(value, dead, 0)) continue;

            (doomed ??= new List<(object, object?)>()).Add((key, value));
        }

        if (doomed is null) return 0;

        var removeMethod = table.GetType().GetMethod("Remove", new[] { table.GetType().GetGenericArguments()[0] })!;
        foreach (var (key, value) in doomed)
        {
            if (remove) removeMethod.Invoke(table, new[] { key });
            if (wrappers is not null) CollectWrappers(value, wrappers, 0);
        }

        return doomed.Count;
    }

    /// <summary>这个对象（或它直接包着的类型/方法/元组元素）是否属于已死程序集。</summary>
    internal static bool Belongs(object? value, HashSet<Assembly> dead, int depth)
    {
        if (value is null || depth > 3) return false;

        switch (value)
        {
            case Type type:
                return TypeBelongs(type, dead);
            case MemberInfo member:
                return member.DeclaringType is { } declaring && TypeBelongs(declaring, dead);
            case Assembly assembly:
                return dead.Contains(assembly);
            case WeakReference<Il2CppObjectBase> weak:
                // Il2CppObjectPool.s_cache 的值：包装对象的类型属于已死程序集（注入类型的旧实例）就算
                return weak.TryGetTarget(out var wrapped) && TypeBelongs(wrapped.GetType(), dead);
            case Delegate del:
                return Belongs(del.Method, dead, depth + 1) || (del.Target is { } target && TypeBelongs(target.GetType(), dead));
            case ITuple tuple:
                for (var i = 0; i < tuple.Length; i++)
                    if (Belongs(tuple[i], dead, depth + 1)) return true;
                return false;
            case Array array when !array.GetType().GetElementType()!.IsPrimitive:
                foreach (var item in array)
                    if (Belongs(item, dead, depth + 1)) return true;
                return false;
            case string:
            case IntPtr: // 指针当 key 的缓存（Il2CppObjectPool.s_cache 等）条目极多，别为它们走反射
                return false;
            default:
                return TypeBelongs(value.GetType(), dead);
        }
    }

    private static bool TypeBelongs(Type type, HashSet<Assembly> dead)
    {
        if (dead.Contains(type.Assembly)) return true;

        if (type.HasElementType && type.GetElementType() is { } element && TypeBelongs(element, dead)) return true;

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (var argument in type.GetGenericArguments())
                if (TypeBelongs(argument, dead)) return true;
        }

        return false;
    }
}
