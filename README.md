# ScriptEngine.IL2CPP

**BepInEx 6 / IL2CPP 版的 ScriptEngine**：让普通的 `BasePlugin` 插件在**游戏运行中热重载**——改代码 → `dotnet build` → 几秒后游戏里就是新代码，不用重启游戏。

> 官方的 [BepInEx.Debug ScriptEngine](https://github.com/BepInEx/BepInEx.Debug) 只支持 BepInEx 5 / Mono（`BaseUnityPlugin`、net3.5）。
> 在 BepInEx 6 IL2CPP 下它根本不会被识别为插件，核心做法（`AddComponent` 把插件当 MonoBehaviour 挂上去、
> `Assembly.Load(bytes)` 进默认上下文）在 IL2CPP 上也走不通。本项目为 IL2CPP 重新设计，**不是**它的移植。

已在 **SPT-Tushonka 5.0 / Escape From Tarkov 1.2.0.0（build 47965）/ BepInEx 6.0.0-be.4 / Il2CppInterop 1.6.7 / CoreCLR 10** 上实测。

## 它做什么

- 监视 `BepInEx/scripts/`，把里面的 `BasePlugin` DLL 装进**可回收的 `AssemblyLoadContext`**；DLL 以字节流读入，**游戏运行时 `dotnet build` 照常能覆盖它们**（不锁文件）。
- 触发重载的三种方式：**热键 F6**、**文件变化自动重载**（带防抖）、**命令通道**（给脚本 / AI 代理用，见下）。
- 重载 = 调用旧插件的 `Unload()` → 清掉它没销毁的注入对象 → 丢掉旧上下文 → 装新的。
- **准备阶段失败不伤旧代码**：DLL 写到一半、编译器还占着文件、Cecil 处理失败……旧版本继续跑，等文件稳定后自动重试。
- 对**不用改写**的社区插件友好（见 [插件作者契约](#插件作者契约)）：
  - 注入的 MonoBehaviour 每次重载自动改名，不会再报 `Type with FullName … is already injected`；
  - `Assembly.GetExecutingAssembly().Location` 在脚本里照常返回磁盘路径（字节流加载的程序集 `Location` 本来是空的）；
  - 保留 PDB，异常堆栈带文件名和行号。

## 安装

1. 从 Release 下载 zip，把里面的 **BepInEx** 文件夹解压到 `EscapeFromTarkov.exe` 所在目录：

   ```text
   游戏根目录/BepInEx/plugins/ScriptEngine.IL2CPP/ScriptEngine.IL2CPP.dll   ← 引擎（普通插件，常驻，不会被重载）
   游戏根目录/BepInEx/scripts/                                              ← 把要热重载的插件 DLL 放这里（首次启动时自动创建）
   ```

2. **同一个插件不要同时放进 `plugins/` 和 `scripts/`**，否则会被 BepInEx 和引擎各装一次（引擎会检测到并跳过后者，日志里有提示）。

## 开发者工作流

在你的插件 csproj 里加一个构建后部署目标，把 DLL 和 PDB 拷进 `scripts/`（而不是 `plugins/`）：

```xml
<Target Name="DeployToScripts" AfterTargets="Build">
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(GameDir)/BepInEx/scripts" />
  <Copy SourceFiles="$(TargetDir)$(TargetName).pdb" DestinationFolder="$(GameDir)/BepInEx/scripts" Condition="Exists('$(TargetDir)$(TargetName).pdb')" />
</Target>
```

然后：**启动游戏一次 → 改代码 → `dotnet build` → 约 1 秒静默期后自动重载**（或者在游戏里按 F6）。
日志（`BepInEx/LogOutput.log`）里会看到：

```text
[Message:ScriptEngine.IL2CPP] Reload (file) done: generation 3, 3 plugin(s) loaded, 3 unloaded, 0 error(s), 145 ms (prepare 14, unload 107, load 22)
```

脚本里有资源文件（图标、音效）时，把 DLL 和资源放在 `scripts/` 的同一个子目录里（例如 `scripts/MyMod/MyMod.dll` + `scripts/MyMod/Icons/`），`Assembly.Location` 就指向那个子目录。
`scripts/` 会被递归扫描，同一批脚本之间可以互相引用。
两个同名程序集（例如同一个依赖拷进了两个子目录）只会装第一份，其余的在日志里警告并跳过；不是 .NET 程序集的 `.dll`（0 字节、文本、LFS 指针……）超过 5 秒没动过就被忽略并警告，不会卡住重载。

`samples/` 里有四个可直接构建的示例，`HelloScript` 是最小的一个。

## 插件作者契约

引擎不可能替你撤销你做过的所有事。想让热重载干净，请遵守：

1. **实现 `Unload()` 并返回 `true`**，在里面撤销你在 `Load()` 里装上的一切：
   Harmony / `PatchManager` 补丁、事件订阅（包括静态事件和 interop 事件）、你创建的 GameObject / 组件 / 贴图 / 音频、停掉协程和线程。
   返回 `false`（`BasePlugin.Unload()` 的默认值）会让引擎在日志里警告——但仍然会重载。
2. **注入的 MonoBehaviour / ScriptableObject 引擎会兜底销毁**（`SweepLeftovers`，默认开启）：`Unload()` 之后，旧代次注入类型还活着的实例会被 `DestroyImmediate`。
   这只是安全网；你自己创建的 GameObject、Canvas、贴图、AudioSource 不在其内。
3. **不要靠类型名字符串找自己的注入类型**（`Type.GetType("My.Ns.MyBehaviour")`、`GetComponent("MyBehaviour")` 的命名空间部分）。
   引擎会把注入类型的命名空间改成 `My.Ns.__g{代次}`（类名不变）。用 `typeof(MyBehaviour)` 没问题。
4. **不要假设自己在游戏启动阶段加载**——重载时 `Load()` 发生在游戏运行中途（可能已经在战局里）。
5. 每个插件实例拥有自己的 `ConfigFile` 和日志源（`BasePlugin` 构造函数每次都新建），所以枚举类型的配置项重载后不会出 `InvalidCastException`。
   引擎会在旧实例卸载后关掉它的 `SaveOnConfigSet`，避免旧对象回写 `.cfg`。

## 命令通道（自动化 / AI 代理）

不开任何网络端口，纯文件。往 `BepInEx/ScriptEngine.IL2CPP/commands/` 放一个 `<id>.<命令>` 文件（内容随意）：

| 命令 | 作用 |
|---|---|
| `<id>.reload` | 强制重载全部脚本（即使内容没变） |
| `<id>.unload` | 卸载全部脚本，不再装新的 |
| `<id>.status` | 只回报状态 |

引擎取走命令文件，执行，写出 `<id>.result.json`（先写临时文件再改名，读到的一定是完整 JSON），内容包含 `ok`、当前代次、已装载插件，以及完整的重载报告（装载 / 卸载列表、`Unload()` 返回值、警告、错误、分段耗时）。
另外每次重载 / 卸载后都会刷新 `BepInEx/ScriptEngine.IL2CPP/status.json`。

仓库自带客户端 `tools/reload.ps1`：

```powershell
.\tools\reload.ps1                  # 强制重载并打印结果 JSON；退出码 0 成功 / 1 引擎报告失败 / 2 超时
.\tools\reload.ps1 -Command status
.\tools\reload.ps1 -Command unload
```

典型的自动化回路：`dotnet build`（自动部署到 `scripts/`）→ `reload.ps1` → 解析 JSON 里的 `ok` / `errors` → 驱动游戏里的测试脚本。

## 配置

`BepInEx/config/com.shiunaya.bepinex.scriptengine.il2cpp.cfg`（也可用 ConfigurationManager 改）：

| 节 / 项 | 默认 | 说明 |
|---|---|---|
| `General / ScriptsDirectory` | 空 = `BepInEx/scripts` | 监视的目录；相对路径相对游戏根目录 |
| `General / ReloadKey` | `F6` | 游戏内重载热键；`None` 关闭 |
| `General / LoadOnStart` | `true` | BepInEx 加载完所有插件后的第一帧装载脚本 |
| `General / AutoReload` | `true` | 文件变化时自动重载 |
| `General / AutoReloadDelay` | `1.0` | 最后一次文件变化后的静默秒数（一次构建会写好几个文件） |
| `Automation / CommandChannel` | `true` | 命令通道和 `status.json` |
| `Advanced / CollectibleContexts` | `true` | 每代用可回收上下文；注入类型出问题时可关（旧代次就不会被回收） |
| `Advanced / SweepLeftovers` | `true` | `Unload()` 之后销毁旧代次注入类型的残留实例 |
| `Advanced / Il2CppGcAfterUnload` | `true` | 卸载后跑一次 IL2CPP + 托管 GC，让旧上下文有机会被回收（可能有一次短暂卡顿） |

## 已知限制

| 限制 | 说明 |
|---|---|
| 注入过 Unity 类型的代次，程序集留在内存里 | Il2CppInterop 对注入类型的原生 trampoline 持有永久 `GCHandle`，旧 `AssemblyLoadContext` 回收不了。每次重载留几十~几百 KB，对开发用途可以忽略；没有注入类型的脚本（纯 `BasePlugin` + 普通类）的旧上下文**会**被真正回收（离线测试 + 单测覆盖）。引擎的旧代码已被中和（实例销毁、不再被调用），留下的只是字节。 |
| 靠类型名字符串反射找注入类型会失效 | 见契约第 3 条 |
| 补丁 / 事件 / 自建对象要靠插件自己的 `Unload()` 撤销 | 引擎只兜底注入类型的实例 |
| 被重载的插件如果被**别的插件**静态引用 / `BepInDependency` 依赖 | 别的插件拿着的是旧代次的类型，不会自动更新；官方 ScriptEngine 也一样 |
| 脚本 DLL 不能与宿主已加载的程序集同名 | 例如把 `0Harmony.dll`、`BepInEx.*.dll` 拷进 `scripts/`：引擎会跳过并警告，脚本改用宿主那份 |
| 只管 `BasePlugin` | 没有 `[BepInPlugin]` 的 DLL 被当成库装载（供同批脚本引用），不会被实例化 |
| 特性参数里的 `typeof(被改名的类型)` 不会跟着改名 | Cecil 对自定义特性的 blob 原样写回；注入类型出现在 `[Attr(typeof(X))]` 里的情况极少见，遇到再说 |

## 工作原理（给贡献者）

一次重载：

1. **快照**：把 `scripts/` 下所有 DLL/PDB 读成字节（`FileShare.ReadWrite|Delete`，读完即关）。读不了 / 不是完整的程序集 → 延后重试，旧代次不动。内容摘要与上次相同则跳过。
2. **准备**（纯函数，失败不伤旧代次）：用 Mono.Cecil 读每个 DLL，
   找出继承 `Il2CppObjectBase` 的类，给它们的（最外层类型的）命名空间加 `.__g{代次}` 后缀，并同步改写其它脚本程序集对它们的类型引用；
   把 `Assembly.get_Location()` 调用换成 `ScriptRuntime.GetAssemblyLocation`；需要改动的程序集连同新的 portable PDB 一起重新写出，其余原样透传；
   用 BepInEx 自己的 `IL2CPPChainloader.ToPluginInfo` 发现 `BasePlugin`。
3. **卸旧**：后装先卸，逐个调用 `Unload()`（异常只记日志）→ 从 `IL2CPPChainloader.Plugins` 移除并清掉 `PluginInfo.Instance` →
   扫掉残留的注入实例 → 抹掉 BepInEx / Il2CppInterop 静态缓存里指向旧类型的条目 → 断开引用并 `AssemblyLoadContext.Unload()` → GC，用 `WeakReference` 观察是否回收。
4. **装新**：新的 `ScriptLoadContext`（回落到默认上下文解析 BepInEx / interop / BCL），按 `BepInDependency` 拓扑排序后用 `IL2CPPChainloader.LoadPlugin` 实例化并登记。
   某个插件 `Load()` 抛异常 → 记录带行号的错误、给它一次 `Unload()` 收拾机会、不影响其它脚本。

几个踩过的坑（都有离线测试或真机日志佐证）：

- `ScriptLoadContext` 绝对不能把自己装进来的 `Assembly` 缓存进字段——上下文 → 程序集 → 加载器分配器的环会让 CLR 认为它仍然可达，`Unload()` 之后永远回收不了。
- `ClassInjector` 按 `Type.FullName` 去重，且 `RegisterTypeInIl2Cpp` 为注入类型建的构造委托被永久 `GCHandle` 钉住 → 注入类型的代次回收不了（见上表）。
- `Resources.FindObjectsOfTypeAll` 才能找到 `BepInEx_Manager`（HideAndDontSave）上的组件，`FindObjectsOfType` 找不到；而且每次调用都要遍历整个 Unity 对象表，所以按基类（MonoBehaviour / ScriptableObject）各扫一次，再用原生 class 指针挑出目标。

## 构建与测试

```powershell
dotnet build ScriptEngine.IL2CPP.slnx                                    # 引擎会自动部署到 <游戏>/BepInEx/plugins/ScriptEngine.IL2CPP/
dotnet build samples/HelloScript                                         # 示例会自动部署到 <游戏>/BepInEx/scripts/
dotnet run --project tests/ScriptEngine.Tests                            # 离线测试：不启动游戏
```

- `GameDir` 默认 `D:/EFTdev/Client.1.2.0.0.47965`，复制 `Directory.Build.local.props.example` 为 `Directory.Build.local.props` 覆盖，或命令行 `-p:GameDir=...`。
- 游戏运行时引擎 DLL 被锁，部署会失败（`-p:DeployToGame=false` 跳过）；脚本 DLL 不会被锁。
- 编译引用全部指向游戏目录里的 `BepInEx/core` 与 `BepInEx/interop`；Mono.Cecil 随 BepInEx 提供，不需要 NuGet。
- `tools/start-game.ps1` / `tools/stop-game.ps1` 启动 / 结束游戏（只动自己起的进程，不需要 SPT 服务端就能验证插件加载）。
- `tools/package.ps1` 打 Release zip。

## 许可证与致谢

[MIT](LICENSE)。设计上参考了官方 [BepInEx.Debug ScriptEngine](https://github.com/BepInEx/BepInEx.Debug)（LGPL-3.0）和
[ScriptDebugEngine](https://github.com/wukong2468/BepinEx.Debug.ScriptEngine) 的思路，但**没有复用它们的代码**。
运行时依赖 BepInEx（LGPL-3.0）、Il2CppInterop、Mono.Cecil（MIT），本仓库不分发它们。
