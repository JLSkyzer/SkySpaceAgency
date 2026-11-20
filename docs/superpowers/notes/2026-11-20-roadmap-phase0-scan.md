# Phase 0 scan for the booster / return / video / scripting roadmap (Redux 26w47a, 2026-11-20)

Phase 0 of `docs/superpowers/roadmap-booster-return-video-scripting.md`: read the game and Redux before designing
anything. Nothing was launched, nothing was installed, no mod code was written. Every answer below is read, not
observed in game. Proof levels used in this note:

- **[IL]** read in the decompiled `Ksp2Redux.dll` (type and member named);
- **[XML]** read in the installed `Ksp2Redux.xml`;
- **[notes]** Redux release notes (`gh api repos/KSP2Redux/Redux/releases`), build number and date given;
- **[docs]** `Modding-Docs/` or `docs/redux-part-modeling-with-blender.md`;
- **[mod]** already written in this repo;
- **[inferred]** deduced from the above, not read;
- **[unknown]** only settled in game.

## Verdict, read this first

**The roadmap holds, but its separation / switch / reload flow (R3, R4, R4b, R4c) was designed without knowing that
Redux 26w47a already ships a stage recovery system with a different flow, and that flow should be the base.** Four
facts decide it:

1. **Redux already does "fly the dropped stage from its separation point, then merge the result back into the
   mission"** (`Redux.StageRecovery.StageRecoveryManager`, added in build 0.2.9.0.104139, 2026-08-15 [notes]). It is
   FMRS-like: mission first, stage later, with two whole-universe reloads. It is not "pause, switch to the booster in
   the same session". See section 1.
2. **Switching to a dropped stage inside the same session would lose the upper stage**: a non-active vessel more than
   2 km away goes on rails, and a vessel on rails whose orbit enters the atmosphere (radius + max(atmosphere depth,
   max terrain height)) is **destroyed**, unless it has parachutes it can come down under [IL]. A suborbital upper
   stage would die while the player flies the booster. Redux avoids this by parking the mission in a save.
3. **Redux has no flight recorder, no video recorder and no replay** in 26w47a [IL][XML][notes]. The Redux Discord
   claim "built-in replay mode with flight recording" is **not confirmed**: the only "replay" in the code is stage
   recovery's own word for "fly a recovery point". R5 and R7 stay ours, as the user decided.
4. **Redux's Lua scripting has been released** (build 0.2.9.0.103836, 2026-08-04 [notes]). By the user's rule of
   2026-10-30 ("only a feature Redux has actually released counts; compare, then decide with the user"), **R6 needs
   that comparison and a decision with the user** (section 6). It is a mod-scripting surface, not a vessel autopilot
   language and not an onboard computer, so R6 is not made redundant, but it overlaps.

**Recommendation** (short): rebuild R3/R4/R4c on Redux's stage recovery instead of on a pause-and-switch, and spec R4b
as "patch Redux's merge step" or drop its ghost replay; keep R5/R7 as planned (nothing to reuse but pieces); ask the
user the two decisions in section 8 before any spec.

### Coverage of the roadmap by what Redux already has

| Roadmap item | Redux 26w47a | What is left for us |
|---|---|---|
| R4 pause, save, wait for Redux's save | Recovery point saved automatically after a split, default 2 s of game time later | Pause only after the save exists (pausing earlier blocks it). Trigger "Fly" ourselves (reflection) |
| R4c steps 1-3, 5-6 (fly the booster, then back to the upper stage) | `StartReplay` / `CompleteActiveLanding` / `ReturnToMission`, mission checkpoint, stage is the active vessel | Auto-start, Return at once, resume of Lift after the reload (we do not survive a universe reset today) |
| R4c step 4, R4b step 6 (booster kept where it landed) | Landed / splashed stage is serialized and merged into the mission; recovered or lost stages are dropped | None for the data. But it appears **at once** and the in-flight copy is **removed** |
| R4b step 5 (visual replay of the booster, never remove the in-flight copy, landed vessel appears at touchdown time) | **Not covered, contradicted** by the merge step | Patch of an internal static method, deferred spawn, kinematic drive. Largest open design cost |
| R1 flight-view picker | Redux already raycasts the terrain from the mouse in flight view (placement mode) | Call it; no new raycast needed |
| R1 return point shown in game / saved | Player waypoints (`PlayerWaypointManager`), saved in the save file | Choose waypoint, or `ModSaves`, or part data |
| R1b recovery computer | Eligibility = any controllable vessel with a `Module_Command` part, not the active one | Patch if only our part may qualify; part to build (already researched) |
| R2 impact point of a falling booster | Redux impact prediction (flown and planned), already read by `ReduxImpactPrediction` | Reuse; upstream boost-back code is already in the repo |
| R5 recording | Nothing. `Unity.Recorder*.dll` are empty stubs, no encoder in the game folder | Everything. Pieces to reuse: Redux's screenshot pipeline, part cameras |
| R6 scripting language | Runtime Lua (MoonSharp) for mods, released | Decision with the user, section 6 |

