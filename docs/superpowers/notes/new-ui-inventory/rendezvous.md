# RENDEZVOUS (+ Dock) — inventory of the classic tab

Classic sources: `Assets/UI/SkySpaceAgency_UI/Dock.uxml` (page code "dock"), `Code/Pilots/Rendezvous/RendezvousUI.cs`
(`RendezvousSettings`, the page's own status logic; the Rendezvous pilot has no controllers with `updateUI`), the
public reads of `RendezvousPilot` (`RendezvousPilot.cs`, `.Dock.cs`) and of `TransferPilot` for the 4.5 chain
(`TransferPilot.Chain.cs`). New tab: `Code/AppUI/Tabs/RendezvousTab.cs`, `ApproachView.cs`,
`RendezvousTabTargets.cs`, `RendezvousTabLogic.cs`, `App/Tabs/Rendezvous.uss`.

## Settings

- [x] `rendezvous.prepare_only` (Prepare only toggle, tooltip kept) → TARGET card, switch "Prepare only" (`AppBind.Switch`)
- [x] `rendezvous.target_id` (remembered target, written by the classic drop-down) → TARGET list: a click writes it,
  the pre-selection reads it (same rule: the game's new target once, else the pick, else the remembered one);
  recorded with `AppBind.Record`

## Drop-downs and lists

- [x] `target_vessel` (every other vessel grouped by body, headers not pickable, game's target pre-selected) → TARGET
  list: a muted header per body, then one row per vessel (name, distance for vessels around our body), the chosen
  row gold; rebuilt only when the list changes (every 2 s, as classic); disabled while the pilot flies
- [x] `our_port` (Auto + numbered ports, "(in use)") → "Docking ports" box, "Change" opens the "Ours" drop-down
- [x] `target_port` → same, "Target's" drop-down; tooltip "Auto: the free compatible pair…" kept on both
- [x] Port picks fall back to Auto when either vessel changes (`ports_for` key) → same logic
- [x] `port_hint` ("The target has no docking port." / "Our vessel has no docking port." / "Auto picks…") → note under
  the ports box, and a CHECKS line (OK / alert)

## Buttons

- [x] `rendezvous_dock` → mode "+ Dock", then the gold main button "Start" (`pilot.Start(RendezvousAndDock, …)`)
- [x] `rendezvous_only` → mode "Rendezvous", then "Start"
- [x] `dock_only` (enabled within `RendezvousFlight.DockOnlyRange`) → mode "Dock only" (the segment is disabled beyond
  the range, and Start stays disabled while it is chosen out of range)
- [x] `transfer_rendezvous_dock` / `transfer_rendezvous` (target around another body, `Transfer.PlanChain`) → the main
  button reads "Transfer + rendezvous + dock" / "Transfer + rendezvous" (from the mode) and calls the same
  `PlanChain`; disabled without the Transfer pilot
- [x] `stop` (Transfer.Stop during the chain's leg, else pilot.Stop) → "Abort" (red), enabled while busy
- [x] `resume` (pilot or chain Resume, shown with a saved plan, enabled by CanResume) → action bar "Resume"
- [x] `cancel_saved` (shown with a saved plan or while running) → action bar "Cancel saved plan"
- [x] Main button while busy → "Planning…" / "Docking…" / "Transfer…" / "Running…" (no action: Abort stops)

## Info lines

- [x] `relative` ("Distance …, relative speed …, relative inclination …", "Choose a vessel…", "The vessel is gone.",
  far hint, read errors) → note under the RELATIVE tiles; distance and closing speed also as tiles
- [x] `resume_offer` → gold note in the side column, and the action bar's note while there is one
- [x] `dock_ports` ("Ports: … on our vessel, … on the target") → the "Docking ports" box shows the pilot's pair while
  it docks
- [x] `translate_mode` (Translation: RCS / RCS and main engine / main engine / refused + note) → CHECKS line
- [x] `dock_phase` (DockLine, "Docked.") → note under the tiles; the phase also in the view's label and the timeline
- [x] `dock_aborts` → CHECKS line (alert)
- [x] `plan_group`: `steps` (kind, Δv, when, explanation, done dimmed) and `totals` (coral when short) → foldable
  "PLAN" section of the TARGET card (opens once when a plan appears); the 4.5 chain's rows and totals too
- [x] `stages_foldout` (stages with what they pay for; live stages before a plan) → foldable "STAGES · Δv" section
- [x] Budget (from the plan's `BudgetResult`) → also a CHECKS line "Δv: n m/s left after the plan" / "short by"

## Status, warnings, errors

- [x] `status_pilot` with the page's rules (ui error as warning; chain status shown while the Rendezvous pilot is
  idle, warnings for Failed / Refused / Not enough / The stages / stopped during; pilot status, warning on Failed or
  "Rendezvous stopped"; idle "Choose a target, then …") → `AppPilotStatus` pill in the side column (prograde when
  Docked or Done); idle text now "Choose a target, then Start."
- [x] `pilot_console` → `AppPilotStatus.Console` (the page writes none; kept for parity)
- [x] `progress` (chain search progress, "Planning") → `AppPilotStatus.Progress`
- [x] "Choose a target vessel first." / "The Transfer pilot is not loaded." → warning pill
- [x] Chain status forgotten when the Rendezvous pilot runs again (`ForgetChainStatus`) → same
- [x] Every pilot refusal (`RendezvousFlight.StartRefusal`, docking failures) → the pilot's Status, in the pill
- [x] Avatar running state (`k2_avatar`) → the tab button lights (`IsRunning`) and the compact bar (`Hud`)

## Not moved

- [ ] Title "Rendezvous and docking" and the empty `settings` panel: the tab bar names the page; nothing to keep.

## New, from the mockup

- APPROACH view (Painter2D): rings 10 / 50 / 200 m, corridor and hold points on the target port's axis, our vessel and
  its dashed line; from `DockDistance` / `DockLateral` while docking ("target frame, port axis"), else from both
  orbits in the target's orbit frame ("target frame, orbit axes"). Repaints only when the state changes.
- RELATIVE tiles: DISTANCE, CLOSING (from the orbits, or `DockClosing`), LATERAL ERR (`DockLateral`, "-" before the
  docking), RCS LEFT (MonoPropellant stored / capacity of the vessel).
- CHECKS: "Target never commanded" (the pilot's rule), ports, RCS thrusters usable (`DockingParts.Thrusters`), budget,
  translation, aborts, far target.
- Timeline: Plan, Plane match, Intercept, Coast, Brake 200 m, Hold 50 m, Hold 10 m, Docked (Rendezvous only ends on
  Done, Dock only is Align, Hold 50 m, Hold 10 m, Docked; the 4.5 chain starts with Transfer).
- Action bar note: the next step ("Next: intercept burn in 12m, 3.4 m/s") while flying, else the mockup's sentence.

## Choices not covered by the mockup

- The mockup's switches "Use RCS for the approach" and "Main engine if no RCS" are not settings: the pilot chooses by
  a thruster probe. They became a note under "Prepare only" (a real setting, which the mockup lacks) and a CHECKS line.
- The mockup's "Back off" button: the pilot has no back-off command (the guidance backs off by itself, DockPhase
  Backoff). Its place holds "Resume" and "Cancel saved plan" (shown only when they apply).
- "Change" toggles the two port drop-downs under the box (label "Done" while open).
- Body headers in the target list (the classic grouping); far vessels show no distance.
- No target, far target or no relative state: the view draws the rings only with a centred message.
- Beyond 200 m the scale is logarithmic up to 200 km and the distance is written under the vessel.
- The mockup's CHECKS "Port aligned, 0.6°" and "Speed under limit" are not read by the pilot's public API: the sample
  shows them, the game shows the checks listed above.
- The timeline has 8 steps (mockup 7): Plan and Coast are real phases.

## Not verified in game

- Everything above (built and compiled; EditMode tests written, not run here).
- The MonoPropellant read (`ResourceDefinitionDatabase.GetResourceIDFromName("MonoPropellant")`,
  `PartOwner.ContainerGroup`).
- The orbit-frame placement (sign of the radial axis) and the port-frame side (the lateral error is a magnitude:
  the vessel is drawn above the axis).
- The distances in the target list (orbits at the current UT, vessels around our body only).
