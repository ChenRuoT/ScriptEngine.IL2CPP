using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace HelloScript;

public enum Greeting
{
    Polite,
    Casual,
}

/// <summary>
/// 最小的可热重载插件：Load() 打日志，Unload() 打日志并返回 true。
/// 改一下 <see cref="Banner"/>，dotnet build，游戏里几秒后就会看到新文字。
/// </summary>
[BepInPlugin("com.shiunaya.samples.hello", "Hello Script", "1.0.0")]
public sealed class HelloPlugin : BasePlugin
{
    // 每个代次一份：脚本的静态字段在每次重载时都是全新的
    private static int _loadCount;

    private const string Banner = "hello from generation B";

    private ConfigEntry<Greeting> _greeting = null!;

    public override void Load()
    {
        _loadCount++;

        // 枚举配置项：每个 BasePlugin 实例有自己的 ConfigFile，重载后不应该出现 InvalidCastException
        _greeting = Config.Bind("General", "Greeting", Greeting.Polite, "Which greeting to use");

        var assembly = Assembly.GetExecutingAssembly();
        Log.LogInfo($"[hello] Load #{_loadCount}: {Banner}, greeting={_greeting.Value}");
        // 脚本是字节流加载的，Assembly.Location 本来是空的 —— 引擎的垫片让它照常工作
        Log.LogInfo($"[hello] Assembly.Location = '{assembly.Location}' (exists={File.Exists(assembly.Location)})");
        Log.LogInfo($"[hello] assembly context = {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly)?.Name}");
        // 带行号的堆栈需要 PDB：这一行故意触发并捕获一个异常来验证
        try { throw new InvalidOperationException("stack trace probe"); }
        catch (Exception ex) { Log.LogInfo($"[hello] stack probe: {ex.StackTrace?.Split('\n')[0].Trim()}"); }
    }

    public override bool Unload()
    {
        Log.LogInfo("[hello] Unload");
        return true;
    }
}