## 1. Stage recovery and separation

### What Redux's stage recovery is exactly

Namespace `Redux.StageRecovery`, all types `internal`, started from `ReduxHooks` through `StageRecoveryManager.Initialize()`
[IL]. Window with an AppBar button (`BTN-ReduxStageRecovery`, window id `redux-stage-recovery`), shown only in flight, map
or loading states [IL]. Release notes: added in 0.2.9.0.104139 ("allowing detached stages to be flown from their
separation point and merged back into the current mission"), option to disable point creation in 0.2.9.0.108007,
campaign checks in 0.2.9.0.109473 [notes]. **No page in `Modding-Docs/` and no public API**: the XML only documents the
enums and the record fields [XML].

**When it saves, and what** [IL, `StageRecoveryManager.OnVesselSplit`, `CaptureAfterSplit`]:

- It subscribes to `VesselSplitMessage` (fields `remainingVessel`, `newVessel`, `isNewVesselFromSubVessel`). An undocking
  is ignored unless `IncludeUndocking` is on; the whole feature is off if `CreateRecoveryPoints` is off (default on).
- One frame later it picks the **candidate stage** (`StageRecoveryCandidateSelector`): a vessel that is not destroyed,
  is `IsControllable` and has `HasCommandModule`, and is **not the active vessel**. The new vessel wins over the
  remaining one.
- It then waits `PersistentProfileManager.StageRecoverySaveDelaySeconds` of **universe time** (setting "Recovery Save
  Delay", default 2 s, clamped 0 to 30 s [IL `PersistentSettings`]) and, if the save system is busy, up to 30 s of real
  time. It then saves the **whole game** with `SaveLoadManager.SaveGameToFile(..., SavedGameType.Auto, ...)`.
- Files: `<active campaign folder>/ReduxStageRecovery/stage-<id>.json` (the snapshot), `mission-<id>.json` (the parked
  mission, written later) and `index.json`. The snapshot is rewritten (`PrepareReplaySnapshot`) so that the stage is the
  active vessel, the game state is `FlightView` and the flight controls are the stage's own.
- Index entry (`StageRecoveryRecord`): `Id`, `VesselGuid` (the stage), `ParentVesselGuid` (the active vessel when it
  was taken), `VesselName`, file names, `CapturedUniversalTime`, `CapturedMissionElapsedTime`, `CompletedUniversalTime`,
  `Origin` (Decoupled / Undocked), `Status` (Ready, Replaying, Landed, Splashed, Recovered, Lost, Failed), `LastError`.
- The records are also stored **inside the save** (mod save id `Ksp2Redux.StageRecovery`, through `ModSaves`), so loading
  an older save brings back only the records of that timeline, and only those whose snapshot file exists. A flight
  revert deletes the records of the reverted flight (`OnFlightReverted`).

**Does it let the player fly one stage, then come back to the other?** Yes, by design [IL]:

1. The player (window button "Fly", or `StartReplay(recordId)`) starts a session. Phases
   (`StageRecoverySessionPhase`): `SavingMission` (saves the current game as the mission checkpoint, `Auto` save),
   `LoadingStage` (`Game.ResetUniverse`, then `LoadGameFromFile` of the snapshot), `FlyingStage`, `LoadingMission`.
2. In `FlyingStage` the stage is the only valid active vessel. Switching away makes Redux load the mission back
   (`OnVesselChanged`).
3. The session ends by `CompleteActiveLanding()` (the stage must be Landed or Splashed; it is serialized with
   `SerializationUtility.VesselToSerializable`), by recovering the vessel (`VesselRecoveredMessage`), by
   `ReturnToMission()` (retry; counts as lost when the difficulty option `AllowQuickLoad` is off), or when the stage is
   destroyed.
4. The merge (`StageRecoveryReconciler.TryReconcile`) edits the **mission checkpoint** file: **it removes every vessel
   with the stage's GUID, then appends the terminal stage** when the outcome is Landed or Splashed (nothing is appended
   for Recovered or Lost). Recovered stages credit their resources (`PendingResourceCredits`). The checkpoint is then
   loaded (`LoadingMission`).

**API and events**: nothing public. Public things we can use: `GameLoadFinishedMessage` (`IsSuccess`),
`QuitToMainMenuStartedMessage`, `VesselChangedMessage`, `VesselSplitMessage`, `VesselDestroyedMessage`,
`VesselLandedGroundAtRestMessage`, `VesselRecoveredMessage` [IL]. Internal (reflection or Harmony, like the mod already
does for waypoints and placement): `StageRecoveryManager.Instance`, `Records`, `ActiveRecord`, `SessionPhase`,
`IsCapturePending`, `IsTransitioning`, `IsReplayActive`, `CanRetryRecovery`, `StartReplay(string)`,
`CompleteActiveLanding()`, `ReturnToMission()`, `DeleteRecoveryPoint(string)`, and the internal event `Changed`.
**Redux's own test server (`CliIntegrationServer`, enabled by a config switch or `KSP2REDUX_CLI_INTEGRATION_PORT`)
calls exactly these members** (`StageRecoveryStart`, `FinishLanding`, `Return`, `Status`), which is a good sign they are
the intended handles, not a proof they are stable. `ReduxStageRecovery.cs` in the mod already reads `Instance`,
`SessionPhase`, `ActiveRecord` by reflection [mod].

**How do we know "it has saved"?** [IL] `IsCapturePending` is false **and** a record for the stage's GUID is in
`Records` with `Status == Ready`; Redux also logs `[Stage Recovery] Saved <name> at MET … (UT …)`. A failed or skipped
capture logs a `[Stage Recovery]` warning and leaves no record.

**Gotcha for R4 (pause)**: the capture waits for universe time to pass the delay. **Pausing at the moment of
separation would block the save** [IL, inferred that paused universe time does not advance]. The mod has to pause
only after the record exists (default delay 2 s of game time), and tell the user when the delay setting is large.

**Gotchas for any mod using it** [IL]:

- During a session, every save or load that Redux did not start is **blocked** (`GuardSaveLoadOperation`); Auto saves
  are blocked silently, manual ones show a message box. Flight revert is blocked too (`AllowFlightRevert`).
- Recovery points are only made for stages with a command module. **Any** pod or probe qualifies, not only ours.
- Mission and stage are both full saves: anything the mod keeps in memory about vessels is stale after each reload.
- Waypoints: `PlayerWaypointManager` keeps player waypoints across the stage session loads (`OnGameLoadFinished`
  checks `StageRecoveryManager.IsReplayActive`; the bug was fixed in build 0.2.9.0.104290 [notes]).

### Which vessel is left when the player flies the stage? (answer to "switch and come back")

Two flows exist; they do not behave the same.

- **Redux's flow** (parked mission): the upper stage is not simulated during the booster flight. It stays in the
  mission checkpoint. [IL]
- **The roadmap's original flow** (switch in one session): the vessel the player leaves goes through the normal rules
  [IL]:
  - Physics: vessels within 4 000 000 m² (**2 km**) of the flight observer are `PhysicsMode.RigidBody`; farther away
    they are `Orbital` (on rails, analytic orbit), or `AtRest` when landed or splashed
    (`FloatingOrigin.CalculateInertialReferenceFrameChildren`). A new vessel from a split gets the same test when it
    is created (the split code in `SpaceSimulation`: `RigidBody` under 4 000 000 m², else `Orbital`).
  - View objects (the loaded model) are loaded within **100 km** (`UniverseView.DEFAULT_PROXIMITY_LOAD_RANGE_METERS`,
    `LoadUnloadProximityViewObjects`) and destroyed beyond; the vessel itself stays in the simulation.
  - **Destruction on rails**: `VesselComponent.TryGetCelestialBodyCollision` + `SpaceSimulation.IsAltitudeBelowCollisionApproximationLimit`:
    an `Orbital` vessel below `radius + max(MaxTerrainHeight, atmosphereDepth)` is destroyed with the log "Approximate
    collision … Destroying". Exception: `TryDescendVesselUnderParachute` sets a parachute-equipped vessel down gently
    when the estimated landing speed is within its crash tolerance. This is the KSP1 behaviour the roadmap feared,
    confirmed. `NonActiveVesselOnCrashTrajectoryMessage` (published from `OrbiterComponent`) warns the player.
  - A vessel with engines running goes on with a tracked on-rails burn (`TryBuildTrackedEngineBurn`, see the 26w46a
    notes in the parent `CLAUDE.md`), in a fixed direction.
  - Consequence: the in-session flow leaves a suborbital upper stage to die, or to fly an uncontrolled burn. Only a
    stage in a stable orbit survives it.

### Vessel switching, pause, ids, staging

- `ViewController.SetActiveVehicle(guid | VesselComponent, observe, force)` has **no pause check** and does not touch
  warp or SAS. `PerformSetActiveVessel` neutralizes the stick and X/Y/Z of the old vessel and does **not** clear its
  throttle (matches `OwnVesselStop` in the mod) [IL]. A switch with `observeVehicle` is **asynchronous** (it waits for
  the body's local space view, `OnLocalSpaceDependenciesLoaded`; `IsPendingVehicleChange`). Whether this completes
  while paused is **[unknown]**.
- Pause: `ViewController.SetPause(bool)` → `UniverseModel.SetTimePaused`; Redux's Lua `Game.SetPaused` also calls
  `TimeWarp.SetIsPaused`. Messages `PauseStateChangedMessage`, `GamePauseToggledMessage` [IL].
- **A separated stage becomes a vessel with a new GUID**; `SpaceSimulation` logs "Part Owner changed … using new Guid"
  [IL]. After a decouple the new vessel's control owner is set by `VesselComponent.SetControlOwnerToDefault` (the
  first `PartComponentModule_Command` part, else the root part). So **the booster's recovery computer becomes its
  control owner automatically** as long as it is the first command module of that vessel. The active vessel is the
  piece with the player's control owner (parent `CLAUDE.md`, jettison notes).
- **Vessel ids are stable across a save and a reload**: Redux itself records `VesselGuid` at capture and finds that
  vessel in the universe after loading the snapshot (`FindActiveRecoveryVessel`, `PrepareReplaySnapshot` matches
  `SerializedAssembly.Guid`) [IL]. The answer to the question of R4c step 7.
- **Is there a "load finished" message?** Yes, `GameLoadFinishedMessage.IsSuccess` [IL]. But Redux ends its session
  slightly later (`OnInternalLoadFinished` clears the phase): the mod should wait for
  `StageRecoveryManager.SessionPhase == Idle` after the message, not only for the message.
- **Does Lift's state survive the reload?** No. The mod subscribes to neither `GameLoadFinishedMessage` nor
  `QuitToMainMenuStartedMessage` (grep of `Assets/`, 0 hits) and Lift does not resume today [mod]. After
  `Game.ResetUniverse` all `VesselComponent` references are dead. Whether the plugin objects themselves survive
  `ResetUniverse` is **[unknown]**. The resume state therefore has to be stored by us at the separation (goal,
  phase, plan, staging), keyed by the vessel GUID, the same pattern as `PlanStore`.
- **What if the player loads an older save or another flight?** Redux scopes its records to the save timeline (see
  above). Matching our resume on the flight and on Redux's record `Id` + `VesselGuid` is enough.

## 2. Control of the booster

- **Eligible and controllable** [IL, `VesselComponent.UpdateControllableStatus`]: `HasCommandModule` /
  `IsControllable` are true as soon as one `PartComponentModule_Command` with a `Data_Command` exists.
  `VesselControlState.FullControl` needs `CommandControlState.FullyFunctional`: enough crew (`minimumCrew`, 0 for a
  probe), the required resources (`requiredResources`, normally ElectricCharge), command enabled, CommNet when
  `requiresCommNet`. Otherwise the vessel is `NoControl` (or `NoCommNet`, `FullControlHibernation`).
- **SAS**: there is no SAS module and no SAS level. `VesselSAS.CanEngageSAS()` is `ControlStatus != NoControl` [IL];
  a powered `Module_Command` is enough (same finding as `docs/redux-part-modeling-with-blender.md`).
  `Module_ReactionWheel` (`Data_ReactionWheel`: pitch/yaw/roll torque, required resources) gives the torque.
- **Detecting an uncontrollable booster at the Lift start**: possible [inferred]: the stage's parts are known before
  the launch (the mod already builds per-stage budgets), so the check is "a command part, EC supply or a battery,
  `minimumCrew == 0`, torque from wheels/RCS/fins". The booster does not exist as a vessel yet, so this is a static
  check, not `IsControllable`.
- **Control surfaces and grid fins**: `Module_ControlSurface` / `Data_ControlSurface` (`AllowControl`,
  `IsCtrlSurfaceActive`, `CtrlSurfaceRange`, `CtrlSurfaceDeployAngle`, `UseAirbrakeControl`, `IgnoreSASInputs`,
  `ActuatorSpeedNormalScale`…). SAS output reaches them: the module reads `flightCtrlState.pitch/roll/yaw` (which
  include SAS) unless `IgnoreSASInputs` (then `inputPitch/…`, the raw input) [IL]. **No "grid fin" concept exists in
  Redux** (grep of the code and of the XML): a grid fin is a control-surface part we would have to build, or an
  existing one used as such. Authority and drag values are in the part data and not measured.
- **Engines**: `Data_Engine` has `allowRestart` per mode (a shut-down engine cannot restart when false),
  `minThrust` (→ `Module_Engine.ThrottleMin`), `ignitionThreshold`, `throttleLocked`, `flameoutBar`. **No ignition
  counter and no ullage logic exist** [IL, grep]. A relight is limited only by `allowRestart` and by propellant flow.
  Landing legs: already handled (`LandingGear`).

## 3. Atmospheric descent physics

- **Lift**: lift exists. `Data_Drag` has `BodyLiftEnabled`, `bodyLiftMultiplier`, a `BodyLiftForce`, with a lifting
  surface curve (`PhysicsSettings.BodyLiftLiftingSurfaceCurve`); wings and control surfaces use `Module_LiftingSurface`
  / `Module_ControlSurface` [IL]. So steering the impact point with attitude is physically possible. How much a
  booster body lifts is **[unknown]**: the mod's `EntryLiftSteering` (K2-D2 boost-back, inactive here) is the
  existing attempt [mod].
- **Wind**: none for vessels. The only "wind" code is vegetation (`PlanetaryWindApplier`, Vegetation Studio) [IL].
  The atmosphere co-rotates, as `ReentryPredictor` already assumes [mod].
- **Redux's impact prediction** (`Redux.Simulation.ImpactPrediction`: `ImpactPredictionSystem`, `ImpactPredictor`,
  result with body position, impact UT, status, footprint ellipse, kinds Flown and Planned, attitude modes such as
  HoldCurrent, Prograde, Retrograde) is internal ECS code [IL]; `ReduxImpactPrediction.cs` reads it by reflection and
  `AtmoEntryGuidance` already uses it [mod].
