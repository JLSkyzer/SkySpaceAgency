# Lift and prepare orbit for transfer (sub-project 4.2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a goal to the Lift tab: "Prepare transfer to [body]". The mod waits on the pad, in warp, until the launch site passes under the parking-orbit plane the transfer needs. It then:
1. launches on the right heading and steers toward that plane during the ascent;
2. reaches the low parking orbit;
3. trims the plane to under 0.1°;
4. stops with "Ready for the {body} transfer: open the Transfer tab".

**Architecture:**
- **Pure code**, namespace `K2D2.Transfer`:
  - a launch-window solver (plane crossing, and inertial azimuth with rotation), shared with 4.4;
  - a parking-plane planner, which asks `TransferSearch` for the departure asymptote and picks the cheapest reachable plane that contains it;
  - a plane-steering correction for the ascent.
- **Game glue:**
  - a goal selector and a pad wait in `LiftPilot`;
  - the heading, taken from the solver;
  - a trim burn after circularisation (`TargetOrbitBurn` + `OrbitTargetGuidance`).

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c.

**Spec:** `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`, sections 4.2 and 6b ("Plane after the ascent"). Builds on `docs/superpowers/plans/2026-10-22-transfer-to-a-body.md` (sub-project 4.1).

## Global Constraints

- **Inherited from the 4.1 plan:** all its Global Constraints apply here. That covers the game version, logs, licence, test command, commit rules, the "Prepare only" and resume rules, and the per-stage Δv rule.
- **Lift's goal "Orbit" stays exactly as it is today:** settings, ascent, coast, circularisation and every existing test. The new goal only sets the heading and the target altitude, and adds its extra steps.
- **Waiting on the pad:**
  - The mod waits until the launch site passes under the required plane. It takes the pass (ascending or descending) that comes sooner and needs less correction.
  - The warp stops at launch time − ascent lead.
  - The ascent lead (time to orbit plus downrange angle) is measured from the Lift ascent. Until a first ascent has logged the real value, an estimate is used.
- **Site latitude above the plane's inclination:** the mod uses the closest reachable plane, and shows the plane change that remains, with its Δv.
- **Parking orbit:**
  - Altitude: the body's `LowOrbitRadius` (atmosphere or highest terrain + 10 km), or the Lift Ap setting if that is higher.
  - Plane: within 0.1° after the trim burn.
- **A distant window is fine.** The plane is inertially fixed (KSP has no precession), so an early launch is valid. The tab says when the transfer window is.
- **The transfer is never started by Lift.** It ends with "Ready for the {body} transfer: open the Transfer tab", and the Transfer tab then plans from the actual orbit.
- **Shown before launch:** the azimuth, the time to wait, the plane, any remaining plane change, the window date and its Δv.
- **Logs:** prefix `[Lift]`.

## Review Focus

1. **Equatorial site, near-equatorial target plane.** The window degenerates to "any time": the solver must return "now" with azimuth 90°, never NaN. (Task 1)
2. **Site latitude above the plane's inclination.** Two near-identical candidates exist: pick one deterministically, and report the plane error and its Δv. (Task 1)
3. **Retrograde planes** (inclination > 90°) and bodies with reversed rotation (`hasInverseRotation`): the azimuth sign must be right. (Task 1)
4. **The player warps or waits past the launch time.** The mod re-solves for the next pass and never launches late onto the wrong plane. (Task 4)
5. **The ascent steering fighting the existing pad drift correction (`heading_correction`).** The two must not add up or oscillate. (Task 3)

---

### Task 1: Launch window solver

**Files:**
- Create: `Pilots/Transfer/LaunchWindow.cs`
- Test: `Assets/Tests/Transfer/LaunchWindowTests.cs`

**Interfaces:**
- Produces `enum PlanePass { Ascending, Descending, Any }`.
- Produces `class LaunchWindowResult`, with these fields:
  - `bool Ok`, `string Refusal`;
  - `double LaunchUT`;
  - `double AzimuthDeg`: the surface heading, corrected for rotation;
  - `PlanePass Pass`;
  - `double PlaneErrorDeg`, `double PlaneChangeDeltaV`: the residual when the site latitude exceeds the inclination;
  - `Vector3d ReachedPlaneNormal`.
