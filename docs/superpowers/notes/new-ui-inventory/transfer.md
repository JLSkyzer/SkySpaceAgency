# TRANSFER — inventory of the classic tab (new UI migration, 2026-11-19)

Sources: `Assets/UI/SkySpaceAgency_UI/Transfer.uxml`, `Code/Pilots/Transfer/TransferUI.cs` (page and
`TransferSettings`), `TransferPilot*.cs` (what the tab reads), `TransferReadout.cs` (texts). The classic page sets
its status itself (no controller `updateUI`); the new tab does the same through `AppPilotStatus.Status`.
New tab: `Code/AppUI/Tabs/TransferTab.cs`, `TransferView.cs`, `App/Tabs/Transfer.uss`.

## Settings (`TransferSettings`, unchanged keys)

- [x] `transfer.target_body` (Body drop-down, every body grouped by parent) → Plan column, DESTINATION tiles (one per
  `BodyChoices.Targets` entry, in the same order, scrolling grid); the card note names the chosen body; key recorded
  with `AppBind.Record`, the tiles follow the setting in Tick.
- [x] `transfer.arrival` (InlineEnum Encounter / Orbit) → segmented "Capture into orbit" / "Flyby" (`AppBind.Segmented`).
- [x] `transfer.low_circular` (Low circular) → switch "Low circular orbit"; unticking copies the target's low orbit into
  Pe and Ap (same listener as the classic tab).
- [x] `transfer.pe_km` (Pe (km)) → field "Capture periapsis" ("Flyby periapsis" for a flyby), shown when Low circular is off.
- [x] `transfer.ap_km` (Ap (km)) → field "Capture apoapsis", shown for Orbit only.
- [x] `transfer.inc_enabled` (Inclination toggle) → switch "Set the arrival inclination".
- [x] `transfer.inc_deg` (Inclination field) → field "Inclination °", disabled while the switch is off.
- [x] `transfer.prepare_only` (Prepare only) → switch "Prepare only (I fly the nodes)".
- [x] `transfer.max_trip_days` (Max trip (days), text) → text field "Max trip (days of 6 h)", placeholder "no limit",
  delayed (written on Enter / leaving the field, as the classic one).
- [x] `transfer.gravity_assist` (Use gravity assist) → switch "Use gravity assist".

## Buttons

- [x] Plan → action bar "Plan" ("Re-plan" once a plan exists); same `TransferReadout.TryInput` + `pilot.Plan`.
- [x] Start → main gold button "Start" (`pilot.Start(prepare_only)`), enabled by `CanStart`.
- [x] Stop → the main button reads "Stop" while flying (`pilot.Stop()`).
- [x] Stop as "Cancel" while searching → the Plan button reads "Cancel search" (`pilot.CancelSearch()`, what the
  classic Stop did while searching).
- [x] Resume → action bar "Resume", shown with a saved plan, enabled by `CanResume`.
- [x] Cancel saved plan → action bar "Cancel saved plan", shown with a saved plan or while flying.
- [x] choose_direct / choose_assist (4.6 choice lines "● Direct: 2 140 m/s, 1y 120d", "○ Via Eve: …", or the direct
  line "— why not offered", disabled) → Side column, two line buttons above the Δv ring, the shown plan gold-outlined;
  same enabling rules and `pilot.ChoosePlan`.

## Info lines and plan read-outs

- [x] low_orbit_hint ("Capture into 74 km above Duna: 10 km above its atmosphere.", "Lowest safe altitude…",
  "Choose a target body.") → note under the Low circular switch (`TransferTabLogic.LowOrbitHint`, same texts).
