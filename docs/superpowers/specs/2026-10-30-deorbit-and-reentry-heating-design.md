# Deorbit, Δv refusal and reentry heating for the Land tab (design)

Date: 2026-10-30. Asked in game the same day: pressing **Brake** in orbit did nothing ("None detected"), with or
without precision landing. Approved choices: a deorbit phase for the normal landing, a Δv refusal before anything
moves, and heating handled twice, "planned + live guard", the guard acting from 75 %.

## Why Brake did nothing

- The normal start (`LandingPilot.CheckCanStart`) accepted a start without a collision course: `LandingFeasibility.Check`
  returns Ok when the brake simulation has no result.
- The flight went QuickWarp → RotationWarp → Waiting (stale `startBurn_UT`) → Brake, and `TouchDown.checkDirection`
  then waits forever for "speed Down": in orbit the retrograde is horizontal (angle ≥ 90°).
- The precision landing deorbits by itself, but only on an airless body, and the Land tab turns it off above a 100 km
  periapsis. On an atmospheric body the Propulsive mode never flies the precision sequence.

## What changes

### Normal landing (Propulsive), started with no collision course

1. **Deorbit phase** (`Mode.Deorbit`, flown by `PrecisionPhase`: a closed-loop `TargetOrbitBurn` toward the orbit
   after a retrograde impulse, re-planned from the real orbit at the burn). The impulse is
   `DeorbitSearch.Burn` (the precision deorbit's own, made internal): the speed along the current velocity is scaled down until the periapsis reaches the
   target, at the earliest time (`PrecisionPhases.EarliestDeorbitUT`, the lead of the precision deorbit).
   - **Airless body**: periapsis `R − 2 km` (`DeorbitSearch.DescentPeriapsisMargin`, the precision landing's own
     target). Below the datum, so the conic always crosses the terrain (the collision search can then see it), and
     shallow, which keeps the braking nearly horizontal: the cheapest powered descent from orbit.
   - **Atmospheric body**: an entry periapsis chosen by the heating model among `R + f × depth`,
     f ∈ {0.6, 0.5, 0.4, 0.3, 0.2, 0.1, 0}: the one with the lowest total Δv (deorbit + pre-brake + brake) whose
     predicted heating stays under the plan limit.
2. Then, airless: the usual Pause → QuickWarp → RotationWarp → Waiting → Brake → TouchDown, now with a collision course.
3. Atmospheric: an optional **pre-brake** (`Mode.PreBrake`, same closed-loop burn, retrograde, ending
   `PreBrakeMargin` before the atmosphere interface), then the **atmospheric coast** (`Mode.AeroCoast`): warp to one
   minute before the interface when auto-warp is on, then 1x in the air, surface retrograde, heat guard on, and
   Brake at the start predicted with drag (below).

The atmospheric planner also runs when the vessel starts **above the atmosphere on a collision course** (a manual
deorbit): no deorbit then, the pre-brake and the coast only.

### Δv refusal before anything moves

`total = deorbit + pre-brake + brake`, refused when `LandingFeasibility.DeltaVMargin × total > Δv`
(`DeltaVMargin` = 1.10), with the numbers:
"Δv 850 m/s for ~1122 m/s needed (deorbit 120, pre-brake 400, brake 500, +10 %)." (the needed figure includes
the margin, so that the two numbers compare directly).

- **Brake, airless**: `StopPointPredictor` on the post-deorbit conic (`BrakeSimulator.FindStart` at the latest
  safe start).
- **Brake, atmospheric**: the descent predicted with drag (no thrust) gives a sample every 2 s. The lowest sample
  from which a vacuum brake (`BrakeSimulator.Simulate`, started at once) still stops above `SafeAltitude` is found
  walking back from the ground in 24 strides, then by bisection (the test is not monotone high up: a horizontal
  hypersonic sample can fail while a later, slower one stops); `StopPointPredictor` from it gives the Δv and the
  start. Pessimistic: no drag counted after that sample.
- The unchanged TWR and engine checks come first. An unknown Δv is a warning, as before.

### Landing with a chosen stage

- **Airless**: works with the deorbit. The deorbit flies on the active stage, which must pay it with 10 % to spare;
  the landing stage must pay its part of the predicted braking (`StopPointPredictor` with `Vehicle.Stages`) with
  `StagedBrakePlan.DeltaVMargin` (20 %), as the precision landing checks it. The drops then follow the existing
  staged plan on the collision course.
- **Atmospheric**: still refused without a collision course. The staged plan (`StagedBrakePlan`) has no drag model:
  its drop heights and stage budgets would be planned from orbital speed. With a collision course inside the
  atmosphere it works as before.

### Precision landing

- Airless: unchanged (it already deorbits and checks every phase's Δv).
- Atmospheric, Propulsive mode: it flies the normal path above (deorbit, heating, Δv check), without a target. No
  atmospheric precision guidance is built. The log says so once.

## Heating model (read in the IL of `Ksp2Redux.dll` 26w42c, 2026-10-30)

Read in the IL (method names in the code comments of `Heating/ThermalModel.cs`):

- **Flux**: `ReentryFlux.Evaluate`, kW:
  `500 × (v/300)³ × (EAP × AreaAirstream) × ρ^0.53 × 1 × Cd^0.3 × harshness`, with v = the part's air speed,
  `AreaAirstream` and `DragCoeff` from the part's drag cube in the flow direction, `harshness` =
  `CelestialBodyComponent.reentryHarshness`. Zero below Mach 1 (every part's `ExposedAreaProportion` is 0 there).
  The constants come from `PhysicsSettings` (`GLOBAL_REENTRY_FLUX_*`, `THERMAL_SKIN_FRICTION`) and are read live.
- **Leading part**: the first part along the flow gets EAP 1, the others 1 − the share of their projected area under
  earlier parts' shock cones (`OcclusionAlgorithmJob`).
- **Temperature**: one part at a time, no conduction (`ThermalIntegrator.Integrate`, backward Euler):
  `C/(dt·s)·(T' − T) = heatScale·Source + G·(Tatm − T') + R·(Tatm⁴ − T'⁴)`, with `C = ThermalMass` (kJ/K),
  `G = WettedArea × 50 × 0.5 × 0.026 × (1 − tanh((alt − depth + 1000)/500))`,
  `R = ε × 5.670373e-8 × WettedArea × 0.001`, s = 1 when heating and 5 when cooling.
- **Destruction**: `Temperature > MaxTemp` (`FinalizeJob`), unless the difficulty option turns it off.

Assumed (not read, or simplified):

- Every part gets EAP 1 on the airstream area of its drag cube **as attached** (faces covered by neighbours already
  count less): conservative, no cone geometry. Only the 12 parts most at risk are integrated.
- The vessel holds surface retrograde the whole descent (engines first), drag from the vessel's retrograde drag table,
  constant mass between burns.
- Difficulty `HeatScaling` = 1 (not read).
- Air temperature from `CelestialBodyComponent.GetTemperature`, radiating to it (0 K above the atmosphere).

**The peak is judged up to the planned brake start.** A dense vessel keeps heating down to a few km if it never
brakes (the EditMode case: 80 % at 10 km against 69 % at the brake start, 16 km); the powered brake slows it hard,
so the heating after its start is not counted (the guard watches it).

**Plan limit 70 %** of `MaxTemp`, not 90 %: planning up to 90 % would make the 75 % guard fire on every nominal entry
and spend Δv the start check did not count. 90 % stays the ceiling the guard defends.

## Live heat guard

Every frame of a run on an atmospheric body, the hottest part's `Temperature / MaxTemp` (all parts) is read.

- In the coasting modes (AeroCoast, and Pause / QuickWarp / RotationWarp / Waiting, which switch to AeroCoast when the
  guard fires): from **75 %** above Mach 1 it thrusts surface retrograde, throttle from 25 % at 75 % to 100 % at 85 %,
  only when within 15° of retrograde, until the part cools back under **70 %** (or Mach < 1, where there is no flux).
- Above **90 %** at full throttle: "Overheating: <part> at 92 % of its max temperature, cannot slow down enough." Out of
  thrust: "Heat guard: no thrust left to slow down." Using the landing's Δv reserve is shown and logged; it keeps
  thrusting (a destroyed engine ends the landing anyway).
- Brake and TouchDown thrust anyway: only logged.
- Log lines `[Landing] heat: …` at most every 5 s, plus each guard start and stop.

## Warp rule

Rails warp only while coasting outside the air (before the deorbit, to the pre-brake, to one minute before the
interface). The burns keep `allow_rails_warp = false`; in the air, 1x.

## Not verifiable outside the game

The heating constants in a real save (difficulty), the airstream areas at retrograde in orbit, the predicted peak
against the real one, the drag-predicted brake start, the guard's effect, and the flight of each new mode.
