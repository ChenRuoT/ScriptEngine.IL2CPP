# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-10-16

A main mod and a mod that depends on it can now both live in `scripts/`, even when the dependent mod does not declare `[BepInDependency]`.

### Changed

- **Load order now follows assembly references as well as `[BepInDependency]`.** A script that only *references* another script's assembly (without declaring `[BepInDependency]`) used to be loaded in GUID order, so its `Load()` could run before the plugin it uses. Now the referenced plugin is loaded first (transitively, through library scripts). If the references form a cycle, the engine falls back to `[BepInDependency]` only.
- Samples `DepMainScript` / `DepConsumerScript` show a main mod and a dependent mod living together in `scripts/` (the case the original ScriptEngine cannot handle).

## [0.1.0] - 2026-10-15

First release. It works, it is verified on a real IL2CPP Unity game, and the limitations below are known and documented rather than hidden.

### Added

- **Hot reload for `BasePlugin` plugins.** `BepInEx/scripts/` is watched (recursively); the DLLs in it are read as byte arrays and loaded into a collectible `AssemblyLoadContext`, so `dotnet build` can overwrite them while the game runs — no file lock, no game restart.
- **Three reload triggers:** the in-game hotkey (default `F6`), a debounced `FileSystemWatcher`, and a file-based command channel (`commands/<id>.reload|unload|status` → `<id>.result.json`, plus `status.json`) for scripts and AI agents. `tools/reload.ps1` is a ready-made client.
- **Cecil-based preparation stage:** injected MonoBehaviours get a per-generation namespace suffix (`My.Ns.__g{N}.MyBehaviour`) so `ClassInjector`'s `FullName` de-duplication never fires; the same rewrite is applied to type references in the other script assemblies. `Assembly.get_Location()` calls are redirected to `ScriptRuntime.GetAssemblyLocation`, and portable PDBs are passed through (verified against the DLL's CodeView GUID) so stack traces keep file names and line numbers.
- **Two-phase unload:** `Unload()`, contract checks, leftover-instance sweeping and interop-delegate neutralisation happen immediately; static-cache purging, reference dropping, `AssemblyLoadContext.Unload()` and collection verification happen half a second later, so `-=` in a component's `OnDestroy()` still finds the delegate it subscribed with.
- **`StaleDelegateNeutralizer`:** every interop delegate the old generation handed to the game has its `ReferencedDelegate` swapped for a same-signature no-op. A handler that was never unsubscribed no longer executes old code; it is reported once instead.
- **`LeftoverSweeper` / `StaticCachePurger` / `PinFinder`:** leftover injected instances are destroyed, static caches pointing at old types are purged, and when a generation without injected types still refuses to be collected, `PinFinder` walks the static state of Harmony / MonoMod / Il2CppInterop / BepInEx and logs the reference chain.
- **Failure isolation:** a failed prepare leaves the running generation untouched (half-written DLL, missing PDB, compiler still holding the file, Cecil failure) and is retried once the files settle. A plugin whose `Load()` throws gets a line-numbered error, one `Unload()` call, and precise removal of the components it already attached — other scripts in the same batch are unaffected. `[BepInIncompatibility]` is checked in both directions.
- **Configuration** (`BepInEx/config/com.shiunaya.bepinex.scriptengine.il2cpp.cfg`): scripts directory, reload hotkey, load-on-start, auto-reload and its debounce, command channel, collectible contexts, leftover sweeping, pinned diagnostics, post-unload GC.
- **Samples:** `HelloScript`, `BehaviourScript` + `SharedLib`, `HotkeyTestScript`, and (not deployed by default) `FaultyScript`, `EventScript`, `PatchScript`.
- **Offline test suite** (87 checks, no game launch) covering the rewriter, load-context collection, Harmony patch undo and collection, file watching, the command channel, PDB matching and the pinned-reference diagnostics.
- **Tooling:** `tools/package.ps1` (Release zip), `tools/reload.ps1` (command-channel client), `tools/send-key.ps1` (unattended key delivery to a Unity window), and the game launch/stop helpers used during development.

### Known limitations

See [Known limitations](README.md#known-limitations) for the full list. In short:

- A generation that injected Unity types cannot be collected: Il2CppInterop pins the native trampolines of injected types with permanent `GCHandle`s. Tens to hundreds of KB are retained per reload. The old code is neutralised, so what stays behind is bytes, not behaviour.
- Harmony patches applied to the **script's own methods** pin the old context through `PatchManager` / `DetourManager` bookkeeping. Patching host (game) methods does not.
- Unsubscribing events, undoing patches, and destroying self-created objects/coroutines remain the plugin author's job; the engine only backstops injected instances and interop event handlers.
- Patching a real host game method was not attempted, because a bad patch can keep the game from starting.

[0.2.0]: https://github.com/ChenRuoT/ScriptEngine.IL2CPP/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ChenRuoT/ScriptEngine.IL2CPP/releases/tag/v0.1.0
