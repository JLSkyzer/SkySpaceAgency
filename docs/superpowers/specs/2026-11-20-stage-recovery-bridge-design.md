# Stage recovery bridge: design (2026-11-20)

First code task of the booster / return / video / scripting roadmap
(`docs/superpowers/roadmap-booster-return-video-scripting.md`, section "First code task and in-game confirmations";
facts in `docs/superpowers/notes/2026-11-20-roadmap-phase0-scan.md`). **Nothing changes for the player.** The bridge
reads Redux's Stage Recovery, can call three of its methods behind a switch that is off, and writes `[StageBridge]`
lines that say, once the user plays, what the plan of R3/R4/R4b must know: does the plugin, its pilots and its state
survive the universe resets that Redux's stage recovery does, and how do the recovery points behave.

## Scope

In:
1. **Read** Redux's `Redux.StageRecovery.StageRecoveryManager` (internal, by reflection): `Records`,
   `IsCapturePending`, `IsTransitioning`, `IsReplayActive`, `CanRetryRecovery`, `SessionPhase`, `ActiveRecord`,
   `CreateRecoveryPoints`, `IsActiveReplayDocked`, and the game setting `PersistentProfileManager.StageRecoverySaveDelaySeconds`.
2. **Call** `StartReplay(string)`, `CompleteActiveLanding()`, `ReturnToMission()` only through a guard:
   setting `stage_bridge_enabled`, **default false**. Nothing in the mod calls them on its own. No button calls them.
3. **Listen**: persistent subscriptions to `GameLoadFinishedMessage` and `QuitToMainMenuStartedMessage`, the plugin's
   `OnCampaignUnloading` / `OnCampaignLoaded`, and a 5 Hz poll of Redux's state that logs only changes.
4. **Log** `[StageBridge]` lines: Redux's state, the changes of phase / pending capture / records, and, around every
   universe reset, a before / after comparison of the mod's own identity (plugin, pilots manager, game instance,
   universe model, active vessel GUID, counts).
5. **A diagnostic button** in SETTINGS (Logs panel): "Stage bridge: log state", next to the switch for the guard.

Out (next tasks): any Harmony patch (the merge patch is R4b), anything that triggers "Fly" by itself, the Lift resume
file, the return point, any change of pilot behaviour.

## Global rules

- Reflection targets are the names read in the IL of 26w47a (`Ksp2Redux.dll` SHA-256 `1f0e9d99…`), listed in the plan.
  A test resolves every one of them against the `Ksp2Redux.dll` the project is built with. A name that does not
  resolve at run time makes the bridge report "Redux stage recovery unavailable" once and do nothing else.
- Every reflected read is in a `try`; the bridge never throws into the game's loop and never writes to the game.
- Pure logic (states, rules, change detection, identity comparison, guarded calls) has no Unity or game types and is
  tested in EditMode. The glue is thin.
- Messages are recycled by the game right after the callback: the callbacks copy what they need at once.
- The log is silent when nothing changes (the poll logs changes only).

## The state read from Redux (`BridgeState`)

`Available`, `Phase` (`Idle, SavingMission, LoadingStage, FlyingStage, LoadingMission`, else `Unknown`),
`IsCapturePending`, `IsTransitioning`, `IsReplayActive`, `CanRetry`, `CreatePoints`, `ActiveReplayDocked`,
`ActiveRecordId`, `SaveDelaySeconds` (NaN when unreadable), `Records`. A record (`BridgeRecord`): `Id`, `VesselGuid`,
`ParentVesselGuid`, `VesselName`, `CapturedUT`, `CapturedMET` (nullable), `CompletedUT` (nullable), `Origin`, `Status`
(`Ready, Replaying, Landed, Splashed, Recovered, Lost, Failed`, else `Unknown`), `LastError`.

## Rules (`BridgeRules`, pure)

- `HasSaved(state, vesselGuid)`: Redux's capture is finished and a `Ready` record for that GUID exists. It is the
  condition for "pause now" in the roadmap flow.
- `CanStartReplay(state, recordId, enabled)` returns a `BridgeVerdict`, first match wins, in this order:
  `ReduxUnavailable`, `Disabled`, `SessionBusy` (phase not Idle), `CapturePending`, `Transitioning`,
  `NoSuchRecord`, `RecordNotReady` (status not Ready), else `Ok`. It mirrors Redux's own start conditions
  (phase Idle, no capture pending, record `Ready`) and adds the guard.
