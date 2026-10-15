# ScriptEngine.IL2CPP

**Hot reload for BepInEx 6 / IL2CPP plugins.** Change your code → `dotnet build` → the running game is already executing the new code, a few seconds later. No restart.

[English](README.md) · [中文](README.zh-CN.md)

> The official [BepInEx.Debug ScriptEngine](https://github.com/BepInEx/BepInEx.Debug) supports BepInEx 5 / Mono only (`BaseUnityPlugin`, net3.5).
> Under BepInEx 6 / IL2CPP it is not even recognised as a plugin, and its core technique (`AddComponent` to hang the plugin on a GameObject,
> `Assembly.Load(bytes)` into the default context) does not work on IL2CPP either. This project was re-designed for IL2CPP — it is **not** a port of it.

## Requirements

| | |
|---|---|
| BepInEx | 6.x, IL2CPP / CoreCLR build — tested with `6.0.0-be.4` |
| Il2CppInterop | whichever version BepInEx 6 ships — tested with `1.6.7` |
| Your plugin's TFM | must match the game's CoreCLR — `net10.0` (CoreCLR 10) in the environment this was developed against |
| The plugins you reload | ordinary BepInEx `BasePlugin`s carrying `[BepInPlugin]` |

The engine itself is an ordinary, always-resident `BasePlugin`, installed like any other plugin.
Every number and behaviour described below was measured on a real Unity IL2CPP game running that stack; figures are engine-side unless stated otherwise.

## What it does

- Watches `BepInEx/scripts/` and loads the `BasePlugin` DLLs found there into a **collectible `AssemblyLoadContext`**. The DLLs are read as byte arrays, so **`dotnet build` can overwrite them while the game is running** (nothing holds a file lock).
- Three ways to trigger a reload: a **hotkey** (default `F6`), **file watching** (debounced), and a **command channel** for scripts and AI agents (see below).
- A reload is: call the old plugins' `Unload()` → destroy injected objects they forgot about, turn event handlers they forgot to unsubscribe into no-ops → drop the old context → load the new generation.
- **A failed preparation never touches the running code.** Half-written DLL, PDB lagging behind, the compiler still holding the file, a Cecil failure… the old generation keeps running and the engine retries once the files settle.
- It is friendly to **unmodified** community plugins (see the [plugin author contract](#plugin-author-contract)):
  - injected MonoBehaviours are renamed per generation, so the `Type with FullName … is already injected` error never comes back;
  - `Assembly.GetExecutingAssembly().Location` still returns a real directory inside `scripts/` (assemblies loaded from a byte array normally report an empty `Location`);
  - portable PDBs are preserved — and verified to belong to that exact DLL — so stack traces keep file names and line numbers.
- A reload normally takes tens of milliseconds. When it doesn't, the log names the plugin whose `Load()` is slow (injecting a large MonoBehaviour can take a second or two — that is Il2CppInterop overhead, not the engine).

## Install

1. Download the zip from Releases and extract its **BepInEx** folder into the game root — the directory that contains `BepInEx/` and the game executable:

   ```text
   <GameRoot>/BepInEx/plugins/ScriptEngine.IL2CPP/ScriptEngine.IL2CPP.dll   ← the engine (an ordinary resident plugin, never reloaded)
   <GameRoot>/BepInEx/scripts/                                             ← put the plugin DLLs to hot-reload here (created on first launch)
   ```

2. **Never put the same plugin in both `plugins/` and `scripts/`** — BepInEx and the engine would each create an instance. The engine detects this, skips its own copy and says so in the log.

## Developer workflow

Add a post-build deployment target to your plugin's csproj so the DLL and PDB land in `scripts/` instead of `plugins/`:

```xml
<Target Name="DeployToScripts" AfterTargets="Build">
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(GameDir)/BepInEx/scripts" />
  <Copy SourceFiles="$(TargetDir)$(TargetName).pdb" DestinationFolder="$(GameDir)/BepInEx/scripts" Condition="Exists('$(TargetDir)$(TargetName).pdb')" />
</Target>
```

Then: **launch the game once → edit code → `dotnet build` → it reloads by itself after the ~1 s debounce** (or press `F6` in game).
`BepInEx/LogOutput.log` will show:

```text
[Message:ScriptEngine.IL2CPP] Reload (file) done: generation 3, 3 plugin(s) loaded, 3 unloaded, 0 error(s), 54 ms (prepare 4, unload 9, load 39)
```

If a script ships assets (icons, sounds), keep the DLL and the assets in the same subdirectory of `scripts/` (e.g. `scripts/MyMod/MyMod.dll` + `scripts/MyMod/Icons/`) — `Assembly.Location` then points at that subdirectory.
`scripts/` is scanned recursively, and scripts in the same batch can reference each other.
Two assemblies sharing a simple name (the same dependency copied into two subdirectories, say) are loaded once; the rest are warned about and skipped. A `.dll` that is not actually a .NET assembly (zero bytes, text, an LFS pointer…) is ignored with a warning after it has sat still for 5 seconds, so it never wedges a reload.

### Sending a key when nobody is at the keyboard

Verifying hotkeys has two traps: a key only reaches the plugin while the game window has focus (the log shows `[probe] focus -> False` when it does not, and then pressing is pointless), and **a key another program has registered as a global hotkey never reaches any window at all**. For unattended key delivery use `tools/send-key.ps1`:

```powershell
.\tools\send-key.ps1 F6     # grabs the foreground first, then sends one real key press
.\tools\send-key.ps1 F5
```

It locates the game window **by window class** (`UnityWndClass`) — `Process.MainWindowHandle` returns BepInEx's console window instead — then uses ALT to unlock foreground-switching restrictions plus `AttachThreadInput`/`SetFocus`, waits until it actually owns the focus, and only then fires `keybd_event`.

## Samples

`samples/` contains buildable examples (`dotnet build samples/<name>` deploys to `<game>/BepInEx/scripts/`; the ones marked † are not deployed by default — add `-p:DeployToScripts=true`):

| Sample | What it demonstrates |
|---|---|
| `HelloScript` | The smallest hot-reloadable plugin: an enum config entry, `Assembly.Location`, a stack trace with line numbers |
| `HotkeyTestScript` | Hotkeys: pressing the configured key (default `F5`) prints one line naming the **generation that handled it** (`__g1` → `__g2`), so "hotkey → reload → new generation responds" is directly observable |
| `BehaviourScript` + `SharedLib` | Injected MonoBehaviours (including one nested inside the plugin class) and cross-DLL references; a well-behaved `TidyPlugin` and a `LeakyPlugin` that has no `Unload()` at all (the engine destroys its components for it) |
| `FaultyScript` † | A `Load()` that throws (error with line number, half-attached components cleaned up, other plugins in the same DLL unaffected) and `[BepInIncompatibility]` |
| `EventScript` † | Subscribing to game events / Unity events: unsubscribing properly, unsubscribing from `OnDestroy`, and forgetting to unsubscribe at all (the stale handler is neutralised and reported) |
| `PatchScript` † | Harmony patching and `UnpatchSelf()` |

## Plugin author contract

The engine cannot undo everything you did. To make reloads clean, please:

1. **Implement `Unload()` and return `true`**, and undo inside it everything you set up in `Load()`:
   - Harmony patches: `harmony.UnpatchSelf()`;
   - event subscriptions (game events, Unity events, static events — anything subscribed through interop): `-=` **the same delegate object** (keep the delegate you subscribed with in a field instead of allocating a new one each time);
   - GameObjects / components / textures / audio you created, coroutines and threads.

   Returning `false` (the `BasePlugin.Unload()` default) makes the engine log a warning — it still reloads.
2. **What the engine does as a backstop** (not an excuse to be sloppy — it just keeps one mistake from wrecking a session):
   - **Injected MonoBehaviours / ScriptableObjects**: instances of the old generation's injected types that are still alive after `Unload()` are `DestroyImmediate`d (`SweepLeftovers`, on by default). GameObjects, Canvases, textures and AudioSources *you* created are not covered by this.
   - **Event handlers you forgot to unsubscribe**: every interop delegate the old generation handed to the game is replaced with a no-op at unload time. When it is later invoked it **no longer runs the old code**; instead the log says once
     `A handler from an unloaded script generation was invoked and ignored: …`, naming the generation and the method that failed to unsubscribe.
     (The no-op handler is still in the game's event list — the engine cannot `-=` for you — so subscriber counts keep growing. Unsubscribing is still your job.)
   - **A plugin whose `Load()` threw**: injected components it half-attached to BepInEx's manager object are destroyed, so a half-built plugin does not keep running in that generation.
3. **Do not look up your own injected types by type-name strings** (`Type.GetType("My.Ns.MyBehaviour")`, the namespace part of `GetComponent("MyBehaviour")`).
   The engine rewrites the namespace of injected types to `My.Ns.__g{generation}` (the class name is unchanged). `typeof(MyBehaviour)` is fine.
4. **Do not assume you are loading during game startup** — on a reload, `Load()` runs in the middle of a running game (possibly already inside a level).
5. Every plugin instance owns its own `ConfigFile` and log source (`BasePlugin`'s constructor creates fresh ones), so enum-typed config entries do not throw `InvalidCastException` after a reload.
   Once an old instance is unloaded, the engine turns off its `SaveOnConfigSet` so a stale object cannot write back to the `.cfg`.
6. Destroying a component in `Unload()` and then unsubscribing from its `OnDestroy()` works: the engine defers clearing Il2CppInterop's conversion cache by half a second, which is enough for the `-=` in `OnDestroy` to still find the delegate that was originally subscribed.

## Command channel (automation / AI agents)

No network port is opened — it is pure files. Drop a `<id>.<command>` file into `BepInEx/ScriptEngine.IL2CPP/commands/` (contents are irrelevant):

| Command | Effect |
|---|---|
| `<id>.reload` | Force a reload of every script, even if nothing changed |
| `<id>.unload` | Unload all scripts and load nothing new (until the next reload or file change) |
| `<id>.status` | Report status only |

The engine takes the command file, runs it, and writes `<id>.result.json` (via a temp file plus rename, so any JSON you read is complete). It contains `ok`, the current generation, the loaded plugins,
and the full reload report (loaded/unloaded lists, per-plugin `Load()` timings, `Unload()` return values, warnings, errors, and a phase breakdown).
When a `reload` arrives while files are still being written (the compiler has not finished, DLL and PDB do not match), the engine retries by itself and **only writes the result once the retry has settled**, so a client never receives a failure that is about to be reversed. A command superseded by a later one gets a failure result carrying a `message`.
Result files nobody picks up are swept after 5 minutes. `BepInEx/ScriptEngine.IL2CPP/status.json` is refreshed after every reload / unload / context-collection status change.

The repository ships a client, `tools/reload.ps1`:

```powershell
.\tools\reload.ps1                  # force a reload and print the result JSON; exit 0 ok / 1 engine reported failure / 2 timeout (60 s by default)
.\tools\reload.ps1 -Command status
.\tools\reload.ps1 -Command unload
```

The typical automation loop: `dotnet build` (which deploys to `scripts/`) → `reload.ps1` → parse `ok` / `errors` from the JSON → drive an in-game test script.

## Configuration

`BepInEx/config/com.shiunaya.bepinex.scriptengine.il2cpp.cfg` (also editable through ConfigurationManager; changes to `AutoReload` / `ScriptsDirectory` take effect a few seconds later):

| Section / key | Default | Meaning |
|---|---|---|
| `General / ScriptsDirectory` | empty = `BepInEx/scripts` | Directory to watch; a relative path is resolved against the game root |
| `General / ReloadKey` | `F6` | In-game reload hotkey; `None` disables it |
| `General / LoadOnStart` | `true` | Load the scripts on the first frame after BepInEx has finished loading its plugins |
| `General / AutoReload` | `true` | Reload when files change |
| `General / AutoReloadDelay` | `1.0` | Seconds of silence after the last file change (one build writes several files) |
| `Automation / CommandChannel` | `true` | The command channel and `status.json` |
| `Advanced / CollectibleContexts` | `true` | One collectible context per generation; turn off if injected types misbehave (old generations will then never be collected) |
| `Advanced / SweepLeftovers` | `true` | Destroy leftover instances of the old generation's injected types (and components half-attached by a failed `Load()`) |
| `Advanced / DiagnosePinned` | `true` | When a generation without injected types still cannot be collected, search the static state of Harmony / MonoMod / Il2CppInterop / BepInEx for what still references the old code and log the reference chain (once per game session; may cost about a second) |
| `Advanced / Il2CppGcAfterUnload` | `true` | For generations that *can* be collected, run an IL2CPP + managed GC after unload and verify collection (generations that injected types are known to be uncollectable and skip the GC) |

## Known limitations

| Limitation | Detail |
|---|---|
| A generation that injected Unity types keeps its assembly in memory | Il2CppInterop holds permanent `GCHandle`s for the native trampolines of injected types, so the old `AssemblyLoadContext` cannot be collected (logged as `retained`). Tens to hundreds of KB per reload — negligible for development. The old code is neutralised (instances destroyed, handlers turned into no-ops); only bytes are left behind. |
| Only generations without injected types really unload | A plain `BasePlugin` plus ordinary classes, including one that subscribed to events (the engine neutralises its handlers), has its context collected within tens of milliseconds (covered by real-game runs and offline tests). A script whose patch *target* lives in the host (a game method) while the patch method is in the script is collected too, after `UnpatchSelf()` (offline coverage only — patching a real game method was not attempted, since a wrong patch can keep the game from starting). |
| Harmony patches on the **script's own methods** pin the old context | Harmony / MonoMod keep bookkeeping for the patched method (`PatchManager`, `DetourManager`) that `UnpatchSelf()` does not clear. Patching game methods is unaffected. `DiagnosePinned` logs the reference chain. |
| Every subscribed event handler leaves a small object pair in Il2CppInterop | The IL2CPP object and its CoreCLR wrapper strongly reference each other, so neither GC collects them. The engine cuts the reference to the old code; the few dozen bytes left no longer pin the assembly. |
| Reflecting on injected types through type-name strings breaks | See contract point 3 |
| Patches, self-created objects and coroutines are yours to undo in `Unload()` | The engine only backstops injected-type instances and interop event handlers |
| A reloaded plugin statically referenced by **another plugin** / depended on via `BepInDependency` | The other plugin holds types from the old generation and will not update; the official ScriptEngine behaves the same way |
| A script DLL must not share a name with an assembly already loaded by the host | Copying `0Harmony.dll`, `BepInEx.*.dll` etc. into `scripts/`: the engine skips them with a warning before analysis, and scripts use the host's copy instead |
| `BasePlugin` only | A DLL without `[BepInPlugin]` is loaded as a library (so scripts in the same batch can reference it) and never instantiated |
| A key registered as a global hotkey by another program never reaches the game | Such a key is swallowed at the OS level, and that is true of both physical and synthetic presses — the symptom is "the plugin never receives this key". On the machine this was developed on, `F7` was already taken (`RegisterHotKey(F7)` → 1409); pick a free key instead. |
| `typeof(rewritten type)` inside an attribute argument is not rewritten | Cecil writes custom-attribute blobs back verbatim; an injected type appearing in `[Attr(typeof(X))]` is rare — revisit if it ever shows up |

## How it works (for contributors)

One reload:

1. **Snapshot**: read every DLL/PDB under `scripts/` into byte arrays (`FileShare.ReadWrite|Delete`, closed immediately). Unreadable files, incomplete assemblies, or a sidecar PDB whose GUID does not match the DLL's CodeView directory and that was just written → defer and retry, leaving the old generation untouched. If the content digests (length-prefixed per segment) match the previous run, skip.
2. **Prepare** (pure functions; a failure never harms the old generation): read each DLL with Mono.Cecil (assemblies that the host already loaded under the same name, duplicate simple names, and `.resources` satellites are dropped before analysis),
   discover `BasePlugin`s with BepInEx's own `IL2CPPChainloader.ToPluginInfo` (this must happen **before** renaming — Cecil caches types under the names it read),
   then find every class deriving from `Il2CppObjectBase`, append `.__g{generation}` to the namespace of its outermost declaring type, and rewrite type references to them in the other script assemblies (assemblies that only *reference* are rewritten too);
   replace `Assembly.get_Location()` calls with `ScriptRuntime.GetAssemblyLocation`; rewrite assemblies that needed changes together with fresh portable PDBs and pass the rest through untouched.
3. **Unload, step one (immediate)**: unload newest-first, calling `Unload()` on each (exceptions are logged) → remove them from `IL2CPPChainloader.Plugins` and clear `PluginInfo.Instance` →
   sweep leftover injected instances → replace the old generation's interop delegates with no-ops. The new generation can be loaded right after.
4. **Load**: a fresh `ScriptLoadContext` (falling back to the default context for BepInEx / interop / the BCL); check `BepInIncompatibility` and hard dependencies, topologically sort by `BepInDependency`, then instantiate and register through `IL2CPPChainloader.LoadPlugin`.
   If a plugin's `Load()` throws → log an error with a line number, give it one `Unload()` to clean up, destroy the components it half-attached, and leave the other scripts alone.
5. **Unload, step two (half a second later, driven by the per-frame pump)**: purge static caches in BepInEx / Il2CppInterop that point at old types → drop the references and call `AssemblyLoadContext.Unload()` →
   for generations that can be collected, run a GC and observe with a `WeakReference`; generations that injected types are marked `retained` on the spot and skip the GC entirely.
   The half-second delay exists because components destroyed from `Unload()` only die at the end of the frame, and their `OnDestroy()` usually wants to `-=` — which requires finding the same interop delegate in the conversion cache.

A few traps, all backed by offline tests or real-game logs:

- `ScriptLoadContext` must never cache an `Assembly` it loaded itself: the context → assembly → loader-allocator cycle keeps the CLR from ever collecting it, so `Unload()` never takes effect.
- Cecil's `DefaultAssemblyResolver` only consults and fills its cache in the **single-argument** `Resolve(name)`; the two-argument overload goes straight to the base class — reopening the on-disk interop DLL every time, with nobody to `Dispose` it. A wrapper resolver has to forward the single-argument overload verbatim.
- `ClassInjector` de-duplicates by `Type.FullName`, and the constructor delegate plus the per-method trampolines it creates for injected types are pinned by permanent `GCHandle`s → generations that injected types cannot be collected.
- For every event handler subscribed through interop, Il2CppInterop creates an instance of the injected `Il2CppToMonoDelegateReference` whose managed wrapper and IL2CPP object strongly reference each other (an IL2CPP handle plus a CoreCLR `GCHandle`), so neither GC can free them, and its `ReferencedDelegate` points at the plugin's code:
  so **even after a proper `-=` the old context cannot be collected**, and a handler you forgot to unsubscribe keeps executing old code. The engine replaces that field with a no-op to break the chain.
- Only `Resources.FindObjectsOfTypeAll` finds components on `BepInEx_Manager` (`FindObjectsOfType` does not); and each call walks the entire Unity object table, so the sweeper scans once per base class (MonoBehaviour / ScriptableObject) and then filters by native class pointer.
- `ClassInjector.IsTypeRegisteredInIl2Cpp` has side effects: it triggers the type's static constructor and puts the type into `Il2CppClassPointerStore.ourPointerReaders` (a strong `Type` reference). Ask the side-effect-free `IsManagedTypeInjected` instead.

## Building and testing

```powershell
dotnet build ScriptEngine.IL2CPP.slnx                                    # the engine also deploys to <game>/BepInEx/plugins/ScriptEngine.IL2CPP/
dotnet build samples/HelloScript                                         # samples deploy to <game>/BepInEx/scripts/
dotnet run --project tests/ScriptEngine.Tests                            # offline tests: no game is launched
```

- Building requires the game's own `BepInEx/core` and `BepInEx/interop` assemblies (that is where every compile-time reference points). Set `GameDir` to the game root:
  copy `Directory.Build.local.props.example` to `Directory.Build.local.props` (git-ignored) and edit it, or pass `-p:GameDir=...`.
- While the game is running the engine DLL is locked and deployment fails (use `-p:DeployToGame=false` to skip it); script DLLs are not locked.
- Mono.Cecil ships with BepInEx, so no NuGet packages are needed.
- The offline tests cover the rewriter, context collection, Harmony patch undo and collection, file watching / the command channel, PDB matching and the rest of the pure managed logic (they need the game's DLLs, but never start the game).
- `tools/package.ps1` builds a Release zip. The scripts under `tools/` that launch or drive a game (`start-game.ps1`, `stop-game.ps1`, `start-server.ps1`, `stop-server.ps1`, `send-key.ps1`) are helpers for the environment this was developed in and are wired to that setup's process names and launch flow; they only ever touch processes they started themselves.

## License and credits

[MIT](LICENSE). The design was informed by the official [BepInEx.Debug ScriptEngine](https://github.com/BepInEx/BepInEx.Debug) (LGPL-3.0) and
[ScriptDebugEngine](https://github.com/wukong2468/BepinEx.Debug.ScriptEngine), but **no code was reused** from either.
At runtime it depends on BepInEx (LGPL-3.0), Il2CppInterop and Mono.Cecil (MIT); none of them are redistributed here.