- **Terrain and pads**: `KscLocation` (mod) reads the pad. Redux has an optional landing-barge decoration on Kerbin
  (`KerbinLandingBargeSpawner`: prefab `Decoration.Kerbin.LandingBarge.prefab` at latitude -0.8, longitude -64.0,
  5 m above sea level, shown within 50 km) [IL]. Whether the barge has a collider a booster can stand on is
  **[unknown]**. There is no generic "landing pad" surface object. `Modding-Docs/Code modding guides/Custom Launch
  Locations.md` says it is not updated for Redux.
- Terrain colliders exist only around the active vessel (parent `CLAUDE.md`, "Rejected"); build 0.2.9.0.104878 says
  terrain collisions are "prepared ahead along each vessel's path" [notes], not verified for a landing far away.
- `CelestialBodyComponent.MaxTerrainHeight` stays [unknown] (no setter called, parent `CLAUDE.md`), which also feeds
  the on-rails destruction limit above.

## 4. Picker and positions

- **Flight-view picking already exists in Redux** (`PlayerWaypointManager.BeginPlacement`, then `TryGetFlightPlacement`)
  [IL]: ray from `GraphicsManager.GetCurrentUnityCamera()` through the mouse, `Physics.RaycastNonAlloc` on the layer
  mask `268435456` (layer 28, terrain colliders), fallback `PQS.RaycastTerrainWorld`, last fallback a ray-sphere
  intersection with the body; altitude through `GetTerrainAltitude`. It works in `FlightView` and `Map3DView`; a left
  click completes it (`PlacementCompleted(bodyName, lat, lon)`), right click or Escape cancels. **The mod's
  `ReduxPlacement` can use the flight-view mode directly; no switch to the map is needed** (today it switches
  [mod], to be re-checked in game).
