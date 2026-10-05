# Landing tab audit (UI, settings, feature completeness)

Date: 2026-10-05. Scope: the Landing tab of SkySpaceAgency (remix of K2-D2) as of the repo state at audit time.
Method: static code reading only. Nothing was run, no file was modified, no game session was observed.

Evidence convention: `file:line` paths are relative to `Assets/SkySpaceAgency/Code/Pilots/Landing/` unless a full path is given. `uxml` = `Assets/UI/K2D2_UI/Landing.uxml`.
Every finding is tagged **[code]** (directly established by reading the code, including grep for absence) or **[inferred]** (follows from the code but depends on game behavior that was not observed and needs an in-game test).

Files read in full: `LandingUI.cs`, `LandingSettings.cs`, `LandingProfile.cs`, `LandingPilot.cs`, `Controlers/TouchDown.cs`, `Landing.uxml`, `ReduxWaypoints.cs`, `KTools/SettingsFile.cs`, `KTools/Settings.cs`, `Nodes/Controlers/WarpTo.cs`, `StagingPilot.cs`. Read in part: `Controlers/Circularize.cs`, `DeorbitBurn.cs`, `MidCourseCorrection.cs`, `LandingTargeting.cs`, `AtmosphericPredictor.cs`, `KSPService/BurndV.cs`, `KSPService/KSPVessel.cs`, `K2D2_Plugin.cs`.

---

## 0. Architecture in one paragraph

`LandingPilot` (`LandingPilot.cs:13`) owns two `LandingSettings` objects, one per body type, each backed by its own JSON file: `settings_atmo` and `settings_vac` (`LandingPilot.cs:22-24, 59-63`). `LandingProfile.IsAtmospheric` (`LandingProfile.cs:19`, refreshed from `LandingPilot.Update` at `LandingPilot.cs:554`) picks which one the `settings` property returns. `TouchDown` (the Brake and TouchDown executor) holds a parallel pair of `TouchDownSettings` in the same two files (`TouchDown.cs:31-82`). The UI is a single UXML page with two sibling panels (atmo and vac), both bound once in `LandingUI.onInit` (`LandingUI.cs:118-121`). `LandingUI` shows one panel and hides the other every UI tick (`LandingUI.cs:177-184`). The state machine is `Off, Circularize, DeorbitBurn, MidCourseCorrection, Pause, QuickWarp, RotationWarp, Waiting, Brake, TouchDown` (`LandingPilot.cs:79-93`). The first three modes are precision-landing (vacuum only) preludes.

Persistence: flat `Dictionary<string,string>` JSON written next to the mod DLL (`K2D2_Plugin.cs:36-59`, so `skyspaceagency_landing_atmo.json` and `skyspaceagency_landing_vac.json` in the assembly folder). Writes are flushed at most once per second by a coroutine (`SettingsFile.cs:138-148, 150-167`). A one-time migration copies old `land.*` keys from the main settings file into the vac file only (`K2D2_Plugin.cs:115-126`). The atmo file starts from defaults.

Stale comments: `LandingSettings.cs:8` and `LandingPilot.cs:18-19` still say `k2d2_landing_*.json`; the real names are `skyspaceagency_landing_*.json`.

---

## 1. Inventory

### 1.1 Visible controls and readouts (page order, `uxml`)

