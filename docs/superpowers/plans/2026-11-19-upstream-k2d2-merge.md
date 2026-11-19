# Upstream K2-D2 1.4.0 / 1.4.1 merge, then a K2-D2 pull request — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
1. Bring everything that exists only in IanMealworm's K2D2Redux 1.4.0 and 1.4.1 (`upstream/main` = `8273fb8`) into SkySpaceAgency, **with our code as the base**: boost-back landings, the Gravity Turn ascent profile, Redux's impact prediction, waypoint follow-on-move, tooltips, UI Scale and Redux's resize handle, the reusable info table, and **every bug fix** of their CHANGELOG and commits that our code still lacks.
2. Record a **real merge** of `upstream/main` in SkySpaceAgency's history (IanMealworm's commits kept), so later upstream releases merge from `8273fb8` onwards.
3. Then build a branch with the **K2-D2 identity** (name, mod_id, folders, Addressables, swinfo, titles, README/CHANGELOG in their voice, credits intact) and propose all our changes to IanMealworm as one pull request from a fork `JLSkyzer/K2D2Redux`.

**Architecture:** one `-s ours` merge commit (history only, plus the dead files both sides agree to delete), then one commit per ported item, each compiled and tested. Upstream files are ported near-verbatim to the path git's directory-rename detection proposes (`Assets/K2D2/Code/X` → `Assets/SkySpaceAgency/Code/X`), so a later `git merge upstream/main` finds them by rename detection. All mod-identity strings move into one class (`ModIdentity`) before the PR branch, so the PR branch differs from `main` by one generated commit.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, SDK template 26w42c (`ksp2redux.sdk#26w42c`), KSP2 Redux 26w46a in game.

**Sources read for this plan (2026-11-19):** `git diff --ignore-cr-at-eol -w 7ea025c upstream/main`, every upstream commit `7ea025c..8273fb8` (24 commits, IanMealworm, 2026-10-0x to 2026-10-16), their CHANGELOG and README, a trial merge with `git merge-tree --write-tree` (no worktree touched), our code at `60bd818`, the mockups `docs/design/ui/*.svg`, the roadmap `docs/superpowers/roadmap-booster-return-video-scripting.md`, and string checks in the installed 26w46a `Ksp2Redux.dll` / `.xml` and `UitkForKsp2.xml`.

---

## 1. What the trial merge says

`git merge-tree --write-tree HEAD upstream/main` (2026-11-19, `HEAD` = `60bd818`):
- **82 conflicted non-`.meta` files + 42 `.meta`** (with `-X ignore-space-at-eol`: still 82, 33 content conflicts instead of 35).
  - 18 **modify/delete**: upstream modified files we deleted or rewrote under a new path (`Assets/K2D2/Code/Pilots/Docks/*`, `Landing/LandingPilot.cs`, `LandingTargeting.cs`, `LandingUI.cs`, `Landing/Controlers/{Circularize,DeorbitBurn,MidCourseCorrection,TouchDown}.cs`, `Lift/{LiftPilot,LiftUI}.cs`, `Lift/Controlers/Final.cs`, `Nodes/NodeExPilot.cs`, `K2D2.asmdef`, `swinfo.json`).
  - 8 **rename/delete**: files we kept (moved) and they deleted (`ExFoldoutGroup`, `K2AutoFitLabel`, `K2SliderInt`, `KSPVesselInformation`, `ReflexionTool`, `AtmosphericPredictor`, `VesselAeroLookup`, `Lift/TouchDown`, `UI/ResizeManipulator`).
  - 19 **file location**: their new files, which git proposes under `Assets/SkySpaceAgency/Code/…`.
  - ~33 **content**: `K2D2_Plugin`, `BurndV`, `KSPVessel`, `ManeuverCreator`, `DronePilot`, `LandingSettings`, `ReduxWaypoints`, `Lift/{Adjust,Ascent,Coasting,LiftAscentPath,LiftSettings}`, `Nodes/{BurnManeuvre,WarpTo,NodeExUI}`, `StagingPilot`, `DragManipulator`, `SkySpaceAgency.asset`, uxml (`Dock`, `K2D2_Window`, `Landing`, `Lift`, `about`), USS (`K2D2.uss`, `K2UI.uss`, `K2Toggle.uss`), `ImportConfiguration.asset`, `UniversalRenderPipelineGlobalSettings.asset` (add/add), `GraphicsSettings`, `PackageManagerSettings`, `Packages/manifest.json`, `project.info`, `README.md`, `CHANGELOG.md`.
- **Auto-merged** (no conflict) but **not neutral**: `KSPVessel` (with `-X ignore-space-at-eol`; altitude semantics change), `KTools/Settings.cs` (clamp on read), `StrTool`, `SASTool`, `FullStatus`, `K2Page`, `TabbedPage`, `K2Slider`, `AttitudePilot/UI`, `NodeExSettings/UI`, `TurnTo`, `AboutUI`, `K2D2Window`, `AssetsLoader`, several USS and uxml. They are taken one by one in the tasks below, never blindly.
- **Line endings:** 23 of their 118 code/uxml/uss files are CRLF now (none of our 247). Use `-X ignore-space-at-eol` / `--ignore-cr-at-eol -w` to read diffs; ported files are written LF (our convention).
- **Their `.gitignore`** ignores eight `Lift/Controlers/*.cs.meta` (so their new Lift files have no committed `.meta`). We do **not** take that rule: Unity GUIDs must be committed. Our port adds those `.meta`; that is a benefit for them in the PR.

## 2. Inventory of upstream 1.4.0 / 1.4.1

Statuses: **covered** (by what), **port** (task), **arbitrate** (two designs; recommendation), **no** (reason). "Pure" = testable in EditMode without the game.

### 2.1 Landing

