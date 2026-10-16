# Fix: closed-loop circularization (Lift + Node tab), NaN circularize nodes

## Root cause (verified 2026-10-16 from Ksp2-2.log and the saves)

### The game's maneuver model breaks on a depleted stage

The game models a maneuver as a finite burn, and its length depends on the stage that is active right now (`ManeuverNodeData.Time` = burn start, `BurnDuration`). At circularization after a Lift ascent, the active stage was still the nearly empty Mainsail stage (`EngineIgnited` true, `staged` true), while the Poodle upper stage was not staged yet. The save shows:

| Node | Δv | `BurnDuration` (game) | Our estimate |
|---|---|---|---|
| `40527c71` | 1122 m/s | **5093 s** | 29 s |

The game's predicted trajectory and its maneuver marker are therefore garbage. Our executors follow that model:
- `BurnManeuver` uses SAS `AutopilotMode.Maneuver`;
- its remaining Δv is `ManeuverPlanSolver.GetVelocityAfterFirstManeuver − current velocity` (`Pilots/Nodes/Controlers/BurnManeuvre.cs:80-151`).

So the vessel burns its full Δv in a direction that keeps it falling. Centering the node (commit `c5d0ecd`) cannot fix this.

### NaN circularize node on a hyperbolic approach

Landing's `Circularize`, at `04:15:26`, ran on a Mun approach with `apoapsis=-863543` (unbound). It produced `deltaV=NaN` and created a NaN node.

### Exception after every node creation

`NullReferenceException` in `KSP.Map.Map3DManeuvers.GetNodeDataForVessels`, raised from our `ManeuverCreator.UpdateMapGizmo_Co`.

## Fix

### 1. Pure guidance

Create `Assets/SkySpaceAgency/Code/Pilots/Orbit/CircularizeGuidance.cs` (namespace `K2D2.OrbitPlanning`, public static), with EditMode tests in `Assets/Tests/OrbitPlanner/CircularizeGuidanceTests.cs`. Write the tests first.

`DeltaVToCircular(Vector3d r, Vector3d v, double mu) : Vector3d` returns `v_circ · t̂ − v`, where:
- `v_circ = sqrt(mu/|r|)`;
- `t̂` is the normalized horizontal component of `v`: `v − (v·r̂) r̂`.

It returns `NaN` components when `|r|` ≤ 0. When the horizontal speed is below 1e-6, it returns `(0,0,0)`: no defined direction, so the caller treats the burn as done or refused.

`Split(Vector3d dv, Vector3d r, Vector3d v, out double radial, out double horizontal)` gives the components along `r̂` and along `t̂`.

**Tests:**
- Circular orbit: about 0 Δv.
- At the apoapsis of a 100 × −430 km (suborbital) Kerbin ellipse: purely horizontal, equal to the vis-viva Δv (compare with `VisVivaEquation`).
- Descending past the apoapsis: the radial component is positive, which kills the downward speed.
- Ascending before the apoapsis: the radial component is negative.

### 2. Closed-loop controller

Create `Assets/SkySpaceAgency/Code/Pilots/Nodes/Controlers/CircularizeBurn.cs`, an `ExecuteController`. It needs no maneuver node.

**Start(atApoapsis):**
- Remove every node of the active vessel, with the existing `ManeuverCreator.RemoveAllNodes`.
- Read the state from the orbit, `GetRelativePositionAtUTZup` / `GetOrbitalVelocityAtUTZup`, as `NodeExPilot.CreateCircularizeNode` does.
- Find the time to the apsis with `LandingTargeting.TimeToNextApoapsis` / `TimeToNextPeriapsis`.
- Propagate to the apsis with `KeplerPropagator`. The Δv needed there is `|DeltaVToCircular(r_apsis, v_apsis)|`.

**Refuse with `Fail(message)`, in English like the rest of the UI:**
- Unbound orbit at the apoapsis (`sma ≤ 0` or NaN): `"No apoapsis: the trajectory doesn't come back."`
- AP requested while already descending with the periapsis below `radius + atmosphereDepth`: `"Already past apoapsis and falling: the next apoapsis comes after impact."` Keep the existing Node tab message.
- No thrust at all (`active` and `full` both 0): `"No engine to circularize with."`

