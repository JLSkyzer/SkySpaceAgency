# Landing target picker (precision landing part 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the Land tab, let the player pick the landing target by clicking the body on the map, list KSC as a ready-made target on Kerbin, and show the target picker on atmospheric bodies too (precision itself stays off there until part 3).

**Architecture:** A pure list builder (`WaypointChoices`, extended) orders KSC, the picked point, the player's waypoints and the mission waypoints, and a pure `TargetPoint` validates the coordinates a click returns; both are tested in EditMode. Two thin game-facing classes reach the game: `ReduxPlacement` drives Redux's internal waypoint placement by reflection, and `KscLocation` reads the launch pad's coordinates. `LandingUI` and `Landing.uxml` wire them for both landing profiles (vacuum and atmospheric).

**Tech Stack:** Unity 6000.6.0f1, C#, UI Toolkit (UITK for KSP2), NUnit EditMode tests, KSP2 Redux 26w42c (`Ksp2Redux.dll`), reflection on internal Redux types.

**Spec:** `docs/superpowers/specs/2026-10-21-precision-landing-design.md` (part 1 only; parts 2 and 3 get their own plans after part 1 is tested in game).

## Global Constraints

- Game: KSP2 Redux 0.2.9, snapshot 26w42c; game code in `Ksp2Redux.dll` (the asmdef already references it). Loggers: `K2D2_Plugin.logger` (`SpaceWarp2.API.Logging.ILogger`).
- The mod is disabled in campaigns that do not enable it and in the main menu; UI is created only once enabled (`K2D2_Plugin.EnsureWindow`). Nothing in this plan may load Addressables or touch the game from `OnInitialized`.
- `Redux.UI.Waypoints.PlayerWaypointManager` is `internal`: reached by reflection only, from the game's own assembly (`typeof(KSP.Sim.impl.WaypointComponent).Assembly`), like `ReduxWaypoints.cs`. A missing member gives a clear message in the tab and in the log, never an exception.
- Pure code (no game type) is tested in EditMode in `Assets/Tests/Landing` (asmdef `SkySpaceAgency.Tests.Landing`). All 192 existing tests stay green.
- UI text and log lines in English. Log prefix for this feature: `[Landing] target:`.
- Coordinates shown with an invariant-culture decimal point, `0.00` format (as `WaypointChoices` already does).
- A click on the map must never add a waypoint to the player's list: compare the player waypoint count before and after each pick and log it.
- KSC is the launch pad, read from the game; a hard-coded value is not used (ruling: when the game gives no launch pad location, the KSC entry is omitted and that is logged once — a wrong fixed value would be worse than none).
- Atmospheric bodies before part 3: the precision toggle and the target picker are visible, precision stays unavailable, with the note `Precision landing on atmospheric bodies: coming with parachute targeting.` `LandingPilot`'s existing atmospheric block is unchanged.
- CC BY-SA 4.0: no GPL code; every visible change goes to `CHANGELOG.md`.
- EditMode test command (Git Bash, timeout 600000 ms):
  ```bash
  S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
  rm -f "$S/results.xml"
  "/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
  grep -c "error CS" "$S/tests.log"; grep -oE 'total="[0-9]+" passed="[0-9]+" failed="[0-9]+"' "$S/results.xml" | head -1
  ```
  A compile error shows as `error CS` lines and no `results.xml`.
- Commits: English imperative, trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, stage only your files plus their `.meta`, push to `origin` after each commit.

## Review Focus

1. A click lands on another body than the one the vessel orbits: the target does not change and the tab says `Picked point is on <body>, not <current body>: ignored.` (Task 4 — pure check `TargetPoint.Accept`, tested in Task 1).
2. Redux passes the click as (body, latitude, longitude) only by inference: a latitude outside [-90, 90] or a non-finite value is rejected and logged with the raw values; a longitude is normalized into [-180, 180) (Task 1 tests).
3. The player leaves the map, closes the window, changes vessel or scene while a pick is waiting: the pick is cancelled, the handler unsubscribed, the button text restored; a second Pick does not subscribe twice (Task 2 `ReduxPlacement` single-subscription, Task 4 cancel paths).
4. Redux renames or removes the placement members: the Pick button is disabled with the tooltip `Map picking unavailable in this game version`, the manual fields still work, one warning in the log (Task 2, Task 4).
5. The dropdown is rebuilt every UI tick: a picked point stays selected across rebuilds, and is dropped when the body changes (Task 1 tests on `WaypointChoices.Build` with a picked point on another body; Task 4 keeps the selection).

---

