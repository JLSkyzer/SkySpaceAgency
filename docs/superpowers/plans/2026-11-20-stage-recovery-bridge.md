# Stage recovery bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read Redux 26w47a's Stage Recovery, call three of its methods behind a switch that is off, and log `[StageBridge]` lines that tell in game whether the plugin and its pilots survive the universe resets.

**Architecture:** Pure logic (state, rules, guarded calls, change detection, identity comparison) in `Code/StageBridge/` with EditMode tests; one reflection class reads Redux's internal manager; one thin glue class subscribes to the game's messages, polls at 5 Hz and writes the log. Three one-line hooks in `SkySpaceAgencyPlugin`; a switch and a button in the SETTINGS > Logs panel.

**Tech Stack:** C# (Unity 6000.6.8f1, project on the Redux 26w47a template), NUnit EditMode tests, reflection on `Ksp2Redux.dll`.

**Spec:** `docs/superpowers/specs/2026-11-20-stage-recovery-bridge-design.md`

## Global Constraints

- Nothing visible changes for the player; no Harmony patch; no code path starts a "Fly" by itself; the only callers of
  `StartReplay`, `CompleteActiveLanding`, `ReturnToMission` are the guarded `StageBridge` methods, which nothing calls.
- Setting `stage_bridge_enabled`, bool, **default false**.
- Reflection names are exactly these, read in the 26w47a IL: type `Redux.StageRecovery.StageRecoveryManager, Ksp2Redux`;
  static property `Instance`; instance properties `Records`, `IsCapturePending`, `IsTransitioning`, `IsReplayActive`,
  `CanRetryRecovery`, `SessionPhase`, `ActiveRecord`, `CreateRecoveryPoints`, `IsActiveReplayDocked`; methods
  `StartReplay(string)`, `CompleteActiveLanding()`, `ReturnToMission()` (all returning `bool`); record fields `Id`,
  `VesselGuid`, `ParentVesselGuid`, `VesselName`, `CapturedUniversalTime` (double), `CapturedMissionElapsedTime`
  (double?), `CompletedUniversalTime` (double?), `Origin`, `Status`, `LastError`; static property
  `KSP.Game.PersistentProfileManager.StageRecoverySaveDelaySeconds` (float).
- Log prefix `[StageBridge]`, through `SkySpaceAgencyPlugin.logger`; silent when nothing changes.
- Message callbacks copy what they need immediately (the game recycles messages).
- One Unity job at a time (`tasklist | grep Unity.exe`, then the command line to see whose it is); never stop another session's.
- Tests assembly `SkySpaceAgency.Tests.StageBridge` (copy of `Assets/Tests/Vab/SkySpaceAgency.Tests.Vab.asmdef`).
- CHANGELOG entries in English, discreet; docs for the user's in-game list in French.
- Commit only own files; trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; never `--force` / `--no-verify`.

## Review Focus

- Redux's `Records` is empty or the manager is `null` (main menu, no campaign): the read returns `Unavailable`, never throws.
- A record whose fields are null or whose `Status` name is new in a later Redux: parsed as `Unknown`, shown, not a crash.
- `ActiveRecord` is `null` while the phase is `FlyingStage` (a glitch): the rules answer `NoActiveRecord`.
- The game's message center is replaced between sessions: the bridge re-subscribes once, never twice on the same one.
- `OnCampaignLoaded` runs with no "before" taken (first campaign load): the comparison says "no before snapshot", does not throw.
- Game objects destroyed (Unity null) when the identity is captured: the capture marks them missing instead of throwing.

---

### Task 1: Settings key, pure types, rules and guarded calls

**Files:**
- Create: `Assets/SkySpaceAgency/Code/StageBridge/BridgeTypes.cs`, `BridgeRules.cs`, `StageBridgeSettings.cs`
- Create: `Assets/Tests/StageBridge/SkySpaceAgency.Tests.StageBridge.asmdef`, `BridgeRulesTests.cs`
- Namespace `SkySpaceAgency.Bridge` (a namespace named `StageBridge` would hide the static class `StageBridge`).

