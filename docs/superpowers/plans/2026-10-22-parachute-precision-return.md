# Parachute precision return (precision landing part 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From a closed orbit around a body with an atmosphere (Kerbin), in Parachute mode with precision on, the pilot deorbits with the engines at the right time, corrects once, jettisons the service module itself, holds retrograde during reentry and lets the game open the parachutes, landing within 5 km of the target (KSC by default).

**Architecture:** A new pure `ReentryPredictor` (namespace `K2D2.Landing.Precision`) integrates the descent through a co-rotating atmosphere with injected density/sound-speed tables, a capsule drag table and parachute phases, and returns a stop point in the same form as `StopPointPredictor`. `DeorbitSearch`, `PlaneChange` and `CourseCorrection` take the predictor as a delegate, so the vacuum path is unchanged and the reentry path reuses the tested searches. Game glue reads the atmosphere, the capsule part set, its drag and the chutes (`ReentryContext`), decides the jettison stage (pure `JettisonPlan`), and `LandingPilot` runs the parachute precision sequence.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c (`Ksp2Redux.dll`).

**Spec:** `docs/superpowers/specs/2026-10-21-precision-landing-design.md` (part 3). Builds on `docs/superpowers/plans/2026-10-21-vacuum-precision-landing.md`.

## Global Constraints

- Game KSP2 Redux 0.2.9 / 26w42c; logger `K2D2_Plugin.logger`; UI and logs in English; log prefix `[Landing] reentry:`.
- Success: land within **5 km** of the target in the model (EditMode); realistic in-game accuracy is 3–10 km until calibrated, and the tab says so ("expected within about N km").
- Control is only the deorbit burn (time, size) and one engine correction before the jettison. No steering under canopy, no lift guidance.
- The pilot jettisons the service module itself, above the atmosphere, after the correction. It activates the next stage only if that stage contains the decoupler that separates the service module from the capsule and contains no parachute; otherwise precision is refused at start with `Cannot jettison the service module safely: stage it yourself after the deorbit, or reorder the stages.`
- Without an engine able to deorbit: precision refused (`No engine to deorbit with.`); plain Parachute mode stays available.
- Start: any closed orbit above the atmosphere (periapsis ≥ atmosphereDepth + 5 km). Plane change automatic and shown, as part 2. Δv checked against the vessel's (preparation + plane change + deorbit + correction reserve).
- Once the deorbit has started, a failure never stops the pilot: it falls back to the plain parachute descent (`ParachuteDescent`), with a message.
- Vacuum precision (part 2), non-precision landing, propulsive atmospheric mode and Lift/Node/Orbit stay unchanged. All existing tests stay green (356 or more on 2026-10-22).
- Frames and rotation as part 2: inertial Zup, z = rotation axis, ω from the measured rotation sign; air velocity = v − ω×r.
- No GPL code. CC BY-SA; CHANGELOG for visible changes.
- Test command and commit rules: as in the part 2 plan.

## Review Focus

1. Thin or no atmosphere at the target altitude (high terrain, or a body whose atmosphere is shallow): the predictor must not divide by zero density or loop forever (Task 1 tests with density 0 and with a step cap).
2. Chute never opens in the model (speed too high for `minimumSpeedToDeploy`, or pressure never reaches `minAirPressureToOpen`): the prediction reports it and the start is refused (Task 1, Task 6).
3. A staging that would also fire the parachutes, or separate the capsule from its chutes: refused (Task 5 tests).
4. Rotation sign or longitude sign wrong in game: the reentry is long (≈ 10 min from interface to touchdown on Kerbin), so a wrong sign costs tens of km — the measured sign and the predicted vs actual impact are logged (Tasks 4, 6).
5. Prediction cost: a search calls the predictor ~25–40 times; each call must stay well under 50 ms (Task 1 measures it).

---

### Task 1: `ReentryPredictor`

**Files:** Create `Precision/ReentryPredictor.cs`; Test `Assets/Tests/Landing/ReentryPredictorTests.cs`.

