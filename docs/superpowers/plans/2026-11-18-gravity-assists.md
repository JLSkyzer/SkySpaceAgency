# Gravity assists (sub-project 4.6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** With the Transfer tab's **"Use gravity assist"** ticked, the search also considers plans with **one** flyby of an intermediate body, of two kinds:
- a **sibling flyby**: a child of the common ancestor of the departure and target bodies (Eve, or Kerbin itself, on the way from Kerbin to Jool);
- a **departure flyby** of a moon of the vessel's body (the Mun, leaving Kerbin for Duna).

The tab shows the direct plan and the best assist side by side ("Direct: 2 140 m/s, 1y 120d — Via Eve: 1 760 m/s, 2y 30d"). The assist is offered only when it saves at least 5 % of the direct plan's Δv and fits the duration cap. The cheaper one is preselected and the player chooses. The mod flies the chosen plan: before the flyby, the corrections aim at its periapsis on the B-plane; the burn at the periapsis is powered when needed; after the flyby, the corrections aim at the target. The 4.5 chain uses the same search through its `TransferFinder` hook.

**Architecture:**
- **Pure code**, in `Pilots/Transfer/` (namespace `K2D2.Transfer`), tested in `Assets/Tests/Transfer/`. It needs `KerbolFixture`.
  - `FlybyModel`: the patched-conic flyby. It gives the turn, the periapsis, the powered Δv and the plane.
  - `AssistPorkchop`: a decoupled 3-D grid over departure × flyby × arrival dates. It uses two sets of 2-D Lambert legs that meet on a common lattice of flyby dates. `GridRefiner.NelderMead3` refines its minima.
  - `TransferSearch.Assist.cs` (sibling flyby) and `TransferSearch.MoonDeparture.cs` (departure flyby): partials of `TransferSearch.Search`. They build each candidate from the actual state with the existing climb, check flight, aim and arrival code.
  - `AssistSearch`: the public entry point. It runs the direct search, then the assists, within a time budget. It applies the 5 % rule and returns both plans.
  - `TransferSearch.AssistResume.cs`: re-plans an assist that has already departed, from wherever the vessel is.
- **Plan model:** a new `StepKind.Flyby` (a periapsis milestone, or a burn when powered), `PlanStep.TargetExcess`, and `TransferPlan.FlybyBody` / `TransferPlan.Assist`. The plan file gains new optional keys.
- **Game glue:** `TransferPilot` (plan with assists, choice, flyby flight, resume routes, the 4.5 finder) and the Transfer tab (the toggle, two plan lines, the choice).

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w46a in game (project still on the 26w42c template).

**Spec:** `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`, sections 4.6, 5, 6 and 6b. Builds on the plans `2026-10-22-transfer-to-a-body.md` (4.1) and `2026-11-18-one-button-transfer-rendezvous.md` (4.5: the `TransferFinder` hook in `FarRendezvous`, the plane aim of `ArrivalTargeting`, `TransferPlan.FinalLegRequest`).

## Global Constraints

- **Inherited:** the Global Constraints of the 4.1 and 4.5 plans:
  - logs in English with the `[Transfer]` prefix, numbers in the invariant culture;
  - licence CC BY-SA and a CHANGELOG entry;
  - K2D2 namespaces kept;
  - stage only your own files;
  - commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; push `origin main`;
  - never commit a game DLL.
- **Test command.** Never run two Unity jobs at once. `S` is your session's scratchpad.
  ```
  rm -f "$S/results.xml"; "/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
  ```
  The run passes when `results.xml` shows every test passed and `tests.log` has 0 `error CS`. Every existing test stays green (1055 `[Test` attributes on 2026-11-18).