### Task 1: `TargetPoint` and the extended `WaypointChoices`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/TargetPoint.cs`
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/WaypointChoices.cs`
- Test: `Assets/Tests/Landing/TargetPointTests.cs`, `Assets/Tests/Landing/WaypointChoicesTests.cs` (add cases; existing cases stay)

**Interfaces:**
- Consumes: `ReduxWaypoint` (`name`, `body_name`, `latitude`, `longitude`, `from_mission`) from `ReduxWaypoints.cs`.
- Produces (namespace `K2D2.Landing`):
  - `static class TargetPoint`:
    - `static bool TryNormalize(double latitude, double longitude, out double lat, out double lon)` — false for NaN/infinity or |latitude| > 90; longitude wrapped into [-180, 180).
    - `static string Accept(string pickedBody, string currentBody)` — null when equal (ordinal, case-sensitive), else the message `Picked point is on {pickedBody}, not {currentBody}: ignored.`
  - `WaypointChoices.Build(IEnumerable<ReduxWaypoint> player, IEnumerable<ReduxWaypoint> game, string bodyName, ReduxWaypoint? ksc, ReduxWaypoint? picked)` — new overload; the existing 3-argument `Build` calls it with nulls.
  - `WaypointChoices.KscName = "KSC"`, `WaypointChoices.PickedName = "Picked point"`.

- [ ] **Step 1: Write the failing tests**

`TargetPointTests`:
- `TryNormalize(10, 20)` → true, (10, 20).
- `TryNormalize(-12.5, 190)` → true, lon = -170; `TryNormalize(0, -180)` → lon = -180; `TryNormalize(0, 180)` → lon = -180; `TryNormalize(0, 540)` → lon = -180; `TryNormalize(0, -190)` → lon = 170.
- `TryNormalize(91, 0)`, `TryNormalize(double.NaN, 0)`, `TryNormalize(0, double.PositiveInfinity)` → false.
- `Accept("Mun", "Mun")` → null; `Accept("Minmus", "Mun")` → `"Picked point is on Minmus, not Mun: ignored."`

`WaypointChoicesTests` (new cases):
- Order: with `ksc` = ("KSC", "Kerbin", -0.1, -74.6), `picked` = ("Picked point", "Kerbin", 1, 2), one player waypoint and one game waypoint on Kerbin → labels in order `KSC (-0.10, -74.60)`, `Picked point (1.00, 2.00)`, `<player> (…)`, `[Mission] <game> (…)`; KSC and picked entries have `from_mission == false`.
- A `ksc` or `picked` whose `body_name` is not `bodyName` is not listed (picked point on Mun while on Kerbin → absent).
- With `ksc` and `picked` null the result equals the 3-argument overload (one existing case run through both).

- [ ] **Step 2: Run the test command — expect `error CS` (types missing)**

- [ ] **Step 3: Implement `TargetPoint` and the `Build` overload**

Labels use the existing invariant-culture `Label` helper with an empty prefix; KSC and picked entries go through the same unique-label `Add`.

- [ ] **Step 4: Run the test command — expect 0 `error CS`, all green (192 + new)**

- [ ] **Step 5: Commit** — `Add target point checks and KSC/picked entries to the waypoint list`

### Task 2: `ReduxPlacement` (reflection on Redux's waypoint placement)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/ReduxPlacement.cs`

**Interfaces:**
- Consumes: `ReduxWaypoints.GetWaypointsForBody(string)` (for the before/after count).
- Produces (namespace `K2D2.Landing`), `static class ReduxPlacement`:
  - `static bool Available` — the manager instance and the members `BeginPlacement()`, `CancelPlacement()`, `IsPlacementActive`, events `PlacementCompleted` (`Action<string,double,double>`) and `PlacementFailed` (`Action<string>`) all resolve.
  - `static bool Waiting` — a pick started by us is in progress.
  - `static bool Begin(Action<string, double, double> onPicked, Action<string> onFailed)` — false (and logs once why) if not `Available`; subscribes our handlers exactly once (a second `Begin` while waiting cancels the first and restarts), calls `BeginPlacement()`.
  - `static void Cancel()` — calls `CancelPlacement()` if waiting, unsubscribes, `Waiting = false`. Safe to call any time.

Reference facts (reflection on `Ksp2Redux.dll`, 26w42c, 2026-10-21): `PlayerWaypointManager` is internal; `internal static PlayerWaypointManager Instance { get; }`; `internal void BeginPlacement()`; `internal void CancelPlacement()`; `internal bool IsPlacementActive { get; }`; `internal event Action<string,double,double> PlacementCompleted`; `internal event Action<bool> PlacementStateChanged`; `internal event Action<string> PlacementFailed`; `internal IReadOnlyList<PlayerWaypointRecord> Records { get; }`. The meaning of `PlacementCompleted`'s arguments (body name, latitude, longitude) is inferred from the window's `ApplyPlacement(string, double, double)` and is to be confirmed in game.

