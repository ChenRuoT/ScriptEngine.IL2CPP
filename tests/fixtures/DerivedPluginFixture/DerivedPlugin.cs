using BepInEx;

namespace DerivedPluginFixture;

[BepInPlugin("com.shiunaya.fixtures.derived", "Derived Fixture", "1.0.0")]
public sealed class DerivedPlugin : PluginBaseFixture.PluginBase
{
    public override void Load()
    {
    }
}
