# 构建与发布维护

面向仓库维护者。使用者只需要 README。

## 自动发布

版本唯一来源是 [Version.props](../Version.props)。发版：改版本号 → 在 `CHANGELOG.md` 补一节同名的版本说明
（`## [0.2.0] - 日期`，`tools/release/notes.ps1` 靠这个标题抽 Release 正文）→ 提交并推送默认分支。

`.github/workflows/ci.yml` 的两个 job：

| job | 什么时候跑 | 干什么 |
|---|---|---|
| `meta` | 所有 push / PR（**含来自 fork 的 PR**） | 校验 `Version.props` 的版本在 `CHANGELOG.md` 里有同名小节且不是占位。不需要游戏程序集，所以 fork 也能跑。 |
| `build` | 非 PR 事件，且仓库是 `ChenRuoT/ScriptEngine.IL2CPP` | 取私有编译参考 → 跑离线测试 → Release 构建 → 打包 zip + SHA256 → 上传 artifact → 默认分支 / `v*` tag 时发 Release。 |

发版细节：

- 发布用 GitHub 自动提供的 `GITHUB_TOKEN`（`contents: write`），不需要个人访问令牌。
  该 token 建的 tag **不会再触发一次工作流**，所以建 tag 和发版在同一次运行里完成。
- `v*` tag 触发时，tag 名必须与 `Version.props` 的 `v<版本>` 一致，否则 `publish.ps1` 直接失败。
- 同版本已有正式 Release 时跳过发布，**绝不覆盖**已发布的版本；失败留下的 draft 会在重跑时补上传并公开。
- Release 先建 draft，zip 和 `.sha256` 两个附件都传完才公开。
- 测试 / 编译 / 打包任何一步失败都不发布。
- ⚠️ `build` job 的 `if` 里写死了仓库全名。**改名或转移仓库后要同步改这一行**，否则 CI 会静默地不再发版。

## 私有编译依赖

编译引用指向游戏目录里的 `BepInEx/core` 与 `BepInEx/interop`（见 [build/GameReferences.props](../build/GameReferences.props)），
而 interop 程序集是从游戏本体生成的派生文件，**不能随公开仓库分发**。所以：

- 公开仓库不存放游戏程序集。私有仓库 **`ChenRuoT/AyaHitmarker-BuildRefs`** 里放着从目标游戏这两层目录提取的 DLL 压缩包
  （`eft-47965-buildrefs.zip`，游戏 build 47965），AyaHitmarker 和本项目**共用同一份**。
- CI 通过本仓库的项目 secret **`BUILD_REFS_SSH_KEY`** 使用那个私有仓库的**只读 deploy key**：密钥没有写权限，也读不到其它仓库。
  同一把只读 deploy key 可以同时供两个仓库使用（deploy key 是登记在依赖仓库上的）。
- [tools/release/buildrefs.lock.json](../tools/release/buildrefs.lock.json) 锁定私有仓库 commit、压缩包名与 SHA256；
  CI 会逐字节校验 SHA256 再解压。更新依赖时这三个字段 + `gameBuild` 一起改。
- 私有依赖解压在 `.buildrefs/`，已从 git 和 MSBuild 的默认 item globs 里排除
  （`Directory.Build.props` 的 `DefaultItemExcludes`）。**这条不能去**：那些 interop 程序集和 `HintPath` 指向的是同一批文件名，
  一旦被收进 `@(None)`，MSBuild 会把它们当 `CandidateAssemblyFiles` 排在 `AssemblySearchPaths` 首位，顶掉 `HintPath` 引用
  —— 编译能过但语义可能全错。
- 不使用 Il2CppDumper 的 `DummyDll`：它们不是 interop 实现。
- 云端 job 只在主仓库的 push / dispatch 使用私有依赖；**来自 fork 的 PR 没有这个访问路径**，所以只跑 `meta`。
  外部 PR 只能由维护者在本地按下面的步骤验证。