- Produces `static LaunchWindowResult LaunchWindow.Find(double bodyRadius, double mu, Vector3d bodyAngularVelocity, Vector3d siteAtUT0 /* body-relative, inertial Zup */, double ut0, Vector3d targetPlaneNormal, double orbitRadius, double ascentLeadSeconds, double ascentDownrangeRad, double horizonSeconds)`.

**How it works:**
- The site rotates with ω. The launch time is when the site, advanced by the downrange angle along the azimuth, lies in the target plane.
- Inertial azimuth: `sin β_i = cos i / cos φ`.
- Surface azimuth: `atan2(v_o·sin β_i − ω·R·cos φ, v_o·cos β_i)`, where `v_o = sqrt(μ/orbitRadius)`.
- Choose the earliest pass after `ut0 + ascentLeadSeconds`.

- [ ] **Step 1: write the failing tests**
  - `EquatorialSite_EquatorialPlane_LaunchNow_Azimuth90`
  - `Kerbin_KscLatitude_PlaneOf6Deg_TwoPassesPerDay_EarliestChosen`
  - `SiteAbovePlaneInclination_UsesClosestPlane_ReportsErrorAndDv`
  - `RetrogradePlane_AzimuthWestward`
  - `InverseRotation_SignHandled`
  - `AzimuthCorrection_MatchesHandComputedValue`: KSC at φ = −0.097°, i = 28.5°, 80 km. The expected value is computed by hand in the test.
  - `NeverNaN_OnDegenerateInputs`
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Solve launch windows into a target plane`.

### Task 2: Parking plane for a transfer

**Files:**
- Create: `Pilots/Transfer/ParkingPlane.cs`
- Test: `Assets/Tests/Transfer/ParkingPlaneTests.cs`

**Interfaces:**
- Consumes: `TransferSearch.Find` (4.1), `LaunchWindow` (Task 1).
- Produces `class ParkingPlan`, with these fields:
  - `bool Ok`, `string Refusal`;
  - `Vector3d PlaneNormal`, `double OrbitRadius`;
  - `double WindowUT`, `double TransferDeltaV`;
  - `string Explanation`;
  - `TransferPlan Transfer`: the plan from a nominal parking orbit in that plane.
- Produces `static ParkingPlan ParkingPlane.Plan(BodySystem s, int launchBody, int targetBody, double siteLatitudeRad, double ut0, double orbitRadius, TransferRequest template, CancellationToken ct)`.

**How it works:**
1. Run `Find` from a nominal circular equatorial orbit at `orbitRadius`. This gives the window and the departure direction:
   - for siblings and different branches, the climb's `RequiredExcess` at the launch body;
   - for parent → child, the target moon's orbit normal and the injection geometry.
2. Of the two planes that contain that direction with inclination `max(|declination|, |siteLatitude|)`, choose the one cheaper for the launch.
3. Re-run `Find` from a nominal orbit in that plane, and report the window.

- [ ] **Step 1: write the failing tests**
  - `KerbinToDuna_PlaneContainsDepartureDirection_Within0_1Deg`
  - `KerbinToMun_PlaneIsMunOrbitPlane`
  - `HighDeclination_InclinationEqualsDeclination`
  - `LowDeclination_InclinationEqualsSiteLatitude`
  - `TransferFromChosenPlane_CheaperThanFromEquatorial_OrEqual`
  - `TargetIsLaunchBody_Refused`
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Pick the parking plane for a transfer`.

### Task 3: Plane steering during the ascent

**Files:**
- Create: `Pilots/Lift/PlaneSteering.cs` (pure)
- Test: `Assets/Tests/Transfer/PlaneSteeringTests.cs`

**Interfaces:**
- Produces `static double PlaneSteering.HeadingCorrectionDeg(Vector3d r, Vector3d v, Vector3d targetPlaneNormal, double maxCorrectionDeg = 10)`: the heading change that turns the current velocity toward the target plane. It is proportional to the out-of-plane angle of the velocity and to the vessel's position relative to the node, and it is clamped.
- Produces `static double PlaneSteering.PlaneErrorDeg(Vector3d r, Vector3d v, Vector3d targetPlaneNormal)`.

**Use in game:** while the goal is not Orbit, `Ascent.cs` uses the solver's azimuth instead of `settings.heading`, plus this correction. The existing `heading_correction` (pad drift) stays off in that case (Review Focus 5). Log `[Lift] plane error {x}°` every 10 s.

