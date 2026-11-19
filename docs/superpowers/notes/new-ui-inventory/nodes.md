# NODES (classic Node and Attitude tabs)

New tab: `Assets/SkySpaceAgency/Code/AppUI/Tabs/NodesTab.cs` (+ `NodesLogic.cs`, `NodesBurnView.cs`,
`NodesNodeList.cs`), style `Assets/UI/SkySpaceAgency_UI/App/Tabs/Nodes.uss`, tests
`Assets/Tests/UI/Tabs/NodesTabTests.cs`. Mockup `docs/design/ui/nodes.png`.

Layout: plan column = card MANEUVER NODES (node list, main settings, Circularize, NODE INFO and ADVANCED
sections); view column = card BURN (navball: nose against the burn vector) and card HOLD ATTITUDE (the
classic Attitude tab); side column = card BURN DATA (Δv left, burn time, throttle, warp, Δv applied, pilot
status); phase timeline; action bar (note, Abort, gold Start / Running…).

## Classic Node tab (`node.uxml`, `NodeExUI.cs`, `NodeExSettings.setupUI`)

- [x] Title "Maneuver Node Autopilot" → card title MANEUVER NODES (tab NODES)
- [x] Button "Circularize at AP" (`pilot.StartCircularize(true)`) → action bar, plain button (disabled while running or without the pilot)
- [x] Button "Circularize at PE" (`pilot.StartCircularize(false)`) → action bar, beside it
- [x] Toggle "Start" / "Stop" (`run`, `pilot.isRunning = v`) → action bar, gold button "Start" / "Running…" (same toggle) and red "Abort" (`pilot.Stop()`, enabled while running)
- [x] Toggle pause (`pause`, `node_ex.pause_on_end`) → switch "Pause the game when done"
- [x] Status line `status_pilot` → BURN DATA, status pill (via `NodeExPilot.UpdateStatus` + `AppPilotStatus`)
- [x] Console `pilot_console` → BURN DATA, console under the pill
- [x] Progress bar `progress` → BURN DATA, progress bar (shown when a controller sets it)
- [ ] Avatar `k2_avatar` (`SetRunning`) → not moved: no avatar in the mockups; the running state lights the NODES tab instead
- [x] Foldout NODE INFO → BURN DATA, "NODE INFO" rows under Δv applied (same rows, same node: the one flown while running, the next one otherwise)
  - [x] "Node" "none" → row "Node: none"
  - [x] "Node in" (`StrTool.DurationToString`) + "  (in the past)" → row "Node in", coral when in the past
  - [x] "dV" (`n2` m/s) → row "dV"
  - [x] "Duration" → row "Duration"
- [x] Foldout ADVANCED → section "Advanced" in MANEUVER NODES
  - [x] // TURN, slider "Max Angle" (`node_ex.turn.max_angle_maneuver`) → field "Max angle" (°) in the main settings (mockup's place)
  - [x] // TURN, slider "Max Angular Speed" (`node_ex.turn.max_angular_speed`) → ADVANCED / TURN slider
  - [x] // WARP, toggle "Auto Warp" (`node_ex.auto_warp`) → switch "Warp to the node"
  - [x] // WARP, "Warp during burns" inline enum Off (1x) / Physics (x4) / Rails (native) (`burn.warp_mode`) → segmented "Warp during burns" (Off / Physics x4 / Rails (native)), mockup's place
  - [x] `warp_settings` shown only with Auto Warp → "Lead time" and "Warp speed" disabled when the warp to the node is off
  - [x] // WARP, slider "Warp Speed" Safe-Quick (`node_ex.warp.speed`) → ADVANCED / WARP slider "Warp speed (safe - quick)"
  - [x] // WARP, field "Before Burn (s)" (`node_ex.warp.safe_duration`) → field "Lead time" (s) in the main settings (mockup's place)
  - [x] // BURN, slider "Adjusting rate (m/s)" Precise-Quick (`node_ex.burn.burn_adjust`, 0.5 to 2) → ADVANCED / BURN slider
  - [x] // BURN, slider "Precision (m/s)" (`node_ex.burn.max_dv_error`, 0.001 to 0.1) → ADVANCED / BURN slider
  - [x] // EXPERIMENTAL, radio T0 / Before / Mid-duration (`node_ex.start_mode`) → ADVANCED / START THE BURN AT, segmented T0 / Before T0 / Mid-duration
  - [x] // EXPERIMENTAL, slider "Start Before T0 (s)" (`node_ex.start_before`), shown only with Before → slider shown only with "Before T0"
  - [x] // EXPERIMENTAL, toggle "Rotate During burn" (`node_ex.burn.rotate_during_burn`) → ADVANCED / BURN switch "Steer during the burn"
  - [x] Button "Reset" (`SettingsFile.Instance.Reset("node_ex")`) → ADVANCED, "Reset node settings" (same call; like the classic one it does not reset `burn.warp_mode` nor `attitude.*`)
- [ ] Tooltips (auto warp "Warp time before burn", warp during burns, "Nb seconds in x1 before next phase (min:5)", "Keep SAS during burn phase.") → not moved: the kit has no tooltips (labels say the gist)
- [ ] Hidden "settings" page ("Node Auto-Executor Settings") → dropped, it was empty and never shown
- [ ] Flight Plan block (`FlightPlanCall`) → not moved: disabled in the classic tab too (call sites commented out)

### Pilot messages (`NodeExPilot.UpdateStatus`, ex `UpdateUI`) → status pill / console

