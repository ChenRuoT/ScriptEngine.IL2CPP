using System.Reflection;
using System.Runtime.CompilerServices;

namespace ScriptEngineIl2Cpp;

/// <summary>
/// 引擎对脚本公开的一点点运行时信息。
///
/// 脚本程序集是以字节流加载的，<see cref="Assembly.Location"/> 恒为空串。
/// 引擎在改写脚本 IL 时，把所有 <c>Assembly.get_Location()</c> 调用替换成
/// <see cref="GetAssemblyLocation"/>，于是 <c>Assembly.GetExecutingAssembly().Location</c>
/// 之类的老写法在脚本里照常能拿到磁盘上的 DLL 路径 —— 插件不用为热重载改代码。
///
/// 这个类型是 public 的，因为改写后的脚本程序集要直接调用它。
/// </summary>
public static class ScriptRuntime
{
    private static readonly ConditionalWeakTable<Assembly, string> Locations = new();

    /// <summary>当前（最新一次装载的）脚本代次，从 1 开始；还没装载过则为 0。</summary>
    public static int Generation { get; internal set; }

    /// <summary>
    /// 返回程序集的磁盘路径。脚本程序集返回引擎登记的原始 DLL 路径，其它程序集退回 <see cref="Assembly.Location"/>。
    /// </summary>
    public static string GetAssemblyLocation(Assembly assembly)
    {
        if (assembly is null) return assembly!.Location; // 保持与原调用一致：抛 NullReferenceException

        return Locations.TryGetValue(assembly, out var path) ? path : assembly.Location;
    }

    /// <summary>该程序集是否由脚本引擎加载。</summary>
    public static bool IsScriptAssembly(Assembly assembly) => Locations.TryGetValue(assembly, out _);

    internal static void RegisterLocation(Assembly assembly, string path) => Locations.AddOrUpdate(assembly, path);
}
