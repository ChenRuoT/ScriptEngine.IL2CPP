using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace ScriptEngineIl2Cpp.Cleanup;

/// <summary>
/// 把旧代次交给游戏的「事件处理器 / 回调」（Il2CppInterop 转换出来的 IL2CPP 委托）背后的托管处理器换成空操作。
///
/// 背景：脚本里 <c>SomeEvent += handler</c> 时，Il2CppInterop 会造一个 IL2CPP 委托，它的 <c>m_target</c> 是一个注入类
/// <c>Il2CppToMonoDelegateReference</c> 的实例，实例里的 <c>ReferencedDelegate</c> 字段指着托管处理器 —— 也就指着旧代次的代码。
/// 这个实例是 IL2CPP 对象 ↔ CoreCLR 包装对象互相强引用的一对（IL2CPP 句柄 + CoreCLR GCHandle），两边的 GC 都收不掉它，
/// 所以哪怕事件早就 <c>-=</c> 退订了：
/// <list type="bullet">
/// <item>旧代次的程序集仍被 <c>ReferencedDelegate</c> 钉住（只订阅了事件、没有注入类型的脚本本来是可以被回收的）；</item>
/// <item>忘了退订的处理器会继续在事件触发时执行<b>旧代次的代码</b>，和新代次的处理器同时跑。</item>
/// </list>
/// 换成空操作之后：旧程序集不再被引用；忘了退订的处理器被触发时只会打一行警告（每个处理器一次）就被忽略。
/// 这一步必须在插件 <c>Unload()</c>（以及被它 <c>Destroy</c> 的组件的 <c>OnDestroy</c>）之后做 ——
/// 它们退订时要用转换缓存找到<b>同一个</b> IL2CPP 委托。
/// </summary>
internal static class StaleDelegateNeutralizer
{
    internal sealed class Result
    {
        public int Neutralized { get; set; }

        /// <summary>不是指着已死程序集的（已经被换成空操作的、别的代次的……）或者 Il2CppInterop 内部布局不认识的。</summary>
        public int Skipped { get; set; }
    }

    /// <param name="wrappers">已从 <c>DelegateSupport</c> 转换缓存里摘下来的、属于已死程序集的 IL2CPP 委托包装对象。</param>
    /// <param name="dead">已死的程序集（只改写引用它们的处理器）。</param>
    /// <param name="onStaleInvoked">空操作被触发时的回调，参数是被忽略的处理器的描述。</param>
    public static Result Neutralize(IEnumerable<Il2CppObjectBase> wrappers, HashSet<Assembly> dead, Action<string> onStaleInvoked)
    {
        var result = new Result();
        foreach (var wrapper in wrappers)
        {
            try
            {
                if (TryNeutralize(wrapper, dead, onStaleInvoked)) result.Neutralized++;
                else result.Skipped++;
            }
            catch
            {
                result.Skipped++; // 尽力而为：Il2CppInterop 的内部布局变了就放弃，别影响重载
            }
        }

        return result;
    }

    private static bool TryNeutralize(Il2CppObjectBase wrapper, HashSet<Assembly> dead, Action<string> onStaleInvoked)
    {
        if (wrapper is not Il2CppSystem.Delegate nativeDelegate) return false;

        // m_target 就是 Il2CppToMonoDelegateReference 的实例：它是注入类，Il2CppObjectPool 对注入类直接返回托管对象本身
        object? reference = nativeDelegate.m_target;
        if (reference is null) return false;

        var field = reference.GetType().GetField("ReferencedDelegate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(reference) is not Delegate managed) return false;

        // 只动指着已死程序集的；委托类型本身就是脚本里定义的话，编译出来的空操作又会引用脚本类型，反而钉住，也不处理
        if (!managed.GetInvocationList().Any(d => StaticCachePurger.Belongs(d, dead, 0))) return false;
        if (StaticCachePurger.Belongs(managed.GetType(), dead, 0)) return false;

        var method = managed.Method;
        var generation = method.DeclaringType is { } declaring
            ? System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(declaring.Assembly)?.Name
            : null;
        var description = $"{generation ?? "?"}: {method.DeclaringType?.FullName}.{method.Name}";
        field.SetValue(reference, CreateNoOp(managed.GetType(), description, onStaleInvoked));
        return true;
    }

    /// <summary>造一个和 <paramref name="delegateType"/> 签名一致的委托：调一下回调，然后返回默认值。</summary>
    private static Delegate CreateNoOp(Type delegateType, string description, Action<string> onStaleInvoked)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();

        Expression notify = Expression.Invoke(Expression.Constant(onStaleInvoked), Expression.Constant(description));
        Expression body = invoke.ReturnType == typeof(void)
            ? notify
            : Expression.Block(notify, Expression.Default(invoke.ReturnType));

        return Expression.Lambda(delegateType, body, parameters).Compile();
    }
}
