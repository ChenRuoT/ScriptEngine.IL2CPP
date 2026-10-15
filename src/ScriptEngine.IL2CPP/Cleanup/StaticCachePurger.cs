using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

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
    private static readonly (string Type, string Assembly)[] Targets =
    {
        ("Il2CppInterop.Runtime.Injection.ClassInjector", "Il2CppInterop.Runtime"),
        ("Il2CppInterop.Runtime.Injection.InjectorHelpers", "Il2CppInterop.Runtime"),
        ("Il2CppInterop.Runtime.DelegateSupport", "Il2CppInterop.Runtime"),
        ("Il2CppInterop.Runtime.Il2CppObjectPool", "Il2CppInterop.Runtime"),
        ("BepInEx.Configuration.TomlTypeConverter", "BepInEx.Core"),
    };

    private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <returns>字段名 → 抹掉的条目数（只含非零项）。</returns>
    public static Dictionary<string, int> Purge(IReadOnlyCollection<Assembly> deadAssemblies)
    {
        var result = new Dictionary<string, int>();
        if (deadAssemblies.Count == 0) return result;

        var dead = new HashSet<Assembly>(deadAssemblies);

        foreach (var (typeName, assemblyName) in Targets)
        {
            Type? type;
            try { type = Type.GetType($"{typeName}, {assemblyName}", throwOnError: false); }
            catch { type = null; }
            if (type is null) continue;

            foreach (var field in type.GetFields(AllStatic))
            {
                if (!typeof(IDictionary).IsAssignableFrom(field.FieldType)) continue;

                try
                {
                    if (field.GetValue(null) is not IDictionary dictionary) continue;
                    var removed = PurgeDictionary(dictionary, dead);
                    if (removed > 0) result[$"{type.Name}.{field.Name}"] = removed;
                }
                catch
                {
                    // 缓存清理是尽力而为：版本不匹配、字段被改名都不该影响重载
                }
            }
        }

        return result;
    }

    private static int PurgeDictionary(IDictionary dictionary, HashSet<Assembly> dead)
    {
        List<object>? doomed = null;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (!Belongs(entry.Key, dead, 0) && !Belongs(entry.Value, dead, 0)) continue;
            (doomed ??= new List<object>()).Add(entry.Key);
        }

        if (doomed is null) return 0;
        foreach (var key in doomed) dictionary.Remove(key);
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
