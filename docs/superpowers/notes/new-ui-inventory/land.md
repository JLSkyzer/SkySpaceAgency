# LAND tab — inventory of the classic tab

Classic sources: `Assets/UI/SkySpaceAgency_UI/Landing.uxml`, `Code/Pilots/Landing/LandingUI.cs`,
`LandingSettings.cs` (`setupUI` for the vacuum profile, `setupBasicUI` for the atmospheric one),
`Controlers/TouchDown.cs` (`SetupFullUI` / `SetupBasicUI`), the `LandingPilot*.cs` fields the page reads, and the
executors' `UpdateInfoRows`. The classic page writes its status itself (no controller `updateUI` reads `root_el`).
New tab: `Code/AppUI/Tabs/LandTab.cs` (page, tick, status), `LandTarget.cs` (target blocks, waypoint list, map
pick), `LandProfileModel.cs` + `LandViews.cs` (descent profile, heat bar, target icon), `LandPhases.cs` (timeline),
`App/Tabs/Land.uss`. Tests: `Assets/Tests/UI/Tabs/LandTabTests.cs`.

Two profiles, as in the classic tab: the plan column has an atmospheric block and a vacuum block, each bound once
to its own `LandingSettings` (`pilot.settings_atmo` / `settings_vac`, files `land_atmo` / `land_vac`) and to its own
`TouchDown` settings; the one shown follows `LandingProfile.IsAtmospheric` every tick. The card's note says which
("atmospheric profile" / "vacuum profile").

## Settings (same `Setting` objects, bound with `AppBind`; both profiles unless noted)

- [x] `land.mode` (atmo only) — InlineEnum Propulsive / Parachute → LANDING PLAN, segmented "Propulsive / Parachutes"
- [x] `land.precision_landing` — toggle → switch "Precision landing" (atmo: enabled in Parachute mode only and forced
      off in Propulsive mode, as `setupBasicUI` and `LandingUI.onUpdateUI`; vac: shows the target and the
      PRECISION LANDING group when on, as `setupUI`)
- [x] `land.target_latitude`, `land.target_longitude` — float fields → target block, "Coordinates" button opens the
      Latitude / Longitude fields; the values also show in the target box ("17.42° N · 88.10° W · waypoint")
- [x] `land.deploy_legs_early` — toggle → switch "Deploy legs early" (tooltips kept)
- [x] `land.drop_stage_at_km` — text field (delayed, empty = automatic, red when not a number) → ADVANCED > BRAKE,
      text field "Drop lower stage at the latest at (km)", placeholder "automatic", red text when not a number
      (class `ssa-land-field--invalid`), trimmed as in the classic
- [x] `land.auto_warp` — toggle → ADVANCED > WARP "Auto warp"; hides the two warp settings when off (as classic)
- [x] `land.rotation_warp_duration` — integer field → ADVANCED > WARP "Rotation warp duration" (s)
- [x] `land.max_rotation` — slider → ADVANCED > WARP "Safe warp rotation" (°)
- [x] `land.burnBefore` — slider → ADVANCED > BRAKE "Burn before" (s)
- [x] `land.touch_down_altitude` — slider with "Start TouchDown Altitude : <distance>" → ADVANCED > TOUCH DOWN
      "Start touch-down altitude", value shown with `StrTool.DistanceToString`
- [x] `land.touch_down_max_angle` (TouchDown, per profile) — slider → ADVANCED > TOUCH DOWN "Max angle"
- [x] `land.touch_down_ratio` — slider with "Safe-Danger" min/max labels → "Altitude / speed ratio", the value text
      says "· safe" / "· danger" at the ends
- [x] `land.touch_down_speed` — slider → "Touch-down speed" (m/s)
- [x] `land.steering_max_angle` (TouchDown, vac) — slider → ADVANCED > PRECISION LANDING "Max steering angle",
      shown with precision on (as classic)
- [x] `land.reentry_drag_scale` (atmo only) — slider → ADVANCED > PRECISION RETURN "Reentry drag scale"
- [x] `land.entry_angle_of_attack`, `land.entry_flight_path_angle`, `land.boost_back_flip` (atmo, K2-D2 flavour
      only) → ADVANCED > BOOST-BACK group, hidden unless `ModIdentity.IsK2D2` and Propulsive and precision on (never in
      this mod); "Entry steepness" stays hidden in both flavours, as in the classic tab