No unit test (game reflection). Behaviour:
- Look the type up as `ReduxWaypoints` does (game assembly, by full name). Subscribe with `EventInfo.GetAddMethod(true)` / `GetRemoveMethod(true)` and delegates of the exact event types.
- Our completion handler: unsubscribe first, `Waiting = false`, log `[Landing] target: placement completed raw ({a}, {b}, {c}), player waypoints {before} -> {after}`, then call `onPicked`. Failure handler: same cleanup, log, call `onFailed(message)`.
- If Redux's `PlacementStateChanged(false)` fires while we wait without a completion, treat it as cancelled: cleanup and call `onFailed("cancelled")`.
- Every reflective call is in try/catch: on exception, cleanup, log once, report through `onFailed`.

- [ ] **Step 1: Write the class**
- [ ] **Step 2: Run the test command — expect 0 `error CS`, all green**
- [ ] **Step 3: Commit** — `Drive Redux's waypoint placement by reflection to pick a landing target`

### Task 3: `KscLocation` (launch pad coordinates from the game)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/KscLocation.cs`

**Interfaces:**
- Produces: `static class KscLocation` with `static bool TryGet(out string bodyName, out double latitude, out double longitude)` — cached after the first success; false (logged once with the reason) when the game gives no launch pad location.

No unit test (reads game data). Find the launch pad position in the current API before writing: candidates seen in `API-Documentation/xml/cee1eaf9/Ksp2Redux.xml` are `KSP.FX.LaunchSystems.LaunchPadController.LaunchPadLocation` and `RegisteredLaunchLocations`, and `KSP.Game.OABProvider.LaunchLocation` (an enum). Grep that single file only (never the whole `API-Documentation`, 9.7 GB), or reflect on `Ksp2Redux.dll`. Convert the position to latitude/longitude with the body's `GetLatLonAltFromRadius` (degrees). Record in the commit message and the report which member gives the location and why it works outside the KSC scene (in flight and in the map); if no member works outside the KSC scene, return false (Global Constraints ruling) and say so.

Log once on success: `[Landing] target: KSC launch pad at ({lat:0.0000}, {lon:0.0000}) on {body} from <member>`.

- [ ] **Step 1: Find the member, write the class**
- [ ] **Step 2: Run the test command — expect 0 `error CS`, all green**
- [ ] **Step 3: Commit** — `Read the KSC launch pad coordinates for the landing target list`

### Task 4: Land tab wiring (both profiles)

**Files:**
- Modify: `Assets/UI/K2D2_UI/Landing.uxml` (groups `landing_mode_group_vac` and `landing_mode_group_atmo`)
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs`
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingSettings.cs` (bindings for the atmo copies, if the binding lives there)

**Interfaces:**
- Consumes: Task 1 `TargetPoint`, `WaypointChoices.Build(… ksc, picked)`, `KscName`, `PickedName`; Task 2 `ReduxPlacement.Available/Waiting/Begin/Cancel`; Task 3 `KscLocation.TryGet`; existing `pilot.settings_vac` / `pilot.settings_atmo` (`precision_landing`, `target_latitude`, `target_longitude`), `LandingProfile.IsAtmospheric`.

UXML:
- Vacuum group: in `target_settings_vac`, after `waypoint_drop_vac`, a `ui:Button` named `pick_target_vac`, text `Pick on map`, tooltip `Click the body in the map view to set the landing target`.
- Atmospheric group: replace the label text of `atmo_precision_note` with `Precision landing on atmospheric bodies: coming with parachute targeting.`; add, before it, a `K2UI.K2Toggle` `precision_landing_atmo` (label `Precision Landing`) and a `target_settings_atmo` element holding `waypoint_drop_atmo`, `pick_target_atmo`, `target_latitude_atmo`, `target_longitude_atmo`, mirroring the vacuum ones.

