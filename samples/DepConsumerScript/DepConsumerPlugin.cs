using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using DepMainScript;

namespace DepConsumerScript;

// GUID 故意排在主模组（…zmain）之前，文件名也排在前面：最坏情况，和原版 ScriptEngine 的遍历顺序一致
[BepInPlugin("com.shiunaya.samples.dep.aconsumer", "Dep Consumer", "1.0.0")]
#if WITH_DEPENDENCY_ATTRIBUTE
[BepInDependency(DepMainPlugin.Guid)]
#endif
public sealed class DepConsumerPlugin : BasePlugin
{
    private MainBehaviour? _behaviour;

    public override void Load()
    {
        var order = LoadOrder.Next();
        var failures = new System.Collections.Generic.List<string>();

        // 1. 主模组的 Load() 已经跑过（静态状态已初始化）
        if (DepMainPlugin.LoadedOrder == 0) failures.Add("main plugin's Load() has not run yet");

        // 2. 通过 BepInEx 的插件表能拿到主模组，并且 Instance 是同一个程序集里的类型
        if (!IL2CPPChainloader.Instance.Plugins.TryGetValue(DepMainPlugin.Guid, out var info))
            failures.Add("main plugin is not in Chainloader.Plugins");
        else if (info.Instance is not DepMainPlugin main)
            failures.Add($"main PluginInfo.Instance is {(info.Instance?.GetType().FullName ?? "null")}, not DepMainPlugin");
        else
            Log.LogInfo($"[dep-consumer] main says: {main.Greeting}");

        // 3. 能用主模组里（被引擎改名的）注入组件类型
        var behaviour = _behaviour = AddComponent<MainBehaviour>();
        if (behaviour == null) failures.Add("AddComponent<MainBehaviour>() returned null");
        else Log.LogInfo($"[dep-consumer] added {behaviour.GetType().FullName}");

        Log.LogInfo($"[dep-consumer] Load: order={order}, main order={DepMainPlugin.LoadedOrder}");
        if (failures.Count == 0 && DepMainPlugin.LoadedOrder < order)
            Log.LogWarning("[dep-test] PASS: dependent mod saw the main mod fully loaded");
        else
            Log.LogError($"[dep-test] FAIL: {(failures.Count > 0 ? string.Join("; ", failures) : "main loaded after the dependent")}");
    }

    public override bool Unload()
    {
        if (_behaviour != null) UnityEngine.Object.Destroy(_behaviour);
        _behaviour = null;
        Log.LogInfo("[dep-consumer] Unload");
        return true;
    }
}