- [ ] `land.max_throttle_rate_per_sec` (TouchDown) — no control in the classic tab either: not added

## Buttons

- [x] "Brake" / "Land" (ToggleButton `run`, label follows the mode selector, "Stop" while running) → main gold
      button: "Brake" / "Land" idle, "Landing…" while running (click stops); start = `pilot.isRunning = true`, a refused
      start shows `last_error` in the status pill
- [x] "Touch Down" (hidden in a parachute landing and once in Touch Down) → plain button "Touch Down", same rule and
      calls (`isRunning = true`, `setMode(TouchDown)`)
- [x] (new, mockup) "Abort" → abort button while running, `pilot.isRunning = false`
- [x] "Pick on map" (per profile, "Click the body… (Esc to cancel)" while waiting, disabled with
      "Map picking unavailable…" tooltip) → target block "Pick on map", same flow (opens the map, waits up to 3 s,
      `ReduxPlacement.Begin`, a second click cancels); the waiting text is shortened to "Click the body… (Esc)"
- [x] (new, mockup) "Here" → target block: the target becomes the vessel's position (`VesselComponent.Latitude /
      Longitude`, normalised with `TargetPoint.TryNormalize`); writes the same two settings, logs
      `[Landing] target: target set to the vessel's position`
- [x] (new, mockup) "Coordinates" → opens / closes the latitude and longitude fields
- [x] "Apply" (reentry calibration) → plan, under the target, "Apply" (`pilot.ApplySuggestedDragScale()`)
- [x] "Reset" (end of ADVANCED, resets `land_atmo` and `land_vac` chapter "land") → "Reset" at the end of each
      profile's ADVANCED group, resets both files

## Drop-downs and foldouts

- [x] "Waypoint :" (per profile: KSC, the picked point, player waypoints, mission waypoints, "Custom (lat, lon)",
      "No body") → target block: clicking the target box opens the list under it (folded by default) (same `WaypointChoices.Build`, `IndexOfSaved`, `Custom`, " #2"); a
      rebuild never writes the target; a chosen player waypoint that moves takes the target with it
      (`WaypointChoices.Follow`, same log line); rebuilt 4 times a second instead of every frame
- [x] "Land with stage" (per profile, locked while running or with one stage, hidden in Parachute mode on the atmo
      profile, "Stage n (landing stage, k drop(s) left)" while running, "No stage data") → dropdown "Land with stage",
      same rules and log line `[LandingUI] land with stage …`
- [x] Foldout "ADVANCED" → `AppSection` "Advanced" at the end of each profile's block
- [x] Foldout "LANDING INFO" → Side, `AppSection` "Landing info" (folded by default)

## Info lines (LANDING INFO table and always-visible rows)

- [x] "Collision: None detected / Detected" (always visible) → Side, info row above "Landing info" (coral when detected)
- [x] Fall Speed, Altitude, Legs, Parachutes (atmo), Collision In, Collision Speed, Start Burn In, Burn Duration
      (not under a canopy), Predicted Landing, Target Error, Precision Phase, the precision distance row
      (`precision_distance_label`), executor rows (`current_executor.UpdateInfoRows`), Hottest Engine, Stage Drop,
      Burned → Side, "Landing info" table, same texts and conditions; rows are updated in place and removed when
      their condition ends; a readout that throws shows "?"
- [x] Precision plan label (`precision_plan_vac` / `_atmo`: final message, summary, warnings) → plan, note under the
      target (both profiles)
- [x] Orbit gate "Periapsis above 100 km: lower the orbit first (Orbit tab)." (vac, before a start) → plan, coral
      note under the precision switch
- [x] "Precision landing on atmospheric bodies: Parachute mode only." → plan, note under the atmo precision switch
      (shown when precision is not allowed)