Behaviour in `LandingUI`:
- Each profile binds its own toggle, dropdown and fields to its own settings object (`settings_vac` / `settings_atmo`). The atmospheric precision toggle is shown but disabled (`SetEnabled(false)`) and forced off; its target picker stays usable (the target is saved for part 3).
- The dropdown of the visible profile is built from `WaypointChoices.Build(player, game, body, ksc, picked)`; `ksc` is passed only when `KscLocation.TryGet` succeeds and its body is the current one; `picked` is the session-only last picked point (`ReduxWaypoint` named `PickedName`, `from_mission = false`). Selecting any entry writes its latitude/longitude to that profile's settings (existing behaviour). A picked point on another body is dropped from the list on the next rebuild.
- Pick button:
  - disabled with tooltip `Map picking unavailable in this game version` when `!ReduxPlacement.Available`;
  - click while not waiting: switch to the map view if the game is in flight view (use the game's own map toggle; if none is found, show `Open the map (M), then click the body.` and still start the pick), `ReduxPlacement.Begin(...)`, button text `Click the body… (Esc to cancel)`;
  - click while waiting, or Esc pressed while waiting: `ReduxPlacement.Cancel()`, text restored;
  - on picked: `TargetPoint.TryNormalize` (reject with `Picked point is not a valid position: ignored.` + raw log), then `TargetPoint.Accept(body, current body)` (reject with its message), else write the profile's latitude/longitude, set the session picked point, select its dropdown entry, restore the text, status `Target set to ({lat}, {lon}).`;
  - on failed: restore the text; show the message unless it is `cancelled`.
- Cancel a waiting pick (`ReduxPlacement.Cancel()`) when the tab is hidden, the window closes, the landing pilot starts, the vessel changes, or the scene is no longer flight or map (hook the existing per-tick `onUpdateUI` / visibility gate and `K2D2_Plugin.ResetControllers` path; read them first).
- Status/messages go to a label in the target block (add `pick_status_vac` / `pick_status_atmo`, hidden when empty).

- [ ] **Step 1: UXML changes**
- [ ] **Step 2: `LandingUI` / `LandingSettings` wiring**
- [ ] **Step 3: Run the test command — expect 0 `error CS`, all green**
- [ ] **Step 4: Commit** — `Add the map target picker and KSC to the Land tab, show the target picker on atmospheric bodies`

### Task 5: Build, install, document (controller)

**Files:**
- Modify: `CHANGELOG.md` (`## Unreleased (SkySpaceAgency)` → `### Added`), `README.md` (autopilot list line for Landing, Known limitations), parent `D:/KSPReduxModding/CLAUDE.md` (Landing sub-project 3 bullet) and the submodule pointer.

- [ ] **Step 1: Build** with `Deploy to Zip File` (command and judging rules in the parent `CLAUDE.md`): 0 `error CS`, 0 `logLevel: 2`, zip present.
- [ ] **Step 2: Install** into `I:/SteamLibrary/steamapps/common/Kerbal Space Program 2/mods/SkySpaceAgency`, keeping `skyspaceagency_*.json` and `vab/`; `cmp` the DLL.
- [ ] **Step 3: CHANGELOG** entry: "Pick on map" button (map view, Esc to cancel, no waypoint created), KSC entry on Kerbin, picker shown on atmospheric bodies with precision still unavailable there. README Known limitations: precision on atmospheric bodies arrives with parachute targeting.
- [ ] **Step 4: CLAUDE.md** bullet: plan path, what is not verified in game (argument order of `PlacementCompleted`, no waypoint created, `PlacementStateChanged` on cancel, KSC member outside the KSC scene, map toggle), the log lines to read (`[Landing] target:`). Commit and push the submodule, then the parent pointer.
- [ ] **Step 5: In-game checklist for the user:**
  1. In orbit of the Mun, map view, Land tab: Pick on map, click the Mun → latitude/longitude filled, "Picked point" selected; Redux's Waypoints window shows no new waypoint.
  2. Esc and a second click both cancel; leaving the map cancels.
  3. Click Kerbin while orbiting the Mun → refused with the message.
  4. In orbit of Kerbin: "KSC" first in the list; the precision toggle is visible but disabled, with the note.
  5. Send back the `[Landing] target:` lines.

## Deferred after review (2026-10-21)

- Task 1: boundary tests missing (NaN lon, -inf, lat ±90/-91); no test that a from_mission=true KSC/picked is reset; no test of a player waypoint named KSC/Picked point (label uniqueness); Accept(null,null) accepts; per-tick array alloc
- Task 3: failing ticks allocate reason strings / rescan PQS; ~40 m possible rotation staleness of the cached value (check in game)
- Task 4: a real waypoint stays selected after the player types different values in the fields (stale selection, nothing written); stale comment at LandingUI.cs:525; Esc may also open the game's pause menu (check in game); cancel-reason log order cosmetic; atmo toggle forced off twice
- Final: OnPicked selects the first non-mission "Picked point" (name clash with a player waypoint); setupBasicUI forces atmo precision off before the settings file may load (covered by per-tick + LandingPilot refusal)
