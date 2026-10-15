using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace ScriptEngineIl2Cpp.Cleanup;

/// <summary>
/// 「谁在钉住旧代次？」的诊断工具：从已加载程序集的静态字段出发，沿着对象图（字段 / 集合元素 / 字典键值 / 委托）找出
/// 指向已死程序集的引用链，把链路打印出来（例如 <c>HarmonyLib.HarmonySharedState.state → Dictionary key → RuntimeMethodInfo(PatchScript.Target.Compute)</c>）。
///
/// 局限（所以它只是诊断，不是清理手段）：
/// <list type="bullet">
/// <item>只看得见静态字段能走到的引用；<c>GCHandle</c>（Il2CppInterop 的注入类 trampoline、<c>Marshal.GetFunctionPointerForDelegate</c> 等）看不见；</item>
/// <item>ConditionalWeakTable 的条目不当作根（key 没人拿着就不会钉住 value）；</item>
/// <item>遍历有节点数和时间上限，超限会提前结束并在结果里标明；</item>
/// <item>读静态字段会触发还没初始化的类型的静态构造 —— 所以默认关闭，只在 <c>Advanced / DiagnosePinned</c> 打开时、或离线测试里使用。</item>
/// </list>
/// </summary>
internal static class PinFinder
{
    internal sealed class Result
    {
        public List<string> Paths { get; } = new();
        public int VisitedObjects { get; set; }
        public bool Truncated { get; set; }
        public long ElapsedMs { get; set; }
    }

    private static readonly string[] DefaultRootAssemblies =
    {
        "0Harmony", "MonoMod.Core", "MonoMod.RuntimeDetour", "MonoMod.Utils", "MonoMod.ILHelpers", "MonoMod.Backports", "Mono.Cecil",
        "Il2CppInterop.Runtime", "Il2CppInterop.Common", "Il2CppInterop.HarmonySupport",
        "BepInEx.Core", "BepInEx.Unity.IL2CPP", "BepInEx.Unity.Common", "BepInEx.Preloader.Core",
        "ScriptEngine.IL2CPP",
    };

    private const BindingFlags AllMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static Result Find(
        IReadOnlyCollection<Assembly> deadAssemblies,
        int maxPaths = 6,
        int maxObjects = 400_000,
        int timeBudgetMs = 3000,
        IEnumerable<string>? rootAssemblyNames = null)
    {
        var result = new Result();
        var stopwatch = Stopwatch.StartNew();
        var dead = new HashSet<Assembly>(deadAssemblies);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var roots = new HashSet<string>(rootAssemblyNames ?? DefaultRootAssemblies, StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AssemblyLoadContext.Default.Assemblies.Where(a => roots.Contains(a.GetName().Name ?? "")))
        {
            foreach (var type in SafeTypes(assembly))
            {
                if (type.IsGenericTypeDefinition) continue;

                foreach (var field in SafeFields(type))
                {
                    if (!field.IsStatic || field.FieldType.IsPointer || field.FieldType.IsByRef) continue;
                    if (Exceeded(result, stopwatch, maxObjects, timeBudgetMs, maxPaths)) goto done;

                    object? value;
                    try { value = field.GetValue(null); }
                    catch { continue; }

                    if (value is null) continue;
                    Walk(value, $"{type.FullName}.{field.Name}", 0, dead, visited, result, stopwatch, maxObjects, timeBudgetMs, maxPaths);
                }
            }
        }

    done:
        result.VisitedObjects = visited.Count;
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool Exceeded(Result result, Stopwatch stopwatch, int maxObjects, int timeBudgetMs, int maxPaths)
    {
        if (result.Paths.Count >= maxPaths) return true;
        if (result.Truncated) return true;
        if (stopwatch.ElapsedMilliseconds > timeBudgetMs || result.VisitedObjects > maxObjects)
        {
            result.Truncated = true;
            return true;
        }

        return false;
    }

    private static void Walk(
        object start, string startPath, int startDepth, HashSet<Assembly> dead, HashSet<object> visited,
        Result result, Stopwatch stopwatch, int maxObjects, int timeBudgetMs, int maxPaths)
    {
        // 显式栈，避免深链表把调用栈打爆
        var stack = new Stack<(object Value, string Path, int Depth)>();
        stack.Push((start, startPath, startDepth));

        while (stack.Count > 0)
        {
            if (Exceeded(result, stopwatch, maxObjects, timeBudgetMs, maxPaths)) return;

            var (value, path, depth) = stack.Pop();
            result.VisitedObjects = visited.Count;

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string || value is Pointer) continue;
            if (type.IsPointer) continue;

            // 终点：指向已死程序集的引用
            if (HitsDeadAssembly(value, dead, out var what))
            {
                result.Paths.Add($"{path}  ==>  {what}");
                continue;
            }

            if (depth >= 8) continue;
            if (!type.IsValueType && !visited.Add(value)) continue;

            // 叶子：Type / MemberInfo 等反射对象只检查，不展开（展开会钻进运行时内部）
            if (value is MemberInfo or Assembly or Module or AssemblyLoadContext) continue;
            if (value is ConditionalWeakTable<object, object>) continue;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>)) continue;