- [x] Reentry suggestion "Suggested reentry drag scale: x (current y)" → plan, note above "Apply"
- [x] Pick status lines ("Target set to (…)", "Open the map (M), then click the body.", refusals, "Redux's
      Waypoints window also saved this click as a waypoint.", "Picked point is not a valid position: ignored.") →
      target block, note under the buttons
- [x] Boost-back gate note (K2-D2 only: Redux prediction settings, `BoostBackStart.FromOrbitMessage`) → plan, note
      under the atmo precision switch (never shown in this mod)

## Status, warnings and errors (classic `status_bar`, now `AppPilotStatus` in the Side column)

- [x] "Landing is only available in Fligh View" → console, typo fixed ("Flight View")
- [x] Phase texts (Deorbit burn, Pre-brake before the atmosphere, Atmospheric coast…, Precision: …, Boost-back burn,
      Entry guidance), "Pause", "Quick Warp", "Rotating Warp" (warning), "Waiting : <time>", "Brake !",
      "Cannot stop before the ground!" (error), precision drop message, "Touch Down...", "No parachute left",
      "Too fast under canopy", "Parachute descent..." → status pill, same texts and levels
- [x] Executor status line, stage message (error when `stage_plan_alarm`), `last_warning`, `descent_summary`,
      `heat_message` (error), precision summary and warnings → console / pill, same order
- [x] Idle: `last_error` (error), `precision_final_message`, "Landing autopilot not enabled" → pill
- [x] Progress bar → `AppPilotStatus.Progress` (the classic page never fills it)
- [x] K2 avatar animation → none (the new window has no avatar; the tab button lights while running)

## New in the new tab (from the mockup)

- [x] Descent profile (centre) — see "Choices" below
- [x] SAFETY: hottest engine bar with the 75 % guard and 90 % marks (`HeatGuard.On`, `HeatGuard.Ceiling`), "Predicted
      peak x % · now y %" (`descent_plan.HeatPeak` when the planned descent modelled the heating), "guard on"
- [x] Tiles Δv NEEDED (precision plan, else descent plan, else brake simulation), Δv ON BOARD (coral when under
      the start checks' 10 % margin), TOUCHDOWN (the profile's touch-down speed setting), TIME TO IMPACT
- [x] Feasibility pill "Landing feasible · n % Δv margin" / "Δv short: …"
- [x] Phase timeline (`LandPhases`) and the compact bar's `HudState`
- [x] Action-bar note: the current phase's hint, or what Brake / Land will do

## Choices not covered by the mockup

- The vacuum profile (the mockup only shows Duna): same plan card with the precision switch first, the target block
  only with precision on (classic rule), no mode selector; SAFETY says "No atmosphere here: no reentry heating to
  watch." instead of the heat bar.
- "Heat guard" is a switch in the mockup, but there is no such setting (the guard is always on, `HeatGuard`): a
  read-only row "automatic · on at 75 %, off under 70 %" in the switch's place. No new key.
- "Here" and "Coordinates" are new buttons (mockup); "Here" writes the existing target settings.
- The waypoint list opens under the target box when the box is clicked (the mockup has no list); the box shows the
  chosen entry's name, the saved coordinates and the entry's kind.
- The precision switch is off in the sample (the mockup shows it on in Propulsive mode, which this mod refuses), and its
  note reads "Precision: Parachute mode only on atmospheric bodies."
- The atmosphere tint and heat band are drawn fainter than the SVG's opacities (linear colour space makes them stronger).
- The landing-info table, the collision row, the status pill, the console and the progress bar are added to SAFETY
  (closest mockup style: info rows and pills of the other tabs).
- Descent profile: x is the ground distance still to fly to the predicted touchdown (right), y the height above the
  terrain. With a planned descent that modelled the heating, the curve is the predictor's samples
  (`descent_plan.Heat.Samples`, from now on), with the Brake marker at `BrakeStartUT` and the peak-heating band (from
  half the peak to the peak); on a collision course without samples, a smooth curve between the vessel and the
  predicted touchdown (`predicted_landing_lat/lon`; its ends are real, the shape is drawn: the card note says so),
  with the Brake marker placed by time; the flown track (gold) is recorded once a second while running. Deorbit and
  pre-brake markers are in the sample only (they happen before the first sample). Redrawn once a second.
- The main button says "Brake" / "Land" (the classic labels) rather than "Start".

## Not verified in game

- The descent profile with real data: the frame of `DescentSample.R` (assumed the same for all samples of one
  prediction: the arc to the last sample is taken as the distance to go), the curve's look, the flown track.
- `VesselComponent.Latitude / Longitude / AltitudeFromTerrain` for "Here" and the track.
- The hottest-engine reading of the tab (the same loop as `LandingPilotDescent.ReadHottest`).
- A map pick started from the new window: the cancels (tab hidden detected by a gap of more than 3 frames between
  ticks; pilot reset through the classic page's `CancelPick`, which cancels Redux's placement), the waiting text.
- The two profiles switching on a body change with the new window open.
- The Δv-needed tile and the feasibility pill before a start (only the brake simulation is known then).
