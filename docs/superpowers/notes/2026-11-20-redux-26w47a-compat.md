# SkySpaceAgency against Redux 26w47a (static check, 2026-11-20)

Nothing was launched and nothing was installed: this is the static check the project does after every game update
(see `D:\BDC` "Après une mise à jour du jeu, comparer les MemberRef…", applied to 26w46a on 2026-11-15). The game
itself was not run, so every behaviour below is read from IL, not observed.

## Versions

| | Redux | Build | Unity | `Ksp2Redux.dll` SHA-256 |
|---|---|---|---|---|
| project template / editor tests | 26w42c | 0.2.9.0.106120 | 6000.6.0f1 | `5cdf90c6…` |
| previous game | 26w46a | 0.2.9.0.109196 (2026-11-15) | 6000.6.8f1 | `77c8e243…` |
| installed game (this check) | **26w47a** | **0.2.9.0.109473** (2026-11-18 20:32Z) | 6000.6.8f1 | `1f0e9d99…` |

Game folder: `G:\SteamLibrary\steamapps\common\Kerbal Space Program 2` (moved from `I:`, which no longer exists). Its
`Assembly-CSharp.dll` is the 291 KB facade of type forwards, as expected (the 11 MB stock file seen on 2026-11-20 in the
PartScale build is not what is installed here).

Release notes (`gh api repos/KSP2Redux/Redux/releases`, "Snapshot 26w47a") that can touch the mod: on-rails burns and
auto-warp (warp ending early during a burn, auto-warp speeding up again, stopping when an engine runs out), maneuver
plans (burns in progress losing direction or target after a load, later nodes disappearing while the plan loads,
predicted paths missing flybys or SOI changes), docking through several ports and SM+ ports, the parachute Deploy
button, delta-v counting fuel from dropped tanks, physics warp over landed vessels. The rest (clouds, Free Flight camera,
save error handling, SDK baking, KSP1 legs) does not reach the mod.

## Method