            switch (value)
            {
                case IDictionary dictionary:
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is not null) stack.Push((entry.Key, $"{path} → key", depth + 1));
                        if (entry.Value is not null) stack.Push((entry.Value, $"{path} → [{DescribeKey(entry.Key)}]", depth + 1));
                    }
                    break;

                case Array array when !type.GetElementType()!.IsPrimitive && !type.GetElementType()!.IsPointer:
                    if (array.Length > 50_000) break; // 巨型数组（缓冲区之类）不展开
                    foreach (var item in array)
                        if (item is not null) stack.Push((item, $"{path} → [i]", depth + 1));
                    break;

                case IEnumerable enumerable when value is not string && IsCollectionLike(type):
                    var count = 0;
                    foreach (var item in enumerable)
                    {
                        if (++count > 50_000) break;
                        if (item is not null) stack.Push((item, $"{path} → item", depth + 1));
                    }
                    break;

                default:
                    PushFields(value, type, path, depth, stack);
                    break;
            }
        }
    }

    private static void PushFields(object value, Type type, string path, int depth, Stack<(object, string, int)> stack)
    {
        // 委托只看 Target（Method 在 HitsDeadAssembly 里查过了）
        if (value is Delegate del)
        {
            if (del.Target is not null) stack.Push((del.Target, $"{path} → Delegate.Target", depth + 1));
            return;
        }

        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            // 运行时/BCL 内部类型不钻（几乎全是噪声，且可能触发不安全的反射）
            if (t.Namespace is { } ns && (ns.StartsWith("System.Reflection", StringComparison.Ordinal) || ns.StartsWith("System.Runtime", StringComparison.Ordinal)
                                          || ns.StartsWith("System.Threading", StringComparison.Ordinal))) continue;

            foreach (var field in SafeFields(t))
            {
                if (field.IsStatic || field.FieldType.IsPointer || field.FieldType.IsByRef || field.FieldType.IsPrimitive) continue;

                object? child;
                try { child = field.GetValue(value); }
                catch { continue; }

                if (child is not null) stack.Push((child, $"{path} → {t.Name}.{field.Name}", depth + 1));
            }
        }
    }

    private static bool IsCollectionLike(Type type)
        => type.Namespace is { } ns && (ns.StartsWith("System.Collections", StringComparison.Ordinal) || ns == "System.Linq")
           || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() is var d
               && (d == typeof(ICollection<>) || d == typeof(IReadOnlyCollection<>)));

    private static string DescribeKey(object? key) => key switch
    {
        null => "null",
        Type type => type.FullName ?? type.Name,
        MemberInfo member => $"{member.DeclaringType?.FullName}.{member.Name}",
        _ => key.GetType().Name,
    };

    /// <summary>这个对象本身（或它直接包着的类型 / 方法 / 程序集）是否属于已死程序集。</summary>
    private static bool HitsDeadAssembly(object value, HashSet<Assembly> dead, out string what)
    {
        what = "";
        switch (value)
        {
            case Type type when TypeBelongs(type, dead):
                what = $"Type {type.FullName}";
                return true;
            case MemberInfo member when member.DeclaringType is { } declaring && TypeBelongs(declaring, dead):
                what = $"{member.GetType().Name} {declaring.FullName}.{member.Name}";
                return true;
            case Assembly assembly when dead.Contains(assembly):
                what = $"Assembly {assembly.GetName().Name}";
                return true;
            case Delegate del when del.GetInvocationList().Any(d => d.Method.DeclaringType is { } t && TypeBelongs(t, dead)
                                                                    || d.Target is { } target && TypeBelongs(target.GetType(), dead)):
                what = $"Delegate {del.Method.DeclaringType?.FullName}.{del.Method.Name}";
                return true;
            case null:
                return false;
        }

        var valueType = value.GetType();
        if (TypeBelongs(valueType, dead))
        {
            what = $"instance of {valueType.FullName}";
            return true;
        }

        return false;
    }

    private static bool TypeBelongs(Type type, HashSet<Assembly> dead)
    {
        if (dead.Contains(type.Assembly)) return true;
        if (type.HasElementType && type.GetElementType() is { } element && TypeBelongs(element, dead)) return true;
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            foreach (var argument in type.GetGenericArguments())
                if (TypeBelongs(argument, dead)) return true;

        return false;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
        catch { return Array.Empty<Type>(); }
    }

    private static IEnumerable<FieldInfo> SafeFields(Type type)
    {
        try { return type.GetFields(AllMembers); }
        catch { return Array.Empty<FieldInfo>(); }
    }
}
