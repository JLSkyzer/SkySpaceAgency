# SkySpaceAgency + K2-D2 side by side: collision inventory

Date: 2026-11-19. Read-only research, nothing modified.

Sources used:
- SSA working tree: `D:/KSPReduxModding/SkySpaceAgency` (code under `Assets/SkySpaceAgency/Code`).
- K2-D2 1.4.1: `git show upstream/main:...` (upstream/main = `8273fb8 Merge branch 'dev'`).
- Redux 26w46a code: the existing full decompilation of `Ksp2Redux.dll` 26w46a in
  `D:/KSPReduxModding/.research/part-scale/decompiled46a/` (ilspycmd 9.1). `UitkForKsp2.dll`, `FlightPlanRedux.dll`,
  `KERNEL.dll` and the other installed mods were decompiled into the scratchpad for this note.
- The Addressables package source in `SkySpaceAgency/Library/PackageCache/com.unity.addressables@42676f2a154e`.
- The SSA bundle installed in the game, decompressed with a small LZ4 reader (scratchpad `ufs.py`).

Labels: **doc** = official XML doc or comments, **IL** = decompiled game or mod code, **code** = our repo or upstream
source, **bundle** = bytes of a built bundle, **unverified** = reasoning only.

---

## 1. Keyboard shortcut

**Where both mods read it**
- SSA: `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs:345`:
  `Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O)`. It only fires when `ValidScene()` is true
  (flight or map, mod enabled).
- K2-D2 1.4.1: `Assets/K2D2/Code/K2D2_Plugin.cs:293`, the same check (`in_flight && window_allowed`).
- Neither mod uses Ctrl+O. Ctrl+O belongs to the game: `VAB/loadWorkspace`.

**Verdict: COLLIDES** (code). With both mods installed, Alt+O toggles both windows at once.

**Default bindings of the game**
Source: IL, the `InputActionAsset.FromJson(...)` string in `KSP.Input/GameInput.cs`, parsed in
`scratchpad/gameinput.json`. The user's `Global/InputBindings.json` only holds overrides (W/S pitch).

- Alt or Ctrl alone:
  - `Flight/TrimModifier` = leftAlt. While Alt is held in flight, the pitch, yaw and roll keys change **trim**
    (`FlightInputHandler.ApplyManualInputToFlightControlState`). So **Alt+W/S/A/D/Q/E are trim keys**.
  - `Flight/PhysicsWarpToggle` = left or right Alt. `Flight/CameraFineMovement` = Alt.
  - `Flight/hotKeyModifier` = Ctrl. `Flight/ThrottleDelta` = Ctrl/Shift.
  - `MapView/altKeyModifier` and `ctrlKeyModifier`. `VAB/modifier` = Alt. `VAB/disableStackAttachment` = Alt.
    `KSC/PhysicsWarpToggle` = Alt.
- Combinations:
  - Global: Alt+F7 (debug window), Alt+F8 (cheat menu), Ctrl+Shift+F8.
  - Flight: Alt+mouse wheel (camera FOV).
  - VAB: Ctrl+C/X/V/N/S/O/Z/Y/L/F/A/P, Alt+X (mirror symmetry), Ctrl+Shift+F/Q.
- Hard-coded in Redux (IL):
  - Alt+C: Space Warp console (`SpaceWarp2.UI.Console/SpaceWarpConsole.cs:45`);
  - Alt+M: mod list (`SpaceWarp2.UI.ModList/ModListController.cs:188`);
  - Alt+F12: DebugTools (`DebugTools.Utils/Configuration.cs:23`).
- Single letters bound in Flight: a b c d e f g h i j k l m n q r s t u v w x z. **Free letters in flight: o, p, y.**
  MapView binds only M.
- Installed mods (IL):
  - FlightPlanRedux: **Alt+P** (`FlightPlanReduxPlugin.cs:159`, KeyCode 112 + 308);
  - KERNEL: **Alt+K** (`KernelSettings.cs:21`, KeyCode 107 + 308);
  - the others (AgencyHeritage, KerbalistTelemetryBridge, WayfarersWingsRedux, KSP1PartMod, PartScale): no key.

**The candidate keys**
| Key | Status |
|---|---|
| **Alt+S** (from the mockup `docs/design/ui/settings.svg`) | **Bad.** S is Pitch, and with Alt held it is pitch trim: each press would also trim the vessel. |
| Alt+K | Taken by KERNEL. K also fires RCS TranslateY. |
| Alt+J | J is RCS TranslateX: it fires RCS while opening the window. |
| Alt+P | Taken by FlightPlanRedux. |
| Alt+O | Taken by K2-D2. |
| **Alt+Y** | **Free.** Y has no flight or map binding, and only Ctrl+Y (redo) in the VAB, where SSA does not read the key. |