1. **Build the mod against the installed game.** `scratchpad/s47/build.mjs` takes the project's newest player
   response file (`Library/Bee/artifacts/*P.dag/SkySpaceAgency.rsp`, 296 sources including `AppUI/`) and points every
   reference the game ships at `<game>/KSP2_x64_Data/Managed` (50 `Packages/KSP2_x64` references, and in the second
   variant the 87 UnityEngine/Unity references of the editor's playback engine too), then compiles with Unity's own
   Roslyn. The output file is named `SkySpaceAgency.dll`: `csc` takes the assembly name from it (KSP1PartMod incident of
   2026-11-18).
   - control: the hand build against the project's own 26w42c references has **the same 1668 MemberRefs** as the DLL
     Unity builds (so the hand build is a valid build);
   - 26w42c vs 26w47a: **identical MemberRef tables** (no signature the mod references changed since the template).
2. **Resolve every MemberRef in the game.** `scratchpad/mr` (System.Reflection.Metadata) decodes each MemberRef
   (parent type, name, full signature) and looks it up in the 211 assemblies of `Managed`, through type forwards, base
   types and interfaces. 1094 non-BCL references, **0 unresolved**; a negative control (only 4 assemblies given) reports
   655 unresolved, so the check does see failures. This covers the UnityEngine 6000.6.0 (compile) against 6000.6.8
   (runtime) difference as well.
3. **Reflection sites.** The 46 `GetField/GetMethod/GetProperty/GetEvent/GetType` sites found by the Cecil tool
   (`scratchpad/compat`, `refl`), cross-checked by `grep` on the source: all targets exist with the same shape in 26w47a
   as in 26w46a and 26w42c (the tool compares the member lists of the target type in the two games).
4. **Behaviour.** Normalised IL of every game method the mod calls directly (`bodydiff`: 496 methods), of every
   method of `Ksp2Redux.dll` (`alldiff`: 383 changed, 1667 added, 1472 removed, almost all compiler-generated or
   rendering) and of `UitkForKsp2.dll`; then `ilspycmd` on the types that matter, 26w46a against 26w47a, XML comments
   stripped.

## Result by zone

| Zone | Type / member | Verdict |
|---|---|---|
| Every referenced member | 1094 MemberRefs | all resolve in 26w47a |
| Reflection, impact prediction | `Redux.Simulation.ImpactPrediction.ImpactPredictionSystem` (`_running`, `Flown`, `Planned`), `ImpactPredictor`, `ImpactPredictionResult` | source identical to 26w46a (decompiled text diff empty) |
| Reflection, waypoints | `Redux.UI.Waypoints.PlayerWaypointManager` (`Instance`, `Records`, `BeginPlacement`, `CancelPlacement`, `IsPlacementActive`, `Placement*` events, record fields) | identical |
| Reflection, stage recovery | `Redux.StageRecovery.StageRecoveryManager` (`Instance`, `SessionPhase`, `ActiveRecord`), `StageRecoveryRecord` | record identical; manager: two path comparisons now ordinal, nothing we read |
| Reflection, docking | `Module_DockingNode._dockingTransform`, `OnControlFromHere` | present, same signature |
| Reflection, SAS | `VesselSAS.sasResponse`, `angularDelta`, `VesselAutopilot._telemetry` | present |
| Reflection, other | `Data_Drag.ComputeAreaDragSimple`, `CurrentPatchedConicsOrbit._entity/Data/EM`, `PanelSettings.ApplyPanelSettings` (Unity 6000.6.8, internal) | present |
| Reflection, other mods | `FlightPlan` / `ManeuverNodeController` types | not Redux, not checked |
| String literals | 874 identifier literals x 554 game types (`pairs`) | no member named by a literal disappeared or changed |
| Maneuver nodes | `ManeuverPlanComponent.RemoveNodes(List)` (the only directly called method whose IL changed) | the mod already passes a copy; the event now lists the nodes really removed. Compatible. `GetNodes()` unchanged |
| Maneuver nodes | `GetState()`, `RebuildNodes`, `UpdateChangeOnNode`/`UpdateTimeOnNode` (now clear `BurnTargetOrbit`) | the mod does not read the state or the burn target orbit |
| Warp, `TimeWarp` | `SetRateIndex(int,bool)`, `WarpTo`, `GetWarpRates`, `ToggleTimeWarpMode`, `CurrentRateIndex` | same IL as 26w46a. New: resource-forecast cap (`LimitRateToResourceForecast`), `OnEngineOutOfFuel` stops an engaged auto-warp |
| On-rails burn | `VesselComponent.HandleOrbitalPhysicsUnderThrustStart/End`, `OnUpdateOrbitUnderThrust`, `TryBuildTrackedEngineBurn` | rewritten: rolling burn plan, see below. `IsOrbitalPhysicsUnderThrustActive` keeps its meaning (true only once a burn is built) |
| On-rails burn | `GetEndBurnOrbit()` | **semantics changed: horizon, not propellant end. Fixed in the mod** |
| Delta-v | `DeltaVPropellantInfo.UpdatePropellantInfo`, `OdeSolverStageComponent` (`FreeMassFlux`, `MassAtUT`) | stage list shape unchanged; fuel of dropped tanks no longer counted (more accurate `StageBudget`) |
| Docking | `Module_DockingNode` (`DockToModule`, `UndockInternal`, snap pairs for SM+ ports) | states and capture thresholds the guidance reads unchanged; only the snapped-pair capture is new |
| Parachutes | `Module_Parachute.ArmChute()` and `Data_Parachute` fields | `ArmChute` unchanged; `DeployNow` and `SetArmedState` changed (arm the toggle properly) |
| Engines | `PartComponentModule_Engine.IsPropellantFree`, `Data_Engine` fields | `ResourceName == "ElectricCharge"` became `PropellantState.IsElectricCharge`; fields we read are unchanged |
| UITK for KSP2 | `ResizeManipulator.EnsureHandle` (`hierarchy.Add`), `GameInputBlockManipulator`, `Extensions.DisableGameInputOnFocus`, `DocumentLocalization` (placeholders) | our `AppWindowView` does not redirect `contentContainer`; placeholders are localised only when they start with `#` (ours do not). `Window.Create`, `WindowOptions`, `MakeResizable`, `WindowSizeSaver` inputs unchanged |
| Addressables | the mod's own catalog (2 bundles, `{SpaceWarpPaths.SkySpaceAgency}` paths) | no dependency on a game bundle or on `Redux/ksp2.catalog` keys; the game's 26w47a catalog was not read |

## The one fix: the game's burn end is a horizon in 26w47a

`RailsBurn.BurnEndUT()` returned `GetEndBurnOrbit().EndUT` and `TargetOrbitBurn` used it, on rails, as the end of the
stage's propellant (margin 10 s) to decide when to leave rails. In 26w46a that was true: the burn was built to the lit
engines' propellant. In 26w47a a burn built from tracked propellant (`VesselComponent.UsesRollingBurnPlan`, private) is
planned over `min(3600 s, half the vessel's mass / mass flow)` and **rolls**: when its last patch ends (transition
`Final`, which does not slow the warp) `OnUpdateOrbitUnderThrust` calls `HandleOrbitalPhysicsUnderThrustStart` again.
Real fuel and power changes are handled by the game itself (`OrbitalBurnResourceChangeUT`, `TimeWarp` caps the rate
before it and `SetRateIndex(0)` at the end). With a chemical stage (15 t, 64 kg/s) the horizon is about 117 s. The
exit margin on rails is the stage margin (10 s) plus 2 s of real time at the current rate (100 s at x50), so the mod
would have left rails a frame or two after entering it, then flown the rest of every burn at 1x. That is a loss of
the warp, not of the burn: closed-loop guidance finishes it either way.

Fix (commit "Rails burn: the game's burn end is a horizon in 26w47a"): `RailsBurn.GameBurnRolls()` reads the private
property by reflection (null on 26w42c and 26w46a, so those behave as before); while it is true `BurnEndUT()` reports no
end, and `RailsBurnPolicy.StageEndUT` falls back on the stage's burn seconds taken at entry (`MaxPossibleBurnTime`),
which the entry decision already required to be long enough. Test: `RailsBurnPolicyTests.StageEndUT_UsesTheGameEndOrTheEntryEstimate`.
The guard log line says `the game's burn: a rolling plan, no end`.

## What is not verifiable without the game

- that `UsesRollingBurnPlan` is true for the burns the mod starts (IL says: whenever propellant is tracked, or
  InfiniteFuel; not observed), and the log line `rails burn: guard ok … the game's burn: a rolling plan, no end`;
- how the game's resource-forecast slowdown looks from our side (the warp index drops, `ExitRails` logs "the warp left
  rails", the burn ends at 1x): the section 18 checks of the transfers list apply, with a flame-out during a rails burn
  as a new case;
- the SAS rewrite and the `CelestialTilt` frame checks already listed in `todo.md` for 26w46a (not re-run);
- the Addressables bundles and the new window in 6000.6.8 (they were built with 6000.6.0f1; nothing in the IL says they
  fail, and they ran in 26w46a, same Unity);
- docking through several ports and SM+ ports, and the Deploy button of an armed parachute: fixed by the game, not
  exercised;
- the game's 26w47a `ksp2.catalog` (61 MB) was not read; the mod does not use it.

## Delivered build (not installed)

- EditMode tests against 26w42c: **1520 passed** (1519 + the new `StageEndUT` test), 0 failed, no compile error.
- `Deploy/SkySpaceAgency.zip` (git-ignored): the pipeline's zip (Unity 6000.6.0f1, template 26w42c references) with only
  `SkySpaceAgency.dll` replaced by the hand build against the installed 26w47a game (its own UnityEngine modules too).
  Unity's DLL is built against 26w42c references: it carries the same MemberRef table, but the hand build is the one
  checked against the game (1094 of 1094 resolved), and it is the precedent of KSP1PartMod and PartScale. Assembly name
  verified: `SkySpaceAgency`. DLL 1 582 592 bytes, SHA-256 `87b5b605…` (see the report for the full digests); zip
  7 723 899 bytes. The Unity-built zip is not kept in the repository.
- The Unity project still cannot target 26w47a: it needs editor 6000.6.8f1, and the SDK Manager now lists the
  `0.2.9-26w47a` template (see the parent `CLAUDE.md`), a separate upgrade.

## Tools (not versioned, in the session scratchpad)

`s47/build.mjs` (hand build), `mr/` (MemberRef dump and resolver), `compat/` (Cecil: `refl`, `pairs`, `bodydiff`,
`alldiff`), `s47/d46/` (the 26w47a `Managed` folder with 26w46a's `Ksp2Redux.dll` and facade, to diff 26w46a against
26w47a), `s47/t46`, `s47/t47` (decompiled types).