**Interfaces:**
- Produces: enums `BridgePhase {Unknown, Idle, SavingMission, LoadingStage, FlyingStage, LoadingMission}`,
  `BridgeStatus {Unknown, Ready, Replaying, Landed, Splashed, Recovered, Lost, Failed}`,
  `BridgeVerdict {Ok, ReduxUnavailable, Disabled, SessionBusy, CapturePending, Transitioning, NoSuchRecord, RecordNotReady, NotFlyingStage, NoActiveRecord, Docked}`;
  `static class BridgeNames { BridgePhase ParsePhase(string); BridgeStatus ParseStatus(string); }`;
  `sealed class BridgeRecord { string Id, VesselGuid, ParentVesselGuid, VesselName, Origin, LastError; double CapturedUT; double? CapturedMET, CompletedUT; BridgeStatus Status; }`;
  `sealed class BridgeState { bool Available, IsCapturePending, IsTransitioning, IsReplayActive, CanRetry, CreatePoints, ActiveReplayDocked; BridgePhase Phase; string ActiveRecordId; float SaveDelaySeconds; IReadOnlyList<BridgeRecord> Records; static BridgeState Unavailable { get; } }`
  (all properties settable by object initializer; `Records` never null);
  `static class BridgeRules { BridgeRecord Find(BridgeState, string id); BridgeRecord FindReady(BridgeState, string vesselGuid); bool HasSaved(BridgeState, string vesselGuid); BridgeVerdict CanStartReplay(BridgeState, string recordId, bool enabled); BridgeVerdict CanCompleteLanding(BridgeState, bool enabled); BridgeVerdict CanReturnToMission(BridgeState, bool enabled); string Explain(BridgeVerdict); }`;
  `readonly struct BridgeCallResult { BridgeVerdict Verdict; bool Invoked; bool Result; }`, `static class BridgeCalls { BridgeCallResult TryStart(BridgeState, string recordId, bool enabled, Func<string,bool> invoke); BridgeCallResult TryComplete(BridgeState, bool enabled, Func<bool> invoke); BridgeCallResult TryReturn(BridgeState, bool enabled, Func<bool> invoke); }`;
  `static class StageBridgeSettings { Setting<bool> Enabled }` (key `"stage_bridge_enabled"`, default false, lazily created like `AppSettings.CompactBar`).
- Consumes: `KTools.Setting<T>`.

