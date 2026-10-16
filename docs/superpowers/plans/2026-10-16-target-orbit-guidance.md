# Target-orbit guidance: precise Orbit-tab plans executed by the Node tab, Lift past-apoapsis fix

## Problem (user report, 2026-10-16)

- **The Orbit tab is imprecise.** A request for a 100 × 100 km orbit ended at 185 × 105 km.
- **Multi-node plans need hand-holding.** Orbit-tab plans must be executable by our Node tab, several consecutive nodes included, without the player re-starting each one.
- **Same root cause as the circularize bug** (`docs/superpowers/plans/2026-10-16-closed-loop-circularize.md`). The Node executor (`BurnManeuver`) steers with SAS `Maneuver` mode and measures the remaining Δv through the game's `ManeuverPlanSolver`. The game models the burn as finite, with a duration based on the currently active stage, which can be wildly wrong (5093 s for 1122 m/s). The executed burn therefore misses the target orbit.
- **Lift regression from the previous fix.** If Lift reaches its final circularization already past the apoapsis and falling, `CircularizeBurn` refuses and stops. For Lift it must burn immediately instead: the closed-loop guidance also cancels the vertical speed.

## Fix

### 1. Pure guidance

Generalize `Pilots/Orbit/CircularizeGuidance.cs`. Either add `OrbitGuidance.cs` and make circularization a call to it, or extend the existing file; avoid duplicate math. Write the tests first, in `Assets/Tests/OrbitPlanner/`.

`DeltaVToOrbit(Vector3d r, Vector3d v, double mu, double ra, double rp, Vector3d planeNormal) : Vector3d` returns the velocity change that puts the vessel, from where it is now, on the orbit with apsis radii `ra ≥ rp` in the plane of unit normal `planeNormal`. The math:
- `a = (ra + rp)/2`, `p = 2·ra·rp/(ra + rp)`, `h = sqrt(mu·p)`;
- `v² = mu·(2/|r| − 1/a)`, `v_t = h/|r|`, `v_r = sign · sqrt(max(0, v² − v_t²))`;
- `sign` is the sign of the current radial speed `v·r̂`. When `|v·r̂|` is below 1 m/s, use `+1` if `|r|` is closer to `rp` than to `ra`, otherwise `−1`;
- `t̂ = normalize(planeNormal × r̂)`. If `t̂` points against the current horizontal velocity (dot < 0), use `−planeNormal`, so the orbit keeps its direction of motion;
- `target = v_r·r̂ + v_t·t̂`, and the result is `target − v`.

**Reachability.** When `|r|` is outside `[rp, ra]` (beyond a small tolerance of 0.1 % or 100 m), the target orbit does not pass through the current radius. Clamp the target radius into the interval for `v_t`/`v_r` (`v_r` = 0 at the bound): this is the best effort, and the caller burns at the right place anyway.

Circularize becomes `DeltaVToOrbit(r, v, mu, |r|, |r|, ĥ_current)`. Keep the existing circularize tests passing.

**Tests:**
- Already on the target orbit: about 0 Δv.
- Circular 100 km to Ap 500 km: at the periapsis, Δv equals the Hohmann first burn (vis-viva).
- Plane change 0° → 30° at the node of a circular orbit: `|Δv| ≈ 2·v·sin(15°)`.
- Circular target from the apoapsis of an ellipse equals the vis-viva circularization.
- Past the apoapsis while descending: the radial component cancels the downward speed.

### 2. Controller

Generalize `Pilots/Nodes/Controlers/CircularizeBurn.cs` into `TargetOrbitBurn`, or add `TargetOrbitBurn` and make `CircularizeBurn` a thin wrapper.

- **Inputs:** a target `(ra, rp, planeNormal)` and a start UT.
- **Before the start time:** wait, warping per the existing rules, and align during the last 20 s.
- **From the start time:** burn every Update toward `DeltaVToOrbit`, with the same aim, throttle and stop rules as `CircularizeBurn`:
  - aim through the `HorizonUp` frame and SAS StabilityAssist with `SetTargetOrientation`;
  - throttle 0 while more than 10° off, otherwise `clamp(|dv|/(accel·1 s), 0.05, 1)`;
  - stop at `|dv| < 1 m/s`, or on overshoot.

  For a target with a normal component, the aim must include it. Express the guidance Δv in three components (radial, along-track horizontal, normal), and build the game-frame aim from `HorizonUp`, the horizontal prograde direction and their cross product. Verify the normal sign against `ManeuverCreator.GameNormalSign`'s convention, and say in the report how you checked it.
- **Log** at start and finish: target, current orbit, `|dv|`.
- **Lift:** circularization never refuses for "past apoapsis". It starts immediately (the guidance handles it). The Node tab's AP-button refusal stays as it is.

### 3. Node targets registry

Add a small registry in `KSPService` (for example `NodeTargets`) that maps a maneuver `NodeID` to its target orbit `(ra, rp, planeNormal)`, in memory only. Cleanup:
- clear an entry when its node is executed or removed;
- clear the whole registry on vessel change.

### 4. Orbit tab registration

For each planned burn, compute the orbit **after** that burn from the planner's simulated post-burn state: `ra`, `rp` and the unit plane normal `r × v`. `OrbitPlanner` already propagates and applies burns; expose the post-burn state or elements in `PlannedBurn`. Register them under the created node's `NodeID` in `ManeuverCreator.CreateNodes_Co`, or in `OrbitUI` through the callback.

### 5. Node executor

In `NodeExPilot`, when the node about to be executed has a registered target, the Burn phase uses `TargetOrbitBurn`:
- the start time is the node `Time`, which is already centered;
- the direction and the stop come from the guidance;
- not `BurnManeuver`, not SAS `Maneuver` mode.

Unregistered user-made nodes keep the existing path unchanged.

After a registered burn finishes:
- delete its node, as today;
- if the vessel's plan still holds nodes, continue automatically with the next one, so a multi-node Orbit plan runs end to end from a single Start;
- the user's Stop still aborts at any time with throttle 0.

Check how `NodeExPilot` handles the next node today (`GetNodesForVessel(...)[0]`) and reuse it.

## Out of scope

User-made nodes (still `BurnManeuver`), Landing DeorbitBurn and MCC, and precision landing.

## Verification

- EditMode: green, existing tests plus the new ones.
- The controller builds, installs, writes the CHANGELOG entry, then commits and pushes.
- **In game:**
  - From a 100 × 100 km Kerbin orbit, the Orbit tab targets Ap 300 and Pe 300, the Node tab Start runs both nodes, and the result is within about 1 % of the targets.
  - Inclination 0° → 10°: the result is within about 0.1°.
  - Lift reaching circularization past the apoapsis still ends in orbit.