- [x] "No Node Created"
- [x] Orbit plan stop message (`plan_message`), circularize refusal (`circularize_error`), target orbit refusal (`target_error`) as warnings
- [x] "Node data glitched" + console "That's a known KSP2 map-loading glitch…"
- [x] "That node's already in the past - replot it."
- [x] "No active engine - stage or switch one on." (`NoActiveEngineMessage`)
- [x] "Ready to execute node!"
- [x] "Planning the next step of the Orbit plan..."

### Controllers' `updateUI(root_el, st)` (none of them queries `root_el`) → status pill / console / progress

- [x] TurnTo: "Aligning...", console "Lining up... x° to go" / "Holding steady..." / "Locked on!", debug lines
- [x] WarpTo: "Warping ahead...", console "Warp ends in … (xN)", debug lines
- [x] BurnManeuver: "Standing by...", console "Ignition in …"; "Burning!" or "No active engine - stage or switch one on", progress (Δv left), console "… m/s remaining | time", debug block
- [x] TargetOrbitBurn / CircularizeBurn: "Warping to the …", "Aligning for the …", "Late ignition: aligning for the …", "Burning!", progress, console, debug lines

### Added by the mockup (read-only data, no new setting)

- [x] Node list: every node of the vessel's plan (up to 6): "Node n · prograde / normal / radial…", Δv, "in m:ss" / "m:ss ago" (coral), next node framed blue
- [x] BURN view: nose (gold chevron) against the burn vector (blue marker), horizon from the pitch, note "aligned to x°" (prograde green under the max angle) / "x° from the burn" / "no burn to point at"
- [x] BURN DATA: Δv left of the next node (as `BurnManeuver` measures it), burn time scaled by the Δv left, throttle %, warp rate with "phys" / "rails"; Δv applied bar "a of b m/s"
- [x] Phase timeline: Align, Warp to node, Ignite, Burn, Done (node executor); Warp, Align, Burn, Done (Circularize, Orbit plan nodes)
- [x] Action bar note: "Next: node 1 in … · … m/s", the phase while running with "Ctrl+O stops the run" (the classic shortcut), or "Plot a node on the map, or circularize at Ap / Pe"
- [x] Compact flight bar (`Hud`): NODES, phase, Δv left, burn time, throttle, Stop

## Classic Attitude tab (`attitude.uxml`, `AttitudeUI.cs`) → card HOLD ATTITUDE (view column)

- [x] Title "Attitude Autopilot" → card title HOLD ATTITUDE
- [x] Group Elevation: field "Elevation (°)" (`attitude.elevation`) → field "Elevation (−90 to 90)" (°)
- [x] − / + repeat buttons (1° steps) → replaced by the typed field (no repeat buttons in the kit)
- [ ] Elevation slider → dropped: the card is one row so the view keeps the page height; the field takes any value
- [x] Group Heading: label "Heading : x" and compass (`attitude.heading`) → field "Heading (0 to 360)" (°): no compass in the kit
- [x] Toggle "Start" / "Stop" (`run`, `pilot.isRunning = v`) → button "Hold" / "Release"
- [x] Status "Holding attitude" / "Attitude autopilot not enabled" → the card's note (prograde green while holding)
- [ ] Console and progress bar (never written by the Attitude tab) → not needed
- [ ] Avatar → as for the Node tab
- [ ] Hidden "settings" page ("No Attitude Settings...") → dropped
- [x] Compact flight bar while holding: ATTITUDE, "Holding attitude", elevation, heading, Stop

## Choices not covered by the mockup

- The mockup's "Drop empty stages" and "Closed-loop trim at the end" switches have no setting for the node executor:
  replaced by the real "Warp to the node" and "Pause the game when done". Its "Trim" phase does not exist in the node
  executor (the closed-loop end is part of Burn); its "Skip node" button has no classic counterpart: not added.
- The mockup's centre is a navball (BURN), not a list: the node plan is the list in MANEUVER NODES, the navball draws
  the next burn's direction.
- Circularize at AP / PE: plain buttons in the action bar (the mockup's "Skip node" place); NODE INFO rows in BURN DATA; ADVANCED folded in the plan card, so the plan keeps the mockup's height.
- Hold attitude: a one-row card under the BURN view (two fields with units and a button, status in the card note),
  since the Attitude tab has no mockup; the navball is therefore a bit smaller than the mockup's.
- The gold button toggles like the classic Start / Stop ("Running…" stops the run); Abort is the visible stop.
- Lead time and Warp speed are disabled, not hidden, when the warp to the node is off (Lead time sits in the
  mockup's main area).
- In the editor (no pilot), actions are disabled and the note says the executor flies in flight.

## Not verified in game

- The navball's axes: burn marker placed at (local x, −local z) of the burn vector in the vessel's frame, pitch from
  `HorizonUp` (signs read from TurnTo/AttitudePilot, not checked on screen).
- The direction label's normal sign (`ManeuverCreator.GameNormalSign`, itself not verified).
- Δv left from `GetVelocityAfterFirstManeuver` while the closed-loop burns fly (their own remaining Δv is in the
  progress bar), and the "Ignite" phase, deduced from the start mode's ignition time.
- The warp tile's "phys" / "rails" (`TimeWarp.IsPhysicsTimeWarp`).
- `NodeExPilot.UpdateStatus` called every frame from the new tab (the classic `UpdateUI` does the same only while
  visible; `HasActiveEngine` logs only when its case changes).
- Layout at the mockup size and the minimum size (the harness for migrated tabs is task 8).