- [x] resume_offer ("Resume plan to Duna (next: …)", mismatch message) → action bar note (else "Next: <step> · <when>"
  while flying, else the mockup's line).
- [x] case ("Case: …") → Side, "Plan details" section, row Case.
- [x] departure ("Departure: date, in …") → "Plan details", row Departure.
- [x] trip ("Trip: duration, arrival date") → "Plan details", row Trip; the duration also in the TRIP TIME tile under the map.
- [x] steps (kind, Δv, when, explanation, flown ones dimmed) → Side, "Steps" section (same rows, "when" updated in
  place); the Δv per step also as the budget rows ("Injection burn (Kerbin) 1061.0 m/s", "· done" once flown), then
  "Reserve".
- [x] totals ("Total … m/s, reserve included (…); the vessel has … m/s") → "Plan details", row Total; and the ring
  ("needed" of "available" m/s, coral when short).
- [x] margin ("Margin: … left at the arrival" / "short by …", coral) → row "Left after arrival" (ok) / "Short by" (alert).
- [x] STAGES foldout (per-stage Δv, steps paid, "Stage it by hand: it drops …" in gold) → Side, "Stages" section.
- [x] stage_note ("The Δv from stage N on needs you to stage by hand…", "No stage is left…") → note in "Stages".
- [x] per-stage budget verdict (`Budget.Ok` / `Budget.Refusal`, seen in the classic status) → budget pill
  "Every stage can pay its burns" (ok) or the refusal (alert).
- [x] warnings ("Warning: …" from `plan.Warnings`, "Warning: Estimate: the … should last about …" long-burn estimates)
  → Side, gold notes under the budget pill (same texts, same estimate with `BurndV` / `BurnTiming`).

## Status, console, progress

- [x] status_pilot: the UI error (Warning), `pilot.Status` (Warning when Failed or "Refused…", else Normal), else
  "Choose a target, then Plan." → `AppPilotStatus` pill in the Side column.
- [x] pilot_console → `AppPilotStatus.Console` (the classic tab never writes it; kept for parity).
- [x] progress (search progress, "Searching") → `AppPilotStatus.Progress` while searching.
- [x] avatar running state → the tab button lights through `IsRunning`; the compact flight bar gets `Hud`.

## Drop-downs and foldouts

- [x] Body drop-down → destination tiles (above).
- [x] STAGES foldout → "Stages" section (folded by default, as the classic foldout).
- [x] Target / Options groups → one DESTINATION card (the mockup's).
- [ ] "settings" sub-page ("No Transfer Settings...") — nothing to move: an empty placeholder.

## New in the new tab (from the mockup, real data)

- Transfer map (`TransferView`): centre body, departure and arrival orbits, the transfer arc, departure / arrival
  markers, ejection node, target today, flyby body (on the arc for a planet; by the departure for a moon on the way
  out), the vessel on the arc while flying. Computed by `TransferViewModel.Build` from `pilot.System` and the plan.
- Tiles PHASE ANGLE (now · at the departure), TRIP TIME, ENCOUNTER (capture / flyby periapsis).
- Timeline: "Wait for window", then one entry per plan step ("Injection · 1 061 m/s", "Leave Kerbin SOI",
  "Mid-course correction", "Flyby Eve", "Capture · 317 m/s"), lit from `pilot.Phase` / `pilot.StepIndex`; before a
  plan, the generic phases.

## Choices not covered by the mockup

- The mockup shows 6 destination tiles; the real list has every target body (~16 in the stock system): the grid
  scrolls (two rows high), in `BodyChoices` order (planets with their moons after them); moons get a smaller dot.
  Body colours are the mockup's for its six bodies, guesses for the others.
- The mockup's "Priority: Lowest Δv" field has no setting (the search always takes the cheapest): its place holds
  the Max trip field. Its "Drop empty stages in burns" switch has no setting either (always on in the pilot,
  `burn.stage_when_dry = true`): not shown.
- Apoapsis, Low circular and its hint: fields / switches in the mockup's style, in the Destination card. Gravity
  assist, the inclination switch and field and Max trip are folded in an "Options" section at the bottom of the card
  (the mockup's plan column has no room for them).
- Case, departure, trip, total, steps and stages: foldable sections of the Side card (closest style: AppSection).
- Assist choice lines: small line buttons, the chosen one gold-outlined (no mockup).
- Labels on the timeline: the mod's step names, shortened; "Enter <body> SOI" of the mockup is not a plan step and
  is not shown. A plan with many steps (8+) may crowd the timeline labels at the minimum width.
- The arc is schematic: real departure and arrival positions (projected on the departure body's orbit plane),
  radii compressed ((r / rmax)^0.6, at least 0.3) and eased between them, not the propagated trajectory.
- Map note: "<centre> system · not to scale"; empty map text "Plan a transfer: its trajectory shows here."

## Not verified in game

- The map's geometry on real plans (direction of the arc, phase angle sign, moon-to-planet and planet-to-moon cases,
  a gravity assist through Eve, a Mun flyby on the way out).
- The body tiles' list (and that the scroll shows the chosen one), the classic and new tabs both setting Pe / Ap when
  Low circular is unticked (same values, two listeners).
- The budget ring and rows against the classic Totals / Margin texts while flying (from the current step).
- The timeline index during Wait for window, burns and coasts; the vessel marker on the arc.
- The compact flight bar readouts (Target, Next, Δv left).