**Recommendation**: default **Alt+Y**, and update the settings mockup.

**Making it configurable is cheap.** Redux has
`SpaceWarp2.API.Input.KeyboardShortcut` (`.Down` checks the modifiers + `GetKeyDown`, and is silent while a
rebind is in progress) and a settings component `Redux.UI.Settings.Component/SettingsKeyboardShortcut.cs`.
FlightPlanRedux and KERNEL both use it:

```csharp
SWConfiguration.Bind<KeyboardShortcut>("Keybinds", "Toggle window", new KeyboardShortcut(KeyCode.Y, KeyCode.LeftAlt), "...")
```

In `Update`, the check becomes `shortcut.Value.Down`: about 5 lines. To keep it in our own settings instead
(`KTools/Settings.cs`, `Setting<T>`), store the string and parse it with `new KeyboardShortcut(string)`. Our own
Settings tab could later use `InputHelper.KeyboardShortcutRebindingOperation` (IL).

## 2. AppBar buttons, window ids, static id strings

| Item | SSA | K2-D2 1.4.1 | Collides? |
|---|---|---|---|
| Flight AppBar id | `BTN-SkySpaceAgencyFlight` (`K2D2_Plugin.cs:76`) | `BTN-K2D2Flight` (`:71`) | No |
| OAB AppBar id | `BTN-SkySpaceAgencyOAB` (`:77`), registered | `BTN-K2D2OAB` (`:72`), registration commented out | No |
| KSC id | `BTN-SkySpaceAgencyKSC` (unused) | `BTN-K2D2KSC` (unused) | No |
| `WindowId` | `"SkySpaceAgency"` (`:234`) | `"K2D2"` (`:200`) | No |
| VAB window | `VabToolsWindow` (its own id) | none | No |

- `WindowId` becomes the GameObject name and the PanelSettings name (`UitkForKsp2.API/Window.cs:88`,
  `UitkForKsp2.Panel/PanelFactory.cs:29`, IL). Each window has its own PanelSettings, so neither mod's USS can
  restyle the other.
- `K2D2Window.cs:60` and `VabToolsWindow.cs:174` find their button with `GameObject.Find(<button id>)`. The ids
  differ, so this is safe.
- **Already done in SSA.** The only thing to do is move the strings into `ModIdentity`, so that a K2-D2 1.5.0
  built from our code gets `BTN-K2D2*` and `"K2D2"` back.

## 3. Harmony

- SSA: no `Harmony` or `HarmonyPatch` in `Assets/SkySpaceAgency/Code` (grep). `0Harmony.dll` is only an asmdef
  reference.
- K2-D2 1.4.1: same (`git grep` only matches `K2D2.asmdef:12`).
- Redux never calls `PatchAll` for a mod on its own. `KerbalMod.CreateHarmonyAndPatchAll()` would use
  `new Harmony(SWMetadata.Guid)`, so the id would be the mod_id (IL, `Redux.ExtraModTypes/KerbalMod.cs:83`).

**Verdict: no collision.** If SSA ever adds patches, they get the id `SkySpaceAgency` through that helper.

## 4. Same full type names in two assemblies

- **Mod loading** (IL, `SpaceWarp2.API.Backend.Modding/PluginRegister.cs:233,296`):
  - each mod DLL is loaded by `Assembly.LoadFile`;
  - `FindModType` looks for the `ISpaceWarpMod` type **inside that assembly only**;
  - `UnloadedMod.Load` adds the component to `ReduxManager.GetAlwaysLoadedObject(SWMetadata.Guid)`, keyed by mod_id.

  Two `K2D2.K2D2_Plugin` types in two assemblies therefore load as two separate mods.
- **CLR**: a type's identity includes its assembly, so the statics (`K2D2_Plugin.Instance`,
  `K2D2PilotsMgr.Instance`, `NodeExPilot.Instance`, `SettingsFile.Instance`, `WarpGate` …) are separate per
  assembly. No cross-talk through statics.