| # | Element | uxml | Behavior | Default / units / range | Notes |
|---|---|---|---|---|---|
| 1 | "Collision" row, `collision_value` | 17-20 | Read-only: "Detected" or "None detected" from `pilot.collision_detected` (`LandingUI.cs:249-252`) | n/a | Means "an unpowered, drag-free trajectory crosses terrain within about 6000 s". Never says "impact predicted", and flips to "None detected" mid-burn (see 4.2). |
| 2 | Atmo note `atmo_precision_note` | 33 | Static text on atmospheric bodies: "Precision landing isn't available on atmospheric bodies yet - Brake/Touch Down still work as usual." | n/a | The only place the atmospheric WIP limit is explained. |
| 3 | `Precision Landing` toggle (vac only) | 46 | Bound to `land.precision_landing` (`LandingSettings.cs:127`) | false (`:111`) | Switches the run from "brake where I fall" to Circularize, DeorbitBurn, MidCourseCorrection, then the normal sequence (`LandingPilot.cs:221-224`). Persisted. |
| 4 | Orbit gate label `precision_landing_orbit_gate_vac` | 55 | Red text when Ap or Pe above 100 km (`Circularize.max_starting_altitude_m`, `Circularize.cs`); also force-sets Precision Landing to false while not running (`LandingUI.cs:260-297`) | 100 km hard-coded | Not tied to the toggle's visibility. |
| 5 | `Waypoint :` dropdown (vac, shown only if Precision on) | 57 | Lists Redux player waypoints for the current body via reflection (`ReduxWaypoints.cs`); selecting writes target lat/lon (`LandingUI.cs:72-79`); rebuilt every tick (`:342`) | n/a | See 4.4 for auto-select problems. |
| 6 | `Target Latitude` FloatField | 58 | Bound to `land.target_latitude` (`LandingSettings.cs:140`) | 0, no clamp, no unit shown (haversine treats it as degrees, `LandingPilot.cs:535-544`) | |
| 7 | `Target Longitude` FloatField | 59 | Bound to `land.target_longitude` | 0, no clamp, no unit, no convention (E positive or 0..360 not stated) | |
| 8 | `Brake` ToggleButton (label becomes `Stop`) | 69 | Starts or stops the whole landing sequence (`LandingUI.cs:86-90`, `LandingPilot.cs:185-230`) | n/a | Misnamed: it starts the entire autopilot (warp, wait, brake, touchdown), not only the "Brake" mode. |
| 9 | `Touch Down` button | 70 | `isRunning = true; setMode(TouchDown)` (`LandingUI.cs:92-96`). Hidden while already in TouchDown mode (`:359`) | n/a | Skips straight to the final-descent speed profile from wherever you are. |
| 10 | K2 avatar, status line, console | 78-84 | Mode text via `status_bar.Status/Warning` (`LandingUI.cs:362-384`); executor `status_line` as console (`:386-387`); idle text "Landing autopilot not enabled" (`:393`) | n/a | Wiped and rebuilt every tick. |
| 11 | `progress` K2ProgressBar | 85 | Declared in UXML but hidden by `status_bar.Reset()` every tick (`FullStatus.cs:36`) and never re-shown by Landing | n/a | Dead UI element. [code] |
| 12 | `LANDING INFO` foldout (closed by default) | 99-101 | Table rebuilt by `updateContext` (`LandingUI.cs:299-333`) | n/a | See 1.2. |
| 13 | `ADVANCED` foldout (closed by default) | 117-196 | All numeric settings; Reset button appended in code (`LandingUI.cs:154-171`) | n/a | See 1.3. |

### 1.2 LANDING INFO rows (all inside the collapsed foldout)

| Row | Condition | Source | Unit |
|---|---|---|---|
| Fall Speed | always | `-SurfaceVelocity.y` (`LandingPilot.cs:558-560`) | m/s |
| Altitude | always | `GetApproxAltitude` = ground height minus bounding-sphere radius (`KSPVessel.cs:562-570`) | `DistanceToString` |
| Collision In | collision predicted | `adjusted_collision_UT - now` | duration |
| Collision Speed | collision predicted | orbital speed at predicted impact | m/s |
| Start Burn In | collision predicted | `startBurn_UT - now` | duration |
| Burn Duration | collision predicted | `speed_collision / full_dv` (`LandingPilot.cs:280`) | s |
| Predicted Landing | collision predicted | lat, lon, two decimals | no unit shown |
| Target Error | collision predicted and Precision on | haversine to target | `DistanceToString` |
| Max Speed, Delta Speed | running and executor is TouchDown | `TouchDown.UpdateInfoRows` (`TouchDown.cs:921-924`) | m/s |
| Burned | running and burned dV above 0 | `BurndV` | m/s |
| Debug-only extras | `K2D2Settings.debug_mode` | gravity, throttle, heading/arc correction, RCS state (`TouchDown.cs:926-960`) | various |

Not present anywhere on the tab: terrain height under the vessel, vertical speed separate from fall speed, TWR, available Δv, required Δv, fuel remaining, predicted touchdown speed, distance to target while not predicting a collision.

### 1.3 Settings (ADVANCED foldout). Real defaults/ranges come from the C# settings, because `K2Slider.Bind(ClampSetting)` overwrites Min, Max and value from the setting (`K2UI/K2Slider.cs:351-358`).

Vacuum panel (`uxml:143-195`) shows everything. Atmo panel (`uxml:120-140`) shows only the rows marked "A".

