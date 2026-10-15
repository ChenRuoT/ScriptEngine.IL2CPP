using System;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace PluginBaseFixture;

public abstract class PluginBase : BasePlugin
{
    public sealed class Ticker : MonoBehaviour
    {
        public Ticker(IntPtr pointer) : base(pointer) { }
    }
}
