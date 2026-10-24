# Changelog

## Unreleased (SkySpaceAgency)

### Added

- **Transfer tab** (transfer to a body, sub-project 4.1, 2026-10-24; not yet flown in game). From an orbit around any body, plan and fly the cheapest transfer to any other body: a moon, the parent, a sibling, another planet or a moon of another planet.
  - **Inputs**: the target body (list grouped by parent), the arrival (Encounter, or Orbit, default low circular, with Pe, Ap and inclination changeable), Prepare only, a maximum trip duration. Use gravity assist is shown but not available yet.
  - **Plan**: the departure date and the wait, the trip and arrival dates, each step with its Δv and the reason for any non-obvious burn (plane change first, lowering the orbit first, a departure that also turns the plane), the total with its reserve (2 %, at least 10 m/s), and the Δv per stage. A stage the mod will not drop by itself is flagged. Refused with a message when the vessel's stages cannot cover it, including the capture.
  - **Multi-level trips** (Mun → Duna, Ike → Laythe) are planned leg by leg; a leg that starts after a capture is planned again when the vessel gets there.
- **Transfer pilot**, behind the Transfer tab: the tab's Plan, Start, Stop, Resume and Cancel buttons drive it.
  - **Plan**: the transfer search runs on a worker thread from the vessel's actual orbit; the result is checked against the Δv of each stage.
  - **Flight**: warp to the window, then each step of the plan: orbit or plane change, injection toward the departure asymptote, SOI exit, three corrections solved in flight toward the arrival periapsis, and the capture at the periapsis. The warp stops before every burn and every SOI change. A correction under 1 m/s is skipped only when the path already meets the wanted periapsis within 1 km. When a correction picks another arrival pass, the capture and the later steps move with it.
  - **Staging during the transfer's burns**: an engine running dry for 0.5 s stages, unless the next stage would drop a docking port, a parachute or the command part. Then the burn stops and asks the player to stage. Other tabs never stage by themselves.
  - **Prepare only**: one game node at a time for the next step, no warp; the next step is planned from the orbit reached once the player has flown the node (node removed, or at least 80 % of its Δv flown with the engines off for 5 s). Only the mod's own node is removed.
  - **Resume**: the plan is saved per vessel in `mods/SkySpaceAgency/plans/` at every phase change. When that vessel is next active in flight, the mod offers "Resume plan to {body} (next: {step} in {time})", and never flies a saved burn again: every burn still ahead is planned again from the actual state.
  - **Failures** stop with the throttle at 0 and the warp off and keep the plan for Resume. A failed capture says "Capture failed: you will fly past {body}".

- **Lift: Prepare transfer to [body]** (sub-project 4.2, 2026-10-24; not yet flown in game). A goal selector in the Lift tab; Orbit keeps today's behaviour and settings.
  - The mod picks the parking orbit plane the transfer needs (it contains the departure direction; from the KSC to Duna about 4°), waits on the pad in warp until the launch site passes under it, fires the first stage itself (only on a vessel with nothing staged yet), flies the Lift ascent on the computed heading while steering toward the plane, circularizes at the low parking orbit and trims the plane to under 0.1°.
  - It ends with "Ready for the {body} transfer: open the Transfer tab"; it never starts the transfer. A missed launch time is solved again for the next pass. The tab shows the launch countdown, the heading, the plane, any plane change left, and the transfer window.
  - The ascent time used for the launch timing is measured on each transfer ascent and kept per body.

- **Landing target picker** (Land tab, precision landing part 1).
  - **Pick on map**: the button opens the map view and waits for a click on the body; the latitude and longitude of the clicked point become the target. Esc or a second click cancels. A click on another body than the one orbited is refused. The click does not add a waypoint of its own; if Redux's Waypoints window also saves it, the tab says so.
  - **Target list**: KSC (the launch pad, read from the game) first on Kerbin, then the picked point, the player's waypoints and the `[Mission]` waypoints. The saved target is never replaced by the list: when it matches no entry it shows as "Custom (lat, lon)".
  - **Atmospheric bodies**: the target picker is now shown there too (used by the parachute precision return below).

