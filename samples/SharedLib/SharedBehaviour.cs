using System;
using UnityEngine;

namespace SharedLib;

/// <summary>另一个脚本程序集里的注入 MonoBehaviour。</summary>
public sealed class SharedBehaviour : MonoBehaviour
{
    public SharedBehaviour(IntPtr pointer) : base(pointer) { }

    public static event Action<string>? Message;

    private int _frames;

    private void Update()
    {
        if (++_frames == 60) Message?.Invoke($"SharedBehaviour alive, type = {GetType().FullName}");
    }
}