**Waiting:**
- Keep `T = BurnTiming.Duration(|Δv|, thrust, mass, isp)` up to date every 0.5 s. Thrust is the active thrust, falling back to the full thrust when it is 0; mass and Isp come from a `BurndV`. Use the time-to-apsis computed live.
- Warp with the existing `WarpTo` mechanics or `TimeWarpTools` until `t_apsis − T/2 − 20 s`, then stop warping.
- Align during the last 20 s.
- Start burning when `t_apsis − T/2 ≤ now`. If that moment is already past, start immediately.

**Burning, every Update:**
1. `dv = DeltaVToCircular(r, v, mu)`, from the current state.
2. `Split` it into `radial` and `horizontal`.
3. Build the aim in the game frame from `SASTool.getTelemetry()`: `up = HorizonUp`, and `fwd` = the horizontal part of `OrbitMovementPrograde`, reframed into `HorizonUp.coordinateSystem` and normalized. Then `aim = normalize(up·radial + fwd·horizontal)`.
4. Apply it with SAS StabilityAssist and `SAS.SetTargetOrientation(new Vector(HorizonUp.coordinateSystem, aim), false)`, the same pattern as `TouchDown.checkDirection`.
5. Throttle:
   - 0 while the angle between the vessel's forward and `aim` is above 10°;
   - otherwise `clamp(|dv| / (accel · 1.0 s), 0.05, 1)`, with `accel = thrust/mass` (active, else full).
   - If `accel` is 0, throttle 0. Auto-staging keeps running independently.
6. Stop with `finished = true` and throttle 0 in either case:
   - when `|dv| < 1.0 m/s`;
   - when `|dv|` has risen for 10 consecutive updates after dropping below 5 m/s (overshoot).
7. Status line `Circularizing: {|dv|:n1} m/s left`.
8. Log at start and finish: `[Circularize] start/finish: ap {..} km pe {..} km, dv {..} m/s, T {..} s`.

Do not use `ManeuverPlanSolver`, any node, or SAS `Maneuver` mode anywhere in this controller.

### 3. Lift

In `Assets/SkySpaceAgency/Code/Pilots/Lift/Controlers/Final.cs`, replace `FinalCircularize`'s create-node + `TurnTo`/`WarpTo`/`BurnManeuver` sequence with `CircularizeBurn.Start(atApoapsis: true)`. Keep the existing "finished" semantics toward `LiftPilot`, and keep its logs. Read the file before editing, and keep the other phases intact.

### 4. Node tab buttons

"Circularize at AP" and "Circularize at PE" (`NodeExPilot.CreateCircularizeNode`, wired in `NodeExUI`) no longer create a node. They start `NodeExPilot` in a dedicated circularize run that delegates to a `CircularizeBurn` (AP or PE).
- `isRunning = true`, so the Start/Stop button reflects it and Stop aborts it with throttle 0.
- A refusal (`Fail`) shows its message through the existing `circularize_error` display, and the pilot stops.
- The normal node-execution path stays unchanged for user-made nodes.

### 5. Landing `Circularize` (precision precondition)

In `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/Circularize.cs`:
- When `CheckOrbit` / `OrbitalElementsFromStateVectors` gives an unbound orbit (`sma ≤ 0`, NaN, or negative apoapsis radius), `Fail("Orbit not closed: capture into orbit first.")`.
- Never create a node with a NaN Δv.

Defense in depth: `ManeuverCreator.RemoveAllNodesThenCreate` and `CreateNodes` must refuse a NaN or infinite Δv. They log `[ManeuverCreator] refused node with invalid Δv` and invoke `onCreated(null)` / skip, without calling the game.

### 6. Map gizmo exception

In `ManeuverCreator.UpdateMapGizmo_Co`, wrap the `Map3DManeuvers` call in try/catch and log once per session with `LogWarning`. Read the method first, and keep its behaviour when it works.

## Out of scope

- The Orbit tab's nodes and the generic Node executor for user-made nodes (unchanged).
- Landing DeorbitBurn / MCC.

## Verification

- EditMode green: 95 + the new guidance tests.
- The controller builds, installs, writes the CHANGELOG entry, then commits and pushes.
- **In game:**
  - Lift to orbit with auto-circularize, on a rocket whose first stage is nearly empty at apoapsis: it stages and ends in a closed orbit.
  - Node tab "Circularize at AP" on a suborbital arc before the apoapsis: closed orbit.
  - The same button after the apoapsis: refusal.
  - Read the `[Circularize]` log lines.