- **Parachute precision return** (Land tab, Parachute mode + Precision, precision landing part 3, 2026-10-22; not yet flown in game). From a closed orbit above the atmosphere (Kerbin), the pilot brings the capsule back under parachutes close to the target (KSC by default).
  - **Plan at start**: plane change if the target is not under the orbit, deorbit time and size from a reentry model (atmosphere read from the game, the capsule's drag at retrograde, the parachutes' opening rules), one mid-course correction before the atmosphere. The tab shows the Δv of each burn and the expected accuracy ("expected within about N km", from the model's sensitivity to drag; about 20 km until calibrated).
  - **Jettison**: after the correction and above the atmosphere, the pilot fires the next stage itself, only when that stage holds the decoupler between the capsule and the service module and nothing else on the capsule side. It refuses at start when the stage would also fire a parachute, drop the heat shield, leave the control part on the service module, or use a docking port as the separator ("Cannot jettison the service module safely: …"). It leaves time warp before staging and checks the part counts 3 s later.
  - **Reentry**: the existing parachute descent (retrograde hold, chutes armed, opened by the game, legs). The predicted distance is refreshed every 5 s before the atmosphere; the final distance is shown after landing.
  - **Calibration**: after a return, the tab suggests the reentry drag scale that would have matched the actual touchdown ("Suggested reentry drag scale: x.xx (current y.yy)") with an Apply button; the setting is never changed by itself (also in ADVANCED). Once calibrated, the expected accuracy uses ±10 % drag instead of ±20 %.
  - **Refusals**: no engine able to deorbit, orbit too low, not enough Δv, parachutes that would never open in the model, a body rotation direction that cannot be measured. Plain Parachute mode stays available.
  - **Failures**: once the deorbit has started, a failure lands under parachutes without target ("Precision return aborted: …"). A deorbit that ends without entering the atmosphere stops the pilot ("Deorbit fell short …"); if the capsule is not the active vessel after the split, the pilot stops and says so.
  - The precision toggle is enabled on atmospheric bodies in Parachute mode only; Propulsive mode keeps it off.

- **VAB tools** (VAB AppBar → "SkySpaceAgency VAB tools"), a small window that only opens in the VAB. Files go to `mods/SkySpaceAgency/vab/`.
  - **Export** writes `vab/exports/<vessel>_<date>.json`, made to be handed to an AI:
    - a readable summary: per stage, Δv (vacuum, sea level, actual), TWR, Isp, thrust, masses and burn time; every part with its parent, stage, symmetry group, resources and modules; the resource totals;
    - instructions for the AI;
    - the game's own JSON of the rocket.
  - **Import** loads a file from `vab/imports/` (or `vab/exports/`) as the main rocket. It accepts a SkySpaceAgency export, the game's assembly JSON, or a VAB workspace file.
    - **Refusals**: before touching anything, it refuses a file with a part this VAB does not offer (the parts are listed), a broken structure, or JSON the game cannot read.
    - **Resources**: amounts above a part's capacity are lowered and reported.
    - **Backup**: the whole workspace is first saved to `vab/exports/before_import_….json`, which can be imported back. If loading fails, the previous rocket is restored: the workspace first, else the main rocket alone (detached assemblies may then be missing, the message says so).
    - The wait for the game to finish loading grows with the part count (10 s to 60 s).
  - **Dump parts** writes `vab/dumps/parts_<campaign>_<date>.json`: every part, unlocked or not, its tech node, characteristics, engine figures, raw definition, plus an example assembly and instructions for building a rocket from scratch.
  - **Open folder** opens `vab/` in the Explorer.

- **Landing modes**: on bodies with an atmosphere, the Land tab has a mode selector, **Propulsive** or **Parachute**. Airless bodies are always propulsive.
  - **Propulsive (legs)**:
    - Landing legs deploy on their own when Touch Down starts, or from Brake with "Deploy legs early". 3 s later every leg is re-read, and any leg still retracted is extended one by one.
    - Below 50 m the vessel flies upright, tilted at most 15° to cancel drift. Thrust is scaled by alignment instead of cut, and it regulates the fall speed, never below 0.5 m/s.
    - The landing ends on the game's landed or splashed state. SAS is then released to Stability Assist and the engines stay off.
  - **Parachute** (no engines, one "Land" button):
    - The vessel holds retrograde, arms every stowed parachute, and lets the game open them when it is safe. Once a canopy is open it releases SAS and deploys the legs if there are any.
    - It warns "Too fast under canopy" and "No parachute left".
    - It refuses to start with no parachute, or when the trajectory stays above the atmosphere ("deorbit first").
  - **Both modes**: refuse to start on a vessel that is already landed. The tab shows the state of the legs and how many parachutes are deployed or armed.

- **Orbit tab**: tick the targets to change (Ap, Pe, inclination; unticked ones keep the current value) and press "Create maneuvers" to put every node needed on the plan at once: orbit shape at the apsides first, then the plane change at the orbital node farther from the body. Run the nodes from the Node tab as usual; after a burn, pressing "Create maneuvers" again re-plans from the actual orbit. Refuses escape trajectories, unstable current orbits, and targets with Pe below the surface, inside the atmosphere or above the Ap. Running autopilots are stopped before the plan is replaced.

### Changed

- **Transfer and rendezvous search** (2026-10-24): the search no longer considers a wrong solution. The orbit solver behind it could report, between two points of nearly the same orbit with several revolutions, an orbit that missed the aim by up to 700 km; it now finds those orbits precisely or reports none.
- **Precision landing rewritten for bodies without atmosphere** (precision landing part 2, 2026-10-21; not yet flown in game).
  - Goal: stop within 100 m of the target for at most 20 % more Δv than a landing without target; over 20 % the landing is refused at start with the numbers.
  - The planner predicts the real stop point (braking and body rotation included, the longitude and rotation directions measured in game), searches the deorbit time, plans a plane change itself when the target is not under the orbit, solves a mid-course correction, brakes to a stop at the Touch Down altitude, then a terminal guidance brings the vessel down above the target. Δv per phase, predicted and final distances are shown in the Land tab.
  - Refuses a starting orbit that is too low for the terrain under it (raise it with the Orbit tab) or whose periapsis is above 100 km.
  - Once the vessel is on an impact course, a precision failure never turns the pilot off: the correction is skipped, or the pilot lands without target.
  - The old empirical rotation factor (×1.307) and the 36-sample deorbit scan are gone, with their Extend/Shorten/RCS fine-correction settings.

- **Precision landing safety** (2026-10-21, airless bodies).
  - Once the deorbit has lowered the orbit into the ground, a failed precision burn no longer stops the autopilot. A failed mid-course correction is skipped ("Course correction skipped: …") and the descent goes on with the brake and the terminal guidance. A deorbit burn that fails after lowering the orbit switches to a landing without target ("Precision landing aborted: …. Landing without target."). A burn that fails while the orbit still clears the ground stops as before.
  - Precision start refuses an orbit whose periapsis is below 10 km, or below the highest terrain under its ground track + 2 km ("Orbit too low for a safe precision approach …: raise it with the Orbit tab."). This also applies to an elliptical orbit before it is circularized.
  - Precision start refuses when the two measurements of the body's rotation direction (surface speed and longitude drift) disagree.
  - The plane-change burn is planned at least 130 s ahead (was 60 s), so the vessel has time to turn before it.
  - The precision brake goes to full throttle as soon as the simulation says it would stop below the hand-over height (before: only below 50 m).
  - The "Braking ends below the hand-over height" warning is also shown when the deorbit is re-planned; the last "Landed … m from the target" message is cleared when any landing starts.

- **Closed-loop burns turn early and manage time warp** (2026-10-21): circularization (Lift, Node tab), Orbit-tab plan steps and precision landing burns.
  - The vessel turns toward the burn direction from the start, also while warping. The warp stops instantly before the burn, early enough for the angle to turn at the vessel's measured turn rate (20 s to 120 s; 3°/s assumed until measured).
  - A vessel still more than 10° off at the planned start waits ("Late ignition") instead of burning off-centre; the closed-loop guidance corrects the orbit. If it still cannot align after max(60 s, twice the alignment lead), the burn stops with "Could not align with the burn direction: check SAS and reaction wheels."
  - During the burn the mod limits time warp so that a frame never applies more than 5 % of the remaining Δv and at least 10 s of real time remain, and drops to 1x when off-aim or under 10 s of burn left. With auto-warp on it also raises the warp during long burns; otherwise it only lowers the player's warp. The throttle accounts for the game time of a warped frame.
- **Mission waypoints in the Land tab** (2026-10-20). The precision-landing waypoint list now also shows every waypoint of the game on the current body, marked `[Mission]`, after the player's own waypoints. A mission that needs a precise point (the Mun's mysterious signal, for example) creates such a waypoint when it activates, so it appears in the list by itself. A player waypoint is listed once. Coordinates in the list use a decimal point on every Windows locale.
- The player's waypoints are read from the game's own assembly (`Ksp2Redux` since Redux 26w42) instead of through the `Assembly-CSharp` name.
- **Orbit tab plans run one step at a time** (2026-10-20). The game dropped the later nodes of a multi-node plan ("the game holds 1 node(s) for 2 planned"), so the Node tab could not run the whole plan.
  - "Create maneuvers" now creates the node of the first step only. The tab lists every step and shows which one is in progress.
  - After each step, the Node tab plans the next one again with the same targets, from the orbit actually reached and with the vessel's real mass. It then creates the next node, warps and burns. The whole plan still runs from a single Start, and a small error of one burn is corrected by the next.
  - The plan stops with a message when the target becomes unreachable, when the vessel leaves the body, after two correction steps beyond the plan, or when its node is edited by hand (the normal executor then flies that node). Stop interrupts it, and Start resumes with the node on the plan.
