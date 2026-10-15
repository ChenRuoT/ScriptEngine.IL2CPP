using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ConsumerFixture;

[BepInPlugin("com.shiunaya.fixtures.consumer", "Consumer Fixture", "1.0.0")]
public sealed class ConsumerPlugin : BasePlugin
{
    public override void Load() => AddComponent<SharedLib.SharedBehaviour>();
}