- [ ] **Step 1: Write the failing tests** in `BridgeRulesTests.cs` (the asmdef first, so the project compiles it):
  `ParsePhase_KnownNames_AndUnknown` (`"FlyingStage"` → FlyingStage, `"Idle"` → Idle, `"flyingstage"` → FlyingStage, `null`/`"x"` → Unknown); `ParseStatus_...` likewise for the seven names;
  `HasSaved_RequiresReadyRecordAndNoPendingCapture` (pending true → false; record Ready for the GUID, pending false → true; record `Landed` only → false; other GUID → false);
  `CanStartReplay_VerdictOrder` (one assertion per verdict in the spec's order: `Unavailable` state → ReduxUnavailable even when disabled; `enabled=false` on a good state → Disabled; phase `FlyingStage` → SessionBusy; pending → CapturePending; `IsTransitioning` → Transitioning; unknown id → NoSuchRecord; record `Landed` → RecordNotReady; all good → Ok);
  `CanCompleteLanding_And_CanReturn_Verdicts` (phase Idle → NotFlyingStage; FlyingStage with `ActiveRecordId` null → NoActiveRecord; `ActiveReplayDocked` → Docked for complete only; transitioning → Transitioning; good → Ok);
  `BridgeCalls_NeverInvokeWhenVerdictIsNotOk` (a counter delegate stays 0 for Disabled, SessionBusy, NoActiveRecord; is 1 and `Result` equals the delegate's return for Ok; `Invoked` flag matches);
  `Explain_HasAnEnglishSentenceForEveryVerdict` (loop over `Enum.GetValues`, non-empty);
  `Setting_DefaultIsFalse_AndKeyIsStageBridgeEnabled` (`StageBridgeSettings.Enabled.V == false`, `.key == "stage_bridge_enabled"`).
- [ ] **Step 2: Run to see it fail** (`rt.sh`-style EditMode run with `-testFilter SkySpaceAgency.Tests.StageBridge`): compile errors on the missing types.
- [ ] **Step 3: Implement** the types and rules exactly per the Interfaces block. `FindReady` returns the newest (last in `Records`) record with that `VesselGuid` and `Status == Ready`. `BridgeState.Unavailable` has `Available=false`, `Phase=Unknown`, `SaveDelaySeconds=NaN`, empty records.
- [ ] **Step 4: Run the filter again**: all pass.
- [ ] **Step 5: Commit** (`git add` the new files only): "Add the stage bridge's pure state, rules and guarded calls".

### Task 2: Change detection and identity comparison (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/StageBridge/BridgeWatch.cs`, `UniverseIdentity.cs`
- Test: `Assets/Tests/StageBridge/BridgeWatchTests.cs`, `UniverseComparisonTests.cs`

**Interfaces:**
- Consumes: `BridgeState`, `BridgeRecord`, `BridgePhase` (Task 1).
- Produces: `sealed class BridgeWatch { IReadOnlyList<string> Observe(BridgeState state); }`;
  `sealed class UniverseIdentity { int Plugin, PilotsManager, Game, Universe, Simulation, Messages; int PilotCount; IReadOnlyList<string> RunningPilots; string ActiveVesselGuid, ActiveVesselName, GameState; int ActivePartCount, VesselCount, PluginCount; double UniverseTime; bool Paused, HasWindow; string Phase; }` (0 / null mean "missing"; `RunningPilots` never null);
  `static class UniverseComparison { IReadOnlyList<string> Compare(UniverseIdentity before, UniverseIdentity after); string Format(UniverseIdentity id); }`.

- [ ] **Step 1: Failing tests.** `BridgeWatchTests`: `FirstObservation_ReportsOnce` (one line starting `first read`, then an empty list on the same state); `NoChange_IsSilent`; `PhaseChange_IsReported` (line contains `phase Idle -> SavingMission`); `CapturePendingToggle_IsReported`; `NewRecord_ReportsRecoveryPointSaved` (line contains `recovery point saved`, the record id, name, guid, parent guid); `RecordStatusChange_IsReported` (`Ready -> Landed`); `RecordRemoved_IsReported`; `ReduxUnavailableThenAvailable_IsReported`; `ActiveRecordChange_IsReported`. `UniverseComparisonTests`: `AllSame_SaysSameEverywhere` (every line contains `same`, none contains `NEW` or `changed`); `NewUniverseModel_IsFlaggedNew` (line `universe model NEW`); `ActiveVesselGuid_SameAndChanged` (`active vessel guid same` / `changed A -> B`); `CountsAndRunningPilots_ArePrinted` (`vessels 3 -> 2`, `pilots running [Lift] -> []`); `MissingBefore_SaysSo` (null before → single line `no before snapshot`); `Format_ListsEveryField` (contains the vessel name and the game state).
- [ ] **Step 2: Run, see them fail** (types missing).
- [ ] **Step 3: Implement** `BridgeWatch` (keeps the last state; records are compared by `Id`) and `UniverseComparison` (wording in the tests above; one line per item: plugin, pilots manager, game instance, universe model, space simulation, message center, window, plugin objects, active vessel guid, active vessel name, vessels, active part count, pilots, game state, phase).
- [ ] **Step 4: Run the filter**: all pass.
- [ ] **Step 5: Commit**: "Add the stage bridge's change detection and universe identity comparison".

### Task 3: Reflection access to Redux's manager

**Files:**
- Create: `Assets/SkySpaceAgency/Code/StageBridge/StageRecoveryAccess.cs`
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/ReduxStageRecovery.cs` (delegate `Get()` to the new class; keep `ReduxReplay` and its fields)
- Test: `Assets/Tests/StageBridge/StageRecoveryAccessTests.cs`

**Interfaces:**
- Consumes: `BridgeState`, `BridgeRecord`, `BridgeNames` (Task 1).
- Produces: `static class StageRecoveryAccess { bool Resolved { get; }; BridgeState Read(); bool StartReplay(string id); bool CompleteActiveLanding(); bool ReturnToMission(); }` — `Read()` returns `BridgeState.Unavailable` when the type or the instance is missing or any read throws (never throws); the three calls return false when unresolved or when Redux throws.

- [ ] **Step 1: Failing tests.** `ReflectionTargets_ResolveAgainstTheRealRedux`: for the type name and every property / method / field in the Global Constraints, `GetProperty`/`GetMethod`/`GetField` is non-null with the right type (record field `Status` is an enum whose names include the seven status names; `SessionPhase` an enum whose names include the five phases; `StartReplay` takes one `string` and returns `bool`); the static property `PersistentProfileManager.StageRecoverySaveDelaySeconds` exists and is `float`. `Read_WithoutAGame_IsUnavailableAndDoesNotThrow` (`StageRecoveryAccess.Read().Available == false` in the editor).
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement.** Resolve once (cache a `bool? _resolved` like `ReduxStageRecovery`), binding flags `Public|NonPublic|Instance|Static`; `Records` is enumerated as `IEnumerable`; each record read with cached `FieldInfo`s, `Status`/`Origin` through `ToString()` then `BridgeNames.ParseStatus`; the save delay by reflection on `PersistentProfileManager` (NaN when absent). Then make `ReduxStageRecovery.Get()` build its `ReduxReplay` from `StageRecoveryAccess.Read()` (phase text, active record name and captured UT), same output as before.
- [ ] **Step 4: Run the StageBridge filter and the Landing tests**: all pass.
- [ ] **Step 5: Commit**: "Read Redux's stage recovery through one reflection class".

### Task 4: The glue (`StageBridge`) and the plugin hooks

**Files:**
- Create: `Assets/SkySpaceAgency/Code/StageBridge/StageBridge.cs`, `UniverseIdentityReader.cs`, `BridgeFormat.cs` (pure text of the state lines)
- Modify: `Assets/SkySpaceAgency/Code/SkySpaceAgencyPlugin.cs` (call `StageBridge.Init()` at the end of `OnInitialized`, `StageBridge.Tick()` in `Update` after the `loaded` check, `StageBridge.OnCampaignUnloading()` first in `OnCampaignUnloading`, `StageBridge.OnCampaignLoaded()` in `OnCampaignLoaded`; add `internal bool HasAppWindow => app_window != null;`)
- Test: `Assets/Tests/StageBridge/StageBridgeGlueTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces: `static class StageBridge { void Init(); void Tick(); void OnCampaignUnloading(); void OnCampaignLoaded(); void LogState(string reason); bool StartReplay(string recordId); bool CompleteActiveLanding(); bool ReturnToMission(); static Action<string> Sink; }` (`Sink` defaults to the plugin logger's `LogInfo`, replaced by tests); `static class UniverseIdentityReader { UniverseIdentity Capture(); }` (reads the game; every item in its own `try`; Unity-null objects count as missing).

- [ ] **Step 1: Failing tests** (editor, `Sink` captured into a list): `LogState_WithoutRedux_WritesOneStateLineSayingUnavailable`; `Tick_WithoutRedux_IsSilentAfterTheFirstLine`; `OnCampaignLoaded_WithoutBefore_LogsNoBeforeSnapshot`; `OnCampaignUnloading_ThenLoaded_LogsBeforeThenComparison` (Capture returns an empty identity in the editor: lines start with `[StageBridge] before reset` then `[StageBridge] after`, the second containing `survival`); `TheCalls_WhenTheSettingIsOff_AreRefusedAndReturnFalse` (line contains `refused (ReduxUnavailable)`: no Redux in the editor, and unavailable wins over disabled; the Disabled verdict itself is pinned in Task 1); `EveryLineStartsWithThePrefix`.
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement** per the spec's "What the glue does": persistent subscribe through `Game.Messages.PersistentSubscribe<T>` remembering the message center (re-subscribe when it differs); the poll every 0.2 s of `Time.realtimeSinceStartup`; `GameLoadFinishedMessage` callback copies `IsSuccess` then logs `event: game load finished success=…` and an "after" comparison; `QuitToMainMenuStartedMessage` logs and takes the before; the phase returning to `Idle` after a non-Idle phase logs an "after (back to Idle)" comparison; a recovery point saved or a phase change logs `Format(Capture())`. The call methods use `BridgeCalls` with `StageRecoveryAccess` delegates and `StageBridgeSettings.Enabled.V`, and log `call <name> refused (<verdict>): <Explain>` or `called <name> -> <bool>`.
- [ ] **Step 4: Run the StageBridge filter**: all pass. Compile the whole mod (Unity run in Task 6).
- [ ] **Step 5: Commit**: "Add the stage bridge glue and hook it into the plugin".

### Task 5: SETTINGS switch and diagnostic button

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/AppUI/Tabs/SettingsTab.cs` (the `Logs` panel; a public `AppButton StageBridgeButton` and `AppSwitch StageBridgeSwitch`)
- Test: `Assets/Tests/UI/Tabs/SettingsTabTests.cs` (add)

**Interfaces:**
- Consumes: `StageBridgeSettings.Enabled`, `StageBridge.LogState(string)`.

- [ ] **Step 1: Failing tests** (`SettingsTab_LogsPanel_HoldsTheStageBridgeSwitchAndButton`: the Logs panel contains a switch bound to `stage_bridge_enabled` and a button whose text is `Stage bridge: log state`; clicking the button calls `StageBridge.LogState` (observe through `StageBridge.Sink`) with the reason `button`, and works with the setting off).
- [ ] **Step 2: Run, see them fail.**
- [ ] **Step 3: Implement.** In `Logs`: `AppBind.Switch("Stage bridge: allow the calls", StageBridgeSettings.Enabled)`, a note ("Diagnostic of the booster return work. Off: nothing calls Redux's stage recovery. The button below only reads and logs."), and `new AppButton("Stage bridge: log state", AppButton.Kind.Plain, () => StageBridge.LogState("button"))`.
- [ ] **Step 4: Run the UI and StageBridge filters**, including `AppSettingsCoverageTests` (the new key now has its control): all pass.
- [ ] **Step 5: Commit**: "Settings: stage bridge switch and diagnostic button".

### Task 6: Full test run, build, documentation, review

**Files:**
- Modify: `CHANGELOG.md` (discreet entry under "Unreleased"), `docs/testing/2026-10-24-transfers-in-game-checklist.md` (section 31, French), `tasks/skyspaceagency/todo.md`, `tasks/skyspaceagency/lessons.md`, parent `CLAUDE.md` (one short bullet), roadmap "First code task" line marked done.

- [ ] **Step 1: Full EditMode run** with the project's `unity47.sh tests` (wait for any other Unity job): all pass (previous total 1524 plus the new ones).
- [ ] **Step 2: Build the zip** with the normal pipeline (`Deploy to Zip File`, executeMethod `ThunderKit.Core.Pipelines.Pipeline.BatchModeExecutePipeline`; exit code 1 is normal, judge on the log and the zip); revert the two ThunderKit-renamed assets before committing; note size and SHA-256 of the zip and of the DLL. **Do not install in the game.**
- [ ] **Step 3: Documentation**: the entries above. Section 31 lists what to read in `Player.log` and the manoeuvres: restart a campaign, press the button in flight, decouple a stage with a probe core, wait 2 s, check the `recovery point saved` line, press Fly in Redux's window, fly a second, return, and read the `survival` lines at each reset; also leave to the main menu and reload.
- [ ] **Step 4: Final review** by a fresh subagent (code, tests, spec coverage); fix what it finds.
- [ ] **Step 5: Commit** (submodule, push, then the parent pointer with `todo.md`, `lessons.md`, `CLAUDE.md`).