- **KSP2 Redux 26w42 compatibility** (2026-10-18). Redux 0.2.9 compiles the game into a single `Ksp2Redux` assembly, merges Space Warp and UITK for KSP2 into it, and removes Redux-Lib. The previous build referenced `ReduxLib`, `SpaceWarp2` and `SpaceWarp2.UI`, so it no longer loaded.
  - Template upgraded to 26w42c (SDK package `ksp2redux.sdk#26w42c`), which replaces the `8ed8010` pin.
  - Loggers now come from `SpaceWarp2.API.Logging.LogManager`.
  - Campaign features: the mod is off in campaigns that do not enable it. Alt-O and the autopilots now stay inactive there, as the AppBar button already does.
  - Fixed (2026-10-20): the window stayed invisible although its AppBar button showed it open. The mod is off in the main menu, where it used to load its UI, so the game refused the UXML and the window was created empty. The window is now created when a campaign that enables the mod loads, or on the first click or Alt-O.
- Project remixed as SkySpaceAgency, starting from IanMealworm's K2D2Redux 1.3.0 (commit `7ea025c`). README, `NOTICE.md` and `LICENSE.md` now credit the upstream port and state the remix's CC BY-SA 4.0 terms.
- The mod is now **SkySpaceAgency**: mod id `SkySpaceAgency`, assembly `SkySpaceAgency.dll`, window title, AppBar button, About page, Addressables groups and labels, log names and settings files (`skyspaceagency_*.json`). Toolbar button and window ids no longer clash with K2-D2. Settings from K2-D2 are not migrated. Internal C# namespaces and class names still use `K2D2`.
- Release size cut from ~92 MB to ~6.3 MB, based on LeoMarinDev's analysis in [PR #2 of IanMealworm/K2D2Redux](https://github.com/IanMealworm/K2D2Redux/pull/2):
  - `K2UI.uss`'s `.k2-status-line` now names JetBrains Mono instead of NotoSansMonoCJK. The CJK face was never shown (K2D2.uss already overrides it), but referencing it pulled its ~16 MB source font into the Addressables bundle (20.6 MB → 6.5 MB). From the PR.
  - `Caravan.asset` is no longer stamped into `k2d2_ui.bundle`. From the PR.
  - Removed `Copied/assets/bundles` (~65 MB): `k2d2_ui.bundle` and the twelve SDK plume / ray-tracing bundles built alongside it. The UI has been loaded through Addressables since 1.3.0 and nothing loads these bundles anymore. The editor tool that rebuilt them (`K2D2/Rebuild UI Bundle`) is removed with them.
