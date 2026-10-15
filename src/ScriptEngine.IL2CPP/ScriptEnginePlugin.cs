using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace ScriptEngineIl2Cpp;

/// <summary>
/// ScriptEngine.IL2CPP 入口：一个普通的、常驻的 BepInEx 6 IL2CPP 插件（放 BepInEx/plugins/），永不重载。
///
/// 它监视 <c>BepInEx/scripts/</c>，把里面的 <see cref="BasePlugin"/> DLL 装进可回收的加载上下文，
/// 并在热键 / 文件变化 / 命令触发时卸旧装新。详见 README。
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ScriptEnginePlugin : BasePlugin
{
    public const string PluginGuid = "com.shiunaya.bepinex.scriptengine.il2cpp";
    public const string PluginName = "ScriptEngine.IL2CPP";
    public const string PluginVersion = ReleaseVersion.Value;

    internal static ManualLogSource Logger { get; private set; } = null!;

    public override void Load()
    {
        Logger = Log;

        var settings = new EngineSettings(Config);
        var host = new ScriptHost(Log, settings);

        EngineBehaviour.Pump = new EnginePump(Log, settings, host);
        AddComponent<EngineBehaviour>();

        Log.LogInfo($"{PluginName} {PluginVersion} ready — scripts load on the first frame; reload key: {settings.ReloadKey.Value}");
    }
}