| Section | Control (label) | Key | Default | Real range | Unit | Atmo | Used by |
|---|---|---|---|---|---|---|---|
| PRECISION (visible only with Precision on) | Max Plane Trim (m/s) | `land.max_plane_trim_dv` | 20 | 0-200 | m/s | no | DeorbitBurn, MidCourseCorrection |
| | Max Steering Angle ° | `land.steering_max_angle` | 40 | 0-80 | deg | no | `TouchDown.cs:310, 584` |
| | Max Extend Angle ° | `land.arc_extend_max_angle` | 80 | 0-90 | deg | no | `TouchDown.cs:495` |
| | Max Shorten Angle ° | `land.arc_shorten_max_angle` | 10 | 0-45 | deg | no | `TouchDown.cs:496` |
| | Use RCS for Fine Correction | `land.use_rcs_fine_correction` | false | bool | n/a | no | `TouchDown.cs:310-323, 783-794` |
| | RCS Threshold (m) | `land.rcs_fine_correction_threshold_m` | 1000 | 100-5000 | m | no | same |
| | RCS Power | `land.rcs_fine_correction_power` | 0.5 | 0.05-1 | fraction | no | `TouchDown.cs:823` |
| WARP | Auto Warp | `land.auto_warp` | true | bool | n/a | A | `LandingPilot.cs:138, 149` |
| | Rot. Warp Duration | `land.rotation_warp_duration` | 60 | no clamp (IntegerField) | s (unlabeled) | A | `LandingPilot.cs:376` |
| | Safe Warp Rotation ° | `land.max_rotation` | 10 | 5-30 | deg | A | **nothing (dead)**, see 1.4 |
| BRAKE | Burn Before (s) | `land.burnBefore` | 0 | 0-10 | s | A | `LandingPilot.cs:287` |
| TOUCH DOWN | Start TouchDown Altitude | `land.touch_down_altitude` | 1500 | 500-5000 | m (label rewritten with `DistanceToString`, `LandingSettings.cs:155-158`) | A | `LandingPilot.cs:583, 646, 656`; `TouchDown.cs:447` |
| | Max Angle | `land.touch_down_max_angle` | 30 | **0**-45 | deg (no unit in label) | A | `TouchDown.cs:364` |
| | Altitude/speed ratio | `land.touch_down_ratio` | 0.5 | 0.5-3 | 1/s x10 (see 4.1) | A | `LandingSettings.cs:201-206` |
| | Touch-Down speed (m/s) | `land.touch_down_speed` | 2.5 | **0**-10 | m/s | A | same |
| (bottom) | Reset | n/a | n/a | n/a | n/a | A | `LandingUI.cs:154-171` |

UXML defaults and ranges are ignored for sliders (UXML says for example Start TouchDown Altitude 0.001-0.1 default 0.08, Safe Warp Rotation 0-90 default 30, Touch-Down speed default 0.2). They are harmless at runtime but misleading to anyone editing the UXML. [code]

### 1.4 Dead, hidden, hard-coded