- `CanCompleteLanding(state, enabled)` and `CanReturnToMission(state, enabled)`: `ReduxUnavailable`, `Disabled`,
  `NotFlyingStage` (phase not FlyingStage), `NoActiveRecord`, `Transitioning`, else `Ok`
  (`CanCompleteLanding` also refuses `ActiveReplayDocked` with `Docked`; `CanReturnToMission` answers `WouldLose` when
  `CanRetry` is false, because Redux's `ReturnToMission` then marks the stage Lost for good).
  Added after the review of 2026-11-20.
- `BridgeCalls.TryStart / TryComplete / TryReturn(state, …, enabled, invoke)`: evaluate the rule; **only when `Ok`**
  call `invoke` (a delegate to Redux's method) and return its bool; otherwise the delegate is not called. The result
  carries the verdict, whether it was invoked, and the bool.

## Change detection (`BridgeWatch`, pure)

`Observe(BridgeState)` returns the log lines for what changed since the last observation: first read, Redux
available / unavailable, phase change (`phase Idle -> SavingMission`), capture pending on / off, transitioning on / off,
a record added (`recovery point saved: id name guid parent UT MET` when a capture was seen pending at that read or the
previous one, else `recovery point appeared (no capture seen, loaded or restored): …`), a record removed, a status
change, active record change. Records are compared only between two reads of an available manager (after the first
read or when Redux comes back they are a baseline). No change: an empty list.

## Identity before / after (`UniverseIdentity`, `UniverseComparison`, pure)

`UniverseIdentity` holds object ids (`RuntimeHelpers.GetHashCode`) of the plugin, the pilots manager, the game
instance, the universe model, the space simulation, the message center; the counts `PilotCount`, the names of the
running pilots; `ActiveVesselGuid`, `ActiveVesselName`, `ActivePartCount`, `VesselCount`, `UniverseTime`, `GameState`,
`Phase`, `Paused`, `HasWindow`, `PluginCount` (the number of plugin objects in the scene, expected 1), and a
`WeakReference` to each of the six objects: an id is a hash code, so `same` means the same hash **and** the same object
(`NEW (same hash …, but not the same object …)` otherwise).
`UniverseComparison.Compare(before, after)` returns one line per item: `same` or `NEW`/`changed` with both values; the
active vessel line says whether the GUID is the same or changed; the pilots line says which pilots were running before
and after.

## What the glue does

- `StageBridge.Init()` from the plugin's `OnInitialized`: subscribes (persistent) to the two messages, remembers which
  message center it subscribed on; `Tick()` from `Update` every 0.2 s of real time: re-subscribes if the game's
  message center changed, reads Redux's state, feeds `BridgeWatch`, logs its lines. A recovery point or a phase change
  logs the full identity line too.
- Plugin `OnCampaignUnloading`: takes the "before" identity and logs it. `OnCampaignLoaded`, `GameLoadFinishedMessage`
  (success flag copied at once) and the return of the phase to `Idle`: take an "after" identity and log the
  comparison with the last "before". `QuitToMainMenuStartedMessage` logs and keeps the before.
- `StageBridge.LogState(reason)`: the full state, the records, the identity (the diagnostic button).
- The guarded calls are `StageBridge.StartReplay(recordId)`, `CompleteActiveLanding()`, `ReturnToMission()`:
  they log `[StageBridge] call … refused (<verdict>)` or `called … -> <bool>`.

## Log lines (what the player reads)

`[StageBridge] armed: …`; `state (<reason>): …`; `record …`; `event: phase Idle -> SavingMission`;
`event: recovery point saved …`; `before reset (<where>): …`; `after (<where>): …`; `survival (<where>): plugin same`,
`pilots manager same`, `game instance same`, `universe model NEW`, `active vessel guid same|changed A -> B`,
`vessels 3 -> 3`, `pilots running [Lift] -> []`, `window same`; `call … refused|called`.

## The setting

`stage_bridge_enabled` (bool, false), in the mod's settings file; Settings > Logs: switch "Stage bridge: allow the
calls" with a note, and the button "Stage bridge: log state" (works whatever the switch says: it only reads).

## Tests

EditMode, new assembly `SkySpaceAgency.Tests.StageBridge`: phases and statuses parse; every rule and its order;
`BridgeCalls` never calls `invoke` when the verdict is not Ok; `BridgeWatch` lines for each transition and silence
without change; `UniverseComparison` for same / new / changed cases; the reflection names resolve against the real
`Ksp2Redux.dll` (the type, the properties, the methods, the record fields and their types); the setting default;
the Settings panel holds the switch and the button; the existing settings coverage test keeps passing.

## Not verifiable here (in the in-game list, section 31)

Whether Redux's stage recovery fires its messages as read; whether the plugin objects survive `ResetUniverse`;
whether the mod's `OnCampaignUnloading` / `OnCampaignLoaded` run at each reset; durations; the order of
`GameLoadFinishedMessage` and the phase returning to Idle.
