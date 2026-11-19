# LIFT tab — inventory of the classic tab

Classic sources: `Assets/UI/SkySpaceAgency_UI/Lift.uxml`, `Code/Pilots/Lift/LiftUI.cs`, `LiftSettings.cs`
(`setupUI`), `LiftAscentPath.cs` (the graph), `LiftPilot.cs`, and the controllers' `updateUI` / `UpdateInfoRows`
(`Pilots/Lift/Controlers`: `Ascent`, `ReactiveAscent`, `Coasting`, `Adjust`, `Final` → `CircularizeBurn`,
`WaitLaunch` (+ `.Rendezvous`), `PlaneTrim` → `TargetOrbitBurn`, `RendezvousHandoff`). None of the controllers
queries the classic uxml through `root_el`: they only write the `FullStatus`, so they run unchanged on the
`AppPilotStatus`. New tab: `Code/AppUI/Tabs/LiftTab.cs`, `LiftProfileView.cs` (centre view), `LiftModel.cs`
(`LiftProfileModel`: planned curve, ranges, downrange; `LiftPhases`: timeline), `App/Tabs/Lift.uss`,
tests `Assets/Tests/UI/Tabs/LiftTabTests.cs`.

## Settings (`LiftSettings`, the pilot's own object, 29 keys, bound with `AppBind`)

