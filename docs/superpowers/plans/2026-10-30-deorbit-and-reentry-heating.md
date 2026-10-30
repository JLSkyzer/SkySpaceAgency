# Deorbit and reentry heating Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** the Land tab's Brake deorbits by itself from orbit, refuses a landing the vessel cannot pay for, and
plans and guards the reentry heating on atmospheric bodies. Spec:
`docs/superpowers/specs/2026-10-30-deorbit-and-reentry-heating-design.md`.

**Architecture:** pure code in `Pilots/Landing/Heating/` (namespace `K2D2.Landing.Heating`, EditMode tests in
`Assets/Tests/Landing`), game glue in `DescentContext.cs` (reads the game once, in one try/catch) and
`LandingPilotDescent.cs` (partial `LandingPilot`: start check, phases, coast, guard).

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c.

## Tasks

- [ ] **1. Thermal model** (`Heating/ThermalModel.cs`): `ThermalConstants` (game defaults, read live by the glue),
  `ThermalPart`, `ReentryFlux`, `Conductance`, `RadiationCoefficient`, backward-Euler `Step` with the game's
  heating/cooling rate scales. Tests: the flux formula on hand values, 0 below the reference checks, the step's
  equilibrium and its stability at large steps, cooling 5× faster.
- [ ] **2. Heating predictor** (`Heating/HeatingPredictor.cs`): Kepler coast to the interface, RK2 with drag against
  the co-rotating air (as `ReentryPredictor`), every part's temperature each step (flux above Mach 1 only), peak
  ratio and part, samples every 2 s, skip-outs coasted (4 passes at most). `Atmosphere` gains a temperature table.
  Tests: vacuum trajectory reaches the ground with no heating, a faster entry heats more, peak below 1 for a
  slow entry, a skip-out is followed.
- [ ] **3. Atmospheric brake** (`Heating/AeroBrake.cs`): the lowest sample from which a vacuum brake still stops,
  by bisection over the samples, then `StopPointPredictor` from it. Tests: monotone case, Impossible when even the
  first sample cannot stop, Δv below the vacuum-only brake.
- [ ] **4. Descent planner** (`Heating/DescentPlanner.cs`): airless deorbit (`DeorbitSearch.RetrogradeBurn`, made
  public) + `StopPointPredictor`; atmospheric candidates, pre-brake bisection, brake; staged checks (airless);
  `DescentFeasibility` with the refusal text. Tests: no deorbit when already on an impact course, totals add up,
  refusal message with the breakdown, hot entry gets a pre-brake, staged refusal on stage 1.
- [ ] **5. Heat guard** (`Heating/HeatGuard.cs`): hysteresis 75 % / 70 %, throttle 25 % → 100 % from 75 % to 85 %,
  alignment gate, the 90 % alarm, no flux below Mach 1. Tests for each rule.
- [ ] **6. Glue**: `DescentContext` (state, body, vehicle, stages, air tables, retrograde drag table, the 12 parts most
  at risk with their airstream area and Cd at retrograde, constants from `PhysicsSettings`), modes `PreBrake` and
  `AeroCoast` at the end of `Mode` (never `nextMode()` from them), `CheckCanStart` routing, phase planners and their
  failures, the coast, the guard in the coasting modes, `LandingUI` texts.
- [ ] **7. Tests and build**: EditMode suite (958 before), zip build.
- [ ] **8. Docs**: CHANGELOG, checklist section 16, parent `CLAUDE.md` pointer bump only.