| # | Upstream item (files) | Mechanism (Redux/game API) | Status |
|---|---|---|---|
| L1 | Redux impact prediction (`ReduxImpactPrediction.cs` with `ReduxReading`) | Reflection on `Redux.Simulation.ImpactPrediction.ImpactPredictionSystem, Ksp2Redux`: static `_running`, `Flown`/`Planned` → `Vessel`, `Body`, `Result` (`Status` 0..5, `BodyPosition` double3 in `body.transform.bodyFrame`, `ImpactUniversalTime`, `SnapshotUniversalTime`, `IsApproximate`); usable if Impact, < 10 s old, in the future. `ReduxReading`: attitude settled, two results 1 s apart within 500 m, 40 s timeout. `AtmoSettingsProblem()` reads (compile-time) `KSP.Game.PersistentProfileManager.Settings.ImpactPredictionAttitude` (must be Current or SAS) and `ShowImpactPredictionInMap/InFlight`. All names present in the installed 26w46a `Ksp2Redux.dll`/`.xml` (string check). | **port** (T24): first as a `[Landing] redux impact:` comparison log in our `computeValues`, then as the boost-back's sensor. `ReduxReading` pure. Nothing equivalent in our tree. |
| L2 | Stage recovery log (`ReduxStageRecovery.cs`) | Reflection on `Redux.StageRecovery.StageRecoveryManager.Instance`, `SessionPhase`, `ActiveRecord.VesselName/CapturedUniversalTime`. Log only. | **port** (T25), glue. Also the detector the booster-return roadmap (R1) needs. |
| L3 | **Boost-back** for boosters in an atmosphere, powered or unpowered flip (`BoostBackBurn`, `AtmoEntryGuidance` + `EntryHeatLimit`, `AtmoAttitudeHold`, `AtmoBurnPlanner`, `AtmoLandingBurn`, `ImpactModel`, `LandingTargeting.TargetSite/Miss`, `LandingPilot` modes) | Starts on the Atmo profile, precision on, suborbital, inside the atmosphere, `AtmoSettingsProblem() == null`. BoostBack (burn sized on `ImpactModel`, direction search) → AtmoReentry (entry guidance closed on Redux's Flown prediction, lift vector by attitude, heat-limited AoA) → Brake (`AtmoLandingBurn`). From orbit it is refused (`atmo_landing_from_orbit = false`). Their README says it was tested in game (their claim, not ours). | **port** (T26–T31), see §4 for where it plugs. Conflicts handled there: precision meaning, frames, heat guard, final descent. |
| L4 | Landing burn timing in air for a vessel already falling (`AtmoLandingTiming`, `AtmoBurnPlanner`, `AtmoLandingBurn`, `compute_startBurn`) | Measures k in drag = k·ρ(h)·v² while coasting (Δv/Δt − g, 0.5 s samples; ρ = `body.GetDensity(GetPressure(h), GetTemperature(h))`); below 35 km integrates a 2-D fall with drag and 90 % thrust to find the last ignition ending at 50 m; then one throttle-regulated burn. | **port** (T26). From orbit our `AeroCoast`/`AeroBrake.Find` already models drag (covered), **but** a vessel already in the air on a collision course (`NeedsDescentPlan` false) goes to QuickWarp and `BrakeSimulator.FindStart` (vacuum): the "burn much too early" bug they fixed is still ours. |
| L5 | Lowers the legs near the ground (`LandingPilot.DeployLegs`) | `GetActionGroupPartCount(Gear) != 0` → `SetActionGroup(Gear, true)` once, below 1000 m or in TouchDown. | **covered** by `KSPService/LandingGear.cs` (richer: `Data_Deployable`/`Data_WheelBase`, state check, re-check at 3 s, `toggleExtend` fallback, parachute trigger). Only add `GetActionGroupPartCount` to the `[Landing] gear:` log (T12). |
| L6 | One waypoint list per profile | `waypoint_drop_vac/_atmo` | **covered** (`SetupTarget` per `LandingSettings`, `WaypointChoices`). |
| L7 | Target follows a moved waypoint (`WaypointPicker`, `ReduxWaypoints` reads `PlayerWaypointManager.Records[].Id`) | Keeps the chosen waypoint's `Id` and position; each UI tick, rewrites the target if the target still equals the old position, unless typed over. | **port** (T14). Ours matches by label or position (`WaypointChoices.IndexOfSaved`, 0.005°): a moved waypoint becomes "Custom". Pure rule into `WaypointChoices`. |
| L8 | More accurate vacuum precision (`ImpactModel`, `VacuumDescentGuidance`, node-based `Circularize`/`DeorbitBurn`/`MidCourseCorrection` via `TargetedBurnController`, slim `TouchDown`) | Kepler coast in `bodyFrame`, spin from `body.relativeAngularVelocity`, terrain `SurfaceProvider.GetTerrainAltitudeFromCenter`; burns flown as real maneuver nodes; closed-form linear-thrust law to a gate at 100 m. | **arbitrate → keep ours** (`Precision/` ZEM/ZEV, `DeorbitSearch`, `PlaneChange`, `CourseCorrection`, closed loop with no nodes, EditMode-tested, 0.4 m end-to-end test). Take one idea only: log `body.relativeAngularVelocity` in `bodyFrame` next to our `RotationProbe` sign (T24), which may later replace our calibrated rotation sign. Not ported: `VacuumDescentGuidance`, `TargetedBurnController`, their `Circularize/DeorbitBurn/MidCourseCorrection`. |
| L9 | Atmospheric precision from orbit (`AtmoDeorbitBurn`, `AtmoEntryCorrection`, `AimedBurn`) | Switched **off** upstream (`atmo_landing_from_orbit = false`). | **no**: off and unproven upstream; our descent planner + heat guard and our parachute return cover from-orbit; the R2 roadmap keeps it for later. `max_plane_trim_dv_atmo` is therefore not ported either. |
| L10 | Touchdown always noticed (`UpdateStanding`) | In Brake/TouchDown: ground speed < 1.5 m/s and thrust ≤ 0.5 g held max(1.5 s, 2·1.5/((1−0.5)·g)), at most 5 s; or `Landed && speed < 3`. Cause upstream: a height reading 70 m off. | **port** (T12). Our net needs `altitude < 5`, the same miss. Hold-time formula pure. |
| L11 | Max rotation slider ignored | `warp_to.Start_Retrograde(ut, true, settings.max_rotation.V)` | **port** (T12, one line). We still call `Start_Retrograde(startBurn_UT, true)`. |
| L12 | Pad altitude / height reading (`KSPVessel.GetApproxAltitude`, `GetGlobalGroundAltitude`) | 0 when Landed/Splashed, clamped ≥ 0; `VesselComponent.AltitudeFromTerrain` near the ground (< 3 km, terrain read at sea level). | **arbitrate → port as new methods** (T13): our `LandingPilot.cs:984`, `WarpTo.cs:155` and `DronePilot` rely on today's semantics; switch callers one by one, log both readings first. |
| L13 | No errors during crashes (`BurndV`, prediction) | try/catch NRE around `ThrustDirRelativePartWorldSpace`, skip `engineInfo.Engine`/`PartRef == null`. | **port** (T5). |
| L14 | Precision toggle no longer turns itself off | Toggle left on; `precision_landing_orbit_gate_vac` label explains; Start refuses. | **port** (T12). Ours forces it off (`LandingUI.cs` ~224–229 and ~792 `PeriapsisTooHigh`). Line ~944 (Atmo precision off outside Parachute mode) is our rule and changes with T31. |
| L15 | Typed-in target not replaced | `WaypointPicker` never writes on rebuild. | **covered** (our rebuild writes only on an explicit choice). |
| L16 | Stop cleans its maneuver nodes | `RemoveAllNodes()` on Stop from node phases. | **no**: our landing phases create no nodes. |
| L17 | Stop releases SAS (`ReleaseSasTarget`) | StabilityAssist, `lockedMode = false`, `SetTargetOrientation(current)`. | **port** (T12): our `FinishLanding` does it on landing, not on Stop. |
| L18 | Precision frozen per run | `run_precision` | **covered** (`precision_run`, `reentry_run` in `CheckCanStart`). |
| L19 | Deleted `AtmosphericPredictor.cs`, `VesselAeroLookup.cs` | — | `AtmosphericPredictor`: dead for us too → delete in T2. `VesselAeroLookup`: **keep ours** (we added `BuildRetrogradeDragTable`/`SubsetDragTable`, used by `DescentContext` and `ReentryContext`). |

### 2.2 Lift

| # | Upstream item (files) | Mechanism | Status |
|---|---|---|---|
| A1 | **Gravity Turn profile** (`AscentProfile`, `AscentControllerBase`, `GravityTurnControl`, `AscentPredictor`, `ReactiveAscent`; `LiftPilot`, `LiftSettings`, `LiftUI`, `Adjust`) | Vertical rise to the kick speed, pitch kick, then surface prograde in thick air and orbital prograde in thin air. Throttle = min(max throttle, TWR cap, max-Q cap from `DynamicPressure_kPa`, time-to-Ap hold via `SignedTimeToApoapsis`, taper near target Ap), floored at a minimum. Nose above prograde when throttle-limited or the hottest part nears its limit (`part.Temperature / part.MaxTemp`). "Auto" picks kick 3–15° and hold 40–60 s with a point-mass simulation (stages from `VesselDeltaV.StageInfo`, density table, drag measured during the rise, each candidate also flown at 1.6× drag), spread over frames. Steering `SAS.SetTargetOrientation`, `lockedMode = false`. Controller frozen per run (`run_ascent`). | **port** (T18–T23). `GravityTurnControl` pure as is; the `AscentPredictor` core pure once split from `VesselComponent`; `ReactiveAscent` glue. Depends on `ImpactModel` (only `StateAt`, `AirVelocity`, `mu`) and `BurndV.active_full_dv` (→ our `active_dv`). **Orbit goal only** at first (decision 3). |
| A2 | Closed-loop circularization at the end of the climb (`Final.cs`) | Vis-viva Δv, ignition at Ap − T/2, steering along circular-minus-current velocity with `AtmoAttitudeHold`, 2 s taper, stop < 0.5 m/s or stalled. | **arbitrate → keep ours** (`FinalCircularize` → `CircularizeBurn` → `TargetOrbitBurn` + `CircularizeGuidance`): shared with Node, tested, rails/physics warp policies, `stage_when_dry`, past-apoapsis case, refusals. Check (T10) that ours goes to full throttle when no engine is lit, as theirs does. |
| A3 | "Circularize once ascent complete" and "Pause leaving atmosphere" now work; pause default off (`LiftPilot.NextMode`, `Coasting`, `LiftSettings`) | `auto_circularize` off → run ends after Adjust (optional pause `SetRateIndex(0)` + `SetIsPaused(true)`); pause moved to end of Adjust; toggle greyed while auto-circularize is on; `pause_on_final` default false. | **port** (T10). Our `auto_circularize` is bound but **ignored** (`LiftGoalRules.NextStatus` always circularizes); circularization stays forced for `PrepareTransfer` and `Rendezvous` goals. |
| A4 | Coasting fixes | `GetSeaAltitude()` (atmosphere depth is from sea level), `compute_wanted_warp_index(t)` instead of `+1`, guard vertical speed ≤ 0, coast still runs with Coasting Warp off. | **port** (T10). All four bugs are in our `Coasting`. |
| A5 | Roll program lock released (`ReleaseRollLock`) | `lockedMode = false` on leaving Ascent. | **port** (T10), in our `LiftPilot.status` setter. |
| A6 | Heading correction fades in (`HeadingCorrectionWeight`: 0 at 5 m/s, 1 at 50 m/s) | — | **port** (T10), Orbit goal only (transfer goal already sets `heading_correction = 0`). Pure. |
| A7 | Guards: `SASTool.getTelemetry() == null` (Ascent, TurnTo, AttitudePilot); `max_throttle` min 0.05 | — | **port** (T7). |
| A8 | Guided-only settings shown only with Guided; graph hidden with Gravity Turn | `guided_profile_settings` group | **port with A1** (T23). |
| A9 | Run buttons follow `pilot.isRunning`, not the click | — | **port** (T7) for Lift, Node, Attitude (our pilots can refuse a start). |
| A10 | Info rows only with Debug Mode | `K2D2Settings.debug_mode` | **port for Lift and Node** with `InfoTable` (T17); our own tabs unchanged. |
| A11 | Deleted `Lift/TouchDown.cs` | — | dead for us too (`K2D2.Node.TouchDown`, never created) → delete in T2. |

### 2.3 Node, Docking, Staging, Attitude, Drone, KSPService

| # | Upstream item | Status |
|---|---|---|
| N1 | Circularize buttons no longer delete future nodes (`CreateManeuverNodeAtUT` instead of `RemoveAllNodesThenCreate`) | **arbitrate** (decision 4): our buttons create no node; `CircularizeBurn.cs:137` calls `RemoveAllNodes()` on purpose. Recommendation: remove only the nodes **before** the burn UT, keep the player's later nodes (T9). |
| N2 | No active engine: `BurndV.active_full_dv`/`has_active_engine`; `BurnManeuver` uses it for the burn time and goes full throttle with a warning; `NodeExPilot` refuses ("No Active Engine", "Node In The Past"); `isRunning` flips back; `execute_node` null-check | **port** (T5) with **our** definition of active (`EngineIgnited && !EngineShutdown`; `IsOperational` depends on the throttle, see `BurndV.IsActive`). `BurnManeuvre.cs:201` still divides by `full_dv`. |
| N3 | `TurnTo` clamps (`max_angle_maneuver` ≥ 0.1, `max_angular_speed` ≥ 0.05) | **port** (T7). |
| N4 | `WarpTo` logger, idle `checkManeuver` 4×/s only when visible, Ctrl+O removed, `node_ex.show_node_infos` removed | logger: covered; idle check: **port** (T17); Ctrl+O: **no** (we keep Alt-O and Ctrl+O as today); `show_node_infos`: delete the unused key (T7). |
| D1 | Docking: kill speed at full throttle (`Compute_Thrust()`), clear-target NRE, `ResetControllers` on start, X/Y/Z zeroed on stop | **no / covered**: the Dock tab is gone; `Rendezvous/Translate.cs` already calls `Compute_Thrust()`, `RendezvousSnapshot` null-checks the target, `RendezvousPilot`/`OwnVesselStop` reset and zero. Cleanup only: unused `burn_dv` field in `RendezvousPilot.Flight.cs:22` (T7). |
| S1 | Auto-staging stages everything when an empty engine stays attached (`spent_engines` `HashSet<PartComponent>`, reset on vessel change; skip `CapacityUnits <= 0` (0/0 = NaN); compute fuel only when on) | **port** (T6), keeping our `SuspendsAutoStaging` landing guard; check `Nodes/BurnStaging` + `DryStageTimer` for the same weakness. Pure rule extracted. |
| T1a | Attitude keys renamed `warp.elevation`/`warp.heading` → `attitude.*` | **port with fallback** (T11): read the old key once if the new one is absent, so neither K2-D2 1.3/1.4 nor SkySpaceAgency players lose values. |
| T1b | Drone: comment and logger | covered (our `DronePilot` is not created). |
| K1 | 26w41a/26w42a API migration (SDK tag, `Ksp2Redux.dll` reference, `LogManager.GetLogger`, window created in `OnCampaignLoaded`) | **covered**: we did the same (template 26w42c, `LogManager`, `EnsureWindow`). |
| K2 | `KSPVessel` dead-code removal (yaw/trim setters, `FlightCtrlState` instruction API, `getApoapsis` & co.) | **port** (T13) after a grep proves none of ours is used. |
| K3 | `SASTool` caches its `FieldInfo` (null-safe) instead of `ReflexionTool` | **port** (T8), then delete `ReflexionTool`. |
| K4 | `ManeuverCreator.UpdateMapGizmoWhenReady_Co`: waits for `GameState.Map3DView` (0.5 s poll, 120 s cap) before `GetNodeDataForVessels`/`UpdatePositionForGizmo` | **port** (T9): our `UpdateMapGizmo_Co` only catches and logs the NRE. |

### 2.4 Plugin, window, UI framework, settings

| # | Upstream item | Mechanism | Status |
|---|---|---|---|
| P1 | Only runs in campaigns that have it on | `KerbalMod.Enabled`, `OnCampaignLoaded`, `OnCampaignUnloading` (closes the window); `if (!loaded) return; if (!Enabled) return;` first in `Update`; `UpdateScene()` once per frame, `ResetControllers()` only on the flight → non-flight transition; PhotoMode counts as flight (pilots keep flying, window hidden then reopened). | **arbitrate → take their state-transition design into our file** (T3). Ours resets controllers on every Update/FixedUpdate/LateUpdate outside flight, stops a pilot in photo mode, runs `current_vessel.Update()` and `Debug.developerConsoleVisible = false` while disabled, and has no `OnCampaignUnloading`. Keep our `ResetCause`, `EnsureWindow`, `WarpGate.Tick()`, `PhysicsWarp.ReleaseIfAbandoned()` (check it still runs when disabled). |
| P2 | No hang while loading on 26w42a | Window created in `OnCampaignLoaded`; `L.Log` null-safe. | **covered** by `EnsureWindow`; port null-safe `L.Log`/`L.Vector3` (T3). |
| P3 | Window scales with the game's UI Scale | `UitkForKsp2.API.WindowOptions.UseStockScale = true` (in the installed `UitkForKsp2.xml`). | **port** (T16), main window and `VabToolsWindow`. The mockup's own "Interface scale" (settings.svg) would multiply on top: no conflict. |
| P4 | Redux's resize handle, wider and taller | `<UitkForKsp2.Controls.WindowResizeHandle target-name="MainFrame" min-width min-height/>` in the `AppShell` (from `ksp2redux.sdk`, shipped as `uitkforksp2.controls.Runtime.dll`; builds `UitkForKsp2.API.Manipulator.ResizeManipulator(ResizeOptions, handle)` by reflection). Their `ResizeManipulator.cs` and `Tick()` deleted; size no longer saved. | **arbitrate** (decision 5) → adopt `WindowResizeHandle`, delete our `UI/ResizeManipulator.cs`, keep saving the size with a `GeometryChangedEvent` saver on `MainFrame` (T16). `UiLayoutHarness` (350 px fixed) and the K2D2.uss handle rule follow. |
| P5 | Info button matches other buttons (`k2-button k2-button--square`) | — | **no** for the styling (the mockups replace the "i" button with a SETTINGS tab); keep only the shared-class idea. |
| P6 | Tooltips (`UI/Tooltips.cs`) | UITK draws `tooltip` only in the editor; `PointerOverEvent` (TrickleDown) on `MainFrame`, nearest element with `tooltip`, absolutely positioned `Label.k2-tooltip` after 500 ms, hidden on leave/down/wheel. Texts from uxml `tooltip="…"`. | **port** (T15). We already have **66 `tooltip=` attributes, invisible in game today**; ~40–55 settings still lack a text. |
| P7 | Trimmed info panel, `InfoTable` (`Begin/Add/End`, reuses rows) | The old code cleared and rebuilt rows every tick (relayout every frame). | **port** (T17): replace the rebuild loops in `LiftUI` (~248–300), `NodeExUI` (~77), `LandingUI` (~717), `RendezvousUI` (~483). Pure. |
| P8 | UI tweaks: `K2UI.uss` rewritten around variables and classes (`k2-button`, `k2-button-row`, `k2-title`, `k2-section-header`, `k2-note`, `k2-help`), `K2D2.uss` 680 → 94 lines, stylesheets loaded once in `K2D2_Window.uxml` with K2D2 last, tab body in `InvertedCornerBox`, `K2Slider format` attribute, `@schlosrat` typo, Reset All also resets `land_atmo`/`land_vac` | — | **no** for their palette and USS content (conflicts with the mockups' navy/gold/cyan); **port**: `K2Slider format` (T4), the typo and the Reset All bug (T4). The load-order idea is noted for the future UI foundations work, not done here. |
| P9 | Window drag (LeoMarinDev): left button only, absolute `left/top`, bounds from `panel.visualTree.contentRect`, saved position applied on the first non-zero geometry and clamped, saved only after a real move | — | **arbitrate** (decision 5) → adopt theirs (T16); our right-drag and "jump to the corner" bugs are still present (no button filter; any pointer-up saves, possibly (0,0)). Keep a pure static clamp and adapt `DragClampTests`. Old `main_window_pos` (a translation) lands ~(11, 36) px off once, then is clamped: harmless. Also `VabToolsWindow` (`vab_window_pos`). |
| P10 | No errors at startup | `[NonSerialized]` on `K2D2_Plugin.current_vessel`, `pilots_manager`, `K2Page.panel/settings_page/main_page/tab_button` (Unity's "skipped by serialization"). `GetLoadedAssemblyPath()` instead of `Assembly.Location`. | **port** the attributes (T3); **no** for the path change (our `mods/SkySpaceAgency/` folder holds the json, `plans/`, `vab/` next to the DLL; `Location` works). |
| P11 | Smaller download | Bundles in `Copied/assets/bundles` and the Caravan font deleted. | **covered** by our `88ff184` (same PR #2 approach). Both sides deleted the bundles: no conflict. Delete the unreferenced `Assets/Fonts/Caravan.*` in T2; `K2UI.uss:192` points at a missing `Sax Mono.asset`: cosmetic, T4. |
| P12 | `TabbedPage.SetWindowOpen(bool)` (panels `isVisible = false` while closed), `FullStatus` batches its writes in one scheduled `Apply()`, `StrTool` tenths digit (`Milliseconds / 100`), `ClampSetting` clamps on read and notifies, `ClampedSettingInt` deleted, `GameStateChangedMessage` subscription removed (does not reach mods, per LeoMarinDev) | — | **port** all (T4, T17); our `GameStateChangedMessage` handler is empty → remove. |
| P13 | Deleted K2UI files (`ExFoldoutGroup`, `K2AutoFitLabel`, `K2SliderInt`), `StatusGroup.uxml`, `KSPVesselInformation` | — | dead for us (comments only) → delete in T2. |

### 2.5 Settings files and keys (players' JSON, both directions)

- **Storage is unchanged on both sides** (`SettingsFile.cs` identical to the base): a flat `Dictionary<string,string>` with dotted keys, next to the DLL. Only the file names differ: `k2d2_settings.json`, `k2d2_landing_atmo.json`, `k2d2_landing_vac.json` (K2-D2) vs `skyspaceagency_*.json` (ours). Neither mod reads the other's files; a player may copy and rename them: unknown keys are kept and written back, missing keys take defaults.
- **Rule for every port: keep upstream's key names, types, defaults and ranges verbatim**, so a K2-D2 1.4.x file loads unchanged in the PR branch, and keep upstream's uxml element names. New keys: `lift.ascent_profile` (`EnumSetting<AscentProfile>`, Guided), `lift.gravity_turn_kick_speed_mps` (100, 10–300), `lift.gravity_turn_pitch_kick_deg` (7.5, 1–25), `lift.gravity_turn_hold_time_s` (45, 25–120), `lift.gravity_turn_auto` (true), `lift.gravity_turn_min_throttle` (0.25, 0–0.5), `lift.gravity_turn_max_twr` (2.5, 1.2–5), `lift.gravity_turn_max_q_kpa` (40, 10–100), `land.entry_angle_of_attack` (10, 0–30), `land.entry_flight_path_angle` (10, 0–60), `land.boost_back_flip` (`FlipStyle`: Powered/Unpowered), `attitude.elevation`, `attitude.heading`. Changed: `lift.max_throttle` range 0.05–1; `lift.pause_on_final` default false (a saved value is kept).
- **Meaning shift to handle:** `land.precision_landing` in the Atmo file means boost-back for K2-D2 and the parachute return for us. Resolved by our `land.mode` key (absent in K2-D2 files → Propulsive): **Atmo + Propulsive + precision = boost-back; Atmo + Parachute + precision = parachute return** (T31). A K2-D2 file therefore keeps its meaning.
- Keys we removed and K2-D2 still has (`land.max_plane_trim_dv`, `land.steering_max_angle` is the reverse: they removed it, we use it): kept or ignored silently; no migration needed.
- After T4 (`ClampSetting` on read) an out-of-range saved value is clamped when read: check the installed `skyspaceagency_*.json` before shipping. After T16, `main_window_pos` changes meaning once; `main_window_size` stays used by the size saver.

## 3. Technical conflicts and their resolution

| Area | Upstream | Ours | Resolution |
|---|---|---|---|
| SDK / template | `ksp2redux.sdk#26w42b`, `project.info` 26w42b | `#26w42c`, 26w42c | **ours**. Both dropped `ksp2community.ksp2unitytools` and `uitkforksp2.controls`, both added `com.unity.modules.amd/nvidia`: the only manifest difference is the tag. The PR moves them to 26w42c. |
| `Packages/manifest.json` | as above | as above | **ours**. |
| asmdef | `K2D2.asmdef` with 10 more precompiled refs (`AmplifyShaderEditor.Samples.BuiltIn`, `com.rlabrecque.steamworks.net`, `HoudiniEngineUnity`, `Mortoc.Compute.TestTools`, `ShapesRuntime`, `System.Collections.Immutable`, `System.Threading.Tasks.Extensions`, `TrackIRUnity`, `Unity.AI.Navigation`, `UnityEngine.ProfilerModule`, …) | `SkySpaceAgency.asmdef` (26w42c list); none of those 10 DLLs is in our `Packages/KSP2_x64` | **ours** (modify/delete: keep deleted). A ported file that needs one of them would fail to compile: none of the ported files does (checked by the compile of each task). |
| ThunderKit | `ImportConfiguration.asset` (reordered steps), `ThunderKitSettings.asset` (their game path) | ours, rewritten by `tools/setup-project.mjs` | **ours**, always (CLAUDE.md: never commit another machine's import state). |
| Render settings | added `UniversalRenderPipelineGlobalSettings.asset`, `DefaultVolumeProfile.asset`, `GraphicsSettings`, `PackageManagerSettings` | added by our 26w42c template | **ours** (add/add). |
| `Assets/ReduxSDK/Generated/**` | regenerated by their SDK (plumes, I2Languages, interactions) | ours (26w42c) | **ours** (generated by the SDK; take whatever our template generates). |
| Bundles `Copied/assets/bundles` | deleted (0e1ff7d, LeoMarinDev) | deleted (`88ff184`) | identical; nothing to do. Never reintroduce a bundle in `Copied/`. |
| Addressables | **unchanged** upstream (`AddressableAssetsData` not in their diff) | renamed groups/profile/labels to SkySpaceAgency through the SDK generators | **ours**. |
| uxml / USS | restyle, new elements | our layout, new tabs | **ours**; each ported element is added by hand in its task, with upstream's element name, styled with existing classes (no new USS except `.k2-tooltip`). |
| `swinfo.json`, mod asset | 1.4.1, K2D2 | generated, SkySpaceAgency | **ours**; swinfo regenerated by the pipeline. |
| README / CHANGELOG / NOTICE / LICENSE | 1.4.0/1.4.1 sections | our sections + remix credits | **ours** in the merge commit; their 1.4.0/1.4.1 sections are quoted in our CHANGELOG under "Merged from K2-D2 1.4.0/1.4.1" (T32), credits kept. |
| Line endings | 23 CRLF files | LF | read with `-X ignore-space-at-eol`; ported files LF. |

## 4. Merge strategy

### 4.1 Why a `-s ours` merge, then one commit per port

Resolving the 82 conflicts inside the merge commit would give one commit of ~15 000 changed lines that cannot be compiled or tested halfway, mixes two landing designs, and hides which upstream change landed where. Instead:
1. **Merge commit** (`git merge -s ours --no-ff --no-commit upstream/main`): a real merge, both parents, IanMealworm's 24 commits in our history. Its tree is ours, minus only the dead files both sides agree to delete (T2). It compiles and every test passes.
2. **Port commits**, one per item of §2: each takes upstream's code from `upstream/main` (or a diff `7ea025c..upstream/main` re-applied to our path), adapts it to our structure, adds tests, compiles, runs every test. Each message cites the upstream commit(s) and IanMealworm (CC BY-SA attribution; CHANGELOG line).
3. Later upstream releases: `git merge -X ignore-space-at-eol upstream/main` from base `8273fb8`. Files ported near-verbatim at git's suggested path are found by rename detection, so their later fixes apply as patches.

Techniques for a port commit:
- New upstream file: `git show upstream/main:Assets/K2D2/Code/<p> > Assets/SkySpaceAgency/Code/<p>`, convert to LF, then adapt. Keep the class, method and setting names unless they collide with ours.
- Upstream change to a file we kept: `git diff --ignore-cr-at-eol 7ea025c upstream/main -- Assets/K2D2/Code/<p> | git apply -3 --directory=… -p…` onto our path, or by hand when the context moved.
- Never `git checkout upstream/main -- <our file>`: it overwrites our code.

### 4.2 Where upstream features plug into our structure

- **Gravity Turn (Lift tab):** `LiftPilot.ascent` becomes an `AscentControllerBase`; the existing `Ascent` is the Guided controller; `ReactiveAscent` the Gravity Turn one. `LiftStatus.Ascent` and `LiftGoal` unchanged. The chosen profile is frozen per run. Gravity Turn is accepted for `LiftGoal.Orbit` only; with `PrepareTransfer`/`Rendezvous` the start says "Gravity Turn flies Orbit launches only for now; using Guided" and flies Guided (decision 3). `ascent_lead.*` measurements are stored per profile (`lift.ascent_lead.<body>.gt.*`) so the two profiles do not mix their leads.
- **Air landing timing (Land tab, all modes):** in `LandingPilot.compute_startBurn`, when `body.hasAtmosphere` and the vessel is already on a collision course in the air (not the from-orbit `AeroCoast` path), the ignition comes from `AtmoLandingTiming`/`AtmoBurnPlanner` instead of `BrakeSimulator.FindStart`; `AtmoLandingBurn` flies the burn down to its 50 m handover; below it, our `FinalDescent`/`TouchDown` (legs, hover, standing check) take over unchanged.
- **Boost-back (Land tab, Atmo profile):** two new `LandingPilot.Mode` values, `BoostBack` and `AtmoReentry`, left only by explicit transitions (as our ReentryCoast and Jettison phases). Start conditions in `CheckCanStart`: Atmo profile, `land.mode == Propulsive`, precision on, suborbital, below `atmosphereDepth`, `AtmoSettingsProblem() == null`, else a refusal that says which check failed (lesson 2026-11-04). Hand-over AtmoReentry → our Brake with `AtmoLandingBurn`, then `FinalDescent`/`TouchDown`. Stop calls `ReleaseAtmo()`. **Frames:** `ImpactModel` (body frame, game spin vector) is used only inside the boost-back chain and the Gravity Turn predictor; our `Precision/` code keeps its inertial frame; never mix vectors of the two (lesson 2026-11-18: a turned-frame test for every frame-sensitive function). **Heat:** our `HeatGuard` stays the authority; `EntryHeatLimit` only lowers the angle of attack. **Staged landing** (`LandingStageDrop`) stays refused in an atmosphere. **Warp:** 1× only during boost-back, entry and landing burns (our landing rule).
- This is the first brick of roadmap R2 ("shared atmospheric powered descent to a point, used by Return and by the Land tab's Propulsive precision"). The future Return tab will reuse these classes and add the separation-clearance checks R2 requires; this plan does not build the Return tab (decision 1).

### 4.3 UI, minimal and following the mockups

Rule (memory `ui-follow-the-mockups`): no new K2-D2-style interface. As in the 4.6 plan: existing controls (`K2Toggle`, `K2Slider`, `InlineEnum`/dropdown, `K2Button`) in existing tabs, wording from the mockups where they have it, upstream's element names, **no new USS** except one `.k2-tooltip` rule in the mockups' palette (navy `#0a1230` background, cyan `#7fe7ff` text, gold `#e8a93a` border).
- **Lift** (mockup `lift.svg`, MISSION PLAN: "Gravity turn start", "Turn shape", "Max angle of attack"): one `ascent_profile_drop` (Guided / Gravity Turn) under the goal selector; `guided_profile_settings` (existing Guided sliders) and `gravity_turn_settings` (`gravity_turn_auto`, `gravity_turn_kick_speed_mps`, `gravity_turn_pitch_kick_deg`, `gravity_turn_hold_time_s`, `gravity_turn_min_throttle`, `gravity_turn_max_twr`, `gravity_turn_max_q_kpa`) shown by profile; the ascent graph hidden with Gravity Turn; the pause toggle greyed while auto-circularize is on.
- **Land** (mockup `land.svg`: Propulsive / Parachutes, "Precision landing"): in the Atmo profile with Propulsive, the Precision toggle is enabled (it is greyed today) and shows `boost_back_flip_atmo` (Powered / Unpowered), `entry_angle_of_attack_atmo`, `entry_flight_path_angle_atmo`, and the `precision_landing_gate_atmo` note (Redux's prediction settings, suborbital only). `PrecisionLanding_atmo` group name kept.
- **Window:** Redux resize handle, stock UI scale, tooltips. No restyle.

## Global Constraints

- **Inherited:** licence CC BY-SA 4.0, credits never removed, a CHANGELOG line per visible change (here: "from K2-D2 1.4.x by IanMealworm"); K2D2 namespaces kept; logs in English with the existing prefixes, numbers in the invariant culture; stage only your own files; never commit a game DLL; commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; push `origin main` in the submodule, then the pointer in the parent (`git add SkySpaceAgency` only).
- **Branch:** work on `merge/k2d2-1.4.1` in the submodule, merged into `main` (fast-forward) after T32 and a green test run. Never `--force`, never `--no-verify`.
- **Test command.** Never run two Unity jobs at once. `S` is the session scratchpad.
  ```
  rm -f "$S/results.xml"; "/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
  ```
  Passes when `results.xml` shows every test passed and `tests.log` has 0 `error CS`. Baseline: 1153 EditMode tests on 2026-11-19 (`tasks/skyspaceagency/todo.md`); every existing test stays green.
- **Build:** `Deploy to Zip File` in batch mode (exit code always 1: judge on the ThunderKit log and the zip, CLAUDE.md).
- **Option off = today.** Every ported feature with a setting defaults so that a player who changes nothing flies as today, except bug fixes. Gravity Turn defaults to Guided; boost-back only with Atmo + Propulsive + precision on.
- **Upstream names verbatim** for settings keys and uxml elements (§2.5).
- **Frames** (lesson 2026-11-18) and **refusals that name the failed check** (lesson 2026-11-04) apply to every ported pilot.
- **Each pilot tested from every start state** (lesson 2026-10-30): boost-back from orbit (refused), suborbital in the air, already falling, on the pad (refused).

## Decisions taken in this plan (the user can correct them)

1. **Boost-back lives in the Land tab** (Atmo profile, Propulsive, Precision) now; the Return tab of the roadmap comes later and reuses it.
2. **Vacuum precision and Lift circularization: ours stay**, theirs are not ported (two designs of the same thing; ours is tested and more general).
3. **Gravity Turn for Orbit launches only**; transfer and rendezvous launches stay Guided until it is tested in game.
4. **Node circularize buttons keep the player's later nodes** (remove only nodes before the burn).
5. **Window: adopt Redux's resize handle and upstream's drag manipulator**, keep saving size and position.
6. **No atmospheric precision from orbit** (switched off upstream).
7. **PR scope**: everything in `Assets/`, tests included; `docs/` excluded from the PR branch (French test lists, SkySpaceAgency-branded mockups and plans), see T35.

## Review Focus

1. The merge commit's tree equals `main`'s minus the dead files (T2: `git diff main --stat` shows deletions only).
2. No ported setting key or uxml name differs from upstream's (T19, T23, T31: a test lists them).
3. Boost-back never starts from orbit, on the pad, in Parachute mode, or with Redux's prediction settings wrong; each refusal says why (T30).
4. `ImpactModel` vectors never meet `Precision/` vectors (T18, T28: turned-frame tests).
5. After T33, `git grep -i skyspaceagency -- Assets` lists only `ModIdentity.cs`, generated assets and the mod asset (test `IdentityLiterals_OnlyInModIdentity`).

---

## Tasks

### Phase A — the merge

#### Task 1: Baseline

- [ ] `git -C SkySpaceAgency status` clean; `git fetch upstream`; check `upstream/main` is still `8273fb8` (if not, redo §1 with `git merge-tree` and update this plan first).
- [ ] `git switch -c merge/k2d2-1.4.1 main`.
- [ ] Run the test command; note the count (1153 expected).

#### Task 2: The merge commit

- [ ] `git merge -s ours --no-ff --no-commit upstream/main`.
- [ ] Delete the dead files both sides agree on, with their `.meta`: `Code/K2UI/ExFoldoutGroup.cs`, `Code/K2UI/K2AutoFitLabel.cs`, `Code/K2UI/K2SliderInt.cs`, `Code/KSPService/KSPVesselInformation.cs`, `Code/Pilots/Landing/AtmosphericPredictor.cs`, `Code/Pilots/Lift/TouchDown.cs`, `Assets/UI/K2D2_UI/StatusGroup.uxml`, `Assets/Fonts/Caravan.asset`, `Assets/Fonts/Caravan.ttf`. Fix the comments that name them (`K2Slider.cs`, `Landing.uxml`, `KSPVessel.cs`, `VesselAeroLookup.cs`). Grep first: no code reference may remain (GUID of Caravan included).
- [ ] `git diff main --stat` shows only these deletions and comment edits.
- [ ] Test command green; `Deploy to Zip File` builds.
- [ ] Commit: `Merge K2-D2 1.4.1 (IanMealworm/K2D2Redux 8273fb8), history only` with a body listing: tree kept as ours (decision 1), dead files deleted, each upstream item ported in the following commits (point to this plan).

### Phase B — fixes (small, each its own commit)

#### Task 3: Plugin lifecycle and startup errors (P1, P2, P10)
**Files:** `Code/K2D2_Plugin.cs`, `Code/K2UI/Tabs/K2Page.cs`, `Code/KTools/*` (`L.Log`).
- [ ] `[System.NonSerialized]` on `current_vessel`, `pilots_manager`, `K2Page.panel/settings_page/main_page/tab_button`.
- [ ] `Update`: `if (!loaded) return;` before `current_vessel.Update()`; `if (!Enabled)` path does nothing but `PhysicsWarp.ReleaseIfAbandoned()` (check) and hides the window.
- [ ] `UpdateScene()` once per frame: `ResetControllers(ResetCause.SceneChange)` only on the flight → non-flight transition; PhotoMode counts as flight; window hidden then reopened (`window_hidden_by_scene`). Drop `Debug.developerConsoleVisible = false`.
- [ ] `OnCampaignUnloading()` closes the window and resets (`ResetCause.ModDisabled`).
- [ ] Null-safe `L.Log`/`L.Vector3`. Remove the empty `GameStateChangedMessage` subscription.
- [ ] Test: pure `SceneTransition.Next(prevInFlight, inFlight, photo)` → `{reset, hideWindow, reopenWindow}` (`Assets/Tests/UI/SceneTransitionTests.cs`).
- [ ] CHANGELOG; commit `Port K2-D2 1.4.1 plugin lifecycle: reset once on leaving flight, photo mode, campaign unload`.

#### Task 4: KTools and small UI fixes (P8, P11, P12)
**Files:** `KTools/StrTool.cs`, `KTools/Settings.cs`, `K2UI/K2Slider.cs`, `UI/AboutUI.cs`, `Assets/Runtime/K2UI/USS/K2UI.uss`.
- [ ] `StrTool`: tenths digit `Milliseconds / 100`. Test `DurationToString_TenthsDigit` (5 ms → ".0", 950 ms → ".9").
- [ ] `ClampSetting<T>`: clamp on read, notify listeners when a write is clamped; delete `ClampedSettingInt` (0 uses). Tests with an in-memory `SettingsFile`. Before shipping: read the installed `skyspaceagency_*.json`, list any value that would be clamped.
- [ ] `K2Slider` `format` attribute (`N2` default, `"percent"`). Test the formatting function.
- [ ] `AboutUI`: Reset All also resets `land_atmo` and `land_vac`; typo `@schlosrat`.
- [ ] `K2UI.uss:192`: remove the reference to the missing `Sax Mono.asset`.
- [ ] Commit `Port K2-D2 1.4.x settings and text fixes`.

#### Task 5: No-engine and crash guards (L13, N2)
**Files:** `KSPService/BurndV.cs`, `Pilots/Nodes/Controlers/BurnManeuvre.cs`, `Pilots/Nodes/NodeExPilot.cs`.
- [ ] `BurndV`: skip `engineInfo.Engine == null || PartRef == null`; try/catch NRE around `ThrustDirRelativePartWorldSpace` (log once per vessel); `active_full_dv` and `has_active_engine` from **our** `active_dv` rule.
- [ ] `BurnManeuvre`: burn time from the active Δv; no engine lit → full throttle and status "No active engine" (as upstream).
- [ ] `NodeExPilot`: refuses "No active engine" / "Node in the past"; `isRunning` flips back when a start finds nothing; null-check `execute_node` in `UpdateUI`.
- [ ] Test: pure `BurnTime.Choose(activeDv, fullDv, needed, thrust, mass)` cases (no active engine, staged engine only).
- [ ] Commit `Port K2-D2 1.4.0 active-engine and crash guards`.

#### Task 6: Auto-staging with an empty engine attached (S1)
**Files:** `Pilots/StagingPilot.cs`, new pure `Pilots/StagingRules.cs`; check `Nodes/BurnStaging.cs`, `Nodes/DryStageTimer.cs`.
- [ ] `spent_engines` (`HashSet<PartComponent>`, reset on vessel change): an engine already empty when its stage fires is ignored afterwards; skip propellants with `CapacityUnits <= 0`; compute fuel only when auto-staging is on. Keep `SuspendsAutoStaging`.
- [ ] Same check in `BurnStaging`/`DryStageTimer`; fix if they share the weakness.
- [ ] Tests `StagingRulesTests`: empty SRB kept attached does not fire the next stages; 0-capacity tank is not NaN; parachute stage not fired by a spent engine.
- [ ] Commit `Port K2-D2 1.4.0 auto-staging fix for spent engines`.

#### Task 7: Guards and run buttons (A7, A9, N3, N4, D1)
- [ ] `getTelemetry() == null` guards in `Lift/Controlers/Ascent.cs`, `Nodes/Controlers/TurnTo.cs`, `Attitude/AttitudePilot.cs`.
- [ ] `lift.max_throttle` range 0.05–1; `TurnTo` minimums (`max_angle_maneuver` 0.1, `max_angular_speed` 0.05).
- [ ] Run buttons of Lift, Node, Attitude follow `pilot.isRunning` (also after a refusal).
- [ ] Delete unused `node_ex.show_node_infos` and `RendezvousPilot.Flight.cs` `burn_dv`.
- [ ] Commit `Port K2-D2 1.4.0 pilot guards and run-button state`.

#### Task 8: SASTool cached reflection (K3)
- [ ] `static readonly FieldInfo` lookups, null-safe; delete `KTools/ReflexionTool.cs` (+ `.meta`) once unused.
- [ ] Commit `Port K2-D2 1.4.0 cached SAS reflection`.

#### Task 9: Maneuver gizmo and node buttons (K4, N1)
- [ ] `ManeuverCreator`: `UpdateMapGizmoWhenReady_Co` (wait for `GameState.Map3DView`, poll 0.5 s, give up 120 s) replaces the catch-and-log.
- [ ] `CircularizeBurn`: remove only the nodes before the burn UT (decision 4). Pure helper `NodeTargets.NodesToRemove(nodes, burnUT)` (in the existing `KSPService/NodeTargets.cs`) with tests.
- [ ] Commit `Port K2-D2 1.4.0 map gizmo wait; keep later nodes on circularize`.

#### Task 10: Lift coasting, pause, auto-circularize, roll lock, heading (A2 check, A3–A6)
**Files:** `Lift/Controlers/Coasting.cs`, `Lift/Controlers/Adjust.cs`, `Lift/LiftPilot.cs`, `Lift/LiftGoal.cs` (`LiftGoalRules`), `Lift/LiftSettings.cs`, `Lift/LiftAscentPath.cs`, `LiftUI.cs`.
- [ ] Coasting: sea altitude, `compute_wanted_warp_index(t)`, guard vertical speed ≤ 0, coast without warp when Coasting Warp is off; keep the `target_Ap_km` override.
- [ ] `LiftGoalRules.NextStatus(finished, goal)` (`LiftGoal.cs:224`) gains an `autoCircularize` parameter: Orbit goal with auto-circularize off ends after Adjust; `PrepareTransfer`/`Rendezvous` always circularize. Pause at the end of Adjust; `pause_on_final` default false; pause toggle greyed while auto-circularize is on; auto-circularize hidden for the forced goals.
- [ ] Release `SAS.lockedMode` when leaving Ascent.
- [ ] `HeadingCorrectionWeight(hSpeed)` (0 at 5 m/s, 1 at 50 m/s), Orbit goal only.
- [ ] Check `TargetOrbitBurn` with no engine lit (A2); fix if it does not throttle up.
- [ ] Tests: `LiftGoalRulesTests` (new cases), `HeadingCorrectionWeightTests`, a pure `CoastWarp.WantedIndex` if extracted.
- [ ] Commit `Port K2-D2 1.4.0 Lift coasting, pause and circularize-once fixes`.

#### Task 11: Attitude keys (T1a)
- [ ] `attitude.elevation`/`attitude.heading`, reading `warp.*` once when the new key is absent. Test with an in-memory settings file.
- [ ] Commit `Port K2-D2 1.4.0 attitude setting names, old names still read`.

#### Task 12: Landing small fixes (L5, L10, L11, L14, L17)
**Files:** `Landing/LandingPilot.cs`, `Landing/LandingUI.cs`, `Landing/Controlers/TouchDown.cs`, `KSPService/LandingGear.cs`, `Landing.uxml`.
- [ ] `Start_Retrograde(startBurn_UT, true, settings.max_rotation.V)`.
- [ ] Standing detection next to `net_condition` (speed < 1.5 m/s, thrust ≤ 0.5 g, hold max(1.5, 2·1.5/((1−0.5)·g)) s, ≤ 5 s; or `Landed && speed < 3`). Pure `StandingCheck` with tests (Kerbin, Mun, Minmus g; hover in `FinalDescent` must not trigger: thrust ≈ 1 g).
- [ ] Precision toggle no longer forced off: a gate label (`precision_landing_orbit_gate_vac`) and a Start refusal that says why.
- [ ] Stop releases SAS (`ReleaseSasTarget`).
- [ ] `[Landing] gear:` log adds `GetActionGroupPartCount(Gear)`.
- [ ] Commit `Port K2-D2 1.4.0 landing fixes: max rotation, standing check, precision toggle, SAS release`.

#### Task 13: KSPVessel altitude and dead code (L12, K2)
- [ ] Add `GetApproxAltitudeGrounded()` (0 when Landed/Splashed, ≥ 0) and the `AltitudeFromTerrain` fallback as new methods; log both readings in `[Landing] altitude:` once per phase; switch no existing caller in this task.
- [ ] Remove the members upstream removed after `git grep` shows no use.
- [ ] Commit `Port K2-D2 1.4.0 ground-altitude readings (logged, not used yet)`.

#### Task 14: Waypoint follows when moved (L7)
**Files:** `Landing/ReduxWaypoints.cs`, `Landing/WaypointChoices.cs`, `Landing/LandingUI.cs`, `Landing/LandingSettings.cs`.
- [ ] `ReduxWaypoint.id` read from `PlayerWaypointManager.Records[].Id` (reflection, optional: missing → no follow).
- [ ] UI block keeps the selected id and its position; pure `WaypointChoices.Follow(selectedId, savedPos, target, waypoints)` → new target or none (moved and not typed over → follow; typed over → keep).
- [ ] Tests `WaypointFollowTests`. Commit `Port K2-D2 1.4.0 landing target follows a moved waypoint`.

### Phase C — UI infrastructure

#### Task 15: Tooltips (P6)
- [ ] Port `UI/Tooltips.cs`; create it in `K2D2Window.OnUiReload` and in `VabToolsWindow`; `.k2-tooltip` rule in the mockups' palette (§4.3).
- [ ] Our 66 existing `tooltip=` become visible; write the ~40–55 missing texts (one line each, what the setting does and its unit).
- [ ] Test in `Assets/Tests/UI`: the nearest-ancestor lookup and the placement (below, or above near the bottom edge) as pure functions.
- [ ] Commit `Port K2-D2 1.4.0 tooltips; texts for every setting`.

#### Task 16: Window scale, resize handle, drag (P3, P4, P9)
- [ ] `UseStockScale = true` (main and VAB windows).
- [ ] `WindowResizeHandle` in `K2D2_Window.uxml` (`target-name="MainFrame"`, min 300×250); delete `UI/ResizeManipulator.cs`; size saver on `GeometryChangedEvent` (`main_window_size`).
- [ ] Upstream `DragManipulator` (left button, left/top, panel bounds, first-geometry restore, save only after a move), clamp kept as a pure static; adapt `DragClampTests` and `UiLayoutHarness` (width no longer fixed).
- [ ] Commit `Port K2-D2 1.4.1 window: UI scale, Redux resize handle, drag fixes`.

#### Task 17: Info table and idle work (P7, P12, A10, N4)
- [ ] Port `UI/InfoTable.cs`; use it in `LiftUI`, `NodeExUI`, `LandingUI`, `RendezvousUI`; Lift and Node rows trimmed as upstream (extra rows with Debug Mode).
- [ ] `TabbedPage.SetWindowOpen(bool)` from `K2D2Window.IsWindowOpen`/`OnUiReload`; `FullStatus` single scheduled `Apply()`; Node idle `checkManeuver` 4×/s and only when visible.
- [ ] Test: `InfoTable` reuses rows (harness), `FullStatus` writes only changed values.
- [ ] Commit `Port K2-D2 1.4.0 info table and idle UI work`.

### Phase D — Gravity Turn (Lift)

#### Task 18: ImpactModel (pure core)
- [ ] Port `Landing/ImpactModel.cs` near-verbatim; put propagation, spin and terrain behind a small interface (`IImpactBody`: `mu`, `radius`, `spin` (rad/s, body frame), `TerrainRadius(lat, lon)`) with a game adapter and a test fake.
- [ ] Tests: coast state agrees with our `KeplerPropagator` (1 m over one orbit); air velocity with spin; impact on a spherical body (analytic); **a turned body frame gives the same impact** (lesson 2026-11-18).
- [ ] Commit `Port K2-D2 1.4.0 ImpactModel with a testable body interface`.

#### Task 19: Ascent profile plumbing
- [ ] Port `Lift/AscentProfile.cs`, `Lift/Controlers/AscentControllerBase.cs`; `Ascent` derives from it; `LiftPilot` freezes the profile at start; the eight `lift.gravity_turn_*` keys and `lift.ascent_profile` verbatim (§2.5).
- [ ] Test `SettingKeysMatchUpstream`: the key list equals upstream's (literal list from `upstream/main`).
- [ ] Commit `Port K2-D2 1.4.0 ascent profile plumbing (Guided unchanged)`.

#### Task 20: GravityTurnControl (pure)
- [ ] Port `Lift/Controlers/GravityTurnControl.cs` as is (`Inputs` → `Update`).
- [ ] Tests: vertical below kick speed; kick angle applied; surface → orbital prograde with density; throttle = min of caps; nose-up when throttle-limited or hot; min throttle floor.
- [ ] Commit `Port K2-D2 1.4.0 gravity-turn control law`.

#### Task 21: AscentPredictor (pure core)
- [ ] Port `Lift/Controlers/AscentPredictor.cs`, split: `AscentSim` (pure: stage list, density table, drag k, candidate) and the game reader (`VesselDeltaV.StageInfo`, `body.GetPressure/GetDensity/GetTemperature`). Frame-spread work kept.
- [ ] Tests with a fixture rocket on a fixture Kerbin: the chosen kick/hold reaches the target Ap within tolerance; the 1.6× drag check rejects a marginal candidate; a "thrust, 0 s burn" stage is ignored.
- [ ] Commit `Port K2-D2 1.4.0 gravity-turn predictor`.

#### Task 22: ReactiveAscent and LiftPilot wiring
- [ ] Port `Lift/Controlers/ReactiveAscent.cs` (glue; part heat from `PartOwner.Parts`); `BurndV.active_full_dv` → our `active_dv`.
- [ ] Orbit goal only (decision 3) with the start message; `ascent_lead` per profile.
- [ ] Commit `Port K2-D2 1.4.0 Gravity Turn ascent profile (Orbit launches)`.

#### Task 23: Lift UI for the profile
- [ ] `ascent_profile_drop`, `guided_profile_settings`, `gravity_turn_settings` (upstream names) per §4.3; graph hidden with Gravity Turn; tooltips.
- [ ] UI harness check (`Assets/Tests/UI`): no control taller than the row, dropdown widths fixed (lesson KerbalUI `flex-grow`).
- [ ] CHANGELOG ("Gravity Turn ascent profile, from K2-D2 1.4.0, experimental"); commit.

### Phase E — Landing in an atmosphere and boost-back

#### Task 24: Redux impact prediction (sensor + log)
- [ ] Port `Landing/ReduxImpactPrediction.cs`; `ReduxReading` pure (`now` passed in).
- [ ] `[Landing] redux impact:` comparison line in `computeValues` (ours vs Redux, distance, age); log `body.relativeAngularVelocity` vs our `rotation_sign` once per landing (L8 idea).
- [ ] Tests `ReduxReadingTests`: agreement within 500 m 1 s apart; stale (> 10 s) and past results rejected; 40 s timeout.
- [ ] Commit `Port K2-D2 1.4.0 Redux impact prediction reader (logged)`.

#### Task 25: Stage recovery log
- [ ] Port `Landing/ReduxStageRecovery.cs`; log `[StageRecovery]` at landing start. Commit.

#### Task 26: Landing burn timing in air (L4)
- [ ] Port `AtmoLandingTiming` (glue), `AtmoBurnPlanner` (pure; density as a delegate), `AtmoLandingBurn` (glue, hands over at 50 m to our `FinalDescent`).
- [ ] Plug into `compute_startBurn` for a vessel already falling in the air (§4.2); the from-orbit `AeroCoast` path unchanged.
- [ ] Tests `AtmoBurnPlannerTests`: no drag → equals the vacuum stop; with drag the ignition is later; ends at 50 m ± 5 m at the planned speed; Duna and Kerbin density tables.
- [ ] Commit `Port K2-D2 1.4.0 drag-aware landing burn timing in an atmosphere`.

#### Task 27: Attitude hold and entry heat limit
- [ ] Port `AtmoAttitudeHold` (glue; `SAS.LockRotation`, `Ascent.RotationFromNoseAndDorsal`) and `EntryHeatLimit` (pure extrapolation; heat shields skipped as upstream). Our `HeatGuard` stays the authority; the limit only lowers the AoA.
- [ ] Tests `EntryHeatLimitTests`. Commit.

#### Task 28: BoostBackBurn and targeting helpers (pure parts)
- [ ] Port `BoostBackBurn` and the needed `LandingTargeting.TargetSite/Miss` helpers (into a new `Landing/BoostBack/` folder if names collide with our `LandingTargeting`), on `IImpactBody`.
- [ ] Tests: sizing returns the Δv that lands on target on a fixture body; direction search converges for downrange and crossrange misses; powered vs unpowered flip choice; refuses when the Δv on board is short (message with numbers).
- [ ] Commit.

#### Task 29: AtmoEntryGuidance
- [ ] Port `AtmoEntryGuidance` (secant on the miss, lift-vector by attitude, hand-over time from `AtmoBurnPlanner`), closed on `ReduxReading`.
- [ ] Tests for the secant and lift-vector logic with a fake prediction. Commit.

#### Task 30: LandingPilot wiring
- [ ] `Mode.BoostBack`, `Mode.AtmoReentry`; start conditions and refusals (§4.2), each refusal naming its check; `ReleaseAtmo()` on Stop; 1× warp only; staged landing still refused in air.
- [ ] Tests (pure start rules `BoostBackStart.Check(state)`): from orbit → refused; on the pad → refused; Parachute mode → parachute return as today; Redux settings wrong → refused with the setting's name; suborbital in air with precision → accepted.
- [ ] Commit `Port K2-D2 1.4.0 boost-back landings (Atmo, Propulsive, Precision)`.

#### Task 31: Land UI for boost-back
- [ ] Atmo + Propulsive: Precision enabled; `boost_back_flip_atmo`, `entry_angle_of_attack_atmo`, `entry_flight_path_angle_atmo`, `precision_landing_gate_atmo` (upstream names, §4.3); Parachute mode unchanged.
- [ ] `land.boost_back_flip`, `land.entry_*` keys verbatim; test extends `SettingKeysMatchUpstream`.
- [ ] CHANGELOG; commit.

### Phase F — finish SkySpaceAgency

#### Task 32: Docs, checklist, install
- [ ] CHANGELOG: section "Merged from K2-D2 1.4.0 and 1.4.1 (IanMealworm)" listing every ported item and what was kept ours (with reasons); README features; NOTICE credit line.
- [ ] In-game checklist, **section 24** of `docs/testing/2026-10-24-transfers-in-game-checklist.md` (French): one line per §6 item below, with the log lines to read.
- [ ] Full test run, build the zip, merge `merge/k2d2-1.4.1` into `main` (fast-forward), push, bump the pointer in the parent. Installation in the game is the main agent's call (check the game is closed first, lesson 2026-10-30).

#### Task 33: One home for the mod identity
- [ ] `Code/ModIdentity.cs` (`K2D2.ModIdentity`): `Title`, `ModId`, `SettingsPrefix` (`skyspaceagency_`), `WindowId`, `VabWindowId`, `ToolbarButtonPrefix`, `LogPrefix`, `VabFormatPrefix`, `AboutTitle`. Every literal of §5 goes through it; window and About titles set from code, not uxml.
- [ ] VAB import accepts both `SkySpaceAgency.*` and `K2D2.*` formats (files exchanged between the two mods).
- [ ] Test `IdentityLiterals_OnlyInModIdentity`: scans `Assets/SkySpaceAgency/Code` and `Assets/UI` for `SkySpaceAgency` (case-insensitive) outside `ModIdentity.cs`.
- [ ] Commit `Gather the mod identity in ModIdentity`.

### Phase G — the K2-D2 pull request

#### Task 34: Identity switch editor script
- [ ] `Assets/Editor/Identity/IdentitySwitch.cs` with its own `.asmdef` (references `ksp2community.ksp2unitytools.editor` and the Addressables editor; lesson: `Assets/Editor` alone compiles into `Redux.Template.Editor.asmdef`, which references neither). Methods `ToK2D2()` / `ToSkySpaceAgency()`: set the `Mod` asset (`id`, `name`, `author`, `description`, `version`, `source`, `m_Name`), call `Mod.CreateAddressablesGroups()` and `Mod.RefreshPipelines()` (`Library/PackageCache/ksp2redux.sdk@*/Editor/Modding/Mod.cs`, lines ~646 and ~477 on 2026-11-19), save assets. Batch: `Unity.exe -batchmode -nographics -projectPath … -executeMethod K2D2.EditorTools.IdentitySwitch.ToK2D2 -quit -logFile "$S/identity.log"`.
- [ ] Dry run on a scratch clone; compare with the rename commit `ffb1a13` reversed (same file set).
- [ ] Commit to `main` (harmless there, reusable for each PR update).

#### Task 35: Branch `pr/k2d2` with the K2-D2 identity
From `main` after T34: `git switch -c pr/k2d2`. One commit "Switch to the K2-D2 identity", everything below:
- [ ] `git mv Assets/SkySpaceAgency Assets/K2D2` (GUIDs kept by the `.meta`); `git mv Code/SkySpaceAgency.asmdef Code/K2D2.asmdef`, asmdef `name` `K2D2`; test asmdefs reference `K2D2` (names `K2D2.Tests.*`).
- [ ] Run `IdentitySwitch.ToK2D2`: mod asset `K2-D2 [K2D2]`, id `K2D2`, name `K2-D2`, author `IanMealworm` (contributors in README), their description, version **1.5.0**, source `https://github.com/IanMealworm/K2D2Redux`; Addressables groups `K2D2_*`, `addressables_K2D2_all`, profile `{SpaceWarpPaths.K2D2}`, labels `K2D2_patches`/`K2D2_prefab_patches`; pipelines and manifests (`Deploy/K2D2`, `Assets\K2D2/Copied`); `swinfo.json` regenerated (`main_assembly` `K2D2.dll`).
- [ ] `ModIdentity` to K2-D2 values: title `K2-D2`, settings `k2d2_settings.json` / `k2d2_landing_atmo.json` / `k2d2_landing_vac.json` (their players' files load, §2.5), `WindowId` `K2D2`, toolbar `BTN-K2D2Flight/OAB/KSC`, loggers `K2D2.*`, VAB window id and title, VAB formats `K2D2.*`.
- [ ] `project.info` name `K2D2Redux`; README in their structure with our features added and a contributors line; CHANGELOG: a `K2-D2 v1.5.0` section summarizing our changes in their style, older sections kept; NOTICE and LICENSE credits intact (Christophe Floutier, IanMealworm, LeoMarinDev, JLSkyzer).
- [ ] Drop `docs/` from the branch (decision 7), unless the user decides otherwise.
- [ ] Checks: `git grep -i skyspaceagency` returns only the contributors/credits lines; tests green; `Deploy to Zip File` gives `K2D2.zip` with `mod_id` `K2D2` and `K2D2.dll`; the identity test of T33 runs against `K2D2`.
- [ ] Later updates: merge `main` into `pr/k2d2` (conflicts only in identity files; re-run `ToK2D2`), never rebase a pushed branch.

#### Task 36: Fork and pull request (needs the user)
- [ ] The user creates the fork `JLSkyzer/K2D2Redux` (`gh repo fork IanMealworm/K2D2Redux --clone=false`): not done by this plan. Our history contains theirs up to `8273fb8`, so the fork accepts the branch as is.
- [ ] Add remote `k2d2fork`, push `pr/k2d2`; draft the PR description (what changes for K2-D2 players: Dock tab replaced by Rendezvous with docking, new tabs Orbit and Transfer, VAB tools, settings compatibility, what was not tested in game, LF line endings on files that were CRLF). **Opening the PR is publishing: ask the user first.**

---

## 5. Identity occurrences to switch (inventory at `60bd818`)

`git grep -i -c skyspaceagency` outside `docs/` (32 docs files also mention it):
- **Generated / assets:** `Assets/SkySpaceAgency/SkySpaceAgency.asset` (5), `swinfo.json` (7), `Pipelines/{Build for Editor,Build for Player,Deploy to Zip File} Manifest.asset` (9 each), `Pipelines/Deploy to Zip File.asset` (2), `AddressableAssetsData/AddressableAssetSettings.asset` (4: profile, `{SpaceWarpPaths.SkySpaceAgency}`, two labels), 7 groups and 14 schemas `SkySpaceAgency_*` / `addressables_SkySpaceAgency_all*` (file names and `m_Name`).
- **Folders and assemblies:** `Assets/SkySpaceAgency/` (Code, Copied, Definitions, Pipelines), `Code/SkySpaceAgency.asmdef`, six test asmdefs `SkySpaceAgency.Tests*` referencing `SkySpaceAgency`.
- **Code:** `K2D2_Plugin.cs` (3 settings file names, 3 toolbar ids, migration message, `WindowId`), `UI/VabToolsWindow.cs` (window id, title), `VabTools/VabDump.cs`, `VabExport.cs`, `VabImport.cs` (format strings, texts) and their tests, `K2D2OtherModsInterface.cs` (log source), ~25 logger names (`LogManager.GetLogger("SkySpaceAgency.*")`).
- **UI:** `K2D2_Window.uxml` (AppShell title, info tooltip), `about.uxml` (title).
- **Project and docs:** `project.info`, `README.md`, `CHANGELOG.md`, `NOTICE.md`, `LICENSE.md`.
- Alt-O is shared with K2-D2 (both must not be installed together): unchanged.

## 6. Risks, and what only a flight can settle

- **Merge mechanics:** a `-s ours` merge records upstream as merged; anything not ported in Phases B–E is lost silently. Mitigation: §2 is the checklist, each row ticked by a commit or a written "no".
- **Future upstream merges:** files we rewrote (LandingPilot, LiftPilot, NodeExPilot, Docks) will conflict again; ported files kept near-verbatim should not.
- **Redux 26w46a vs their 26w42b:** every reflected name exists in the installed `Ksp2Redux.dll` (string check 2026-11-19), not their signatures. `AtmoSettingsProblem` binds at compile time: a rename breaks the build, not the flight.
- **In game only:**
  - boost-back on our test boosters: attitude hold authority (upstream measured 40–60 % of the expected lateral effect), entry guidance against Redux's Flown prediction under our autopilot, the flip choice, hand-over to our final descent, legs;
  - Redux's prediction while our pilots steer (it assumes the player's "Assumed attitude" setting), and whether it models parachutes;
  - air landing timing on capsules and wide vessels (drag fit tuned on boosters upstream);
  - standing check during our `FinalDescent` hover on low-g bodies;
  - `body.relativeAngularVelocity` against our calibrated rotation sign;
  - Gravity Turn: `DeltaVStageInfo` on 26w46a ("thrust, 0 s burn" stages), predictor stutter, heat guard units, transfer azimuths (not offered until tested);
  - auto-circularize off ends the run after Adjust and pauses; transfer/rendezvous goals still circularize; coasting with the new warp index does not overshoot; coast without warp reaches the target Ap;
  - spent-engine staging with attached SRBs, with 26w46a's `ActivateNextStage`;
  - map gizmo positioned once the map opens, no NRE;
  - `WindowResizeHandle` found by reflection on 26w46a and not fighting our drag; `UseStockScale` with the clamp at non-100 % UI scale, VAB window included; right-drag no longer moves the window; a click no longer sends it to the corner after a restart;
  - tooltips: pointer events in this embedding (our old resize code exists because `PointerUp` did not arrive), clipping by the ScrollView/AppShell, VAB window;
  - photo mode: pilots keep flying, the window comes back; `SetWindowOpen(false)`: status catches up on reopen;
  - waypoint ids stable when a waypoint is moved; mission waypoints without id;
  - `ClampSetting` on read changes no tuned value in the installed json.
- **PR side:** IanMealworm may not want the Dock tab replaced or the size of the change; the PR description must say what K2-D2 players gain and lose, and that much of it is not flown in game yet. Their CRLF files show as whole-file changes (review with "hide whitespace").