- **Another session commits in the parent repo.** Run `git status` and `git diff` before each `git add`.
- **Option off = today, exactly.** With "Use gravity assist" unticked (the default), the search, the plan, the flight and the plan file are byte-for-byte those of 4.5. Every new field defaults to "absent": `FlybyBody = -1`, `Assist = AssistKind.None`, `TargetExcess = Vector3d.zero`. `PlanStore.CurrentVersion` stays 1, and the new keys are optional.
- **One flyby per plan, never both kinds** [spec: call]. The flyby body is never the target or one of its ancestors. A sibling flyby is a child of the common ancestor `A`, other than `cT` (the child of `A` on the target's side). A departure flyby is a child of `From`, only when the vessel orbits `From` directly and the case is `Siblings` or `DifferentBranches`.
- **Flyby periapsis floor:** `S[b].LowOrbitRadius`, which is the atmosphere or the terrain + 10 km, as the spec says. **Ceiling:** `FlybyModel.MaxPeriapsisShare` = 0.5 × the SOI.
- **Offer rule:** an assist is offered when `assist.Ok`, `assist.TotalDeltaV ≤ 0.95 × direct.TotalDeltaV` and `assist.TripSeconds ≤ MaxTripSeconds`. When the direct plan is refused (no window within the cap) and an assist is Ok, the assist is offered alone. Both totals include their reserve (`max(10, 2 %)`).
- **Frames (lesson 2026-11-18):** every plan vector stays in the system's common frame. The flyby plane normal handed to `ArrivalTargeting` is in the **flyby body's own frame**: `CelestialFrames.ToLocal(S[b].FrameToCommon, ĥ)`. Every frame-sensitive test also runs with a turned frame, because an identity proves nothing.
- **Responsiveness:** everything runs on the existing worker job (`JobKind.Search`, progress bar, cancellation). The assist phase has a wall-clock budget of `AssistSearch.TimeBudgetSeconds` = 30 s, checked between grids and between builds. When the budget is reached, the best plan found so far is kept and the log says so.
- **Search resolution** (decided here, see `AssistPorkchop`):
  - **Sibling flyby:** 160 flyby dates over the window, × 24 values for the first time of flight (0.3 to 1.5 Hohmann from `cF` to the flyby body) × 24 values for the second (0.3 to 1.5 Hohmann from the flyby body to `cT`). That makes 7 680 Lambert solves per flyby body and direction, plus 92 160 flyby evaluations. When the flyby body is `cF`, the first leg also gets the 1-revolution solutions, both branches.
  - **Then:** the 4 best local minima are polished by `NelderMead3`, and the 3 cheapest are built.
  - **Departure flyby:** 48 flyby dates over one period of the moon × 16 values for the first time of flight (0.5 to 1.5 Hohmann from the vessel's orbit to the moon).
- **UI rule (memory `ui-follow-the-mockups`):** no new interface in the old K2-D2 style. The tab gets only the existing toggle, now active, one container `assist_choice` holding two existing-style buttons `choose_direct` and `choose_assist` (one line each), and the existing tables. No new USS.

## Decisions taken in this plan (the user can correct them)

1. **Two lines at most:** "Direct" and the **cheapest offered assist**, whatever its kind. The other candidates are only logged.
2. **No turn deficit burn:** a candidate whose turn exceeds the maximum turn at the floor is dropped. The powered flyby is a burn along the motion at the periapsis, as the spec says.
3. **Unpowered flyby:** when the planned flyby burn is under `FlybyModel.MinBurn` = 2 m/s, the flyby is a milestone with no burn. The first correction after the flyby absorbs the rest.
4. **No warp during a flyby burn:** its geometry changes fast around the periapsis, so the burn runs at 1× (`burn.allow_warp = false`).
5. **The 4.5 chain** uses the assist search when the Transfer tab's "Use gravity assist" is ticked, and takes the cheaper offered plan by itself. The Rendezvous tab gets no choice, and its plan table shows the flyby row.
6. **Resume:**
   - **Before the departure:** the assist search runs again and starts the cheaper offered plan.
   - **After the departure:** `TransferSearch.AssistResume` re-plans from the vessel's state:
     - **cruising to the flyby:** a correction now, the flyby, then the target;
     - **in the flyby body's SOI, periapsis ahead:** a new outgoing excess and a new burn;
     - **past the periapsis, or after the flyby:** the plain search (around the ancestor: `ToDescendant`, or the climb out of the flyby body).
   - The assist is never dropped silently. When `AssistResume` refuses, the status says so and falls back to the plain search.
7. **Leg layout of an assist plan** (`PlanStep.LegIndex`):
   - **Sibling flyby:**
     - leg 0: injection, SOI exit, the 3 corrections aimed at the flyby;
     - leg 1: `Flyby`, then the flyby body's SOI exit;
     - leg 2: the 3 corrections aimed at the target, then the arrival steps as today.
   - **Departure flyby:**
     - leg 0: injection, the 3 corrections aimed at the moon;
     - leg 1: `Flyby`, then the moon's SOI exit;
     - leg 2: re-aim burn toward `RequiredExcess`, the SOI exit, then the cruise corrections and the arrival as today.

## Review Focus

1. **Turned flyby body frame.** A flyby of a body whose `FrameToCommon` is not the identity must give the same plan, and the same B-plane aim after conversion, as the identity frame. (Tasks 5, 6: `Assist_TurnedFlybyFrame_SameTotal`)
2. **Resume at each stage of an assist:**
   - before the departure;
   - cruising to the flyby;
   - in the flyby SOI before and after the periapsis;
   - after the flyby.

   Each case has a continuation, and none loops on a refusal (lessons 2026-11-18). A saved vector is never flown (`SavedVectorsAhead`). (Tasks 4, 8: `AssistResume_*`, `DecideResume_FlybyAhead_Replans`)
3. **Direct refused, assist possible** (duration cap, or no direct window): the assist is offered alone, the tab shows one line, and nothing claims a saving. **Assist too long for the cap:** it is not offered. (Task 7)
4. **Cancel and time budget:**
   - Stop or Cancel during the assist phase ends the job within one grid row;
   - a tiny budget returns the direct plan with the note `assist search stopped: time budget`, never a half-built assist. (Task 7)
5. **Flyby periapsis drifting under the floor** between the plan and the flight: the approach correction is refused (`JudgeCorrection` floor = `LowOrbitRadius − 10 km`, the existing rule), never flown into Eve's atmosphere. Low-thrust flyby burns longer than 10 % of the time from the SOI entry to the periapsis are warned about, like long burns. (Tasks 4, 8)

---

### Task 1: The flyby model (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/FlybyModel.cs`
- Test: `Assets/Tests/Transfer/FlybyModelTests.cs`

**Interfaces:**
- Produces `public struct FlybySolution { public bool Ok; public double PeriapsisRadius, TurnDeg, DeltaV; public Vector3d Normal; public string Why; }`. `Normal` is unit, normalize(v∞in × v∞out), in the frame of the inputs.
- Produces `public static class FlybyModel` with:
  - `public const double MaxPeriapsisShare = 0.5, MinBurn = 2;`
  - `public static double TurnDeg(double rp, double vInfIn, double vInfOut, double mu)`: asin(1/(1 + rp·vin²/μ)) + asin(1/(1 + rp·vout²/μ)), in degrees.
  - `public static FlybySolution Solve(Vector3d vInfIn, Vector3d vInfOut, double mu, double minRadius, double maxRadius)`:
    - δ = angle(vin, vout);
    - rp comes from `TurnDeg(rp) = δ`, by bisection in [minRadius, maxRadius] (the turn decreases with rp), to 1 m;
    - **Δv** = |sqrt(vout² + 2μ/rp) − sqrt(vin² + 2μ/rp)|;
    - **refusals** (`Ok = false`, `DeltaV = +∞`), each with its `Why` text:
      - `"turn {δ:F1}° above the {max:F1}° possible over the floor"` when `TurnDeg(minRadius) < δ`;
      - `"turn {δ:F2}° too small: the periapsis would be beyond half the SOI"` when `TurnDeg(maxRadius) > δ`;
      - `"no plane: in and out excess aligned"` when |vin × vout| < 1e-9·|vin|·|vout|.

- [ ] **Step 1: write the failing tests** (Eve values from `KerbolFixture`: μ = 8.1717302e12, floor 800 km)
  - `Unpowered_ClassicTurn`: `TurnDeg(8e5, 3000, 3000, μ)` = 64.2283° (1e-3).
  - `Powered_RecoversThePeriapsisAndTheBurn`: vin = 3000 m/s along x; vout = 3500 m/s rotated by 52.00432° about z → `Ok`, rp = 1 000 000 m (1 m), Δv = 313.056 m/s (0.01), `Normal` = +z (1e-9).
  - `Unpowered_SameSpeed_ZeroBurn`: |vin| = |vout| = 3000, turn 40° → Δv < 1e-6.
  - `TooLargeTurn_Refused`: vin 3000, vout 3500, turn 60° > 59.16° possible at 800 km → `Ok == false`, `Why` starts with "turn 60.0° above the 59.2°".
  - `TooSmallTurn_Refused`: turn 0.01° with a 42.5 Mm ceiling → refused, "too small".
  - `Aligned_Refused`: vout = 1.1·vin → "no plane".
  - `NormalSign_FollowsTheTurn`: the same turn about −z gives `Normal` = −z.
- [ ] **Step 2:** run the test command. Expected: FAIL (`FlybyModel` missing).
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Add the patched-conic flyby model`.

### Task 2: Independent brute-force references, and the pinned cases (tests only)

**Files:**
- Create: `Assets/Tests/Transfer/BruteForceAssistReference.cs`
- Create: `Assets/Tests/Transfer/AssistReferenceTests.cs`

The spec requires (6b) a reference from an independent brute-force optimizer, with no code shared with the production search. These references use `Lambert` and `KeplerPropagator` (as `BruteForceReference` does), but not `FlybyModel`, `AssistPorkchop` or `GridRefiner`: their flyby formula is written again here. They use the same ideal end costs as `BruteForceReference`: the cheaper of straight through and a low periapsis at each level.

**Interfaces (test assembly):**
- `public static double SiblingAssist(BodySystem s, int from, double parkingRadius, int flyby, int to, double arrivalRadius, bool capture, double ut0, double horizon, int departures, int tof1, int tof2, out double tDep, out double tFly, out double tArr, out double flybyDv)`:
  - a full 3-D grid, with no decoupling and no refinement;
  - the departure t runs over [ut0, ut0 + horizon], with tof1 and tof2 each from 0.3 to 1.5 Hohmann;
  - cost = ideal leave + powered flyby (periapsis ≥ `LowOrbitRadius`, the turn checked) + ideal reach;
  - +∞ when nothing is feasible.
- `public static double MoonDeparture(BodySystem s, int body, double parkingRadius, int moon, Vector3d wantedExcess, double tExit, int flybys, int tofs, out double saving)`:
  - the cost from a circular equatorial parking orbit, injection phase sampled at 360 points, to a moon flyby whose outgoing Kerbin-relative velocity has the asymptote `wantedExcess`;
  - `saving` is the ideal straight departure (Oberth) minus that cost.

- [ ] **Step 1: write the reference and the pinning tests**:
  - `Reference_KerbinJoolViaEve`: a direct reference `BruteForceReference.IdealTotal(...)` at 240 × 120, and `SiblingAssist` at 160 × 60 × 60 over two Kerbin–Jool synodic periods. Both start from a 100 km Kerbin orbit and capture into a low Jool orbit. Write both totals, the dates and the flyby Δv to `TestContext.Out`.
  - **Pin the known case:** run it at `epochUT` = 0. If the assist saves less than 7 %, step `ut0` by one Kerbin–Eve synodic period at a time, at most 6 times, until a case saves at least 7 %. Hard-code that `ut0` and the reference numbers in the test as named constants, with a comment ("pinned 2026-11-xx from this reference; not an external figure"). Then assert `assist ≤ 0.93 × direct`.
    - If no `ut0` qualifies, **stop and report to the controller**: the spec's test needs another known case (an open question, not a silent change).
  - `Reference_KerbinDunaNoAssist`: the same comparison for Kerbin → Duna, with every sibling as the flyby. Pin a case where the best assist saves under 3 % (expected: no Eve or Kerbin flyby pays on the way to Duna). Assert it.
  - `Reference_MunDeparture_KerbinDuna`: the departure excess comes from the direct reference's best window. Pin the saving the Mun departure finds, whatever its size, and assert that it is finite. The production test (Task 6) compares against it.
- [ ] **Step 2:** run the test command. Expected: these tests pass (they define the references). Note their run time in `TestContext.Out`. If a reference test takes more than 60 s, lower its grid and say so in its comment.
- [ ] **Step 3: commit** `Add brute-force references for gravity assists and pin the test cases`.

### Task 3: The assist porkchop and a 3-D Nelder–Mead (pure)

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Transfer/GridRefiner.cs` (add `NelderMead3`)
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/AssistPorkchop.cs`
- Test: `Assets/Tests/Transfer/AssistPorkchopTests.cs`

**Interfaces:**
- Produces `public static double NelderMead3(Func<double, double, double, double> f, ref double x, ref double y, ref double z, double dx, double dy, double dz, CancellationToken ct)`, in `GridRefiner`, with the same stopping rules as `NelderMead`. Returns the minimum.
- Produces `public struct AssistCandidate { public double DepartureUT, FlybyUT, ArrivalUT, Cost, FlybyDeltaV, FlybyPeriapsis; public bool Prograde1, Prograde2; public int Revolutions1; public Vector3d Normal; }`.
- Produces `public sealed class AssistLegs`, the inputs, as delegates so that the grid knows no body tree:
  - `Func<double, (Vector3d r, Vector3d v)> DepartureState`: `cF` around `A`, or a fixed state for a resume;
  - `Func<double, (Vector3d r, Vector3d v)> FlybyState`;
  - `Func<double, (Vector3d r, Vector3d v)> ArrivalState`;
  - `Func<double, double> LeaveCost`: from the departure excess magnitude;
  - `Func<double, double> ArriveCost`: from the arrival excess magnitude;
  - `double Mu, FlybyMu, FlybyFloor, FlybyCeiling, Tof1Lo, Tof1Hi, Tof2Lo, Tof2Hi, FlybyLo, FlybyHi, MaxTrip`;
  - `bool[] Directions1, Directions2`;
  - `int[] Revolutions1` (`{0}`, or `{0, 1}` when the flyby body is `cF`);
  - `bool FixedDeparture`: the resume case, where the departure time is the flyby date minus tof1, and the state is propagated from a fixed state with no Oberth.
- Produces `public static class AssistPorkchop` with:
  - `const int FlybySamples = 160, Tof1Samples = 24, Tof2Samples = 24, Refined = 4;`
  - `static List<AssistCandidate> Search(AssistLegs legs, CancellationToken ct, IProgress<float> progress, float from, float to)`. The steps:
    - **Grid:** for each flyby date k of the lattice, solve the leg-1 Lambert for each tof1 (departure = tF − tof1). That gives v∞in(k, i) and the leave cost. Then solve the leg-2 Lambert for each tof2: v∞out(k, j) and the arrive cost.
    - **Cost:** cost(k, i, j) = leave + `FlybyModel.Solve(v∞in, v∞out, …).DeltaV` + arrive, or +∞ when the trip exceeds `MaxTrip`.
    - **Minima:** the discrete 3-D local minima (26 neighbours), the cheapest `Refined` of them polished by `NelderMead3` on the continuous cost.
    - **Result:** the candidates, cheapest first, with their flyby periapsis and normal.
    - **Cancellation:** `ct` is checked once per flyby date.

- [ ] **Step 1: write the failing tests**
  - `NelderMead3_Quadratic`: the minimum of (x−1)² + 2(y+2)² + 3(z−0.5)² is found within 1e-6.
  - `Search_KerbinJoolViaEve_NearTheReference`: the legs are built from `KerbolFixture` with the ideal leave and arrive costs of `BruteForceReference`, at Task 2's pinned `ut0`. The best candidate's cost is within 2 % of `SiblingAssist`'s, and its flyby date is within 10 days of the reference's.
  - `Search_CostAgreesWithItsParts`: for the best candidate, solve the Lambert legs and `FlybyModel.Solve` again at its three dates. They give `Cost` (1e-6) and `FlybyDeltaV`.
  - `Search_RespectsMaxTrip`: with `MaxTrip` = 0.8 × the best candidate's trip, every returned candidate has a trip ≤ `MaxTrip`.
  - `Search_Cancelled_Throws`: a cancelled token → `OperationCanceledException`.
  - `Search_FixedDeparture_ResumeShape`: a fixed heliocentric state (Kerbin's state + 2 km/s outward) → finite candidates, and the leave cost is |v1 − v| with no Oberth.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement. Log nothing: the callers log.
- [ ] **Step 4:** run the test command. Expected: all green. Write the run time of `Search_KerbinJoolViaEve_NearTheReference` to `TestContext.Out`.
- [ ] **Step 5: commit** `Add the departure x flyby x arrival porkchop for gravity assists`.

### Task 4: The flyby step in the plan, the flight table, the file and the readout (pure)

**Files:**
- Modify: `Pilots/Transfer/TransferPlan.cs`:
  - `StepKind` gains `Flyby`, appended last;
  - `PlanStep.TargetExcess` (`Vector3d`, zero by default): the excess wanted on leaving the flyby, relative to the flyby body, common frame;
  - `public enum AssistKind { None, Sibling, Departure }`;
  - `TransferPlan.Assist` (`AssistKind.None`) and `TransferPlan.FlybyBody` (-1).
- Modify: `Pilots/Transfer/PlanStore.cs`:
  - `TransferPhase` gains `Flyby`, appended after `Capture`, before `Done`. **The phases are saved by name**: check `Enum<TransferPhase>`, and append last if they are saved by number;
  - optional keys `targetExcess` (step), `assist` and `flybyBody` (plan), written only when not default, read with `ContainsKey`;
  - `Check`: a `Flyby` whose time has passed is a milestone, like `Escape`. In `ExpectedEnergy`, a `Flyby` leaves the energy unknown.
- Modify: `Pilots/Transfer/TransferFlight.cs`:
  - `BurnPhase(Flyby)` → `TransferPhase.Flyby`;
  - `IsBurn(step)` is false for an unpowered flyby (`Flyby` with `DeltaVMagnitude < FlybyModel.MinBurn`);
  - `Next`:
    - an unpowered flyby warps (`Coast`, lead 0) to its UT, then returns `StepPassed`; it is also `StepPassed` once the vessel has left `step.Body`;
    - a powered one returns a new `FlightAction.Flyby` once its lead is reached;
  - `LegKind`: a leg whose first step is a `Flyby` → new `LegReplan.Flyby`: nothing to re-plan, since the burn is closed-loop at the periapsis;
  - `GuidanceFor(PlanStep)`: `Flyby` → `new AsymptoteGuidance(step.TargetExcess)`;
  - `StepName`: `"flyby"` (powered) / `"flyby (no burn)"`;
  - `SavedVectorsAhead` counts a `Flyby` with a non-zero `TargetExcess`;
  - `DecideResume`: unchanged rules, so a flyby ahead goes to `Replan`.
  - Produces `public enum AssistResumeRoute { FullSearch, BeforeFlyby, InFlybySoi, AfterFlyby }` and `public static AssistResumeRoute RouteAssistResume(TransferPlan plan, int stepIndex, int currentBody, bool periapsisAhead)`, with these rules:
    - no burn flown yet → `FullSearch`;
    - the `Flyby` step still ahead and `currentBody != FlybyBody` → `BeforeFlyby`;
    - `currentBody == FlybyBody` and `periapsisAhead` → `InFlybySoi`;
    - otherwise → `AfterFlyby`.
- Modify: `Pilots/Transfer/TransferReadout.cs`: the row of a `Flyby` reads `"Flyby (Eve)"`, with Δv `"{dv:F1} m/s"` or `"no burn"`, and explanation `step.Explanation`.
- Test: `TransferFlightTests.cs`, `PlanStoreTests.cs`, `TransferReadoutTests.cs`

- [ ] **Step 1: write the failing tests**
  - `Next_UnpoweredFlyby_PassesAfterItsTime`, `Next_PoweredFlyby_FliesAtItsLead`, and `Next_Flyby_PassedWhenTheSoiIsLeft`.
  - `LegKind_FlybyLeg_IsFlyby`, `LegKind_AfterDepartureFlyby_AimsTheExcess` (leg 2 of a departure flyby, its first step a re-aim with a vector at `ExcessBody` → `AimExcess`).
  - `GuidanceFor_Flyby_AimsTheTargetExcess`.
  - `RouteAssistResume_EachStage`: one assert per route, with a plan built by hand in the leg layout of decision 7.
  - `DecideResume_FlybyAhead_Replans` (Review Focus 2).
  - `RoundTrip_KeepsTheFlyby`: a plan with a `Flyby` step, `TargetExcess`, `Assist = Sibling` and `FlybyBody` → same values after `PlanStore` write and read. `NoAssistKeys_ReadsAsBefore`: a 4.5 file reads with `Assist == None`, `FlybyBody == -1`, and zero excesses.
  - `Check_PassedFlyby_IsAMilestone`.
  - `Rows_Flyby_PoweredAndNot`.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement. Every `switch` on `StepKind` or `TransferPhase` in `Pilots/` gets the new member: grep `StepKind\.` and `TransferPhase\.`, since `BurnPhase` throws on an unknown kind.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Add the flyby step to transfer plans`.

### Task 5: Build a sibling flyby plan from the actual state (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/TransferSearch.Assist.cs` (partial `Search`)
- Modify: `Pilots/Transfer/TransferSearch.cs`: entry point `internal static TransferPlan FindSiblingAssist(BodySystem s, TransferRequest q, int flyby, AssistCandidate c, CancellationToken ct)`, plus a `public` wrapper used by `AssistSearch` (Task 7). It builds a `Search`, runs `Run`'s set-up (bodies, case, vessel elements), then `BuildSiblingAssist`.
- Test: `Assets/Tests/Transfer/AssistSearchTests.cs`

**Interfaces:**
- Consumes:
  - `AssistCandidate` (Task 3) and `FlybyModel` (Task 1);
  - the existing private members of `Search`: `Leave`, `FlyClimb`, `FlightRefusal`, `BuildArrival`, `ChooseArrival`, `BudgetDirect`, `AddCorrections`, `ClimbTargets`, `Finish`, `ResolveDeparture`, `UpLead`, `EstimateLeave`, `EstimateArrive`, and the statics `AsymptoteBurn`, `PeriapsisClear`, `HyperbolicTime`;
  - `ArrivalTargeting.Solve`, the plane overload;
  - `PatchedPropagator.Propagate`.
- Produces in `Search`:
  - `AssistLegs SiblingLegs(int flyby)`: the legs of Task 3 for this request, with `EstimateLeave(Up.Count − 1, ·)` and `EstimateArrive(0, ·)` as end costs.
  - `Built BuildSiblingAssist(int flyby, AssistCandidate c)`, in this order:
    1. **Leg 1 Lambert:** `cF(t)` → flyby body at `tF`, with the candidate's direction and revolutions. Then the climb and the SOI-exit iteration, as in `BuildPorkchop` (the same `MaxExitIterations` and `ExitTolerance`, with `Planned` including the flyby body). `plan.ExcessBody = cF`, `plan.RequiredExcess` = the aimed excess.
    2. **Aim:** the flyby target is `Target(flyby, rp, NaN, NaN, ToLocal(S[flyby].FrameToCommon, ĥ))`. The departure is aimed at it as `AimAt` does in `BuildPorkchop`, through `ArrivalTargeting.Solve` with the plane. Note: factor `AimAt` out of `BuildPorkchop` into a member `double AimDeparture(int k, ref Vector3d aim, ref Leg leg, ref Flight flight, Target target, int arrivalBody, double arriveUT)`, with no change of behaviour (the existing porkchop tests stay green).
    3. **Flyby:**
       - propagate the aimed cruise into the flyby body's SOI (`PatchedPropagator`), and take the periapsis state (rPe, vPe, tPe);
       - **leg 2 Lambert** from the flyby body's state at `tPe` to `cT` at `tA`, which gives v∞out wanted (`wOut`, common frame);
       - `AsymptoteBurn(rPe, vPe, wOut, μ_flyby, LowOrbitRadius − SafetyMargin, out dv, out vAfter)`, which fails when no velocity clears the floor;
       - below `FlybyModel.MinBurn`, `dv` is set to zero and the flyby is unpowered.
    4. **After the flyby:** propagate to the flyby SOI exit. Re-solve leg 2 from the flown exit (the same mismatch loop as the climb, at most `MaxExitIterations`, moving `wOut`). The last mismatch is budgeted as the first correction after the flyby (Δv placeholder 0, the mismatch written in its explanation and added to the reserve check through `DeltaVMagnitude`).
    5. **Arrival:** `ChooseArrival(wa, tA, leg 2, …)` from the post-flyby state, then `AimAt` on its first target, as `BuildPorkchop` does.
    6. **Steps** (decision 7):
       - leg 0 corrections from `AddCorrections(…, aim = flyby target, …, latest = the flyby SOI entry)`;
       - leg 1: `Flyby` (Body = flyby, UT = tPe, `DeltaV` = dv, `TargetExcess` = wOut, `TargetBody = cT`, explanation below) and `Escape` (flyby body, its SOI exit);
       - leg 2: `AddCorrections(…, aim = arrival first target, exit = flyby exit, …)`, then the arrival steps;
       - `plan.Assist = Sibling`, `plan.FlybyBody = flyby`, `plan.CaseName = CaseName + " via " + name`;
       - `plan.FinalLegRequest ??=` the post-flyby request (FromBody = A, R0/V0 the state at the flyby exit, UT0 = that UT), so that `FarRendezvous.ArrivalAsymptote` starts after the flyby.
    7. `Finish`, then `ResolveDeparture`, with shifts as in `BuildPorkchop`.
  - **Flyby explanation:**
    - `"Flyby of {B} at {km} km: turns the path by {δ:F1}° toward {cT}{, powered: {dv:F0} m/s at the periapsis | , no burn}. The corrections before it aim at its periapsis and plane; the ones after it aim at {target}."`
    - The departure burn's explanation gets `". Gravity assist via {B}: see the flyby step"`.

- [ ] **Step 1: write the failing tests** (`KerbolFixture`, vessel in a 100 km circular equatorial Kerbin orbit, Task 2's pinned `ut0`)
  - `SiblingAssist_KerbinJoolViaEve_Built`:
    - the best of `AssistPorkchop.Search(SiblingLegs(eve))` is built: `Ok`, `Assist == Sibling`, `FlybyBody == eve`;
    - the steps in the leg layout of decision 7;
    - the flyby periapsis ≥ Eve's `LowOrbitRadius`;
    - `TotalDeltaV` within 10 % of the reference `SiblingAssist` + reserve (spec 6: "assist search finding a known Eve assist to Jool").
  - `SiblingAssist_FlownWithItsCorrections_ReachesJool`, the end-to-end test in the style of `ArrivalTargetingTests.FlyBurnsTo`:
    - fly the plan's burns and solve each correction with `ArrivalTargeting` in turn, the flyby burn at the propagated periapsis through `AsymptoteGuidance(TargetExcess)`'s Δv;
    - Jool's periapsis must end within 5 km of the low orbit;
    - the Δv flown must be within the plan's total.
  - `Assist_TurnedFlybyFrame_SameTotal` (Review Focus 1): Eve's `FrameToCommon` = 30° about x then 50° about z. The same plan's `TotalDeltaV` is within 1e-3 m/s, and the flyby target's plane normal equals `ToLocal(q, ĥ_identity)` (1e-9).
  - `SiblingAssist_FlybyBodyIsTheDeparture_OneRevolution`: Kerbin as the flyby of Kerbin → Jool. Either `Ok` with leg 1 a 1-revolution transfer, or a refusal that names the turn (`FlybyModel` `Why`). It must not throw.
  - `SiblingAssist_UnplannedSoiOnTheWay_Warned`: the existing `NoteCrossing` warning appears when the cruise crosses an SOI the plan does not include.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement. Use `Trace` for every candidate built: `[Transfer] assist candidate via {B}: …`.
- [ ] **Step 4:** run the test command. Expected: all green, including every existing `TransferSearchTests` test after the `AimDeparture` factoring.
- [ ] **Step 5: commit** `Build sibling gravity-assist plans from the actual state`.

### Task 6: Build a departure flyby of a moon (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/TransferSearch.MoonDeparture.cs` (partial `Search`)
- Modify: `Pilots/Transfer/TransferSearch.cs`: entry point `internal static TransferPlan FindMoonDeparture(BodySystem s, TransferRequest q, int moon, CancellationToken ct)`, with a public wrapper.
- Test: `Assets/Tests/Transfer/AssistSearchTests.cs`

**Interfaces:**
- Produces in `Search`:
  - `Leg LeaveViaMoon(int moon, Vector3d w, double tExit, out string why)`: the replacement for `Leave(0, w, tExit)`.
    - **Grid** (resolution in the Global Constraints): the flyby date `tF` runs over one moon period, centred at `tExit − TimeToRadius(moon position, its velocity + the excess, μ, Soi)`; tof1 runs from 0.5 to 1.5 Hohmann from the vessel's orbit radius to the moon's.
    - **Per point:**
      - **outgoing velocity:** `Hyperbola.VelocityForAsymptote(rMoon, w, μ_body)` gives two Kerbin-relative velocities; keep those that pass `PeriapsisClear(…, Floor(From))`. Each one gives v∞out = v − vMoon;
      - **incoming leg:** the Lambert from the vessel state at `tF − tof1` to `rMoon(tF)` gives v∞in. The cost is the injection |v1 − vVessel| + the `FlybyModel.Solve` Δv;
      - **refinement:** `GridRefiner.NelderMead` on (tF, tof1) around the best point.
    - **Building the cheapest point:**
      - **injection:** at `tF − tof1`, aimed at the moon's periapsis and plane (`ArrivalTargeting`, as in Task 5 step 2);
      - **corrections:** the 3 corrections of leg 0, aimed at the moon;
      - **flyby:** `Flyby` + `Escape` (moon) in leg 1, the burn from `AsymptoteBurn` at the flown periapsis toward the flyby's wanted v∞out;
      - **re-aim:** a burn in leg 2 (Body = `From`, planned vector = the check flight's residual toward `w`, never exactly zero: keep the vector even when it is tiny), then `Escape(From)`.
    - **Result:** `Leg.Cost` and `Leg.ExitUT` are those of the flown flight; `Leg.Ok` is false with `why` when no point clears the floor.
  - `Built BuildPorkchop(double t, double tof, bool prograde, int departureMoon)`: an overload. When `departureMoon >= 0`, the `Leave(k, aim, t)` call of the exit iteration becomes `LeaveViaMoon`. Everything else is unchanged (factor the shared body; the existing overload passes −1).
  - The leg numbers of the climb levels and the cruise move by 2 (decision 7), through `ClimbTargets` and `Finish`.
  - `plan.Assist = Departure`, `plan.FlybyBody = moon`, `CaseName + " via " + moon name`.
  - **Explanation:** `"Flyby of {moon} at {km} km on the way out of {From}: {saving:F0} m/s less than a direct departure{, powered: {dv:F0} m/s | , no burn}."`
  - `FindMoonDeparture` runs the direct porkchop first (its refined candidates, as `Porkchop()` does), then `BuildPorkchop(c.t, c.tof, c.prograde, moon)` on its 2 best candidates.
- **Eligibility** (`AssistSearch`, Task 7): `q.FromBody` has children, the vessel orbits it, and the case is `Siblings` or `DifferentBranches`. Every child of `From` is tried.

- [ ] **Step 1: write the failing tests**
  - `MoonDeparture_KerbinDunaViaMun_NearTheReference`:
    - `Ok`, `Assist == Departure`, `FlybyBody == mun`, the leg layout of decision 7, the Mun periapsis ≥ its `LowOrbitRadius`;
    - its departure cost (the burns of legs 0 to 2) is at most the direct plan's departure + 10 % of the reference saving magnitude (Task 2's `Reference_MunDeparture_KerbinDuna`) + 10 m/s;
    - the SOI exit excess is within 1 % of `w`.
  - `MoonDeparture_FlownWithItsCorrections_LeavesWithTheExcess`: end-to-end, as in Task 5. The excess at the Kerbin SOI exit must be within 1 m/s of the required one after the re-aim burn.
  - `MoonDeparture_NotEligible_FromTheMun`: the vessel around the Mun → the wrapper returns a refusal `"No moon to fly by around Mun."`.
  - `MoonDeparture_TurnedMoonFrame_SameTotal` (Review Focus 1): Minmus as the moon, with a turned `FrameToCommon`, gives the same totals.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Build departure flybys of a moon`.

### Task 7: The assist search, its offer and its resume re-plan (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/AssistSearch.cs`
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/TransferSearch.AssistResume.cs` (partial `Search`)
- Modify: `Pilots/Transfer/TransferReadout.cs` (the two lines)
- Test: `Assets/Tests/Transfer/AssistSearchTests.cs`, `Assets/Tests/Transfer/TransferReadoutTests.cs`

**Interfaces:**
- Produces `public sealed class AssistChoice { public TransferPlan Direct, Assist; public bool Offered; public double Saving; public string NotOffered = ""; public double SearchMs; public bool BudgetReached; public TransferPlan Preselected => Offered && (Direct == null || !Direct.Ok || Assist.TotalDeltaV < Direct.TotalDeltaV) ? Assist : Direct; }`.
- Produces `public static class AssistSearch`:
  - `public const double MinSaving = 0.05, TimeBudgetSeconds = 30;`
  - `public static Action<string> Log;`: the glue sets it to the logger; tests capture it.
  - `public static AssistChoice Find(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress = null, double timeBudgetSeconds = TimeBudgetSeconds)`:
    1. **Direct search:** `TransferSearch.Find` (progress 0 to 0.35).
    2. **Sibling flybys:** for each eligible flyby body (Global Constraints), `AssistPorkchop.Search(SiblingLegs)` (progress 0.35 to 0.8, shared between the bodies), then the cheapest 3 candidates over all bodies are built through `FindSiblingAssist`.
    3. **Departure flybys:** for each eligible moon, `FindMoonDeparture` (0.8 to 0.95).
    4. **Choice:** the cheapest `Ok` assist, then the offer rule (Global Constraints).
       - `NotOffered`: `"no gravity assist saves 5 % or more (best: via {B}, {T:F0} m/s, {pct:F1} % saved)"`, `"no gravity assist found ({first refusal})"` or `"the best gravity assist (via {B}) is longer than the maximum trip"`.
       - `Saving = 1 − assist / direct`, or NaN when the direct plan is refused.
    5. **Time budget:** a `Stopwatch` checked before each body's grid and each build. When it is reached, the rest is skipped, `BudgetReached = true`, and `NotOffered` ends with `" (assist search stopped: time budget)"` when nothing was offered.
  - `public static TransferFinder Finder(bool useAssist)`: `useAssist == false` → `TransferSearch.Find`; otherwise `(s, q, ct, p) => Find(s, q, ct, p).Preselected` (decision 5).
- Produces in `Search`, for `RouteAssistResume`:
  - `TransferPlan BeforeFlyby(int flyby, double arrivalUT)`: `AssistPorkchop.Search` with `FixedDeparture` from the vessel's heliocentric state. The flyby dates run over ±15 % of the time left to the planned flyby, and the arrival over ±20 % of the planned leg 2. The best candidate is built as Task 5 steps 3 to 7, with a first step `Correction` with its planned vector now (leg 0, aimed at the flyby target).
  - `TransferPlan InFlybySoi(double arrivalUT)`: from the state in the flyby body's SOI with the periapsis ahead. The leg-2 Lambert is solved from the flyby body at the predicted exit to `cT`, over arrivals at ±20 % of the planned one (24 samples), and the cheapest burn is kept. Steps: `Flyby` (new `TargetExcess`), `Escape`, the leg-2 corrections, the arrival.
  - Public wrapper `public static TransferPlan ResumeAssist(BodySystem s, TransferRequest q, AssistResumeRoute route, int flyby, double arrivalUT, CancellationToken ct)`. `AfterFlyby` and `FullSearch` are the glue's (Task 8): the plain `Find` or `AssistSearch.Find`.
- Produces in `TransferReadout`:
  - `public static string AssistLine(string label, TransferPlan plan, bool selected)`: `"{● |○ }{label}: {T} m/s, {Duration}"`. T is grouped by thousands with a space (`2 140`), and Duration is `KerbinCalendar.Duration(plan.TripSeconds)`. A refused plan gives `"{label}: refused ({Refusal})"`.
  - `public static string AssistLabel(TransferPlan plan, BodySystem s)`: `"Direct"` or `"Via {FlybyBody name}"`.

**Log lines** (`AssistSearch.Log`, prefix `[Transfer] assist:`):
- `direct {T:F0} m/s, trip {d}, in {ms} ms | direct refused: {why}`
- `grid via {B}: {n} Lambert solves, {m} feasible of {c}, best {cost:F0} m/s (flyby {dv:F0} m/s at {km} km), {ms} ms`
- `built via {B}: {T:F0} m/s | refused: {why}`
- `departure via {moon}: {T:F0} m/s, saves {s:F0} m/s on the departure | refused: {why}`
- `offer: {via B, {T:F0} m/s, saves {pct:F1} % | none: {NotOffered}}; preselected {Direct|Via B}; total {ms} ms{, time budget reached}`
- `resume {route}: {T:F0} m/s | refused: {why}`

- [ ] **Step 1: write the failing tests**
  - `Find_KerbinJool_OffersEve_WhenCheaper` (the spec 6 test): at Task 2's pinned `ut0`:
    - `Offered`, the assist is via Eve, `Saving ≥ 0.05`, and `Preselected == Assist`;
    - the assist total is within 10 % of the reference + reserve.
  - `Find_KerbinDuna_NoAssist`: at Task 2's pinned no-assist case: `Offered == false`, and `NotOffered` starts with "no gravity assist saves 5 %".
  - `Find_AssistTooLong_NotOffered` (Review Focus 3): `MaxTripSeconds` between the direct trip and the assist's → not offered, with the "longer than the maximum trip" text.
  - `Find_DirectRefused_AssistAlone` (Review Focus 3): a cap that refuses the direct plan (`NoWindow`) but fits the assist → `Offered`, `Saving` NaN, and `Preselected == Assist`.
  - `Find_TinyBudget_DirectOnly` (Review Focus 4): `timeBudgetSeconds = 1e-3` → `BudgetReached`, `Assist == null`, `Preselected == Direct`, and `NotOffered` ends with "(assist search stopped: time budget)".
  - `Find_Cancelled_ThrowsQuickly` (Review Focus 4): cancel from another thread 50 ms after the start → `OperationCanceledException` within 2 s.
  - `Finder_Off_IsThePlainSearch`: `Finder(false)` returns the same `TotalDeltaV` as `TransferSearch.Find` (1e-9).
  - `FarRendezvous_WithTheAssistFinder` (the 4.5 hook): `FarRendezvous.Plan(…, AssistSearch.Finder(true), …)` for a Jool-moon station at the pinned `ut0` → `Ok`, the transfer has a `Flyby` step, and `ArrivalAsymptote`'s source is `"last leg"` (it starts after the flyby).
  - `ResumeAssist_BeforeFlyby_ReachesTheFlyby` and `ResumeAssist_InFlybySoi_NewExcess`, from the states of the flown plan of Task 5: `Ok` and the steps of their route. `ResumeAssist_BeforeFlyby_NoCandidate_Refuses`: a state far off the cruise → a refusal with a reason (the glue then falls back, decision 6).
  - `AssistLine_Texts`: `"● Direct: 2 140 m/s, 1y 120d"` for a 2 140 m/s plan of 1 y 120 d, and `"○ Via Eve: …"`.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green. Write the run time of `Find_KerbinJool_OffersEve_WhenCheaper` to `TestContext.Out`. Above 30 s in the editor, lower `FlybySamples` to 120 and say so in the report.
- [ ] **Step 5: commit** `Search for gravity assists and offer them when they save 5 %`.

### Task 8: The pilot (game glue)

**Files:**
- Modify: `Pilots/Transfer/TransferPilot.cs`:
  - `Plan(TransferInput)`: when `input.UseGravityAssist` is set, the job runs `AssistSearch.Find` (same `JobKind.Search`, progress, cancel) and `OnAssistPlanned(choice, …)` stores `Choice` and calls the existing `OnPlanned(choice.Preselected, …)`;
  - `public AssistChoice Choice { get; private set; }`, cleared by `Plan`, `Stop` and `ClearChain`;
  - `public void ChoosePlan(bool assist)`: refused while running, searching, or when that plan is absent or not Ok. It sets `Current` and `Budget = BudgetFor(Current, 0)`, and updates `Status` as `OnPlanned` does. Log `[Transfer] assist: chosen {Direct|Via B} by the player`;
  - `Resume`: when the saved plan has `Assist != None` and the decision is `Replan`, route through `RouteAssistResume`:
    - `FullSearch` → `AssistSearch.Find` and the preselected plan, then `Begin`;
    - `BeforeFlyby` / `InFlybySoi` → `ResumeAssist`, and on a refusal the plain `TransferSearch.Find` with status `"The gravity assist via {B} cannot be kept from here ({why}): planned again without it."`;
    - `AfterFlyby` → plain `TransferSearch.Find`.
- Modify: `Pilots/Transfer/TransferReadout.cs`: `TransferInput.UseGravityAssist` (bool, default false).
- Modify: `Pilots/Transfer/TransferPilot.Flight.cs`:
  - `FlightAction.Flyby` → `StartFlyby(i, snap)`:
    - `ArrivalPeriapsis(snap, out peUT, out rPe, out vPe)` gives the burn time;
    - guidance from `TransferFlight.GuidanceFor`, so `AsymptoteGuidance(TargetExcess)`;
    - Prepare only → `MakeNode` with `guidance.DeltaVToGo(rPe, vPe, peUT, μ)`;
    - else `Ignite` with `burn.allow_warp = false` (decision 4). Give `Ignite` a `bool allowWarp = true` parameter; the existing callers keep it.
    - **Long burn:** the `IsLongBurn` warning uses the time from the SOI entry to the periapsis as the period (Review Focus 5).
  - `StartLeg`: `LegReplan.Flyby` → `MarkLegStarted`, log `leg {n} starts in {B}: flyby, periapsis {km} km predicted (planned {km}), excess in {vin:F0} m/s`.
  - `OnSoiChange`: entering `FlybyBody` logs the predicted periapsis against the plan.
- Modify: `Pilots/Transfer/TransferPilot.Chain.cs` (lines 145 and 439): `TransferSearch.Find` → `AssistSearch.Finder(TransferSettings.gravity_assist.V)`.
- Modify: `Pilots/Transfer/TransferPilot.cs`, in `StartSearch`'s callers: set `AssistSearch.Log = msg => logger.LogInfo(msg)` once, in the constructor.
- Modify: `Pilots/Nodes/RailsBurnPolicy.cs` only if `allow_warp = false` does not already keep rails warp off. Check by reading `BurnWarp`/`TargetOrbitBurn`. If it does not, add the flyby to the exclusions with the same mechanism as the heat guard.

**Logs to add** (`[Transfer]`, invariant culture):
- `flyby of {B}: periapsis predicted {km:F1} km (planned {km:F1}, floor {km:F1}), at UT {t:F0}; burn {dv:F1} m/s toward excess ({x:F1}, {y:F1}, {z:F1}) m/s | no burn`
- `flyby of {B} done: excess out {v:F1} m/s, {deg:F2}° from the wanted one; next: {step}`
- `resume of an assist via {B}: route {route}`

- [ ] **Step 1:** implement, with the log lines.
- [ ] **Step 2:** check by reading that, with the option unticked, `Plan`, `Resume`, the chain and the flight take exactly the old paths (`Choice == null`, `Finder(false)`, no `Flyby` step).
- [ ] **Step 3:** run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 4: commit** `Fly gravity-assist plans and let the player choose`.

### Task 9: The Transfer tab

**Files:**
- Modify: `Assets/UI/K2D2_UI/Transfer.uxml`:
  - the `gravity_assist` tooltip becomes `"Also look for plans with one flyby (a planet on the way, or a moon on the way out); shown when they save 5 % or more"`;
  - after the `plan_group` header (before `case`), `<ui:VisualElement name="assist_choice" style="display: none;">` holds `<ui:Button name="choose_direct" style="height: 26px;" />` and `<ui:Button name="choose_assist" style="height: 26px;" />`. Nothing else.
- Modify: `Pilots/Transfer/TransferUI.cs`:
  - `TransferSettings.gravity_assist = new("transfer.gravity_assist", false)`;
  - the toggle is bound (the "Coming soon" block goes);
  - `Elements` gains `"assist_choice", "choose_direct", "choose_assist"`;
  - `OnPlan` sets `input.UseGravityAssist`;
  - `UpdatePlan`, when `pilot.Choice != null`:
    - show `assist_choice`, with the two lines from `AssistLine(AssistLabel(…), plan, plan == pilot.Current)`;
    - with no assist offered, `choose_assist` is hidden and `choose_direct` reads `AssistLine(…) + " — " + Choice.NotOffered`, disabled;
    - the buttons call `pilot.ChoosePlan(false/true)`, and are disabled while running or searching.
  - The rest of the table shows `pilot.Current` as today.
- Test: `Assets/Tests/UI/WindowLayoutTests.cs` (`TransferTab_IsATemplateOfTheWindow_WithEveryElementItsPageUses` reads `Elements`), `Assets/Tests/UI/UiLayoutHarness.cs` (`SetupTransfer`: show `assist_choice` with two long lines, to check the wrapping).

- [ ] **Step 1:** extend the harness scenario. Run the test command. Expected: FAIL (no `assist_choice`).
- [ ] **Step 2:** implement.
- [ ] **Step 3:** run the test command. Expected: all green. Check the harness output: the lines wrap and stay inside the tab.
- [ ] **Step 4: commit** `Offer the gravity assist in the Transfer tab`.

### Task 10: Build, install, document (controller)

- [ ] **Build** the zip (pipeline `Deploy to Zip File`). Exit code 1 is normal: judge on the ThunderKit log and the zip. Check the DLL against 26w46a as for the 2026-11-18 builds. **Install** only if the game is closed (`tasklist | grep -i ksp2`), keeping `plans/` and the settings json.
- [ ] **CHANGELOG** (Unreleased), see below.
- [ ] **README:** the Transfer line mentions gravity assists.
- [ ] **Parent `CLAUDE.md`:** a 4.6 bullet under "Transferts, rendez-vous et amarrage" with:
  - the plan path;
  - the seven decisions;
  - "non testé en jeu";
  - the log lines;
  - the pinned `ut0`.
- [ ] **`tasks/skyspaceagency/todo.md`** (parent repo): tick 4.6 and add section 23.
- [ ] **Checklist:** append section 23 below to `docs/testing/2026-10-24-transfers-in-game-checklist.md`.
- [ ] **Commit and push** the submodule, then the parent pointer (`git add SkySpaceAgency`), your own files only.

#### CHANGELOG to add

Under `### Added`:

"**Transfer tab: gravity assists** (sub-project 4.6; not yet flown in game). With "Use gravity assist" ticked, the search also looks for plans with one flyby, of a planet on the way (Eve on the way to Jool, or Kerbin itself) or of a moon on the way out (the Mun, leaving Kerbin). The search covers the departure date, the flyby date and the arrival date. A flyby passes at least 10 km above the atmosphere or the terrain. When the turn needs it, it is powered at the periapsis, and that burn counts in the total. An assist is offered only when it saves at least 5 % of the direct plan's Δv and fits the maximum trip. The tab then shows both plans, for example "Direct: 2 140 m/s, 1y 120d" and "Via Eve: 1 760 m/s, 2y 30d". The cheaper one is preselected, and a click chooses the other. In flight, the corrections before the flyby aim at its periapsis and plane, and the ones after it aim at the target; a flyby burn is flown at 1×. Resume keeps the assist when it still can, and otherwise says so and plans without it. Transfer + rendezvous (Rendezvous tab) uses the assist when the option is ticked, and takes the cheaper plan. The search runs in the background, for 30 s at most."

#### Section 23 to append (French)

```markdown
## 23. Assistance gravitationnelle (4.6)

Onglet Transfer, case « Use gravity assist » cochée. Sauvegarde rapide avant chaque essai. Lire `Player.log`, lignes
`[Transfer] assist:` et `[Transfer]`.

1. **Jool via Eve.** Depuis une orbite basse de Kerbin, cible Jool, Orbit, Plan.
   - Lignes `direct …`, `grid via …` (une par corps, nombre de Lambert, durée en ms), `built via …`, puis `offer: …` avec
     la durée totale. Le jeu ne doit pas geler pendant la recherche (barre « Searching ») ; noter la durée totale.
   - Si une assistance est proposée : deux lignes dans l'onglet, « Direct: … » et « Via … », la moins chère marquée ●.
     Cliquer l'autre : le tableau et le total changent, ligne `chosen … by the player`.
   - Sinon : une ligne « Direct: … — no gravity assist saves 5 % … » ; noter le meilleur pourcentage du log.
2. **Vol via Eve.** Start sur le plan « Via Eve ».
   - Les corrections avant le survol : ligne `correction n` avec `plane error predicted` ; périapside visé = celui de
     l'étape « Flyby (Eve) ».
   - Entrée dans la SOI d'Eve : `leg … starts in Eve: flyby, periapsis … km predicted (planned …)`, toujours au-dessus de
     l'atmosphère (plancher 100 km).
   - Au périapside : `flyby of Eve: … burn … m/s` (ou `no burn`), poussée en 1x, sans warp.
   - Après : `flyby of Eve done: excess out … m/s, …° from the wanted one` (attendu : moins de 1°), puis les corrections
     vers Jool ; Δv réel des corrections comparé à la réserve.
   - Arrivée : capture à Jool comme en section 1 à 6 ; comparer le Δv total réel au plan.
3. **Départ via la Mun.** Depuis une orbite basse de Kerbin, cible Duna : si « Via Mun » est proposé, le voler. Lignes
   `departure via Mun`, injection vers la Mun, survol, burn de re-visée dans la SOI de Kerbin, sortie avec l'excès voulu
   (`SOI exit … mismatch`).
4. **Pas d'assistance utile.** Cible la Mun ou Minmus : aucune ligne « Via … », aucune ligne `grid via`.
5. **Durée max.** Mettre une durée max entre la durée du direct et celle de l'assistance : l'assistance n'est plus proposée
   (« longer than the maximum trip »).
6. **Reprise.** Sauvegarder pendant la croisière vers Eve, recharger, Resume : `resume of an assist via Eve: route
   BeforeFlyby`, puis le survol a lieu. Recommencer dans la SOI d'Eve avant le périapside (`InFlybySoi`) et après le
   survol (`AfterFlyby` : plan sans assistance, message clair).
7. **Prepare only.** Même plan avec Prepare only : un nœud à la fois, dont un nœud au périapside d'Eve si le survol est
   propulsé.
8. **Transfer + rendezvous.** Case cochée, station autour de Laythe : le tableau de l'onglet Rendezvous montre l'étape
   « Flyby » si l'assistance a été retenue ; sinon le plan direct.
9. **Option décochée.** Refaire le plan Kerbin → Duna : le plan et les lignes du log sont ceux d'avant 4.6.
```

---

## Only verifiable in game

- The real flyby periapsis after the approach correction, against the plan (the game's patched conics against ours, and Redux's n-body patch types during the flyby burn).
- The excess after a real flyby against the wanted one, which decides the size of the first correction after it.
- The flyby burn at 1× near the periapsis: its length, and the closed loop on `AsymptoteGuidance` inside a fast-turning hyperbola.
- The search time on the player's machine (the editor time is in the test output), and that the game stays responsive.
- Eve's real frame (`FrameToCommon` not the identity in game) and the plane aim in that frame.
- Resume at each stage of a real assist, and Prepare only with a node at the flyby periapsis.
- The 4.5 chain with an assist.

## Answers from the user (2026-11-18)

1. **Resume when the assist can no longer be kept:** re-plan without it and say so (tab and log), as planned.
2. **4.5 chain:** take the cheaper plan automatically; write the chosen plan and the alternative in the plan rows, no extra choice.
3. **Reference test:** if no Eve flyby to Jool saves enough on the synthetic system, pin another body pair with a real saving (e.g. Kerbin -> Eeloo via Jool, or a Mun departure assist) and document why in the test comment; do not lower the threshold.