- 不要把编译参考作为 artifact 或 Release 附件上传；安装包只允许引擎 DLL/PDB、说明文档和 `BepInEx/scripts/README.txt`。

## 首次初始化（本仓库只差这一步）

`gh` 已登录、仓库已建、`GITHUB_TOKEN` 由 Actions 提供，唯一需要人工做的是把依赖仓库那把只读私钥写进 secret：

```powershell
gh secret set BUILD_REFS_SSH_KEY --repo ChenRuoT/ScriptEngine.IL2CPP < <私钥文件路径>
gh secret list --repo ChenRuoT/ScriptEngine.IL2CPP    # 确认已写入（只显示名字和时间，不显示值）
```

私钥不入库。若密钥丢失或需要轮换：生成新 key → 公钥登记为 `AyaHitmarker-BuildRefs` 的只读 deploy key →
私钥写进本仓库 secret → 两个仓库都验证一次 → 再删旧 deploy key。

⚠️ 依赖仓库上可以有多把只读 deploy key，互不影响 —— 新仓库直接新加一把就行，**不必覆盖**已有 secret。
⚠️ **用 `gh repo deploy-key add` 添加的 key 会绑在 `gh` 的登录 token 上**：一旦撤销那个 token / gh app 的授权，
这把 deploy key 会被一起删掉，CI 随即在 checkout 那一步挂掉。不想担这个风险就从网页加
（依赖仓库 → Settings → Deploy keys → Add deploy key，不要勾 write access）。
当前 `ScriptEngine.IL2CPP CI (read-only)` 是用 `gh` 加的，私钥生成在本机 `~/.ssh/buildrefs_ci`（无口令）；
记住这份风险，或者找机会改成网页添加。

## 本地构建和打包

需要 .NET 10 SDK，以及目标游戏已经生成好的 BepInEx interop。`GameDir` 三选一（优先级从高到低）：

```powershell
$env:SE_GAME_DIR = '<游戏根目录>'   # ① 环境变量（一次 setx 长期有效）
# ② 复制 Directory.Build.local.props.example 为 Directory.Build.local.props 再改（该文件不入库）
# ③ 命令行 dotnet build -p:GameDir="E:/OtherEFT"
```

发布前本地完整跑一遍（与 CI 一致）：

```powershell
dotnet run --project tests/ScriptEngine.Tests -c Release -warnaserror   # 87 项离线测试，不启动游戏
pwsh -File tools/release/package.ps1                                     # 出 zip + .sha256
pwsh -File tools/release/notes.ps1 -Version 0.1.0 -OutFile notes.md      # 预览 Release 正文
```

- `-p:DeployToGame=false` 让发布构建不写入正在运行的游戏；日常 `dotnet build` 仍会自动部署。
  （测试项目里各引用已经带上了 `DeployToGame=false` / `DeployToScripts=false`，所以跑测试不会动游戏目录。）
- 打包只使用新编译出来的 `src/ScriptEngine.IL2CPP/bin/<Configuration>/` 产物，不从游戏安装目录捞文件；
  并检查 DLL 的 `ProductVersion` 与 `Version.props` 一致，防止把陈旧产物打进 zip。
- zip 顶层只有 `BepInEx/` 加几份文档，没有多余版本目录；不带 `.cfg`，不覆盖玩家设置。
- 产物在 `artifacts/release/`，该目录不入库。

## 验证边界

CI 覆盖可执行的托管逻辑、编译、打包内容与版本一致性。**没有**覆盖真机行为：热键、注入的 MonoBehaviour 在 Unity 里的实际生命周期、
命令通道在真实帧循环里的时序、以及 AyaHitmarker 这类真实社区插件的重载效果，都需要在游戏里验证（见 README 与 AGENTS.md）。

改引擎行为后至少手工确认一次：真机重载 N 次 → `LogOutput.log` 里代次递增、`retained` / `collected` 与预期一致、无 `error`。
