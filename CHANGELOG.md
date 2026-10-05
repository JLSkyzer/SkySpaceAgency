# Changelog

## Unreleased (SkySpaceAgency)

### Added

- **Orbit tab**: tick the targets to change (Ap, Pe, inclination; unticked ones keep the current value) and press "Create maneuvers" to put every node needed on the plan at once: orbit shape at the apsides first, then the plane change at the orbital node farther from the body. Run the nodes from the Node tab as usual; after a burn, pressing "Create maneuvers" again re-plans from the actual orbit. Refuses escape trajectories, unstable current orbits, and targets with Pe below the surface, inside the atmosphere or above the Ap. Running autopilots are stopped before the plan is replaced.

### Changed

- Project remixed as SkySpaceAgency, starting from IanMealworm's K2D2Redux 1.3.0 (commit `7ea025c`). README, `NOTICE.md` and `LICENSE.md` now credit the upstream port and state the remix's CC BY-SA 4.0 terms.
- The mod is now **SkySpaceAgency**: mod id `SkySpaceAgency`, assembly `SkySpaceAgency.dll`, window title, AppBar button, About page, Addressables groups and labels, log names and settings files (`skyspaceagency_*.json`). Toolbar button and window ids no longer clash with K2-D2. Settings from K2-D2 are not migrated. Internal C# namespaces and class names still use `K2D2`.
- Release size cut from ~92 MB to ~6.3 MB, based on LeoMarinDev's analysis in [PR #2 of IanMealworm/K2D2Redux](https://github.com/IanMealworm/K2D2Redux/pull/2):
  - `K2UI.uss`'s `.k2-status-line` now names JetBrains Mono instead of NotoSansMonoCJK. The CJK face was never shown (K2D2.uss already overrides it), but referencing it pulled its ~16 MB source font into the Addressables bundle (20.6 MB → 6.5 MB). From the PR.
  - `Caravan.asset` is no longer stamped into `k2d2_ui.bundle`. From the PR.
  - Removed `Copied/assets/bundles` (~65 MB): `k2d2_ui.bundle` and the twelve SDK plume / ray-tracing bundles built alongside it. The UI has been loaded through Addressables since 1.3.0 and nothing loads these bundles anymore. The editor tool that rebuilt them (`K2D2/Rebuild UI Bundle`) is removed with them.
- Fixed: `NullReferenceException` in `ValidScene()` from `Update`/`FixedUpdate`/`LateUpdate` during game startup, before the plugin is initialized and while the game state machine is not ready yet.
- The Redux SDK package is pinned to commit `8ed8010` instead of the `26w39a` tag: the `26w39a` SDK no longer compiles against KSP2 Redux 26w40a and later (`PQSRenderer.CreateColliders` was removed). To be replaced by the next template tag.

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
