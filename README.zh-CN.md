# ScriptEngine.IL2CPP

**BepInEx 6 / IL2CPP 插件热重载引擎。** 改代码 → `dotnet build` → 几秒后游戏里跑的就是新代码，不用重启游戏。

[English](README.md) · [中文](README.zh-CN.md)

> 官方的 [BepInEx.Debug ScriptEngine](https://github.com/BepInEx/BepInEx.Debug) 只支持 BepInEx 5 / Mono（`BaseUnityPlugin`、net3.5）。
> 在 BepInEx 6 IL2CPP 下它根本不会被识别为插件，核心做法（`AddComponent` 把插件当 MonoBehaviour 挂上去、
> `Assembly.Load(bytes)` 进默认上下文）在 IL2CPP 上也走不通。本项目为 IL2CPP 重新设计，**不是**它的移植。

## 环境要求

| | |
|---|---|
| BepInEx | 6.x 的 IL2CPP / CoreCLR 构建 —— 实测于 `6.0.0-be.4` |
| Il2CppInterop | 随 BepInEx 6 附带的版本 —— 实测于 `1.6.7` |
| 插件 TFM | 必须与游戏的 CoreCLR 匹配 —— 开发环境为 `net10.0`（CoreCLR 10） |
| 被重载的插件 | 带 `[BepInPlugin]` 的普通 BepInEx `BasePlugin` |

引擎本身就是一个普通的、常驻的 `BasePlugin`，按普通插件的方式安装。
下面所有数字和结论都是在真实 Unity IL2CPP 游戏上跑这套技术栈测出来的；除非特别说明，耗时都是引擎侧的。

## 它做什么

- 监视 `BepInEx/scripts/`，把里面的 `BasePlugin` DLL 装进**可回收的 `AssemblyLoadContext`**。DLL 以字节流读入，**游戏运行时 `dotnet build` 照常能覆盖它们**（不锁文件）。
- 触发重载的三种方式：**热键**（默认 `F6`）、**文件变化自动重载**（带防抖）、**命令通道**（给脚本 / AI 代理用，见下）。
- 一次重载 = 调用旧插件的 `Unload()` → 销毁它没销毁的注入对象、把它忘了退订的事件处理器变成空操作 → 丢掉旧上下文 → 装新代次。
- **准备阶段失败不伤旧代码**：DLL 写到一半、PDB 还没跟上、编译器还占着文件、Cecil 处理失败……旧版本继续跑，等文件稳定后自动重试。
- 对**不用改写**的社区插件友好（见 [插件作者契约](#插件作者契约)）：
  - 注入的 MonoBehaviour 每次重载自动改名，不会再报 `Type with FullName … is already injected`；
  - `Assembly.GetExecutingAssembly().Location` 在脚本里照常返回 `scripts/` 下的真实目录（字节流加载的程序集 `Location` 本来是空的）；
  - 保留 portable PDB（并确认它真的是这份 DLL 的），异常堆栈带文件名和行号。
- 一次重载通常几十毫秒；慢的话日志会点名是哪个插件的 `Load()` 慢（比如注入一个很大的 MonoBehaviour 要一两秒，那是 Il2CppInterop 的开销，不是引擎的）。

## 安装

1. 从 Release 下载 zip，把里面的 **BepInEx** 文件夹解压到**游戏根目录**（放着 `BepInEx/` 和游戏 exe 的那一层）：

   ```text
   游戏根目录/BepInEx/plugins/ScriptEngine.IL2CPP/ScriptEngine.IL2CPP.dll   ← 引擎（普通插件，常驻，不会被重载）
   游戏根目录/BepInEx/scripts/                                              ← 把要热重载的插件 DLL 放这里（首次启动时自动创建）
   ```

2. **同一个插件不要同时放进 `plugins/` 和 `scripts/`**，否则 BepInEx 和引擎各装一个。引擎会检测到并跳过自己那份，日志里有提示。

## 开发者工作流

在你的插件 csproj 里加一个构建后部署目标，把 DLL 和 PDB 拷进 `scripts/`（而不是 `plugins/`）：

```xml
<Target Name="DeployToScripts" AfterTargets="Build">
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(GameDir)/BepInEx/scripts" />
  <Copy SourceFiles="$(TargetDir)$(TargetName).pdb" DestinationFolder="$(GameDir)/BepInEx/scripts" Condition="Exists('$(TargetDir)$(TargetName).pdb')" />
</Target>
```

然后：**启动游戏一次 → 改代码 → `dotnet build` → 约 1 秒静默期后自动重载**（或者在游戏里按 `F6`）。
日志（`BepInEx/LogOutput.log`）里会看到：

```text
[Message:ScriptEngine.IL2CPP] Reload (file) done: generation 3, 3 plugin(s) loaded, 3 unloaded, 0 error(s), 54 ms (prepare 4, unload 9, load 39)
```

脚本里有资源文件（图标、音效）时，把 DLL 和资源放在 `scripts/` 的同一个子目录里（例如 `scripts/MyMod/MyMod.dll` + `scripts/MyMod/Icons/`），`Assembly.Location` 就指向那个子目录。
`scripts/` 会被递归扫描，同一批脚本之间可以互相引用。
两个同名程序集（例如同一个依赖拷进了两个子目录）只会装第一份，其余的在日志里警告并跳过；不是 .NET 程序集的 `.dll`（0 字节、文本、LFS 指针……）超过 5 秒没动过就被忽略并警告，不会卡住重载。

### 无人值守地发送按键

验证热键有两个坑：按键只有在游戏窗口有焦点时才会到达插件（日志里 `[probe] focus -> False` 时按了也没用），而且**被别的软件注册成全局热键的键根本到不了任何窗口**。无人值守发按键用 `tools/send-key.ps1`：

```powershell
.\tools\send-key.ps1 F6     # 先抢前台再发一次真实按键
.\tools\send-key.ps1 F5
```

它按**窗口类名** `UnityWndClass` 找游戏窗口（`Process.MainWindowHandle` 拿到的是 BepInEx 的控制台窗口），用 ALT 解锁前台切换限制 + `AttachThreadInput`/`SetFocus` 抢焦点，确认抢到后才用 `keybd_event` 发键。

## 示例

`samples/` 里有可直接构建的示例（`dotnet build samples/<名字>` 会部署到 `<游戏>/BepInEx/scripts/`；标 † 的默认不部署，加 `-p:DeployToScripts=true`）：

| 示例 | 演示什么 |
|---|---|
| `HelloScript` | 最小的可热重载插件：枚举配置项、`Assembly.Location`、带行号的堆栈 |
| `HotkeyTestScript` | 热键：按配置的热键（默认 `F5`）打印一行消息，消息里带**处理它的代次**（`__g1` → `__g2`），可直接验证「热键 → 重载 → 新代次响应」 |
| `BehaviourScript` + `SharedLib` | 注入的 MonoBehaviour（含嵌套在插件类里的）、跨 DLL 引用；守规矩的 `TidyPlugin` 和没写 `Unload()` 的 `LeakyPlugin`（引擎替它销毁组件） |
| `FaultyScript` † | `Load()` 抛异常（带行号的错误、半路挂上的组件被清掉、不影响同一个 DLL 里别的插件）和 `[BepInIncompatibility]` |
| `EventScript` † | 订阅游戏事件 / Unity 事件：正确退订、在 `OnDestroy` 里退订、忘了退订（旧处理器被空操作化并报告） |
| `PatchScript` † | Harmony 补丁与 `UnpatchSelf()` |

## 插件作者契约

引擎不可能替你撤销你做过的所有事。想让热重载干净，请遵守：

1. **实现 `Unload()` 并返回 `true`**，在里面撤销你在 `Load()` 里装上的一切：
   - Harmony 补丁：`harmony.UnpatchSelf()`；
   - 事件订阅（游戏事件、Unity 事件、静态事件，经 interop 订阅的都算）：`-=` **同一个委托对象**（订阅用的委托要存在字段里，别每次 new）；
   - 你创建的 GameObject / 组件 / 贴图 / 音频、协程和线程。

   返回 `false`（`BasePlugin.Unload()` 的默认值）会让引擎在日志里警告——但仍然会重载。
2. **引擎的兜底**（不是让你偷懒的理由，只是别让一次疏忽把整个会话搞乱）：
   - **注入的 MonoBehaviour / ScriptableObject**：`Unload()` 之后旧代次注入类型还活着的实例会被 `DestroyImmediate`（`SweepLeftovers`，默认开启）。你自己创建的 GameObject、Canvas、贴图、AudioSource 不在其内。
   - **忘了退订的事件处理器**：旧代次交给游戏的所有 interop 委托在卸载时被换成空操作。它们之后被触发时，**不会再执行旧代码**，只会在日志里报一次
     `A handler from an unloaded script generation was invoked and ignored: …`，告诉你是哪个代次的哪个方法没退订。
     （被换成空操作的处理器还留在游戏的事件列表里——没法替你 `-=`——所以订阅者数量会一路涨，退订还是要自己做。）
   - **`Load()` 抛了异常的插件**：它半路挂到 BepInEx 管理对象上的注入组件会被销毁，不会在这一代里一直跑半成品。
3. **不要靠类型名字符串找自己的注入类型**（`Type.GetType("My.Ns.MyBehaviour")`、`GetComponent("MyBehaviour")` 的命名空间部分）。
   引擎会把注入类型的命名空间改成 `My.Ns.__g{代次}`（类名不变）。用 `typeof(MyBehaviour)` 没问题。
4. **不要假设自己在游戏启动阶段加载**——重载时 `Load()` 发生在游戏运行中途（可能已经在一个关卡 / 场景里）。
5. 每个插件实例拥有自己的 `ConfigFile` 和日志源（`BasePlugin` 构造函数每次都新建），所以枚举类型的配置项重载后不会出 `InvalidCastException`。
   引擎会在旧实例卸载后关掉它的 `SaveOnConfigSet`，避免旧对象回写 `.cfg`。
6. 在 `Unload()` 里 `Destroy()` 一个组件、再在它的 `OnDestroy()` 里退订，是可以的：引擎把清理 Il2CppInterop 转换缓存的那一步推迟了半秒，让 `OnDestroy` 里的 `-=` 还能找到当初订阅用的那个委托。

## 命令通道（自动化 / AI 代理）

不开任何网络端口，纯文件。往 `BepInEx/ScriptEngine.IL2CPP/commands/` 放一个 `<id>.<命令>` 文件（内容随意）：

| 命令 | 作用 |
|---|---|
| `<id>.reload` | 强制重载全部脚本（即使内容没变） |
| `<id>.unload` | 卸载全部脚本，不再装新的（直到下一次重载或文件变化） |
| `<id>.status` | 只回报状态 |

引擎取走命令文件，执行，写出 `<id>.result.json`（先写临时文件再改名，读到的一定是完整 JSON），内容包含 `ok`、当前代次、已装载插件，以及完整的重载报告（装载 / 卸载列表、各插件 `Load()` 耗时、`Unload()` 返回值、警告、错误、分段耗时）。
`reload` 遇到文件还在写（编译器没写完、DLL/PDB 对不上）时，引擎会自己重试，**结果文件等重试有了结局才写**，所以客户端不会先拿到一个马上被推翻的失败；命令被后来的命令接替时会写出带 `message` 的失败结果。
没人取走的结果文件 5 分钟后会被清掉。另外每次重载 / 卸载 / 上下文回收状态变化后都会刷新 `BepInEx/ScriptEngine.IL2CPP/status.json`。

仓库自带客户端 `tools/reload.ps1`：

```powershell
.\tools\reload.ps1                  # 强制重载并打印结果 JSON；退出码 0 成功 / 1 引擎报告失败 / 2 超时（默认等 60 秒）
.\tools\reload.ps1 -Command status
.\tools\reload.ps1 -Command unload
```

典型的自动化回路：`dotnet build`（自动部署到 `scripts/`）→ `reload.ps1` → 解析 JSON 里的 `ok` / `errors` → 驱动游戏里的测试脚本。

## 配置

`BepInEx/config/com.shiunaya.bepinex.scriptengine.il2cpp.cfg`（也可用 ConfigurationManager 改，`AutoReload` / `ScriptsDirectory` 运行时改动几秒内生效）：

| 节 / 项 | 默认 | 说明 |
|---|---|---|
| `General / ScriptsDirectory` | 空 = `BepInEx/scripts` | 监视的目录；相对路径相对游戏根目录 |
| `General / ReloadKey` | `F6` | 游戏内重载热键；`None` 关闭 |
| `General / LoadOnStart` | `true` | BepInEx 加载完所有插件后的第一帧装载脚本 |
| `General / AutoReload` | `true` | 文件变化时自动重载 |
| `General / AutoReloadDelay` | `1.0` | 最后一次文件变化后的静默秒数（一次构建会写好几个文件） |
| `Automation / CommandChannel` | `true` | 命令通道和 `status.json` |
| `Advanced / CollectibleContexts` | `true` | 每代用可回收上下文；注入类型出问题时可关（旧代次就不会被回收） |
| `Advanced / SweepLeftovers` | `true` | 销毁旧代次注入类型的残留实例（和 `Load()` 失败时半路挂上的组件） |
| `Advanced / DiagnosePinned` | `true` | 没有注入类型的代次回收不了时，在 Harmony / MonoMod / Il2CppInterop / BepInEx 的静态状态里找出是谁还引用着旧代码，把引用链打进日志（每个游戏会话一次，可能卡一秒左右） |
| `Advanced / Il2CppGcAfterUnload` | `true` | 对**可能被回收**的代次，卸载后跑 IL2CPP + 托管 GC 验证是否回收（注入过类型的代次注定回收不了，不会跑 GC） |

## 已知限制

| 限制 | 说明 |
|---|---|
| 注入过 Unity 类型的代次，程序集留在内存里 | Il2CppInterop 对注入类型的原生 trampoline 持有永久 `GCHandle`，旧 `AssemblyLoadContext` 回收不了（日志里记为 `retained`）。每次重载留几十~几百 KB，对开发用途可以忽略；旧代码已被中和（实例销毁、处理器空操作化），留下的只是字节。 |
| 没有注入类型的脚本才能真正回收 | 纯 `BasePlugin` + 普通类、订阅了事件（引擎会把处理器空操作化）的脚本，旧上下文几十毫秒内被回收（真机 + 离线测试覆盖）；补丁目标在宿主里（游戏方法）、补丁方法在脚本里并 `UnpatchSelf()` 的脚本同样能被回收（离线测试覆盖，没有在真机的游戏方法上试过——打错游戏方法可能让游戏起不来）。 |
| Harmony 补丁打在**脚本自己的方法**上会钉住旧上下文 | Harmony / MonoMod 的内部表（`PatchManager`、`DetourManager`）对被补丁的方法保留记录，`UnpatchSelf()` 也不清。打在游戏方法上不受影响。日志里 `DiagnosePinned` 会点出这条引用链。 |
| 每个订阅过的事件处理器会在 Il2CppInterop 里留下一对小对象 | IL2CPP 对象 ↔ CoreCLR 包装对象互相强引用，两边的 GC 都收不掉；引擎把里面指向旧代码的引用切断了，剩下的几十字节不会再钉住程序集。 |
| 靠类型名字符串反射找注入类型会失效 | 见契约第 3 条 |
| 补丁 / 自建对象 / 协程要靠插件自己的 `Unload()` 撤销 | 引擎只兜底注入类型的实例和 interop 事件处理器 |
| 被重载的插件如果被**别的插件**静态引用 / `BepInDependency` 依赖 | 别的插件拿着的是旧代次的类型，不会自动更新；官方 ScriptEngine 也一样 |
| 脚本 DLL 不能与宿主已加载的程序集同名 | 例如把 `0Harmony.dll`、`BepInEx.*.dll` 拷进 `scripts/`：引擎会在分析之前就跳过并警告，脚本改用宿主那份 |
| 只管 `BasePlugin` | 没有 `[BepInPlugin]` 的 DLL 被当成库装载（供同批脚本引用），不会被实例化 |
| 被别的软件注册成全局热键的按键到不了游戏 | 这种键在系统层就被吃掉了，物理按键和合成按键都一样，表现为「插件收不到这个键」。本机实测 `F7` 已被占用（`RegisterHotKey(F7)` → 1409），换一个空闲的键即可。 |
| 特性参数里的 `typeof(被改名的类型)` 不会跟着改名 | Cecil 对自定义特性的 blob 原样写回；注入类型出现在 `[Attr(typeof(X))]` 里的情况极少见，遇到再说 |

## 工作原理（给贡献者）

一次重载：

1. **快照**：把 `scripts/` 下所有 DLL/PDB 读成字节（`FileShare.ReadWrite|Delete`，读完即关）。读不了 / 不是完整的程序集 / 旁挂 PDB 的 GUID 和 DLL 的 CodeView 目录对不上且刚写过 → 延后重试，旧代次不动。内容摘要（每段带长度）与上次相同则跳过。
2. **准备**（纯函数，失败不伤旧代次）：用 Mono.Cecil 读每个 DLL（宿主已有的同名程序集、同名重复的、`.resources` 卫星程序集在分析之前就丢掉），
   先用 BepInEx 自己的 `IL2CPPChainloader.ToPluginInfo` 发现 `BasePlugin`（必须在改名之前：Cecil 按读入时的名字缓存类型），
   再找出继承 `Il2CppObjectBase` 的类，给它们的（最外层类型的）命名空间加 `.__g{代次}` 后缀，并同步改写其它脚本程序集对它们的类型引用（只引用不定义的程序集同样要重新写出）；
   把 `Assembly.get_Location()` 调用换成 `ScriptRuntime.GetAssemblyLocation`；需要改动的程序集连同新的 portable PDB 一起重新写出，其余原样透传。
3. **卸旧，第一步（立刻）**：后装先卸，逐个调用 `Unload()`（异常只记日志）→ 从 `IL2CPPChainloader.Plugins` 移除并清掉 `PluginInfo.Instance` →
   扫掉残留的注入实例 → 把旧代次交给游戏的 interop 委托换成空操作。新代次紧接着就能装。
4. **装新**：新的 `ScriptLoadContext`（回落到默认上下文解析 BepInEx / interop / BCL），先检查 `BepInIncompatibility` 和硬依赖，按 `BepInDependency` 拓扑排序后用 `IL2CPPChainloader.LoadPlugin` 实例化并登记。
   某个插件 `Load()` 抛异常 → 记录带行号的错误、给它一次 `Unload()` 收拾机会、销毁它半路挂上的组件、不影响其它脚本。
5. **卸旧，第二步（半秒后，由每帧的泵驱动）**：抹掉 BepInEx / Il2CppInterop 静态缓存里指向旧类型的条目 → 断开引用并 `AssemblyLoadContext.Unload()` →
   对可能被回收的代次跑 GC，用 `WeakReference` 观察是否回收；注入过类型的代次当场记为 `retained`，不跑 GC。
   晚半秒是因为插件在 `Unload()` 里 `Destroy()` 的组件要到帧尾才真正销毁，它们的 `OnDestroy()` 里常常要 `-=` 退订，而退订得在转换缓存里找到当初订阅用的同一个 IL2CPP 委托。

几个踩过的坑（都有离线测试或真机日志佐证）：

- `ScriptLoadContext` 绝对不能把自己装进来的 `Assembly` 缓存进字段——上下文 → 程序集 → 加载器分配器的环会让 CLR 认为它仍然可达，`Unload()` 之后永远回收不了。
- Cecil 的 `DefaultAssemblyResolver` 只在**单参数** `Resolve(name)` 里查/写自己的缓存，双参数重载直接走基类——每次重新打开磁盘上的 interop DLL 且没人 `Dispose`。自己包一层解析器时要原样转发单参数重载。
- `ClassInjector` 按 `Type.FullName` 去重，且为注入类型建的构造委托、每个方法的 trampoline 被永久 `GCHandle` 钉住 → 注入类型的代次回收不了。
- 每个经 interop 订阅的事件处理器，Il2CppInterop 会造一个注入类 `Il2CppToMonoDelegateReference` 的实例，它的托管包装 ↔ IL2CPP 对象互相强引用（IL2CPP 句柄 + CoreCLR `GCHandle`），任何一边的 GC 都收不掉，里面的 `ReferencedDelegate` 又指着插件的代码：
  所以**哪怕 `-=` 退订了，旧上下文也回收不了**，忘了退订的处理器还会继续执行旧代码。引擎把这个字段换成空操作来切断它。
- 只有 `Resources.FindObjectsOfTypeAll` 才能找到 `BepInEx_Manager`（HideAndDontSave）上的组件（`FindObjectsOfType` 找不到）；而且每次调用都要遍历整个 Unity 对象表，所以按基类（MonoBehaviour / ScriptableObject）各扫一次，再用原生 class 指针挑出目标。
- `ClassInjector.IsTypeRegisteredInIl2Cpp` 会触发类型的静态构造并把它塞进 `Il2CppClassPointerStore.ourPointerReaders`（强引用 `Type`）；判断「这个类型注入过没有」要用无副作用的 `IsManagedTypeInjected`。

## 构建与测试

```powershell
dotnet build ScriptEngine.IL2CPP.slnx                                    # 引擎会自动部署到 <游戏>/BepInEx/plugins/ScriptEngine.IL2CPP/
dotnet build samples/HelloScript                                         # 示例会自动部署到 <游戏>/BepInEx/scripts/
dotnet run --project tests/ScriptEngine.Tests                            # 离线测试：不启动游戏
```

- 构建需要游戏自带的 `BepInEx/core` 与 `BepInEx/interop` 程序集（所有编译引用都指向那里）。把 `GameDir` 指向游戏根目录：
  复制 `Directory.Build.local.props.example` 为 `Directory.Build.local.props`（已 gitignore）再改，或命令行 `-p:GameDir=...`。
- 游戏运行时引擎 DLL 被锁，部署会失败（`-p:DeployToGame=false` 跳过）；脚本 DLL 不会被锁。
- Mono.Cecil 随 BepInEx 提供，不需要 NuGet。
- 离线测试覆盖改写器、加载上下文的回收、Harmony 补丁的撤销与回收、文件监视 / 命令通道、PDB 匹配等纯托管部分（需要游戏目录里的 DLL，但不启动游戏）。
- `tools/package.ps1` 打 Release zip。`tools/` 下启动 / 驱动游戏的脚本（`start-game.ps1`、`stop-game.ps1`、`start-server.ps1`、`stop-server.ps1`、`send-key.ps1`）是开发环境用的辅助工具，绑定了那套环境的进程名和启动流程；它们只动自己起的进程。

## 许可证与致谢

[MIT](LICENSE)。设计上参考了官方 [BepInEx.Debug ScriptEngine](https://github.com/BepInEx/BepInEx.Debug)（LGPL-3.0）和
[ScriptDebugEngine](https://github.com/wukong2468/BepinEx.Debug.ScriptEngine) 的思路，但**没有复用它们的代码**。
运行时依赖 BepInEx（LGPL-3.0）、Il2CppInterop、Mono.Cecil（MIT），本仓库不分发它们。