- **UXML custom controls (`[UxmlElement]`)**: in Unity 6000.6 the player builds the elements from serialized
  `UxmlSerializedData` objects (`[SerializeReference]`).
  - **The references are assembly-qualified** (bundle). The installed SSA bundle contains strings such as
    `K2UI.Group+UxmlSerializedData, SkySpaceAgency`, `K2UI.K2Toggle+UxmlSerializedData, SkySpaceAgency` and
    `K2UI.Tabs.TabPage+UxmlSerializedData, SkySpaceAgency`.
  - The K2UI namespace is `K2UI`, not `K2D2.K2UI`.
  - The player does not use a global registry keyed by uxml name: the legacy `UxmlFactory` registry is gone, and
    SSA no longer registers any factory (`K2D2_Plugin.cs:117-121`).
  - **Verdict: no collision** between `SkySpaceAgency` and `K2D2` assemblies, even with identical `K2UI.*` names.
  - **Condition**: the assembly names must differ, so `SkySpaceAgency.dll` vs `K2D2.dll`.
  - **Pitfall**: a future K2-D2 1.5.0 built from our tree must be compiled as `K2D2` (asmdef `name`). The
    KSP1PartMod 0.16.2 lesson: a DLL renamed by `csc -out` changed the assembly name, and the types written in the
    saves no longer resolved.
- **`Type.GetType`**:
  - SSA only uses it for other mods (`K2D2OtherModsInterface.cs:47,67`, assembly-qualified with their GUIDs, and
    the class is dead code: the call in `Update` is commented out, `K2D2_Plugin.cs:335`);
  - K2-D2 uses it for `Ksp2Redux` types.

  No lookup of their own types by name. No `[assembly:]` attributes. No `TypeNameHandling` in either.
- **The real type-name problem is item 8 (FlightPlanRedux).**

## 5. Addressables

**What each mod uses (code)**

| | SSA | K2-D2 1.4.1 |
|---|---|---|
| UXML loaded | `AssetsLoader.LoadUxml("K2D2_Window.uxml")` (`K2D2_Plugin.cs:223,264`) | same (`:193`) |
| Address built | `"Assets/UI/K2D2_UI/" + path` (`KTools/AssetsLoader.cs`) | same |
| Address in the group | `m_Address: Assets/UI/K2D2_UI/K2D2_Window.uxml` (`addressables_SkySpaceAgency_all.asset:19`) | same address (`addressables_K2D2_all.asset:19`) |
| Group asset GUID (all group) | `0130084ab70381a40b9d1e906d480b60` | **`0130084ab70381a40b9d1e906d480b60`** (inherited) |
| Other group GUIDs (parts, cbs…) | identical to K2-D2 (`SkySpaceAgency.asset`) | same |
| `m_InternalBundleIdMode` | 1 = GroupGuidProjectIdHash | 1 |
| `cloudProjectId` | empty | empty |
| `productGUID` | `5be2ef0c…` | `5be2ef0c…` (the same) |
| `m_BuiltInBundleNaming` | 0 = ProjectName | 0 |
| Script / prefab labels (swinfo) | `SkySpaceAgency_patches` / `SkySpaceAgency_prefab_patches` | `K2D2_patches` / `K2D2_prefab_patches` |
| Bundle file installed | `addressables_skyspaceagency_all_assets_all_dcd46….bundle` + `0a14868f…_unitybuiltinassets_cdd91….bundle` | not checked (no K2-D2 install here) |

**How Redux merges the catalogs** (IL)
- `SpaceWarp2.InternalUtilities/AssetHelpers.LoadModCatalog` calls `Addressables.LoadContentCatalogAsync` once per
  mod and records the locator on that mod's descriptor.
- All the catalogs end up in the single global `Addressables.ResourceLocators` list. `AddResourceLocator` simply
  appends (Addressables source, `Runtime/AddressablesImpl.cs:247`).
- `KerbalMonoBehaviour.Assets` is the **game's** `AssetProvider`, not a per-mod one (`KSP.Game/KerbalMonoBehaviour.cs:32-40`).
- `AssetProvider.LoadAssetAsync(key)` goes to `FeatureAssetIndex.TryResolveKey`.
  - Since every address in a mod catalog is in `_gatedAddresses`, the gate collects the locations of **every**
    catalog for that key.
  - It then calls `Filter` and takes `.FirstOrDefault()`.
  - `Filter` logs `[Features] More than one enabled feature provides '<address>' under 'its own key'` when two
    kept locations share an address (`Redux.Features/FeatureAssetIndex.cs`, `TryResolveKey`, `Filter`).
- Without the gate, `Addressables.LoadAssetAsync(key)` also takes the first location in locator order
  (`AddressablesImpl.GetResourceLocations`).