**Interfaces — Produces:**
- `struct Atmosphere { double Depth; double[] Density; double[] Pressure; double[] SoundSpeed; double SampleStep; }` — tables by altitude above `Radius` from 0 to `Depth` every `SampleStep` metres (linear interpolation; 0 above `Depth`).
- `struct DragTable { double[] Mach; double[] CdA; }` — the capsule's drag area (m², drag coefficient × area as the game's `AreaDrag` cube with its multipliers folded in) at retrograde attitude, vs Mach; linear interpolation, clamped.
- `struct Chutes { double Count; double AreaDeployed; double SemiAreaFraction; double MinPressureToOpen; double DeployAltitude; double MinimumSpeedToDeploy; double SemiToFullDelay; }` — `SemiAreaFraction` default 0.1 and `SemiToFullDelay` default 4 s are calibration constants, logged.
- `struct ReentryVehicle { double MassT; DragTable Drag; Chutes Chutes; double DragScale; }` — `DragScale` default 1.0, a calibration setting.
- `static StopPrediction Predict(Vector3d r, Vector3d v, double ut, Body body, Atmosphere air, ReentryVehicle vehicle, Impulse? impulse)` — Kepler coast to the atmosphere interface (with the optional impulse applied first), then RK2 (midpoint) in the atmosphere, step 0.5 s, drag `0.5·ρ·|v_air|²·CdA·DragScale / m` against `v_air = v − ω×r`, chute phases (semi-deployed when the static pressure ≥ `MinPressureToOpen` and the air speed ≤ `MinimumSpeedToDeploy`; full at `DeployAltitude` + `SemiToFullDelay`), stop on terrain (`Body.TerrainHeight`, same convention as `StopPointPredictor`). Returns `StopPrediction` with `StopDirNow` (body-fixed direction at `ut`), `StopUT`, and `Reason` notes (e.g. "chutes never open"). Step cap: 4 h of simulated time → Ok = false.
- An optional out `ReentryTrace` with the chute-open UT/altitude for logging.

- [ ] **Step 1: Tests** — synthetic Kerbin (`Mu = 3.5316e12`, `R = 600000`, rotation period 21549.425 s, exponential atmosphere ρ0 = 1.225 kg/m³, scale height 5600 m, depth 70 km, sound speed 340 m/s, pressure = ρ·287·288): a 1 t capsule CdA 1.5 m² with one chute area 25 m² from a 100×30 km descent: touches down; vertical speed at touchdown ≈ terminal velocity under canopy (√(2mg/(ρ0·CdA_chute)) within 10 %); without chutes: lands faster and farther; rotation ω vs 0: the stop point shifts east by the order of the co-rotation drift; density 0 everywhere: behaves as a ballistic impact (no NaN); a chute that never opens (MinimumSpeedToDeploy 1 m/s): Reason says so; timing: one Predict < 50 ms (log the time).
- [ ] **Step 2–4: red, implement, green**
- [ ] **Step 5: Commit** — `Predict a parachute reentry through a rotating atmosphere`

### Task 2: Predictor delegate in the searches

**Files:** Modify `Precision/DeorbitSearch.cs`, `Precision/PlaneChange.cs`, `Precision/CourseCorrection.cs`; Test: existing tests unchanged + `Assets/Tests/Landing/DeorbitSearchReentryTests.cs`.

**Interfaces — Produces:**
- `delegate StopPrediction StopPredictorFn(Vector3d r, Vector3d v, double ut, Impulse? impulse)`; overloads of `DeorbitSearch.Find/Baseline`, `PlaneChange.Plan` and `CourseCorrection.Solve` taking a `StopPredictorFn` and a `double descentPeriapsisRadius`; the existing signatures delegate to them with `StopPointPredictor.Predict` and `R − DescentPeriapsisMargin` (behaviour identical; all existing tests pass unchanged).
- `const double ReentryPeriapsisFraction = 0.45` — reentry descent periapsis = `R + 0.45·atmosphereDepth` (≈ 31.5 km on Kerbin); tune with a test showing why if needed.
- [ ] **Step 1: Tests** — reentry version on the synthetic Kerbin with `ReentryPredictor`: targets on the equator 0/90/180/270° ahead: `Ok`, `|AlongMissM| < 500`; a refused candidate (chutes never open) is skipped, not fatal.
- [ ] **Step 2–4: red, implement, green** — **Step 5: Commit** — `Let the precision searches use any stop predictor`

### Task 3: Reentry end-to-end test

**Files:** Test `Assets/Tests/Landing/ReentryEndToEndTests.cs`.
- Synthetic Kerbin, 100 km circular orbit inclined 3°, target KSC-like (lat −0.10, lon −74.56 in a reference with LonSign +1), vehicle: 4 t service module + 1.2 t capsule (CdA 1.8 m², one chute 30 m²), thrust 60 kN, Isp 300: plane change if needed → deorbit (impulsive) → correction halfway to the interface (impulsive, before the jettison) → jettison (mass and drag switch to the capsule) → reentry integration → stop distance < 5000 m; also with rotation sign −1. Log the deorbit Δv, correction Δv, chute-open altitude, touchdown speed, final distance.
- [ ] **Step 1: Test** — **Step 2–3: run, fix earlier tasks if needed (report)** — **Step 4: Commit** — `Land a simulated parachute return within 5 km of the target`

### Task 4: `ReentryContext` (game inputs)

