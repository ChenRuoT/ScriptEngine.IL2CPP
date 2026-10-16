using System.Collections.Generic;
using BepInEx;

namespace ScriptEngineIl2Cpp.Rewriting;

/// <summary>磁盘上读到的一份脚本 DLL（字节快照，不持有文件句柄）。</summary>
internal sealed class ScriptFile
{
    public ScriptFile(string path, byte[] dll, byte[]? pdb)
    {
        Path = path;
        Dll = dll;
        Pdb = pdb;
    }

    public string Path { get; }
    public byte[] Dll { get; }
    public byte[]? Pdb { get; }
}

/// <summary>改写完成、可以交给 <see cref="ScriptLoadContext"/> 的脚本程序集。</summary>
internal sealed class ScriptImage
{
    public ScriptImage(string path, string assemblyName, byte[] dll, byte[]? pdb)
    {
        Path = path;
        AssemblyName = assemblyName;
        Dll = dll;
        Pdb = pdb;
    }

    /// <summary>原始 DLL 的磁盘路径（<c>Assembly.Location</c> 垫片、<c>PluginInfo.Location</c> 用）。</summary>
    public string Path { get; }

    /// <summary>程序集简单名。</summary>
    public string AssemblyName { get; }

    /// <summary>装载用的字节：未改写时是磁盘原样，改写后是 Cecil 的输出。</summary>
    public byte[] Dll { get; set; }

    /// <summary>对应的 portable PDB；没有符号则为 null。</summary>
    public byte[]? Pdb { get; set; }

    /// <summary>是否经过 Cecil 改写（false = 原样字节）。</summary>
    public bool Rewritten { get; set; }

    /// <summary>该程序集直接引用的程序集简单名（装载顺序用：引用了另一个脚本里的插件，就让它先装）。</summary>
    public List<string> ReferencedAssemblies { get; } = new();

    /// <summary>被改名的顶层类型：原全名 → 新全名。</summary>
    public List<KeyValuePair<string, string>> RenamedTypes { get; } = new();

    /// <summary>被改写的、指向其它脚本程序集里被改名类型的类型引用个数。</summary>
    public int TypeReferencesRewritten { get; set; }

    /// <summary>被替换的 <c>Assembly.get_Location</c> 调用点个数。</summary>
    public int LocationCallsShimmed { get; set; }

    /// <summary>该程序集里发现的 BasePlugin（已按改写后的名字计算 <see cref="PluginInfo.TypeName"/>）。</summary>
    public List<PluginInfo> Plugins { get; } = new();

    /// <summary>PDB 是否保留（原样或重新写出）。</summary>
    public bool HasSymbols => Pdb is not null;
}