- **Waypoints can store the return point** [IL]: `PlayerWaypointManager.Add(name, bodyName, lat, lon, altitude?)`
  (internal) keeps a `PlayerWaypointRecord` (`Id`, `Name`, `BodyName`, `Latitude`, `Longitude`, `AltitudeFromRadius`),
  saved in the save under `Ksp2Redux.PlayerWaypoints`, and creates the in-game marker with
  `SpaceSimulation.CreateWaypointSimObject(WaypointComponentDefinition, body, lat, lon, alt)`, which is public.
- **Where to store a return point** [inferred]: three candidates, all inside the save so they travel with Redux's
  snapshots: a Redux waypoint (visible, but the vessel link is ours), our own `ModSaves.RegisterSaveLoadGameData<T>`
  block (per save), or a `Data_` class with `[KSPState]` on our computer part (per vessel, follows the booster to its
  landing; `docs` "General overview of custom modules": `[KSPState]` is saved in the save). Data written during a
  stage session is lost at the return (the mission checkpoint is loaded), which is right for session-only data and
  wrong for anything that must outlive it (telemetry, recordings: keep those in files).

## 5. Cameras and recording (R5, R7)

- **Redux has no recorder**: no replay or recording type in the code or the XML. `Unity.Recorder.dll` (6 KB) holds
  only a `RecorderBindings` behaviour (a dictionary of object bindings) and `Unity.Recorder.Base.dll` (4.6 KB) only
  `Unity.Media.RefHandle<T>`: neither is a recorder, and nothing in the decompiled `Ksp2Redux.dll` references them
  [IL]. No ffmpeg or video encoder in the game folder.
