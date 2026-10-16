using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace DepConsumerFixture;

// GUID 排在 DepMainScript 的 …zmain 之前；故意不写 [BepInDependency]，只靠程序集引用
[BepInPlugin("com.shiunaya.fixtures.dep.aconsumer", "Dep Consumer Fixture", "1.0.0")]
public sealed class DepConsumerPlugin : BasePlugin
{
    public override void Load() => Log.LogInfo(DepMainScript.DepMainPlugin.LoadedOrder);
}
