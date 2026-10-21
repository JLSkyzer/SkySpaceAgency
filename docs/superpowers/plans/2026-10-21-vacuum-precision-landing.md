# Vacuum precision landing (precision landing part 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On a body without atmosphere, land within 100 m of the chosen target for at most 20 % more Δv than a landing without a target, from any closed orbit up to 100 km, with an automatic plane change when the target is not under the orbit.

**Architecture:** Pure classes in `Pilots/Landing/Precision/` (namespace `K2D2.Landing.Precision`, only `KSP.Sim.Vector3d`, `KeplerPropagator` and the existing pure `Braking` classes) do all the maths and are tested in EditMode, including an end-to-end Mun landing. They predict the real stop point (braking included, body rotation included), search the deorbit time, plan the plane change, solve the mid-course correction, guide the final approach and check the Δv budget. A thin game-facing `PrecisionContext` feeds them from the game, and `LandingPilot` runs a new precision sequence whose burns are flown by the closed-loop `TargetOrbitBurn`.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c (`Ksp2Redux.dll`).

**Spec:** `docs/superpowers/specs/2026-10-21-precision-landing-design.md` (part 2).

## Global Constraints

- Game KSP2 Redux 0.2.9 / 26w42c; logger `K2D2_Plugin.logger`; UI and logs in English; log prefix `[Landing] precision:`.
- Success: stop within **100 m** of the target. Budget: precision Δv ≤ **1.20 ×** the baseline. The baseline is the Δv a landing without target needs for the same vessel, from the same circularized orbit in the same plane (deorbit at the natural point, then braking), predicted by the same simulation at start. The orbit preparation and the plane change are shown separately and are not counted in the 20 %.
- Start: any closed orbit. If it is not circular within 1500 m, the pilot circularizes it at its periapsis first; a periapsis above 100 km is refused with `Periapsis above 100 km: lower the orbit first (Orbit tab).` (ruling: one-burn preparation only).
- Plane change: done by the pilot itself, immediately (no waiting for the body's rotation), when the cross-track miss of the best deorbit exceeds `PlaneChange.Threshold = 1000 m`. Its Δv cost is shown before start.
- Safety first: the sub-project 1 checks (`LandingFeasibility`, `DescentEnvelope.CanStop`, "Cannot stop before the ground!") keep priority over the target. If the target cannot be reached safely (Δv, TWR, terrain) or the budget is exceeded, the pilot says so and lands as close as it safely can.
- No GPL code (no resonance/phasing port from Flight Plan). The `1.307` empirical factor and the 36-sample `FindBestDeorbitBurn` scan are not used by the new precision path; remove them when nothing else calls them.
- Pure code lives in `Assets/SkySpaceAgency/Code/Pilots/Landing/Precision/` and is tested in `Assets/Tests/Landing` (asmdef already references `Ksp2Redux.dll`). Test constants for the Mun: `Mu = 6.5138398e10`, `R = 200000`, rotation period `138984.38 s`. All existing tests stay green (207 on 2026-10-21).
- Frame: body-relative inertial Zup (`GetRelativePositionAtUTZup`), z = rotation axis, as `BrakeSimulator` uses. Body rotation ω = `BodyRotation.AngularVelocity(AngularSpeed(period), sign)` with the runtime sign of `LandingPilot.rotation_sign`.
- Test command and commit rules: as in `docs/superpowers/plans/2026-10-21-landing-target-picker.md` (Global Constraints), test count updated.

## Review Focus

1. A target behind the vessel or on the far side of the body: the deorbit search must still find a solution within one orbit, or refuse clearly (Task 3 test with targets at 0°, 90°, 180°, 270° ahead).
2. A target near a pole or at the anti-meridian (lon ±180): no wrap error in distances and along/cross-track splits (Task 1 tests at lat 89°, lon 179.9 vs −179.9).
3. Low TWR or little Δv: terminal guidance must give up the target before it gives up the safe stop (Task 6 test with an unreachable offset → stops safely, reports the residual distance).
4. Rotation sign or longitude sign wrong in game: the plan logs the calibration and the predicted miss at each phase, so a wrong sign shows as a large predicted-vs-actual gap; the longitude sign is measured, never assumed (Task 8).
5. Terrain higher than the target or a target on a slope: the stop altitude is checked against terrain at the target (Task 2 uses the terrain function at the stop point) and a slope over 15° is reported without moving the target (Task 8, from two terrain samples 50 m apart).

---

### Task 1: `SurfaceGeometry`

**Files:** Create `Precision/SurfaceGeometry.cs`; Test `Assets/Tests/Landing/SurfaceGeometryTests.cs`.

**Interfaces — Produces** (`static class SurfaceGeometry`):
- `struct Reference { Vector3d Position; double Latitude; double Longitude; int LonSign; }` — a known point (the vessel now) with its game latitude/longitude in degrees, and the measured sign of longitude about +z (+1: longitude grows with a positive rotation about z).
- `static Vector3d Direction(Reference reference, double latDeg, double lonDeg)` — unit inertial vector, at the reference time, of a surface point.
- `static void LatLon(Reference reference, Vector3d position, out double latDeg, out double lonDeg)` — inverse, longitude wrapped to [−180, 180).
- `static Vector3d AtTime(Vector3d positionNow, Vector3d omega, double dt)` — rotates a body-fixed point by ω·dt about z (where it is dt seconds later).
- `static double GreatCircle(Vector3d a, Vector3d b, double radius)` — metres between two directions.
- `static void Split(Vector3d from, Vector3d to, Vector3d groundTrackDir, double radius, out double alongM, out double crossM)` — signed along-track (positive: `to` is ahead) and cross-track (positive: to the left of the track, i.e. along `from × groundTrackDir`) components of the miss, in metres.

- [ ] **Step 1: Tests** — round trip `LatLon(Direction(lat, lon))` within 1e-9° for (0,0), (45, 120), (−30, −170), (89, 10); `LonSign = −1` mirrors longitudes; `AtTime` by a quarter period moves a point 90° about z; `GreatCircle` of antipodes = π·R; `Split` on the equator with an eastward track: a point 1 km east → along ≈ +1000, cross ≈ 0; a point 1 km north → cross ≈ +1000 when the track is eastward and +z is north; lon 179.9 vs −179.9 → ≈ 0.2° apart, not 359.8°.
- [ ] **Step 2: Run tests — expect `error CS`**
- [ ] **Step 3: Implement** (reference latitude/longitude fix the body-fixed axes: rotate the reference direction about z by `LonSign·(lon − lonRef)` and set the latitude from the equatorial plane).
- [ ] **Step 4: Run tests — all green**
- [ ] **Step 5: Commit** — `Add surface geometry for precision landing`

### Task 2: `StopPointPredictor`

**Files:** Create `Precision/StopPointPredictor.cs`; Test `Assets/Tests/Landing/StopPointPredictorTests.cs`.

**Interfaces:**
- Consumes: `BrakeSimulator.FindStart/Simulate`, `BrakeInput`, `BrakeResult`, Task 1.
- Produces:
  - `struct Vehicle { double ThrustKN; double MassT; double Isp; }`, `struct Body { double Mu; double Radius; Vector3d Omega; Func<Vector3d,double> TerrainHeight; }` (terrain height above `Radius` for a position in the body's orientation at the prediction's `UT`, as `BrakeInput.TerrainHeight`).
  - `struct Impulse { double UT; Vector3d DeltaV; }` (optional burn applied before coasting).
  - `struct StopPrediction { bool Ok; string Reason; double StopUT; Vector3d StopDirNow; double StopAltitude; double BrakeDeltaV; double BrakeStartUT; }` — `StopDirNow` is the stop point as a body-fixed direction expressed at the prediction's UT (rotated back by −ω·(StopUT − UT)), comparable with `SurfaceGeometry.Direction`.
  - `static StopPrediction Predict(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Impulse? impulse)` — coast (with the impulse), find the impact on terrain, then `BrakeSimulator.FindStart` and `Simulate` from that start.

- [ ] **Step 1: Tests** — non-rotating Mun, flat terrain, a retrograde-deorbited state: the stop point lies before the ballistic impact point by roughly the braking distance (> 1 km for a 30 km orbit at TWR 3) and `BrakeDeltaV` is within 10 % of the orbital speed at impact; same case with rotation: `StopDirNow` differs from the no-rotation result by ≈ ω·(StopUT − UT)·R along the equator (within 5 %); no thrust → `Ok == false` with a reason; an escape trajectory → `Ok == false`.
- [ ] **Step 2–4: red, implement, green**
- [ ] **Step 5: Commit** — `Predict the real stop point of a powered descent`

### Task 3: `DeorbitSearch`

**Files:** Create `Precision/DeorbitSearch.cs`; Test `Assets/Tests/Landing/DeorbitSearchTests.cs`.

**Interfaces:**
- Consumes: Tasks 1–2, `OrbitMath`, `KeplerPropagator`.
- Produces:
  - `const double DescentPeriapsisMargin = 2000` (descent periapsis = body radius − 2000 m, as today; the builder may tune it with a test showing why).
  - `struct DeorbitPlan { bool Ok; string Reason; double BurnUT; Vector3d DeltaV; StopPrediction Stop; double AlongMissM; double CrossMissM; }`
  - `static DeorbitPlan Find(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow, double earliestUT)` — retrograde burn lowering the periapsis to `R − DescentPeriapsisMargin`, at the time within one orbit after `earliestUT` that zeroes the along-track miss (bracket the sign change of `AlongMissM` over the orbit, then bisection/secant to < 10 m); `CrossMissM` reported, not corrected.
  - `static DeorbitPlan Baseline(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, double earliestUT)` — same burn at `earliestUT` (no target), for the budget.

- [ ] **Step 1: Tests** — rotating Mun, 30 km circular equatorial orbit, target on the equator at 0°, 90°, 180°, 270° ahead of the vessel: `Ok`, `|AlongMissM| < 50`, `|CrossMissM| < 1000`; target at latitude 20° from an equatorial orbit: `Ok` with `|CrossMissM| > 1000` (plane change needed); `Baseline` returns `Ok` with a Δv equal to `Find`'s within 1 % (same burn size).
- [ ] **Step 2–4: red, implement, green**
- [ ] **Step 5: Commit** — `Search the deorbit time that puts the stop point on the target`

### Task 4: `PlaneChange`

**Files:** Create `Precision/PlaneChange.cs`; Test `Assets/Tests/Landing/PlaneChangeTests.cs`.

**Interfaces:**
- Consumes: Tasks 1–3, `OrbitMath`, `OrbitGuidance.TargetOrbit`.
- Produces:
  - `const double Threshold = 1000` (m).
  - `struct PlaneChangePlan { bool Needed; bool Ok; string Reason; double BurnUT; TargetOrbit Target; double DeltaV; }`
  - `static PlaneChangePlan Plan(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow)` — `Needed` when `DeorbitSearch.Find`'s cross-track miss exceeds `Threshold`. The new plane keeps the orbit's shape and passes over the target at the time the vessel will be over it after the deorbit (iterate: estimate the arrival time, place the target there with `SurfaceGeometry.AtTime`, choose the plane containing the burn point on the current orbit and that target position, recompute — stop when the arrival time moves < 1 s). `BurnUT` is the next intersection of the two planes (at least 60 s ahead); `DeltaV` its magnitude.

- [ ] **Step 1: Tests** — rotating Mun, equatorial 30 km orbit, target at latitude 20°: after applying the plan's burn (rotate the velocity into the new plane at `BurnUT`), `DeorbitSearch.Find` gives `|CrossMissM| < 300`; target already under the track → `Needed == false`; a target at latitude 85° from an equatorial orbit → `Ok` and `DeltaV` close to `2·v·sin(Δi/2)` within 5 %.
- [ ] **Step 2–4: red, implement, green**
- [ ] **Step 5: Commit** — `Plan the plane change that puts the target under the ground track`

### Task 5: `CourseCorrection`

**Files:** Create `Precision/CourseCorrection.cs`; Test `Assets/Tests/Landing/CourseCorrectionTests.cs`.

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: `struct Correction { bool Ok; Vector3d DeltaV; double MissBeforeM; double MissAfterM; }`; `static Correction Solve(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow, double maxDeltaV)` — impulse now, in the plane spanned by prograde and orbit normal, that zeroes along- and cross-track misses of the predicted stop point (2-D Newton with finite-difference Jacobian, ≤ 6 iterations); clipped to `maxDeltaV`.
- [ ] **Step 1: Tests** — a descent aimed 2 km long and 500 m to the side: `MissAfterM < 30`, `|DeltaV| < 20 m/s`; with `maxDeltaV = 1` → clipped, `MissAfterM < MissBeforeM`; already on target → `|DeltaV| < 0.05`.
- [ ] **Step 2–4: red, implement, green**
- [ ] **Step 5: Commit** — `Solve the mid-course correction on the predicted stop point`

### Task 6: `TerminalGuidance`

**Files:** Create `Precision/TerminalGuidance.cs`; Test `Assets/Tests/Landing/TerminalGuidanceTests.cs` (with a small point-mass simulator in the test file).

**Interfaces:**
- Consumes: `DescentEnvelope`, `FinalDescent` constants.
- Produces:
  - `struct Command { Vector3d Accel; bool TargetDropped; string Reason; }` — commanded acceleration (thrust part only) in the local frame (x east, y north, z up).
  - `static Command Compute(Vector3d offset, Vector3d velocity, double gravity, double maxAccel, double touchDownSpeed, double maxTiltDeg)` — `offset` = vessel minus target in the local frame; ZEM/ZEV-style law with time-to-go from the vertical channel, gravity compensated, magnitude ≤ `maxAccel`, tilt from vertical ≤ `maxTiltDeg` (default 45). When `DescentEnvelope.CanStop` would fail with the lateral demand, the vertical channel takes priority and `TargetDropped = true` with a reason; the vessel then stops safely where it is.
- [ ] **Step 1: Tests** — from 1500 m up, 800 m lateral offset, 40 m/s horizontal and −60 m/s vertical speed, Mun gravity 1.63, max accel 6: simulated landing ends within 20 m of the target with vertical speed ≤ touch-down speed + 0.5; from 300 m up with a 3 km offset and max accel 3: `TargetDropped` becomes true and the vessel still lands with vertical speed ≤ touch-down speed + 0.5; tilt never exceeds `maxTiltDeg`.
- [ ] **Step 2–4: red, implement, green**
- [ ] **Step 5: Commit** — `Add terminal guidance toward the landing target`

### Task 7: `PrecisionBudget` and the end-to-end Mun test

**Files:** Create `Precision/PrecisionBudget.cs`; Test `Assets/Tests/Landing/PrecisionBudgetTests.cs`, `Assets/Tests/Landing/PrecisionEndToEndTests.cs`.

**Interfaces:**
- Produces: `const double MaxRatio = 1.20`; `struct Budget { double BaselineDeltaV; double PrecisionDeltaV; double PreparationDeltaV; double PlaneChangeDeltaV; bool WithinLimit; string Summary; }`; `static Budget Evaluate(DeorbitPlan baseline, DeorbitPlan precision, double correctionReserve, double terminalReserve, double preparationDeltaV, double planeChangeDeltaV)`. `Summary` format: `Δv: landing {p:0} m/s (no target {b:0}, +{pct:0} %), plane change {pc:0} m/s, orbit preparation {op:0} m/s`.
- [ ] **Step 1: Tests** — `WithinLimit` true at ratio 1.19 and false at 1.21; `Summary` text for fixed numbers.
- [ ] **Step 2: End-to-end test** — rotating Mun, 30 km circular orbit inclined 0°, target at latitude 8° on the far side, vehicle TWR 3 (Mun), Isp 320: plane change (Task 4) → deorbit (Task 3, impulsive) → coast and finite brake (Task 2's simulation) → one mid-course correction halfway (Task 5) → terminal guidance from 1500 m (Task 6, point-mass) → final distance < 100 m and precision Δv ≤ 1.20 × baseline (both excluding plane change).
- [ ] **Step 3–4: red, implement, green** (fix the earlier tasks if the end-to-end test fails; report what changed)
- [ ] **Step 5: Commit** — `Check the precision Δv budget and land a simulated Mun descent within 100 m`

### Task 8: `PrecisionContext` (game-facing inputs)

**Files:** Create `Assets/SkySpaceAgency/Code/Pilots/Landing/PrecisionContext.cs`.

**Interfaces — Produces:** `class PrecisionContext` built by `static bool TryBuild(LandingPilot pilot, out PrecisionContext ctx, out string reason)` with: `Vector3d R, V; double UT; StopPointPredictor.Body Body; StopPointPredictor.Vehicle Vehicle; SurfaceGeometry.Reference Reference; Vector3d TargetDirNow; double TargetSlopeDeg`.
- Position/velocity from `orbit.GetRelativePositionAtUTZup/GetOrbitalVelocityAtUTZup(now)`; ω from `BodyRotation` with `pilot.rotation_sign`; terrain from the pilot's existing `TerrainHeightAt`; vehicle from `burn_dV` (`active_thrust`, falling back to `full_thrust`, `mass`, `active_isp`).
- `Reference` from the vessel's position and its game latitude/longitude (`GetLatLonAltFromRadius`). **Longitude sign measured**: compare the game longitude of the vessel position with that of the same position rotated by +0.01 rad about z; log once `[Landing] precision: longitude sign {s} (lon {a:0.0000} -> {b:0.0000})`.
- Target from the active profile's `target_latitude/target_longitude`. Slope from two terrain samples 50 m apart around the target.
- No unit test; compile check.
- [ ] **Step 1: Write**; **Step 2: test command — 0 `error CS`, all green**; **Step 3: Commit** — `Build precision landing inputs from the game`

### Task 9: `LandingPilot` precision sequence and Land tab readouts

**Files:** Modify `Pilots/Landing/LandingPilot.cs`, `Pilots/Landing/LandingUI.cs`, `Assets/UI/K2D2_UI/Landing.uxml`; possibly new `Pilots/Landing/Controlers/PrecisionBurn.cs` (a `TargetOrbitBurn` use for the deorbit and correction burns); remove the dead parts of `LandingTargeting.cs` (`FindBestDeorbitBurn`, `PredictImpactLongitude` and the `1.307` factor) if nothing calls them any more.

**Behaviour:**
- **Start** (precision on, vacuum body): `PrecisionContext.TryBuild`; refuse when the periapsis is above 100 km (exact text in Global Constraints); compute baseline, plane-change plan, deorbit plan and `PrecisionBudget`; show the summary in the tab before flying; refuse with the reason when a plan is not `Ok` or the existing `LandingFeasibility` refuses for the whole Δv (preparation + plane change + landing).
- **Sequence**: OrbitPrep (only if not circular within 1500 m: `TargetOrbitBurn` to the circular orbit at the periapsis radius, started at the periapsis) → PlaneChange (only if `Needed`: `TargetOrbitBurn` to `PlaneChangePlan.Target` at `BurnUT`) → Deorbit (re-plan with `DeorbitSearch.Find` from the actual orbit, fly the burn closed-loop toward the orbit `TargetOrbit.FromState(r_burn, v_burn + DeltaV)`) → MidCourse (halfway to the brake start: `CourseCorrection.Solve`, flown the same way if `|ΔV| ≥ 0.05`) → existing Waiting / warps → Brake (existing retrograde braking from the simulated start, so the prediction stays valid) → TouchDown below `start_touchdown_altitude` with `TerminalGuidance` steering (replacing `ComputeSteeredDirection` when precision is on) → existing final descent below 50 m.
- **Dropping the target**: when `TerminalGuidance` reports `TargetDropped`, or the remaining Δv cannot cover the guidance plus a safe stop, keep landing safely and show `Target out of reach: landing as close as possible ({d:0} m away).`
- **Readouts**: phase, predicted distance to the target (from `StopPointPredictor`, refreshed every 2 s outside burns), the budget summary, and the final distance after landing (`Landed {d:0} m from the target.`). Slope over 15° at the target: warning `Target on a {s:0}° slope.`
- **Logs** (`[Landing] precision:`): calibration, each plan (UT, Δv, predicted misses), each phase start/end with the predicted miss, the actual Δv used per phase, the final distance.
- Keep non-precision landing and the atmospheric profile unchanged.
- [ ] **Step 1: Implement**; **Step 2: test command — 0 `error CS`, all green**; **Step 3: Commit** — `Fly vacuum precision landings with the new planner and terminal guidance`

### Task 10: Build, install, document (controller)

- [ ] Build, install (keep settings files and `vab/`), CHANGELOG (Changed: precision landing rewritten — 100 m goal, plane change, budget, readouts), README Known limitations, parent CLAUDE.md bullet (what is not verified in game: longitude/rotation signs, terrain at the target, closed-loop burns of the descent, terminal guidance with the real SAS response, the 20 % in practice), in-game checklist (spec part 2 tests), commit and push submodule and parent.