**Declared and bound but never read (dead):**
- `land.max_rotation` ("Safe Warp Rotation °"): `grep max_rotation` hits only the declaration, the constructor and the two `Bind` calls (`LandingSettings.cs:27, 105, 149, 188`). `RotationWarp` calls `warp_to.Start_Retrograde(startBurn_UT, true)` with the default `max_angle = 30` (`LandingPilot.cs:154`; default at `Nodes/Controlers/WarpTo.cs:78`). The slider has no effect. [code]
- The `progress` bar (1.1 #11).

**Persisted settings with no UI at all:**
- `land.max_throttle_rate_per_sec` (default 5, range 1-20) in both profiles (`TouchDown.cs:58, 66`). It is read (`TouchDown.cs:911`) but never exposed.
- On the Atmo profile: `precision_landing`, `target_latitude`, `target_longitude`, `max_plane_trim_dv`, the RCS trio, and the steering/extend/shorten caps exist and persist (`LandingSettings.cs:111-119`, `TouchDown.cs:62-66`) but have no control. Reset and the file-based settings treat them as real.

**Hard-coded values a player cannot change** (candidates for exposure or at least documentation):
- Braking hand-over speed `brake_speed = 50` m/s and `pause_time = 1` s (`LandingSettings.cs:29-37`).
- Search start 2 minutes ahead (`LandingPilot.cs:382`), 300 iterations at 20 s (`:407-408`); this caps the prediction horizon at about 6000 s.
- Landing-complete rule: altitude below 5 m and fall speed below 1 m/s (`LandingPilot.cs:597`).
- Warp caps: index 6 in QuickWarp, 2 in RotationWarp (`LandingPilot.cs:144, 155`); warp is forced to index 2 in atmosphere or below 3000 m (`WarpTo.cs:151-157`); the safe-duration margin is a Node-tab setting (`WarpTo.cs:28`), not shown on Landing.
- Precision constants: `min_correction_altitude_margin = 8000` m (`LandingSettings.cs:69`), `arc_correction_full_scale_m = 50`, `max_correction_rate_deg_per_sec = 8`, extend margin 1.5 m/s (`TouchDown.cs:122, 127, 521`), `periapsis_safety_margin = 2000` m (`DeorbitBurn.cs`, `MidCourseCorrection.cs`), circular tolerance 1500 m, start-orbit ceiling 100 km (`Circularize.cs`).
- Auto-staging is a global title-bar toggle (`K2D2Window.cs:225`, default off, `StagingPilot.cs:14`); the Landing tab never mentions it.

**Player-facing but only half-wired:** the waypoint list requires reflection into Redux internals (`ReduxWaypoints.cs`); if the lookup fails the dropdown just says "No waypoints on <body>" with no hint that the integration itself is unavailable (`LandingUI.cs:193-204`; `ReduxWaypoints.available` is used only to skip the loop).

**Whole unwired subsystem:** `AtmosphericPredictor.cs` (488 lines) and `VesselAeroLookup.cs` (853 lines) are referenced by no other file (grep over `Code/`); `AtmosphericPredictor.cs:24` says "NOT WIRED INTO THE AUTOPILOT YET". Nothing in the UI hints at them, which matches the CHANGELOG wording. [code]

---

## 2. Landing legs, gear, lights, chutes, airbrakes

**Finding: none of these exist. The mod has no code path that deploys, retracts or checks landing gear, legs, lights, parachutes, airbrakes, brakes, or any action group.** [code]

Evidence: grep over the whole `Code/` folder (case-insensitive) for `gear|landinggear|ActionGroup|legs|airbrake|parachute|chute|lights|Brakes|Deploy|Retract|ToggleGroup|SetActionGroup` returns only: UI-only matches (`K2Avatar.cs` "lights" are the avatar's decorative LEDs; `K2Page.cs:169` "settings-gear page"), a comment in `AtmosphericPredictor.cs` ("no chutes yet", "once chutes are modeled"), and a comment in `LandingSettings.cs:79` about RCS. The only vessel-level actions the code issues are throttle (`KSPVessel.SetThrottle`), SAS mode and target orientation, RCS translation inputs X/Y/Z, time-warp rate, and, in `StagingPilot.cs:215`, `ActivateNextStage()`.

Consequences:
- **Trigger / configurability**: not applicable, there is nothing to trigger and nothing for the player to configure.
- **Vessel without gear**: the autopilot behaves identically (it never asks). No warning that the lander has no legs, and no check that the touchdown speed setting (default 2.5 m/s, `LandingSettings.cs:109`) is survivable for the vessel.
- **Player flow today**: the player must deploy legs manually before the final phase. Nothing reminds them. The tab has no "deploy at altitude X" setting or "gear down" status.
- **Chutes / airbrakes**: the atmospheric path is propulsive only. In atmosphere the pilot uses the same drag-free impact prediction as in vacuum (see 5.4), so a chute opening would break its timing, and nothing deploys one.
- **Lights**: no behavior; the only "light" is the K2 avatar animation (`LandingUI.cs:85`).
- **After landing**: when `altitude < 5 && fall speed < 1` the pilot just sets `isRunning = false` (`LandingPilot.cs:597-602`), which zeroes throttle. It does not disable SAS, turn off RCS inputs, retract anything or announce "landed".
- **Staging**: auto-staging is a separate global toggle, off by default (`StagingPilot.cs:14`). A landing burn that runs out of the active stage's fuel is not detected or reported by the Landing tab.

---

## 3. Feature gap analysis (what a player expects on a landing tab)

Legend: Yes = present and visible; Partial = present but hidden, indirect or limited; No = absent. "Value" = my ranking of player value (1 = highest).

| Rank | Feature | Status | Evidence / comment |
|---|---|---|---|
| 1 | Pre-flight feasibility check (engines present, TWR above 1 on this body, Δv enough for the descent) with a clear refusal/warning | **No** | `BurndV.full_dv` is read raw (`LandingPilot.cs:280`, `TouchDown.cs:254-261`); zero engines gives `full_dv = 0`, hence `burn_duration = speed/0` and an unguarded division in `compute_Throttle`. No comparison of required stopping Δv vs `VesselDeltaV`. [code] |
| 2 | Abort / emergency behavior (what if it cannot stop?) | **No** (only the Stop toggle) | No "cannot stop" detection and no hold/hover/bailout; user can only press Stop. |
| 3 | Deploy gear/legs (and lights, chutes, airbrakes) | **No** | Section 2. |
| 4 | Suicide-burn countdown and predicted touchdown readout | **Partial** | "Waiting : <time>" appears in the status line (`LandingUI.cs:376`); "Start Burn In", "Collision In", "Collision Speed", "Burn Duration" exist but only in the collapsed LANDING INFO table. No predicted touchdown speed. |
| 5 | Safe-margin setting for burn start | **Partial** | "Burn Before (s)" (0-10 s, `LandingSettings.cs:103`) is the only margin; no altitude margin or percentage (the 8000 m margin is precision-only and fixed). |
| 6 | Max touchdown speed / final contact speed | **Yes** | "Touch-Down speed (m/s)" (`LandingSettings.cs:109`), but range allows 0, and the effective limit is `alt*ratio/10 + speed` (see 4.1). |
| 7 | Target selection UI: waypoint list, manual coordinates | **Partial** | Dropdown plus lat/lon fields (vac only). No map click (explicitly deferred, `ReduxWaypoints.cs:13-19` comment), no "current position", no "target = marker/vessel/KSC", no body name next to the coordinates. |
| 8 | "Land here" vs "land at target" | **Yes** (implicit) | Precision toggle off = land where the present course goes; on = target (`uxml:36-44` comment, `LandingPilot.cs:221-224`). Naming is not obvious (4.1). |
| 9 | Terrain-height / true-altitude readout | **Partial** | "Altitude" is ground-relative minus the vessel bounding-sphere radius (`KSPVessel.cs:562-570`) but is not labeled as such and sits in the collapsed foldout. No slope under the vessel. |
| 10 | Slope / safe-site check | **No** | Nothing evaluates terrain slope at the target or predicted point. `touch_down_max_angle` is the vessel's attitude tolerance, not terrain slope (misleading label, 4.1). |
| 11 | Hover / slow-descent / hold-altitude mode | **No** | The only terminal behavior is the speed profile to the ground; no hover and no horizontal-velocity nulling (no "cancel horizontal drift" outside precision RCS). |
| 12 | Warp control | **Partial** | Auto Warp toggle plus rot-warp duration; maximum warp indices hard-coded; the safe-duration margin lives on the Node tab. |
| 13 | Auto-stage during landing | **Partial** | Global staging toggle in the title bar (`K2D2Window.cs:225`), off by default, not surfaced here. |
| 14 | Fuel / staging warnings | **No** | `StagingPilot.CalculateVesselStageFuel` computes percentages but nothing on the Landing tab shows them. |
| 15 | Retro-burn throttle limit | **Partial / hidden** | `max_throttle_rate_per_sec` is a rate limiter, not a cap, and has no control (`TouchDown.cs:58`). No "max throttle %" setting. |
| 16 | Landing complete confirmation | **No** | Silent stop (`LandingPilot.cs:597-602`). |
| 17 | Precision landing in atmosphere | **No** (WIP, explained) | Note at `uxml:33`; predictor exists unwired. |

Top missing features, ranked by player value: feasibility and "cannot stop" warnings (1, 2), gear and chute deployment (3), a visible burn countdown with predicted impact speed on the main page (4), an explicit confirmation or hand-off at touchdown (16), clearer target selection with coordinate validation (7), a slope or terrain readout (9, 10), hover or hold mode (11).

---

## 4. UX issues

### 4.1 Labels, units, naming
1. **"Brake" is the start button of the whole autopilot** (`uxml:69`, `LandingUI.cs:89`); there is also an internal mode called Brake. "Touch Down" is a second start button that skips all earlier phases (`LandingUI.cs:92-96`). A newcomer cannot tell the difference. Suggest "Land" and "Final descent only".
2. **"Altitude/speed ratio" (min-max label "Safe-Danger")** (`uxml:137`): the actual formula is `speed_limit = altitude * ratio / 10 + touch_down_speed` (`LandingSettings.cs:201-206`). So 0.5 means a speed limit of 5% of altitude per second, no unit given. The tooltip says only "Speed is based on altitude", and is identical on the "Touch-Down speed" slider (`uxml:137-138`).
3. **"Max Angle"** (`uxml:136, 191`) is a vessel-attitude tolerance for firing the engine (`TouchDown.cs:364`) in both Brake and TouchDown; its tooltip says "Max Angle for Final touch Down". It could be read as maximum terrain slope. No unit.
4. **Slider range allows 0 for Max Angle (code range 0-45, `TouchDown.cs:62`)**. At 0 the test `retrograde_angle < 0` is never true, so the engine never fires and the vessel will fall to the ground with no message. UXML min was 5, overridden by the bind (`K2Slider.cs:351-358`). [code]
5. **Touch-Down speed range 0-10** (`LandingSettings.cs:109`). At 0 the final limit is `alt*ratio/10`, which falls toward 0 near the ground. Because the "landed" detection needs fall speed below 1 m/s, a 0 setting makes the vessel hover or crawl near the ground. Not harmful but likely not intended; UXML said 0.1.
6. **"Rot. Warp Duration"** has no unit, is an unclamped IntegerField (`LandingSettings.cs:104`), and is the time before burn at which the quick warp stops and the rotation warp (attitude-checked) starts. Negative or zero values make the quick warp end at or after the burn. Tooltip "During Rotation Warp, Attitude is checked" does not say it is seconds.
7. **"Safe Warp Rotation °"** does nothing (1.4).
8. **"Collision"** label: the tab's goal is landing, so "Detected" reads as bad news. It actually means "impact predicted on the current unpowered path". The first thing a player sees is a red-flag word with no explanation.
9. **Target Latitude / Longitude**: no ° unit, no body name, no hint about range or sign convention, no validation (any float accepted).
10. **Jargon**: "Max Plane Trim", "Max Extend Angle", "Max Shorten Angle" (tooltips are good but long). "Start TouchDown Altitude" is displayed as a formatted distance in the label (good) but the slider's `print-value` is false (`uxml:135`).
11. **Typo** "Landing is only available in Fligh View" (`LandingUI.cs:355`). It also shows in Map view, where `ValidScene` still allows the pilots to run (`K2D2_Plugin.cs:223`), but the mode text is skipped (`:352-357` returns before the mode switch), so a running landing shows no mode status while the map is open. [code]

### 4.2 Settings that interact badly or surprise
1. **Reset wipes the target and the Precision toggle too.** The Reset button sits at the bottom of ADVANCED (`LandingUI.cs:128`) but calls `Reset("land")` on both files (`:168-169`), and every key starts with `land.` including `land.target_latitude`, `land.target_longitude` and `land.precision_landing` (`LandingSettings.cs:111-113`), which live outside the foldout. The inactive profile is reset silently as well. No confirmation. [code]
2. **Waypoint auto-select overrides manual coordinates.** On every tick, if the dropdown's current text is not in the list (empty at startup, or the waypoint was removed) and any waypoint exists, it sets the dropdown to the first waypoint through the normal setter, which fires the handler and overwrites `target_latitude/longitude` (`LandingUI.cs:213-225`). At startup `current` is empty, so persisted manual coordinates are replaced by the first waypoint of the body as soon as the list builds with a valid body. [code]
3. **Dropdown goes stale on manual edit.** Editing lat/lon by hand does not update the dropdown, which keeps showing the old waypoint name (no reverse sync). [code]
4. **Target is global, not per body.** One lat/lon pair is stored (`land.target_latitude`). Switching to a body with no waypoints leaves the old coordinates in force (`LandingUI.cs:226-227` "leave target alone"), and nothing displays which body they were meant for. [code]
5. **Precision toggle can flip itself off.** While not running, any tick with Ap or Pe above 100 km sets Precision Landing to false (`LandingUI.cs:295-296`), including a persisted "true" restored from the file. The red label explains why only while the tab is visible and the orbit is high (`:279-280`). [code]
6. **Precision persisted as true on a body switch**: the value belongs to the vac file and applies whenever the next vacuum body is entered; nothing prompts the player to re-pick a target.
7. **Collision row flickers during a burn.** `compute_real_collision` is documented as fragile and the braking burn constantly changes the orbit it extrapolates (`LandingPilot.cs:567-575`). The always-visible row can therefore say "None detected" in the middle of a landing. [inferred]
8. **Foldout state not persisted**: ADVANCED and LANDING INFO reset to closed after a UI rebuild (`value="false"`, `uxml:99, 117`). Minor.

### 4.3 Missing feedback when something is wrong
Everything below is **[code]** unless marked.
- **No message at all for**: no engines; TWR below 1; not enough Δv; engine not ignited or wrong stage; RCS not enabled or no monoprop while "Use RCS for Fine Correction" is on (tooltip warns, `uxml:170`, but nothing checks); target on the other side of the planet; target body mismatch.
- **Precision failures are mostly invisible**: Circularize with a too-high orbit sets a status line and finishes (`Circularize.cs`, TooHigh branch); `LandingPilot.Update` then calls `nextMode()` (`:679-683`), which starts DeorbitBurn and overwrites the message within a frame. The pre-flight gate (4.2 #5) normally prevents reaching it. DeorbitBurn "Couldn't find a safe deorbit window" (`DeorbitBurn.cs`) likewise finishes and falls through to the next phase, so the sequence continues without the planned burn. [code for the flow, inferred for the visible result]
- **Landing end is silent** (`LandingPilot.cs:597-602`): no "Landed" status, no summary (Δv used is tracked in `burn_dV` but only shown while running, `LandingUI.cs:326-331`).
- **Busy-looking tab, low information**: status lines are rebuilt each tick, and the most useful numbers are in a closed foldout (1.2).
- **Atmospheric limitation**: the tab does state precision landing is unavailable on atmospheric bodies (`uxml:33`), which is good. It does not say what Brake does there (same drag-free prediction as vacuum), that parachutes and airbrakes are ignored, or that timing will be off because of drag (5.4).

### 4.4 Possible rendering/perf churn on the tab [code, impact inferred]
- `updateContext()` clears and rebuilds `landing_infos` with new Labels every UI tick even when the foldout is closed (`LandingUI.cs:301-331`).
- `buildWaypointList()` runs reflection and reassigns `waypoint_drop.choices` every tick (`:342`, `:211`); the code comments assume this does not close an open dropdown list (not verified in game).
- `compute_real_collision()` does up to 300 terrain-altitude queries per frame whenever the tab is visible or the pilot is running (`LandingPilot.cs:548, 563, 407-456`); precision mode adds `PredictUTAtAltitude` per frame (`:340-362`). No cadence throttle.

---

## 5. Robustness from the UI side

| Scenario | What the code does | Result for the player | Tag |
|---|---|---|---|
| **Started without a vessel** | `LandingPilot.Update` returns when `current_vessel == null` or `VesselVehicle == null` (`:549-550`). UI tick: `UpdateOrbitGate` guards null (`LandingUI.cs:266-270`); waypoint list uses `?.` and shows "No body" (`:190, 204`). The window itself closes outside Flight/Map3D (`K2D2_Plugin.cs:223-226, 272-276`). The Precision toggle listener calls `Circularize.CheckOrbit` without the `VesselComponent` null check (`LandingUI.cs:139`; `Circularize.cs` only checks `current_vessel == null`) so toggling Precision during a vessel-less moment could throw. | Mostly safe; one unguarded call. | [code] |
| **On the ground / landed** | Start: `isRunning = true` then, on the next `Update`, `altitude < 5 && fall speed < 1` sets `isRunning = false` (`LandingPilot.cs:597-602`). Touch Down button behaves the same. | The Brake button lights and unlights immediately. No message explaining "already landed". | [code] |
| **In a stable orbit (no periapsis below surface)** | `collision_detected = false` is displayed. Pressing Brake still starts (`LandingUI.cs:86-90`); `LandingPilot.Update` only falls back to TouchDown below `start_touchdown_altitude` (`:581-585`), so in orbit nothing stops the sequence. With stale or zero `adjusted_collision_UT`, `startBurn_UT` clamps to "now" (`:372-374`), so QuickWarp, RotationWarp and Waiting finish in a frame, then Brake sees `fall speed < 50` with altitude above the threshold and goes to Pause (`:652-663`), which restarts QuickWarp after 1 s (`:603-610`). | Probable endless Pause/QuickWarp/Rotating Warp/Waiting/Brake cycle with throttle at 0, status flicker, and two log lines per mode change; no message that there is nothing to land on. Precision mode instead starts a real Circularize/Deorbit sequence. | [inferred; code path traced, not run] |
| **Body with atmosphere** | Atmo profile and panel are selected from `body.hasAtmosphere` (`LandingProfile.cs:25`); Precision is forced off via `!IsAtmospheric` (`LandingPilot.cs:221`). Impact prediction is the same drag-free Kepler search as in vacuum (`compute_real_collision`, `AtmosphericPredictor` unwired). WarpTo clamps to index 2 below 3000 m and in atmosphere (`WarpTo.cs:151-157`). | Brake works as advertised in the note, but burn timing ignores drag, so ignition can be earlier or later than ideal; there is no chute or gear handling and no warning. Reasonable fallback is `altitude < start_touchdown_altitude` then TouchDown. | [code] for the mechanics; [inferred] for the timing error magnitude |
| **TWR below 1** | `compute_Throttle` clamps to [0,1] (`TouchDown.cs:262`); gravity-compensation floor `g*cos/full_dv` can exceed 1 and is clamped away. `full_dv` is thrust at the engines' maximum over total mass (`BurndV.cs:83-90`). | Full throttle until impact, no warning, no abort, no "cannot stop" status. The "Burn Duration" row will show a number that ignores gravity. | [code] |
| **No engines or zero thrust** | `full_dv = 0` gives division by zero in `burn_duration` (`LandingPilot.cs:280`) and in `compute_Throttle` (`TouchDown.cs:254-261`); `Mathf.Clamp` does not sanitize NaN, `SetThrottle` clamps with `Clamp01` (`KSPVessel.cs:89`), also not NaN-safe. | Undefined behavior (probably throttle stuck at 0 or NaN). No message. | [inferred] |
| **Vessel switch mid-landing** | `VesselChangedMessage` calls `ResetControllers()` (`K2D2_Plugin.cs:327-331`), which reaches `LandingPilot.onReset()` then `isRunning = false` (`LandingPilot.cs:232-235, 192-199`): throttle to 0 via `current_vessel.SetThrottle(0)`, mode Off, warp index 0. `SetThrottle` re-reads the active vehicle (`KSPVessel.cs:85`), which at message time is probably the new vessel, so the old vessel may keep its last throttle. | The UI recovers cleanly (button resets, "not enabled" text). The abandoned vessel's engine state is unverified. | [code] for the reset; [inferred] for the throttle target |
| **Stop pressed (or landing completes) while RCS fine correction is active** | `ClearRCSFineCorrection()` (zeroes `current_vessel.X/Y/Z`) is called only from `TouchDown.checkDirection` and `TouchDown.Start()`, and `TouchDown.Start()` is never called (comment at `TouchDown.cs:206`). `LandingPilot.isRunning = false` does not zero them. | RCS translation inputs may be left at the last commanded value, firing after the pilot stops, depending on whether the game resets them. Also SAS is left in its last pointed mode (`TouchDown.cs:340-343`). | [code] for the missing clear; [inferred] for the consequence |
| **Time warp during TouchDown** | `TimeWarpTools.SetRateIndex(0)` every frame in TouchDown (`LandingPilot.cs:671`) and in `Update`/`checkDirection` of TouchDown. | Player cannot warp during the final descent (expected), but there is no message. | [code] |
| **Hyperbolic orbit in the orbit gate** | `OrbitalElementsFromStateVectors` gives a negative semi-major axis (`LandingTargeting.cs:75-82`), so apoapsis radius is negative and the TooHigh test passes; "circularize at apoapsis" is meaningless. | Edge case, only reachable in Precision mode on an unbound trajectory. | [inferred] |
| **Persistence** | `Load` swallows parse errors and logs a warning (`SettingsFile.cs:127-130`); a corrupt file silently falls back to defaults and is then rewritten. Files live next to the DLL (`K2D2_Plugin.cs:36-59`), so replacing the mod folder on update may wipe tuned values. Out-of-range values in a hand-edited file are clamped on construction (`Settings.cs`, `ClampSetting` ctor), but unclamped `Setting<float>` (lat/lon) and `Setting<int>` (rot warp) accept anything. | Acceptable; mention in docs. | [code] |

---

## 6. Recommendations (ordered)

1. Add a pre-start validator with a visible, persistent message: no engines or `full_dv <= 0`, TWR below 1 for this body, Δv needed greater than Δv available, already landed, no impact predicted (stable orbit). Refuse to start (or require confirmation) instead of silently cycling or stopping. Surface the reason in the status line.
2. Add an in-flight "cannot stop" detector (required deceleration vs available) with a clear warning, and make Stop clean up (SAS mode, RCS X/Y/Z, throttle on the right vessel).
3. Add optional automatic gear (and lights) deployment: a toggle plus a trigger altitude; check whether the vessel has deployable gear and say so. Put chutes/airbrakes behind a later task tied to the atmospheric predictor.
4. Move the essential readouts out of the closed foldout: time to burn, burn Δv/duration, predicted impact speed, altitude above ground, vertical vs horizontal speed. Rename "Collision" to something like "Impact predicted".
5. Remove or implement `max_rotation` ("Safe Warp Rotation °"); today it is a placebo. Pass it into `Start_Retrograde` if it is meant to be live.
6. Fix the target UX: show the body name, suffix units (degrees), clamp lat to -90..90 and lon to -180..180, stop auto-selecting a waypoint over existing manual coordinates, sync the dropdown when the coordinates are edited, and store the target per body (or warn on mismatch). Make Reset leave the target alone or add a confirmation.
7. Raise the minimum of "Max Angle" above 0 and decide a sensible floor for "Touch-Down speed"; give both honest tooltips and units. Rename "Altitude/speed ratio" or state its formula in the tooltip.
8. Add a "Landed" confirmation and a final status (burned Δv, touchdown speed), and consider a short hover or horizontal-nulling phase before the 5 m / 1 m/s cutoff.
9. Extend the atmo note: say Brake uses a drag-free prediction, parachutes/airbrakes/gear are not managed, and give the expected burn-timing caveat. Optionally cap warp and show the atmosphere state.
10. Throttle the per-frame collision search (and the info-table rebuild) to a fixed cadence while idle.
11. Housekeeping: fix the "Fligh View" typo, update the stale `k2d2_landing_*.json` comments, remove or use the hidden `progress` bar, and either wire up or park the unused `AtmosphericPredictor`/`VesselAeroLookup` with a clear note in the docs.

---

## 7. What was and was not verified

Verified by reading: control inventory, defaults and ranges, binding behavior, dead settings (grep), absence of gear/chute/action-group code (grep), state-machine flow, reset semantics, persistence paths and migration, unwired predictor.

Not verified (no game or Unity run): visible UI layout and appearance, whether the waypoint dropdown stays usable while it is rebuilt every tick, the stable-orbit loop behavior, the RCS-residual and vessel-switch throttle consequences, NaN handling with zero thrust, and actual performance cost of the per-frame collision search. These are tagged [inferred] above and should be reproduced in-game before being treated as bugs.