- [ ] **Step 1: write the failing tests**
  - `InPlane_ZeroCorrection`
  - `NorthOfPlane_TurnsSouth`
  - `Clamped`
  - `SimulatedAscent_ConvergesUnder0_5Deg`: a crude 2-D-on-sphere ascent model in the test, not the game.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Steer the ascent toward a target plane`.

### Task 4: Lift goal, pad wait and trim burn (game glue)

**Files:**
- Modify:
  - `Pilots/Lift/LiftSettings.cs`: goal enum, target body name;
  - `Pilots/Lift/LiftPilot.cs`: new statuses `WaitLaunch` and `PlaneTrim`;
  - `Pilots/Lift/Controlers/Ascent.cs`: azimuth + correction when the goal is not Orbit;
  - `Pilots/Lift/Controlers/Final.cs`: chain to `PlaneTrim`.
- Create: `Pilots/Lift/LiftGoal.cs`, the pure part of the goal state.
- Test: `Assets/Tests/Transfer/LiftGoalTests.cs`.

**Interfaces:**
- Consumes:
  - Tasks 1–3;
  - `GameBodySystem.Build` and `VesselSnapshot.TryRead` (4.1 Task 9);
  - `TargetOrbitBurn.Start(IBurnGuidance…)` (4.1 Task 10).
- Produces:
  - `LiftGoal { Orbit, PrepareTransfer, Rendezvous }`. `Rendezvous` stays hidden until 4.4.
  - `LiftSettings.goal`, `LiftSettings.transfer_target`.

**Behaviour:**
- **Start with the goal PrepareTransfer:**
  1. Run `ParkingPlane.Plan` on a worker thread. Game reads stay on the main thread.
  2. Run `LaunchWindow.Find`.
  3. Show the plan.
  4. Enter `WaitLaunch`: warp to `LaunchUT − ascentLead − 30 s`, then hold at 1×.
- **Missed launch time:** if the clock passes `LaunchUT + 5 s` before liftoff, re-solve for the next pass and log it (Review Focus 4). A pure, tested function in `LiftGoal` decides launch, wait or re-solve.
- **Ascent:** heading = `AzimuthDeg` + the `PlaneSteering` correction.
- **Circularisation:** as today.
- **`PlaneTrim`:** if the plane error is above 0.1°, burn closed-loop with `OrbitTargetGuidance` at the next node, toward the same apsides with the target normal. Then show the end message.
- **Logs:**
  - the plan (plane, inclination, azimuth, window, Δv);
  - the launch UT against the planned one;
  - the measured ascent lead and downrange, for the estimate;
  - the plane error before and after the trim.

- [ ] **Step 1: write the failing tests** in `LiftGoalTests`: the launch, wait and re-solve decisions, and the end message text.
- [ ] **Step 2:** implement the glue.
- [ ] **Step 3:** run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 4: commit** `Lift into the parking orbit of a transfer`.

### Task 5: Lift tab UI

**Files:**
- Modify: `Assets/UI/K2D2_UI/Lift.uxml`, `Pilots/Lift/LiftUI.cs`.
- Reuse: the body list builder from the Transfer tab (4.1 Task 12).

**UI:**
- A goal dropdown: Orbit, or Prepare transfer.
- A target body dropdown, shown only for Prepare transfer.
- Readouts:
  - launch in T−;
  - azimuth;
  - plane inclination;
  - remaining plane change and its Δv;
  - transfer window date and Δv;
  - status.
- The Orbit goal keeps today's layout.

- [ ] **Step 1:** implement.
- [ ] **Step 2:** run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 3: commit** `Add the transfer goal to the Lift tab`.

### Task 6: Build, install, document (controller)

- [ ] **Build:** as in 4.1 Task 13.
- [ ] **Install:** keep the `plans/` folder.
- [ ] **Document:** CHANGELOG, README, parent `CLAUDE.md`.
- [ ] **Add to the shared in-game checklist:**
  - from the KSC, Prepare transfer to Duna: check the launch time and azimuth, the plane error after the trim, and that the Transfer tab then plans from that orbit;
  - the same to the Mun.
- [ ] **Commit and push** the submodule, then the parent pointer.