- [x] `lift.goal` — dropdown "Goal" (Orbit / Prepare transfer / Rendezvous) → MISSION PLAN, segmented Orbit / Transfer / Rendezvous (frozen while the pilot flies, as before)
- [x] `lift.transfer_target` — dropdown "Target" (bodies, `BodyChoices`, list retried every 2 s) → drop-down "Target" under the goal (Transfer goal)
- [x] `lift.rendezvous_target` — same dropdown, vessels around the launch body (`RendezvousReadout`, refreshed every 2 s, a new game target picked once) → same drop-down (Rendezvous goal), same rules
- [x] `lift.rendezvous_dock` — toggle "Dock after the rendezvous" → switch under the target (Rendezvous goal only, frozen while flying)
- [x] `lift.destination_Ap_km` — integer field "Ap Altitude" → field "Target altitude" (km)
- [x] `lift.heading` — compass "Heading" + its value label → field "Heading" (°), hidden for Transfer / Rendezvous (the launch window gives the heading), as before
- [x] `lift.ascent_profile` — dropdown "Ascent Profile" (Guided / Gravity Turn (Experimental)), Orbit goal only → drop-down "Ascent profile", Orbit goal only
- [x] `lift.start_altitude_km` — integer field "90° Alt." → slider "Vertical climb to" (0–20 km) (Guided)
- [x] `lift.mid_rotate_ratio` — slider "45° Alt" showing the altitude → slider "45° pitch at", value in km (Guided)
- [x] `lift.end_rotate_ratio` — slider "5° Alt" showing the altitude → slider "5° pitch at", value in km (Guided)
- [x] 45° never above 5° (LiftUI's two listeners) → same two listeners in `LiftTab.BuildContent`
- [x] `lift.gravity_turn_auto` — toggle "Auto Kick & Hold" → switch "Auto kick & hold" (Gravity Turn)
- [x] `lift.gravity_turn_kick_speed_mps` → slider "Kick speed" (m/s)
- [x] `lift.gravity_turn_pitch_kick_deg` → slider "Pitch kick" (°), hidden while Auto is on (as before)
- [x] `lift.gravity_turn_hold_time_s` → slider "Hold time to apoapsis" (s), hidden while Auto is on
- [x] `lift.gravity_turn_min_throttle` → slider "Min throttle" (%)
- [x] `lift.gravity_turn_max_twr` → slider "Max TWR"
- [x] `lift.gravity_turn_max_q_kpa` → slider "Max dynamic pressure" (kPa)
- [x] Guided / Gravity Turn settings shown for the profile the run would fly (`LiftGoalRules.ProfileFor`) → same rule (`UpdateVisibility`)
- [x] `lift.roll_program` — toggle "Roll Program" (Guided only) → section ROLL PROGRAM (folded, Guided only), switch "Roll program"
- [x] `lift.roll_program_altitude_km` → slider "Roll altitude" (shown while the roll program is on)
- [x] `lift.roll_program_angle_deg` → slider "Roll angle (from launch)"
- [x] `lift.roll_program_rate_deg_s` → slider "Roll rate"
- [x] `lift.max_throttle` — slider "Max Throttle" → slider "Max throttle" (%) in MISSION PLAN
- [x] `staging.auto_staging` (not a Lift key; the mockup's switch) → switch "Auto staging"
- [x] `lift.coasting_warp` — toggle "Coasting Warp" (ADVANCED // COASTING) → switch "Coasting warp" in MISSION PLAN
- [x] `lift.auto_circularize` — toggle "Circularize Once Ascent Complete", hidden when the goal forces it → switch, same rule
- [x] `lift.pause_on_final` — toggle "Pause Leaving Atmosphere", greyed when forced or auto-circularize → switch, same rule
- [x] `lift.heading_correction` — toggle "Heading Correction" (ADVANCED // ASCENT) → ADVANCED section, switch
- [x] `lift.end_ascent_pc` — slider "Ap Alt Error (%)" → ADVANCED, slider "Ap alt error" (%)
- [x] `lift.adjust` — toggle "Fine Tuning" (shows the tuning group) → ADVANCED, switch "Fine tuning" (shows its slider)
- [x] `lift.end_adjust_pc` — slider "Final Alt Error (%)" → ADVANCED, slider "Final alt error" (%)
- [x] Tooltips of the uxml → same texts on the new controls
- [ ] `lift.ascent_lead.<body>.*` — measured leads, not a control in the classic tab either (written by the pilot)

## Buttons

- [x] "Start" toggle button (`pilot.isRunning = v`, label Start / Stop from the pilot's real state) → main gold button in the action bar, "Start" / "Stop", disabled without the pilot
- [x] "Reset" (ADVANCED foldout, `SettingsFile.Instance.Reset("lift")`) → ADVANCED, button "Reset Lift settings" (same call)
- [ ] "Create Node at AP", "Create Node Now", "Run" (`final_grp`) — always hidden by `LiftUI.onUpdateUI` (`final_grp.Show(false)`) and wired to nothing: dead, not moved

## Info lines and messages

- [x] Labels "End Ascent Alt. : x km", "Final Adjust Alt : x km" → notes under their sliders in ADVANCED ("End ascent alt", "Final adjust alt")
- [x] Transfer hint "Choose the target body." / "Start plans the parking orbit for … and fires the first stage at T-0." → note under the target
- [x] Rendezvous hint "Choose the target vessel." / "Start picks the launch pass for the cheapest rendezvous with …" → same note
- [x] `transfer_infos` (`LiftGoalRules.Readout` of the current plan) → info table under the target
- [x] LIFT INFO foldout (`current_subpilot.UpdateInfoRows`, "Status: Not running" when idle) → TELEMETRY, section LIFT INFO (folded), rows kept by key and updated in place
- [x] Status line while running "Status : <phase>" (+ " (<start note>)", e.g. "Gravity Turn flies Orbit launches only for now; using Guided") → status pill (OK, prograde)
- [x] Controllers' status and console lines ("Coasting", "End warp : … x…", TurnTo's line, "Planning the turn...", "No engine running yet - stage to lift off.", "TWR …: not enough thrust to lift off.", "Planning the transfer..." / "Planning the rendezvous launch..." / "Waiting on the pad", "Plane trim", "Hand-over to the Rendezvous pilot", the circularize and trim burns' lines) → pill and console note (Side), through `AppPilotStatus`
- [x] Progress bar of the burns (`FullStatus.Progress`) → progress bar (Side)
- [x] "Final status : <end status>" (normal or warning) → pill (OK when the run ended well, coral otherwise)
- [x] Idle "Lift autopilot not enabled" → pill
- [x] End texts of `LiftGoalRules` (chain stopped, plan refused, hand-over) → the same "Final status" pill
- [x] Ascent graph (`LiftAscentPath`: Guided pitch curve, atmosphere-coloured gradient, lines at the 90°/45°/5° altitudes, atmosphere and current altitude; Guided only) → centre ASCENT PROFILE (`LiftProfileView`): the same Guided curve from the same settings, the real body's atmosphere line, the target Ap, plus the flown path recorded while the pilot flies (downrange from the liftoff point, sea altitude) and the vessel
- [ ] The graph's grey lines at the 90°/45°/5° altitudes and the body-coloured pressure gradient — not redrawn (the curve shows where the pitch changes; the mockup has neither)
- [ ] K2's avatar animation (`status_bar.avatar.SetRunning`) — no avatar in the mockups

## Added from the mockup (not in the classic tab)

- Telemetry tiles: altitude (sea), surface speed, apoapsis, time to Ap, read from the vessel every frame.
- STAGES · Δv: the game's `VesselDeltaV.StageInfo`, stages with Δv in firing order (active one gold), up to 4.
- Phase timeline (`LiftPhases`): Launch window (Transfer / Rendezvous), Vertical climb, Gravity turn, Coast to Ap,
  Fine tuning (if on), Circularize (if done), then In orbit / Ap set / Plane trim / Hand-over; lit from
  `LiftPilot.status` (Ascent split by the 90° altitude, or by the Gravity Turn's Phase row).
- Action bar note: "Next: <next phase>" while flying, "Ready: <goal> · Ap <km> km" otherwise.
- Compact flight bar (`Hud`): LIFT, phase, ALT / AP / TIME TO AP, Stop.

## Choices not covered by the mockup

- Goal segmented labels "Transfer" (the setting's value is Prepare transfer) — the mockup's own labels.
- The mockup's "Inclination" field is our Heading (the setting the pilot flies); same field style.
- The mockup's "Gravity turn start / Turn shape / Max angle of attack" sliders do not exist; the Guided sliders
  (vertical climb, 45°, 5°) and the Gravity Turn sliders take their place, in the same slider style. "Vertical
  climb to" is now a slider limited to 0–20 km (the classic integer field had no limit).
- "Warp to launch window" and "Plan booster return" switches: no such setting (booster return is roadmap R2);
  the switches are Auto staging, Coasting warp, Circularize once ascent complete, Pause leaving atmosphere.
- Mockup buttons Pause / Abort: no such pilot call in the classic tab; one main button Start / Stop. The sample
  shows the mockup's "Running…".
- Roll program, ADVANCED and LIFT INFO as folded sections (closest: the kit's AppSection).
- Gravity Turn has no planned curve: the view says so and only draws the flown path.
- A run on track shows a prograde OK pill (the classic status line was a yellow warning while running).
- Stage labels "S1 · stage n" (the game's stage number); the mockup's stage names are not in the game data.
- Bottom-left plot label: the body name (the mockup's "KSC Pad 1" is in the sample only).

## Not verified in game

- `IKeplerPatch.TimeToAp`, `VesselVehicle.SurfaceSpeed`, `VesselComponent.Latitude/Longitude/Landed` as read by
  the tiles and the flown path (compiled against 26w42c, values not seen).
- Stages from `VesselDeltaV.StageInfo` (highest `Stage` = active; `DeltaVActual`, else vacuum): order and values.
- The flown path's downrange (great circle from the liftoff point, body-fixed latitude / longitude) against the
  planned curve, and the repaint cost while flying (repaints when the vessel moved 0.3 % of the target Ap).
- Every controller status line through `AppPilotStatus` (pill colours, console, burn progress) during a full
  Orbit, Transfer and Rendezvous run, and the timeline following `LiftPilot.status`.
- A tab built before the pilot exists rebinds to the pilot's settings once it appears (`ResolvePilot`).
- Target drop-down lists (bodies, vessels) and the plan readout in game.