- **Camera system** [IL]: `UniverseCameraManager` with three camera ids (`Flight`, `UI`, `IVA`), a primary screen
  camera, shots (`ICameraShot`), shot overrides (`SetCameraShotOverride`), render stacks per render space
  (`GetCameraRenderStack`). **Part cameras are not extra renderers**: `Module_Camera` / `Data_Camera` (camera
  transform, optics, name, roll; radial and downward cameras, build 0.2.8.0.102311 [notes]) are registered in
  `PartCameraController`, and activating one installs it as the **flight camera's shot override**. So there is still
  one picture on screen, from a part's point of view. "Several points of view" means switching the shot, not
  rendering several at once. A second `Camera` rendering to a `RenderTexture` without breaking the main view
  (URP, post-processing, volumetric clouds and plumes) is **[unknown]**, only settled in game.
- **Camera parts**: they exist (module `Module_Camera`, patchable onto any part: `Data_Camera` fields are
  `[KSPDefinition]`), so a point of view on the booster or the upper stage needs no new part: a Patch Manager patch
  or a module on our computer part can add one [inferred from the module, not tested].
- **What Redux's screenshot does** (`Redux.UI.Screenshots.ScreenshotCapture`, key F1 since build 0.2.9.0.104706
  [notes]) [IL]: pauses the universe, optionally raises render scale and LOD bias, waits for the volumetric plumes
  to refine (`VolumetricPlumeRenderer.BeginScreenshot` / `IsScreenshotReady`, up to 30 s), then
  `HdrOutput.CaptureScreenshot` → `ScreenCapture.CaptureScreenshotIntoRenderTexture` (the **final screen**, size
  `Screen.width × Screen.height`, HDR decoded to SDR) → `AsyncGPUReadback.RequestIntoNativeArray` → JPG or PNG on a
  background callback. Usable pieces for a video: the readback pattern and `HdrOutput.CaptureScreenshot` (internal,
  reflection). Not usable as is: it pauses time and waits for plumes, and it captures the screen **with the UI**.