- Fixed: `NullReferenceException` in `ValidScene()` from `Update`/`FixedUpdate`/`LateUpdate` during game startup, before the plugin is initialized and while the game state machine is not ready yet.
- The Redux SDK package is pinned to commit `8ed8010` instead of the `26w39a` tag: the `26w39a` SDK no longer compiles against KSP2 Redux 26w40a and later (`PQSRenderer.CreateColliders` was removed). To be replaced by the next template tag.
- Fixed (2026-10-16): circularization and Orbit-tab plans now fly closed-loop toward their target orbit.
  - **Cause of the falling trajectories:** the game models a node as a finite burn timed on the *currently active* stage. A nearly empty first stage gave a 5093 s estimate for 1122 m/s, and the executor followed the game's marker into the ground.
  - **Lift and the Node tab's Circularize at AP/PE buttons:** they no longer use a maneuver node. Every frame they burn toward the velocity of the target orbit, they keep working through staging, and they stop within 0.2 m/s.
  - **Orbit-tab nodes:** they remember their target orbit. One Start in the Node tab runs the whole plan, and a node you edit by hand falls back to the normal executor.
  - **Safety:** a burn that diverges is aborted with a message, and "No thrust — stage?" shows when no thrust arrives.
  - **Precision landing:** it refuses to circularize on an orbit that is not closed, instead of creating a broken NaN node.
  - **Log noise:** the map error logged after each node creation is gone.
