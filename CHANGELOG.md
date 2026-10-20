# Changelog

## Unreleased (SkySpaceAgency)

### Added

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

- **Mission waypoints in the Land tab** (2026-10-20). The precision-landing waypoint list now also shows every waypoint of the game on the current body, marked `[Mission]`, after the player's own waypoints. A mission that needs a precise point (the Mun's mysterious signal, for example) creates such a waypoint when it activates, so it appears in the list by itself. A player waypoint is listed once. Coordinates in the list use a decimal point on every Windows locale.
- Fixed: the player's own waypoints no longer appeared in that list since Redux 26w42, which moved the game's code from `Assembly-CSharp` to `Ksp2Redux`.
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