- **Keeping the UI out**: the game has a hide-UI action (`ToggleUIVisibility` → `UIViewController.SetUIHidden`) [IL].
  Whether it also hides Redux and mod windows (UI Toolkit panels) is **[unknown]**. A capture of the camera alone
  would avoid the question; see the unknown above.
- **Plumes and clouds are temporally refined** (screenshot code and release notes on cloud smearing) [IL][notes]: a
  video will not look like a screenshot. To judge in game.
- **Output size**: 720p / 1080p means either rendering a camera to a fixed-size `RenderTexture` or changing the render
  scale (`GraphicsManager.SetRenderScalePercent`, which Redux does for screenshots) [IL]. [unknown] which one is
  acceptable.
- **Cost and frame pacing**: [unknown], to be measured in game as the roadmap already says.
- **Audio**: not asked. The game ships the Wwise binding with `AkSoundEngine.StartOutputCapture(path)`,
  `StopOutputCapture`, `AddOutputCaptureMarker` [IL, `AK.Wwise.Unity.API.dll`]. Whether the shipped sound engine
  honours it is [unknown].
- **Pause and warp states during a recording**: `PauseStateChangedMessage`, `TimeWarp`; `ScreenshotCapture` already
  shows the pattern for pausing without side effects (`SetTimePaused(true, raiseEvents: false)`) [IL]. The recording
  rule "follow game time, not wall clock" is ours to implement. **Reloads move game time backwards**: stage recovery
  loads the mission checkpoint at the checkpoint's UT, so "drop every frame at or after the reloaded UT" (R5) is
  exactly the case that will happen at every return from a stage session.
- **For the overlay (R7)** [mod / IL]: per-engine ignition and thrust (`EngineIgnited`, `EngineShutdown`,
  `FinalThrustValue`, already used by the mod), engine positions (`Data_Engine.ThrustTransforms`, live transforms
  only on a loaded vessel), propellants per stage (`VesselDeltaVComponent` stage info). Composing an overlay into a
  captured frame under URP: [unknown].

## 6. Scripting (R6) and Redux's Lua

- **What is released** [IL][notes]: "Runtime Lua scripting support" (0.2.9.0.103836, 2026-08-04), "Lua hooks that run
  only for mods in the active feature set" (0.2.9.0.105810). It runs on the shipped `MoonSharp.Interpreter.dll`
  (`Redux.Lua`, `SpaceWarp2.API.Lifecycle.ModScriptRuntime`, `ModScriptLoader`). Mod environments get `Game`
  (`Update`, `FixedUpdate`, `SceneEnter/Exit`, `OnFlight/OnKSC/OnEditor/OnMainMenu`, `OnActiveVesselChanged`,
  `OnCampaignLoaded/Unloading`, `OnSetActivated`, `StartCoroutine`, `SetPaused`, `WarpTo`, `WarpToTime`, `State`,
  `Time`, `IsPaused`), `GameState`, `Input` (key queries), `Files` (read/write/list, rooted at the mod folder),
  and a `Wait` prelude (`Seconds`, `Until`, `InputDown/Up/Held`; `Wait.Event` is **not implemented**, it logs a
  warning). A script run in the game's `scripts/` folder also gets `Script` (`Id`, `Name`, `Kind`, `Stop`). The
  stock KSP2 script objects `View` (`SetActiveVessel`, `GetActiveVessel`, vessel info, telemetry, orbit, aero…) and
  the teleport helpers of `SpaceSimulationLua` (`TeleportToSurface`, `TeleportToOrbit`, `TeleportToRendezvous`) are
  also in the game [IL, `KSP.Sim.impl.lua`; the registration of the latter was not read].