**Files:** Create `Pilots/Landing/ReentryContext.cs`; possibly extend `VesselAeroLookup.cs` (drag table for a subset of parts at retrograde).
- `static bool TryBuild(LandingPilot pilot, out ReentryContext ctx, out string reason, bool log = true)` reusing `PrecisionContext` for state, body, rotation sign, reference, target, terrain.
- Atmosphere tables from `body.GetPressure(alt)`, `GetTemperature(alt)`, `GetDensity(p, T)` and `AeroUtilities.GetSoundSpeed(atmosphereAdiabaticIndex, p, ρ)` every 250 m from 0 to `atmosphereDepth` (sampled once per build, no game calls during searches).
- Capsule part set: the parts on the parachute side of the decoupler nearest below the chutes (walk `ParentPart` from a parachute part to the first decoupler; capsule = that decoupler's side holding the chutes); refuse when ambiguous (`Cannot identify the capsule: …`). Capsule mass = Σ `TotalMass`. Drag table at retrograde (angle 0), Mach 0–25, from the capsule parts only (a `VesselAeroLookup` variant restricted to a part set), multipliers folded in (`DragCubeMultiplier`, `DragMultiplier`, pseudo-Reynolds at a representative ρ·v — document the simplification).
- Chutes: `Data_Parachute` of the capsule's chute parts (`areaDeployed` summed, `deployAltitude`, `minAirPressureToOpen`, `minimumSpeedToDeploy`).
- `DragScale` from a new atmospheric-profile setting `reentry_drag_scale` (default 1.0, range 0.3–3).
- Log once: atmosphere depth, density at 0/10/30/50 km, capsule parts and mass, CdA at Mach 0.5/2/5, chute data.
- No unit test (compile check). **Commit** — `Build reentry inputs from the game`

### Task 5: `JettisonPlan` (pure) and the jettison action

**Files:** Create `Precision/JettisonPlan.cs`, `Pilots/Landing/Jettison.cs`; Test `Assets/Tests/Landing/JettisonPlanTests.cs`.
- Pure: input = per part `(id, parentId, decoupleStage, activationStage, isDecoupler, isParachute, isHeatshield)` and the current stage; output = `Ok` + the stage to activate, or a refusal reason. Rules: the next stage must contain the decoupler that separates the capsule side from the rest; it must contain no parachute; the capsule side must contain all parachutes; one stage activation at most.
- Game: build that input from `vessel.SimulationObject.PartOwner.Parts` (`DecoupleStage`, `ActivationStage`, `ParentPart`, module data), call `ActivateNextStage()` once, then verify the service-module parts are gone within 3 s (log the part count before/after); on failure report it (the sequence then falls back per Global Constraints).
- [ ] Tests: decoupler and chute in different stages → Ok; same stage → refused; chute below the decoupler → refused; no decoupler → refused; two decouplers in the next stage → Ok only if the capsule-side one is there and no chute. **Commit** — `Plan and perform the service module jettison`

### Task 6: `LandingPilot` parachute precision sequence

**Files:** Modify `Pilots/Landing/LandingPilot.cs`, `LandingPilotPrecision.cs` (or a new `LandingPilotReentry.cs` partial), `LandingUI.cs`, `Assets/UI/K2D2_UI/Landing.uxml` (atmospheric precision toggle enabled when the mode is Parachute; still disabled for Propulsive, with its note), `LandingSettings.cs` (`reentry_drag_scale`).
- Start (Parachute mode + precision on an atmospheric body): `ReentryContext.TryBuild`; refuse per Global Constraints (engine, jettison plan, orbit above the atmosphere, Δv, chutes never open in the model, rotation sign). Plan: plane change (if needed) and deorbit with `DeorbitSearch.Find` using a predictor that models the pre-jettison vessel (Kepler only above the interface) and the capsule from the planned jettison UT; show "Deorbit {Δv} m/s at T+…, expected within about {N} km" where N = max(3 km, the model's sensitivity estimate — e.g. the stop shift for DragScale ±20 %).
- Sequence: plane change (TargetOrbitBurn) → deorbit (TargetOrbitBurn toward `TargetOrbit.FromState(r_b, v_b + ΔV)`) → correction halfway to the interface (CourseCorrection with the reentry predictor, DoneDeltaV 0.05) → jettison (Task 5) above the interface → existing `ParachuteDescent` (retrograde hold, arm chutes, game opens them, legs) → landing; readouts: predicted distance (refreshed every 5 s above the interface), final distance after landing.
- Failure after the deorbit started: fall back to plain `ParachuteDescent` with `Precision return aborted: <reason>. Landing under parachutes without target.`; before the deorbit: refuse/stop as today.
- Logs (`[Landing] reentry:`): plan, each phase, the jettison part counts, predicted vs actual at the chute opening (altitude, UT, position) and at touchdown (distance) — the data needed to calibrate `reentry_drag_scale`.
- [ ] Implement; test command (0 `error CS`, all green); **Commit** — `Fly parachute precision returns to the target`

### Task 7: Build, install, document (controller)
- Build, install, CHANGELOG, README (precision on atmospheric bodies in Parachute mode), parent CLAUDE.md, in-game checklist (orbit ≥ 80 km, quicksave before the deorbit, the decoupler stage separate from the chute stage), commit and push.