**Verdict 5a, same address: COLLIDES** (IL + code).
- Both mods ask for `Assets/UI/K2D2_UI/K2D2_Window.uxml`. Each gets the copy from whichever catalog was loaded
  first, and the log gets a `[Features]` error.
- The mod that loses gets the other mod's window tree. With SSA's code on K2-D2's UXML, the element names and
  types differ, so expect null queries or exceptions in `K2D2Window`. With K2-D2 on SSA's UXML, the same.
- The order of the catalogs is not proven. Mods are registered in `GetFiles("swinfo.json", AllDirectories)`
  order, and the catalog loading order was not traced.

**Verdict 5b, same internal AssetBundle name: VERY LIKELY COLLIDES** (Addressables source + code; the failure
itself is not seen in game).
- The internal bundle name is built in `BuildScriptPackedMode` (lines 1207-1214, 1249, 1321, 1343, 1191-1197):
  1. `groupHash = HashingMethods.Calculate(group.Guid, Application.cloudProjectId)`;
  2. `name = (groupHash + "_assets_all.bundle").ToLower()`;
  3. internal name = `HashingMethods.Calculate(name) + ".bundle"`.
- That name depends **only** on the group GUID and the cloud project id. Both mods have the same group GUID and an
  empty `cloudProjectId`, so they produce the **same internal AssetBundle name**, whatever the file name.
