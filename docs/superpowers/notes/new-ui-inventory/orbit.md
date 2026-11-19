# ORBIT tab — inventory of the classic tab

Classic sources: `Assets/UI/SkySpaceAgency_UI/Orbit.uxml`, `Code/Pilots/Orbit/OrbitUI.cs`, `OrbitSettings.cs`,
`KSPService/OrbitPlanRun.cs` (progress lines written by `NodeExPilot`). The classic tab has no pilot and no
`setupUI` settings page (its `settings` panel only says "No Orbit Settings..."). New tab:
`Code/AppUI/Tabs/OrbitTab.cs`, `OrbitView.cs`, `OrbitViewModel.cs` (view geometry and texts), `App/Tabs/Orbit.uss`.

## Settings (`OrbitSettings`, same `Setting` objects, bound with `AppBind`)

- [x] `orbit.ap_enabled` — toggle "Ap (km)" → TARGET ORBIT, switch "Change Ap"
- [x] `orbit.ap_km` — field → TARGET ORBIT, field "Apoapsis" (km)
- [x] `orbit.pe_enabled` — toggle "Pe (km)" → switch "Change Pe"
- [x] `orbit.pe_km` — field → field "Periapsis" (km)
- [x] `orbit.inc_enabled` — toggle "Inclination (°)" → switch "Change inclination"
- [x] `orbit.inc_deg` (clamped 0..180) — field → field "Inclination" (°)
- [x] Toggle tooltips ("Change the apoapsis altitude", …) → same tooltips on the switches
- [x] Ticking a target starts it from the current value (rounded 0.1 km / 0.01°) → same listeners in `OrbitTab.OnBuild`
- [x] Unticked target: its field is disabled and shows the live current value without touching the saved target
      → same, refreshed twice a second (`ShowTarget`)

## Buttons

- [x] "Create maneuvers" → main gold button "Create nodes" (same checks, logs and node creation, ported in
      `OrbitTab.CreateNodes`); once the plan's node exists it becomes "Start" / "Stop", which runs the Node pilot
      (`NodeExPilot.isRunning`, the Node tab's own call)
- [x] (new, mockup) "Plan" → plans the preview again at once and clears an old refusal

## Info lines and messages (classic `summary` label)

- [x] Initial hint "Tick the targets to change, then create the maneuvers…" → console (Side), reworded for Start
- [x] Plan summary: capture line, "n. T+hh:mm:ss  label: x m/s", "Total Δv", "Final orbit: Ap, Pe, inclination"
      → console (the created plan, else the live preview), plus the PLAN rows (label → Δv), the total row, the
      capture line above the rows, and the view's target line
- [x] `OrbitPlanRun.Status` under the plan ("Step i/n: creating the node…", "node created…", "Plan complete…",
      "Plan stopped…", "Warning: the game holds n node(s)…") → console, and its first sentence in the status pill
      (complete: green, stopped / warning: coral, steps: info)
- [x] "No trajectory to plan from: be in flight." → pill + console
- [x] "Unexpected reference frame: computed inclination … No node created." → pill + console
- [x] "Tick at least one target (Ap, Pe or Inclination) first." → pill (also shown live) + console
- [x] "The capture at X needs … m/s, the vessel has … m/s. No node created." → pill + console (also live)
- [x] `OrbitPlanner.Describe` errors (escape, unstable orbit, Pe below surface / in atmosphere, Pe above Ap,
      inclination range, capture below surface / in atmosphere / leaves SOI) → pill + console (also live)
- [x] "Already on the target orbit: nothing to do." → pill (green) + console
- [x] "Could not plan the first step (…)." → pill + console
- [x] Capture info (`OrbitUI.CaptureLine`) → note above the PLAN rows, first line of the console
- [x] Logs `[OrbitUI] frame guard …`, `[OrbitUI] capture: …`, `[OrbitUI] capture refused: …` → same lines, same prefix

## Drop-downs and foldouts

- [x] none in the classic tab (Group "Target orbit" → card TARGET ORBIT; title "Orbit Planner" → tab label)

## New in the new tab (from the mockup)

- [x] Centre view: body, current orbit (cyan dashed), the orbit the plan ends on (gold), a blue marker per planned
      burn with "i · label · Δv", the vessel, "current A x P km, i°" and "target …" lines; drawn from the vessel's
      state and the live preview plan
- [x] CURRENT ORBIT tiles: Apoapsis, Periapsis, Inclination, Period ("open" for an open trajectory)
- [x] Timeline: Plan, one step per burn (short label), Done; lit on the step the Node pilot flies
      (`OrbitPlanRun.Sequence.StepsStarted`), on Done after "Plan complete"
- [x] Compact flight bar (`Hud`) while the Node pilot flies an Orbit plan: step, Ap / Pe / Inc, Stop

## Choices not covered by the mockup

- "Keep Ap / Keep Pe" of the mockup are "Change Ap / Change Pe": the settings mean "change this target"
  (classic tooltips); an inverted switch would need an inverted binding the kit does not have.
- "From orbit / From flyby" is not a setting: the planner captures an open trajectory by itself
  (`OrbitPlanner.NeedsCapture`). The segmented control shows what was found and snaps back if clicked.
- "Ascending node: free" is shown read-only: the planner always tilts the plane at the farther orbital node.
- "One node at a time" is a locked-on switch (drawn at full brightness, as the mockup): one node at a time is the only mode since 2026-10-20.
- The main button is "Create nodes" until the plan's node exists, then "Start" / "Stop" (the mockup only shows
  "Start"); creating and flying stay two clicks, as in the classic tabs.
- The plan is previewed live (twice a second, and at once after a target changes) instead of only on a click.
- The view is drawn by altitude (every radius shortened by 0.75 of the body's radius, directions kept, as the
  mockup's proportions), seen along the current orbit's normal: the target ellipse is not rotated as in the mockup
  (an inclined target shows flattened along the line of nodes). The sample uses the mockup's texts on
  real geometry (Kerbin 100 km circular, raise to 250 km, then circularize + 45°).
- The mockup's pill "Plane change merged: 380 m/s saved" is not something the planner computes; the pill says
  "Plan ready: n burns, Δv" (the sample shows that line too).
- The classic summary's long texts go into the console note under the pill (pill: first sentence only).

## Not verified in game

- The whole tab (built and ticked in the editor only): live preview cadence and cost, tiles, view drawing on real
  orbits (incl. hyperbolic flybys and Minmus's tilted frame), marker positions vs the nodes the game creates.
- Start / Stop through `NodeExPilot.isRunning` from this tab, the timeline following the steps, and the compact bar.
- That the classic window's own "ticking a target" listener and this tab's (both active while both windows exist)
  do not fight (they write the same value).