- **What it is not**: no onboard computer, no per-vessel script that runs with the vessel, no autopilot library
  (no throttle, attitude or staging calls beyond the stock `View`/simulation objects), no sandbox beyond what the
  mod folder gives (`Files` is rooted at the mod folder). `Modding-Docs/Lua scripting.md` is an empty page; the Patch
  Manager Lua pages cover patching, not flight.
- **Overlap with R6**: partial (scheduling, waiting, pause and warp helpers). The roadmap's "Rejected: Redux's Lua
  surface" stays coherent if the user keeps it; the user's own rule asks for the comparison to be shown to him, hence
  the decision in section 8. A fact for that decision: `MoonSharp.Interpreter.dll` is already loaded in the game, so
  an interpreter for our own language could reuse it instead of writing one [inferred, to judge in R6's spec].
- **What runs on an unloaded vessel** [docs]: `PartComponentModule` update loops ("executed even on unloaded
  vessels", `General overview of custom modules.md`); `PartBehaviourModule` exists only while loaded. Caveat from
  `KSP1PartMod` (26w42c → 26w46a): a part module type must be registered in the ECS `TypeManager` (26w42c) and from
  26w46a `PartComponentModule` is no longer a component data type [parent `CLAUDE.md`]; check again for 26w47a
  before writing a module.
- **Per-part saved data**: a `ModuleData` subclass with `[KSPState]` fields is saved in the save; `[KSPDefinition]` in
  the part JSON [docs]. A script and its state fit in a `[KSPState]` string field (size limits unknown).
- **A new part (the computers)**: already researched in `docs/redux-part-modeling-with-blender.md` (Blender for the
  asset, Unity mandatory to assemble, SDK "Probe core" archetype with `Module_Command`, `Module_ReactionWheel`,
  `Module_ResourceCapacities`; sizes as separate parts rather than V-SwiFT variants; no SAS module). Tech-tree
  placement is a `techNodeData` asset in the mod's `<Mod>_tech_nodes` Addressables group, behind the feature
  `Ksp2Redux:Nodes` for Redux's own tree [docs `Campaign features.md`, `Mod Project Structure.md`]. The detection by
  our pilot: `PartOwner.GetPartModules<OurPartComponentModule>()`, the way `VesselComponent` finds
  `PartComponentModule_Command` [IL].

## 7. R4b: serializing, spawning, deleting, driving a vessel

- **Serialize a live vessel**: `SerializationUtility.VesselToSerializable(SimulationObjectModel, isAutosave)` →
  `SerializedAssembly` (public) [IL]. Redux uses it for the terminal stage.
- **Create one from it**: `SpaceSimulation.CreateVesselSimObject(SerializedAssembly, owningPlayerGuid, ownerPlayerId,
  authorityPlayerId)`; also `ReplaceVesselSimObject(...)` [IL, public]. `SerializedAssembly.Guid` is the vessel GUID.
  Two vessels cannot share a GUID, which matters for R4b: **Redux's merge gives the landed stage the same GUID as the
  in-flight copy and deletes the copy**.
- **Delete one**: `SimulationObjectModel.Destroy()` (the game itself calls it for the collision case) [IL]; recovery
  goes through `VesselRecoveredMessage`.
- **Redux's merge step for R4b** (section 1): it runs inside the mission checkpoint file before the load. To keep
  the in-flight copy and make the landed vessel appear at touchdown time, the merge would have to be changed
  (Harmony on the internal static `StageRecoveryReconciler.TryReconcile`, "No Redux bug reports, patches in our mod"
  [rule]), the terminal stage kept in our own record, and spawned later with `CreateVesselSimObject`, under a GUID
  that does not clash. **[unknown]**: that `SerializedAssembly.Guid` can be rewritten safely; that a landed vessel
  appears correctly when spawned mid-flight.
- **Driving the real in-flight copy without physics (R4b option (a))**: the game has building blocks, none tested
  for this use [IL]: `PhysicsMode.AtRest` with `AtRestRigidBodyDriver` (`UpdatePosition`, `UpdateRotation`), already
  used to set a vessel down on a parachute (`SpaceSimulation.SetVesselDownUnderParachute` updates the position of an
  `AtRest` vessel that is not landed), and `TeleportToSurface` on the Lua side. Concerns: `FloatingOrigin` defers
  `AtRest` vessels without ground under them; the vessel must not go `Orbital` (destroyed in the atmosphere, see
  section 1); engines, plumes, legs and fins would still be driven by the module states. **[unknown]** whether it
  looks right. The fallback (b), a ghost built from the models, has no API read; plumes are volumetric and tied to the
  engine modules, so a clone is likely hard [inferred].
- **Render and physics range for the copy**: 2 km RigidBody, 100 km view load (section 1).

## 8. Decisions taken (2026-11-20)

The two questions this note asked were answered by the user on 2026-11-20:

1. **R3 / R4 / R4c are built on Redux's stage recovery capture, and R4b keeps our replay.** The merge step is
   **patched, not endured**: Harmony on the internal static `StageRecoveryReconciler.TryReconcile` so that the
   in-flight copy stays, the terminal stage goes to our own booster-landed record, and the landed vessel appears at its
   touchdown time (swap of the replayed copy). The cost listed in section 7 is accepted. The revised step-by-step flow
   is in the roadmap ("Phase 0 result and decisions").
2. **R6 stays our own language, without MoonSharp** and without Redux's Lua. The interpreter is written by us; the
   remark in section 6 about reusing the shipped `MoonSharp.Interpreter.dll` is dropped.

Open design points that these decisions leave (to settle in the R4b spec, after the in-game checks of section 9):
the GUID clash between the replayed copy and the landed vessel, the exact patch point (prefix replacing the merge, or
postfix undoing it), and what the patch does when our recorder has no recording for the stage (fall back to Redux's
own merge).

## 9. Left to confirm in game

- Pausing right after the recovery record exists: does the snapshot get made, and does pausing before it block it?
- `StartReplay` from our code: does it behave like the window's "Fly" (and is the plugin still alive after both
  `ResetUniverse` loads, so a Lift resume can find its state)?
- Time of the two loads (each is a full save and a full universe load), and whether the game is paused when the
  mission is back.
- Redux flight-view placement without the map; whether our `ReduxPlacement` can drop its map switch.
- Switching vessel while paused (`SetActiveVehicle` with the asynchronous local-space load).
- A second camera to a `RenderTexture` in URP; the cost; hide-UI effect on UI Toolkit windows; plume and cloud quality
  in a continuous capture; whether the Wwise output capture works.
- Kinematic driving of a real vessel (`AtRest` + `UpdatePosition`), and the landing barge's collider.
- Body lift of a booster, and how SAS drives grid-fin-like control surfaces at speed.
- That `Data_Command` of a probe reports `FullyFunctional` with its EC and no crew on a stage without solar panels
  (battery life during the return).

## 10. Items to add to the roadmap

- R3 / R4: a **delay check** (recovery save delay, pause only after the record exists) and a **settings check** (point
  creation on, delay shown, `AllowQuickLoad` consequences: with quickload off a failed attempt is final).
- R4c: replace "the player loads Redux's separation save" by Redux's own return path, and add the **resume after
  `ResetUniverse`** as a first-class piece (store the Lift state, re-attach to the vessel by GUID, barrier against
  resuming on the landed booster using the record's `VesselGuid`).
- R1b: Redux makes **any** pod or probe an eligible stage. If only our computer may qualify, that is a Harmony patch on
  the internal `StageRecoveryCandidateSelector`, and it changes a Redux behaviour players may want; decide.
- R2: upstream boost-back code is already in the repo (`Pilots/Landing/BoostBack/`, inactive here): read it before
  designing the flip, boostback and entry.
- R5: recording across reloads (frames at or after the reloaded UT are dropped) is not an edge case, it happens at
  every return from a stage session. And the record of the booster's telemetry for R7 must live in files, not in
  the save.
- R5: part cameras are shot overrides of one flight camera, so "one video per camera" means sequential recordings
  unless a second render works in game.

## Sources

- Decompilation of `G:\SteamLibrary\steamapps\common\Kerbal Space Program 2\KSP2_x64_Data\Managed\Ksp2Redux.dll`
  (SHA-256 `1f0e9d99…`, 26w47a, build 0.2.9.0.109473) made on 2026-11-20 with `ilspycmd` and the game's DLLs as
  references; the same file hash as the one of the earlier compat check
  (`2026-11-20-redux-26w47a-compat.md`). Key types: `Redux.StageRecovery.*`, `KSP.Sim.impl.VesselComponent`,
  `SpaceSimulation`, `FloatingOrigin`, `UniverseView`, `ViewController`, `AtRestRigidBodyDriver`,
  `Redux.UI.Waypoints.PlayerWaypointManager`, `Redux.Modules.Module_Camera` / `Data_Camera` / `PartCameraController`,
  `Redux.UI.Screenshots.ScreenshotCapture`, `Redux.Rendering.HdrOutput`, `Redux.Lua.*`, `KSP.Modules.Data_Command` /
  `Data_Engine` / `Data_ControlSurface` / `Data_Drag`, `KSP.Messages.*`.
- `Ksp2Redux.xml` of the same install (stage recovery enums and record fields).
- Redux release notes through `gh api repos/KSP2Redux/Redux/releases` (builds 0.2.8.0.102311 to 0.2.9.0.109473).
- `Modding-Docs/` (searched for recovery, replay, recording, camera, Lua: nothing on stage recovery or recording;
  `General overview of custom modules.md`, `Campaign features.md`, `Lua scripting.md` empty).
- The mod: `ReduxStageRecovery.cs`, `ReduxImpactPrediction.cs`, `ReduxWaypoints.cs`, `ReduxPlacement.cs`,
  `Pilots/Landing/BoostBack/`; `docs/redux-part-modeling-with-blender.md`.