- Unity refuses to load a second AssetBundle with the same internal name ("another AssetBundle with the same files
  is already loaded"). One of the two mods would then have no UI at all.
- **Unverified**: the exact error text in this Unity version. Comparing the `CAB-…` name inside K2-D2's release
  bundle with ours (`CAB-f714aa7d5728088e5d2ce6dbe81fa8b3…`) would settle it without a test. I did not download
  the K2-D2 release (a download needs your permission).
- Other mods are not affected: their group GUIDs differ (KSP1PartMod `6558ec34…`). The clash comes from the fork.

**Verdict 5c, `unitybuiltinassets` bundle: POSSIBLE collision** (Addressables source).
- The prefix is `Hash128.Compute(<project folder name>)` (`GetBuiltInBundleNamePrefix`, `GetProjectName()`, lines
  245-262 and 1028).
- Two projects built in folders with the same name produce the same builtin bundle name. That is likely for a K2-D2
  1.5.0 built from a clone of our tree. IanMealworm's folder name is unknown.
- Fix: `m_BuiltInBundleNaming: 2` (Custom) with `mBuiltInBundleCustomNaming: skyspaceagency` in SSA
  (`k2d2` in 1.5.0).

**Labels**: distinct (`SkySpaceAgency_patches` vs `K2D2_patches`). No collision.

**Catalog `m_LocatorId`**: every mod's catalog is `AddressablesMainContentCatalog`, ours included. It is harmless
(the list appends, and several mods already coexist).

## 6. Settings files and folders

- Both resolve paths from `Assembly.GetExecutingAssembly().Location`:
  - SSA (`K2D2_Plugin.cs:48-71`): `skyspaceagency_settings.json`, `skyspaceagency_landing_{atmo,vac}.json`, `vab/`,
    `plans/`;
  - K2-D2 (`:53-64`): `k2d2_settings.json`, `k2d2_landing_{atmo,vac}.json`.
- Each mod has its own folder (`mods/SkySpaceAgency/…`, confirmed in the installed game), so even identical names
  would not clash.
- `SWConfiguration` is `<mod folder>/<ModID>-config.json` (IL, `PluginRegister.cs`, `RegisterMod`).
- `AssetsLoader.LoadIcon` reads `<SWMetadata.Folder>/assets/images/icon.png`.
- No `PlayerPrefs`, no `Application.persistentDataPath` in SSA (grep).

**Verdict: no collision.** For a 1.5.0 built from our code, the `skyspaceagency_*` file names must come from
`ModIdentity`.

## 7. Saves, ECS, statics, ids, logs, features

- **Saves / ECS**: SSA defines no `PartComponentModule`, `PartBehaviourModule`, `IComponentData`, system, Newtonsoft
  `$type` or save-flow step (grep). Its `IOProvider` calls (`VabLoader.cs:20-35`) only serialize game types. Plans
  are JSON in the mod folder. **Nothing of SSA's is written into a save**, so no `SavedTypeResolver` issue (that one
  was specific to KSP1PartMod's module).
- **Static singletons**: separate per assembly (item 4). No cross-talk.
- **mod_id and features**: `KerbalMod.Enabled => SWMetadata.Enabled`, per descriptor. `ActiveFeatures.IsModEnabled`
  takes the mod id. `PluginList.RegisterPlugin` skips a duplicate mod_id (case-insensitive, `PluginRegister.cs`).
  SSA = `SkySpaceAgency`, K2-D2 = `K2D2`. **No collision**, but 1.5.0 must keep `K2D2`.
- **Mod GameObject**: keyed by `SWMetadata.Guid` (IL, `UnloadedMod.cs:31`). Distinct.
- **Logs**: the main logger is `LogManager.GetLogger(Guid)` (IL, `SpaceWarpPluginDescriptor.cs:164`), giving
  `[SkySpaceAgency]` vs `[K2D2]`. The sub-loggers are already `SkySpaceAgency.*` in SSA (grep: no
  `GetLogger("K2D2…")`).
  - **Readability problem**: message prefixes such as `[K2D2_Plugin]` (`K2D2_Plugin.cs:142,226,267`),
    `[NodeEx]`, `[Landing]`, `[Transfer]` are generic. Exception stack traces print `K2D2.Landing.LandingPilot…`
    without the assembly. With both mods installed, `Player.log` cannot tell whose code threw.
- **swinfo**: `conflicts: []` in both, and `incompatibilities: []` in both mod assets. Keep it that way.
- **Behaviour (not a name collision)**:
  - both mods can fly the same vessel at the same time (throttle, SAS, warp) and would fight;
  - SSA's `WarpGate.Tick()` runs every frame in flight **before** `ValidScene()`, so even when SSA is disabled in
    the campaign (`K2D2_Plugin.cs:330`). It drops a rails burn at throttle 0 that SSA did not ask for. A K2-D2 burn
    is at throttle > 0, so this is probably harmless, but it should be gated on `Enabled`.
  - both set `Debug.developerConsoleVisible = false` every frame (harmless).

## 8. Other mods that look for "K2D2"

- **FlightPlanRedux** (installed, IL, `FlightPlanRedux.Bridges/K2D2Bridge.cs`):
  - it scans `AppDomain.CurrentDomain.GetAssemblies()` and takes the **first** assembly where
    `GetType("K2D2.K2D2_Plugin")` is not null;
  - then it reflects `Instance`, `FlyNode`, `StopFlyNode`, `IsFlyNodeRunning` and `GetStatus`;
  - it gates on `ActiveFeatures.IsModEnabled("K2D2")`.
- SSA still has `K2D2.K2D2_Plugin` with all five members (`K2D2_Plugin.cs:464-483`).
- **Verdict: COLLIDES** (IL + code). With both installed, FlightPlan's Execute button may drive **SSA's**
  NodeExPilot while it believes it is K2-D2. Which one wins depends on assembly order (unverified).
- With SSA alone, FlightPlan never offers Execute, because mod `K2D2` is absent. SSA therefore gains nothing from
  keeping the name today.
- **After the rename**, FlightPlan finds only the real K2-D2. SSA would need its own bridge (or a FlightPlan PR) to
  be callable.
- `K2D2OtherModsInterface` (SSA → FlightPlan / MNC) is dead code and creates no collision. It only looks for
  *other* mods by their GUID.
- No other installed mod references K2D2 (grep of the installed DLLs and swinfos: only FlightPlanRedux.dll and
  SkySpaceAgency.dll contain the string).

## 9. Other findings

- **Shared Unity asset GUIDs**: SSA's UXML, USS, font and image assets have the same `.meta` GUIDs as K2-D2's. This
  is harmless at runtime (bundle objects are addressed by CAB + PathID). It matters only through the group GUIDs
  (5b) and with `GroupGuidProjectIdEntriesHash`.
- **`productGUID`** is the same in both projects. Not used by Addressables naming (only `cloudProjectId`, empty).
  No runtime effect found.
- **Descriptions**: `SkySpaceAgency.asset` and `swinfo.json` still say "(Alt-O or AppBar)". Update them with the
  new key.
- **Ctrl+O** is not used by either mod. In the game it is VAB "Load workspace".

---

## (a) Fixes SSA needs

Most go through a central `ModIdentity` (mod id, display name, file prefix, AppBar ids, window id, logger prefix,
UXML folder, default shortcut), so that a K2-D2 1.5.0 is the same code with other constants.

1. **New Addressables group GUIDs** for all SSA groups (`addressables_SkySpaceAgency_all` and the six
   `SkySpaceAgency_*`, plus `Default Local Group` for safety).
   - Regenerate them through the SDK generators (`Mod.CreateAddressablesGroups` / `RefreshPipelines` in batch
     mode), not by editing YAML, and update `SkySpaceAgency.asset`.
   - This removes the internal-bundle-name collision (5b). **Mandatory.**
2. **New UXML address**:
   - rename the folder `Assets/UI/K2D2_UI` to e.g. `Assets/UI/SkySpaceAgency_UI`;
   - rename `K2D2_Window.uxml` to `SkySpaceAgency_Window.uxml`;
   - update the address in the group, and `AssetsLoader.LoadUxml` builds it from `ModIdentity`.
   - Removes 5a. **Mandatory.** Renaming the address alone, without moving the folder, also works.
3. **Builtin bundle naming**: Custom, `skyspaceagency` (5c). **Recommended.**
4. **Rename `K2D2.K2D2_Plugin`** (at least its namespace or class) so that FlightPlanRedux's
   `GetType("K2D2.K2D2_Plugin")` cannot pick SSA (8). **Mandatory.**
5. **Shortcut**: Alt+O → **Alt+Y**, configurable through `SWConfiguration.Bind<KeyboardShortcut>` (Redux settings
   UI) or our own `Setting<string>`. Fix the mockup (Alt+S trims pitch). Update the description strings. **Mandatory.**
6. AppBar ids, `WindowId`, settings file names, logger names: already distinct. Move them into `ModIdentity` so that
   1.5.0 gets the K2D2 values back.
7. Replace the generic log tags `[K2D2_Plugin]` with SSA ones, and optionally prefix all tags, so that
   `Player.log` and stack traces can be told apart (7).
8. Gate `WarpGate.Tick()` on `Enabled` (7). Minor.
9. Keep `conflicts: []`. Do not declare K2-D2 incompatible.

## (b) What only an in-game test can confirm (for the French checklist)

- With K2-D2 1.4.1 **and** SSA installed and both enabled in the same campaign:
  - both windows open, each with its own content;
  - no `[Features] More than one enabled feature provides 'Assets/UI/…'` line in the log;
  - no `can't be loaded because another AssetBundle with the same files is already loaded`, and no
    `Failed to load UXML` from either mod.
- **Before the fix**, confirm 5b with this same test (or by comparing the `CAB-` names of both bundles): it
  measures whether the collision is real.
- The new shortcut opens SSA only; Alt+O opens K2-D2 only; Alt+Y does not trim, warp or fire RCS; Alt+P
  (FlightPlan) and Alt+K (KERNEL) still work.
- If the shortcut is bound through `SWConfiguration`: it appears in Redux's mod settings and can be rebound.
- FlightPlanRedux "Execute" drives K2-D2's node pilot (K2-D2 log lines, not `[NodeEx]` lines from SSA). SSA's
  window stays idle.
- Each mod enabled alone in a campaign (the other installed but disabled): the disabled one shows no button and
  does nothing; the enabled one works.
- The settings files stay apart: `mods/SkySpaceAgency/skyspaceagency_*.json` and the K2-D2 folder's `k2d2_*.json`
  both change only when their own mod changes a setting.
- Both mods running a pilot at the same time: expected fight (document it rather than fix it), and SSA's warp
  watchdog does not drop a K2-D2 burn (no `[Warp] the game started a burn under rails warp that we did not ask for`
  while K2-D2 flies).

## (c) Is renaming all namespaces required?

**Not strictly, for runtime coexistence.** The evidence:
- the CLR and the Redux loader keep both assemblies separate (types found per assembly, components keyed by mod_id,
  statics per assembly);
- the UXML elements resolve through assembly-qualified references (`…, SkySpaceAgency` seen in the bundle);
- there is no Harmony, no ECS type, no save-file type and no `$type` JSON.

**What is required by name:**
- the **one type that other mods look up by full name**, `K2D2.K2D2_Plugin` (FlightPlanRedux);
- the **non-code identities**: the Addressables address, the group GUIDs, the builtin bundle prefix, the shortcut.

**A full rename is still worth it** for:
- support: stack traces and `Player.log` lines that say which mod failed;
- future name-based lookups by other mods;
- making the two codebases impossible to confuse in a decompiler.

The cost is losing easy cherry-picks from IanMealworm, the reason the names were kept on 2026-10-05.

A middle ground is to rename only `K2D2_Plugin` (or the root `K2D2` namespace) plus the identities above, and keep
`K2UI`, `KTools` and the sub-namespaces.