- Superseded: Lift's auto-circularize node and the Node tab's "Circularize at AP/PE" buttons (and Landing's circularize phase, and the Orbit tab's nodes) produced a trajectory that still fell back to the ground.
  - **Cause:** the game treats a node's time as the *start* of the burn, and the node executor starts there too, but these nodes were placed *at* the apsis, so a long burn happened entirely after it.
  - **Fix:** nodes are now centered on the apsis, from the active stage's thrust, mass and Isp. A burn too long to center starts as soon as possible, and the log says so (`[ManeuverCreator] centered burn: …`).
  - **Node execution mode:** with "mid-duration", these nodes would now start half a burn too early. Use "T0", the default.
- Fixed: "Circularize at AP" just after the apoapsis on a suborbital arc aimed at the next apoapsis, which comes after the impact; the game then threw an error. It now refuses with a message.
- Fixed: the window could not be dragged up or down. The drag limits assumed the window is laid out from the screen's top-left corner, at the screen size UITK reports. They now use the window's actual layout position and the panel's real size, and the drag start logs that geometry.
- **Landing autopilot safety**:
  - **Refused starts**: Start (Brake or Touch Down) now refuses to start, with a message that stays in the tab, when:
    - no engine is active;
    - the local TWR is too low to stop safely (the plan counts on 85 % of the thrust);
    - even braking now would hit the ground;
    - the vessel has less than 110 % of the Δv the braking burn needs.

    When the game reports no Δv, the tab shows a warning instead of refusing.
  - **Braking start**: braking starts at the latest moment that still stops 50 m above the terrain. A simulation of the burn finds that moment, accounting for gravity, the approach angle, mass loss, body rotation and the terrain under the path. "Burn before" is now an extra margin on top.
  - **Descent speed**: the speed limit in Brake (precision) and Touch Down is now capped by what the active engines can stop. The altitude/speed profile still applies when it is slower.
  - **Engines**: only active engines (ignited, not shut down) count for landing. With no thrust the throttle goes to 0 instead of NaN.
  - **"Cannot stop before the ground!" alert**: shown when stopping is no longer possible: in Brake, according to the simulation; in Touch Down, when even full thrust is not enough. Touch Down then brakes at full throttle.
  - **Brake (non-precision)**: now ends on total surface speed instead of vertical speed. Before, a shallow approach could leave Brake before it burned at all.
  - **Failed phases**: a Circularize or deorbit phase that cannot do its job now stops the landing and shows why, instead of moving on to the next phase.
- **Orbit prediction on very eccentric orbits**: the two-body propagator used by the landing, Orbit and Node predictions could, in rare cases near periapsis, fail to converge and return a position far off (1.6e9 m in the case found). It now falls back to a bracketed solve; every case that converged before gives the same result.
- **Orbit prediction on long hyperbolic steps**: the same propagator could return a wrong or NaN position on an escape or arrival hyperbola predicted far ahead (for example 1e7 s after leaving low Kerbin orbit). It now starts from a hyperbolic guess and its fallback survives the overflow far out. Closed orbits that converged before give the same result.

## 1.3.0

### Added

- Node autopilot: two new buttons to create a maneuver node at the next apoapsis or at the next periapsis.
- Ascent autopilot can now create and fly its own circularization node at the end of ascent, instead of requiring one to be built by hand.
- Ascent autopilot can fly a specified roll program during the climb.
- Landing autopilot can now perform precision landings on bodies with no atmosphere, targeting a specific site via Redux's own waypoint system. (An atmospheric version of precision landing exists in code but is still a work in progress and not yet exposed to players.)

### Changed

- Every autopilot now deletes the maneuver node it executes once the burn finishes, instead of leaving it sitting on the plan.
- Precision landing settings for bodies with and without an atmosphere are now two fully separate autopilot profiles, so tuning one can't affect the other. (The atmospheric profile is still work in progress and not player-facing yet.)
- Node's "Rotate During Burn" option (Node tab, Experimental section) now defaults to on, so SAS stays locked to the maneuver direction for the whole burn instead of dropping to Stability Assist partway through. Since every burn in the mod shares the same execution code, this improves accuracy for Node, Lift's circularize, and Landing's burns, not just Node's own. The toggle is still there for anyone who wants the old fixed-orientation behavior back.
- All custom UI controls migrated from the legacy `UxmlFactory`/`UxmlTraits` pattern to the modern `UxmlElement` attribute system.
