# Transfer to a body (sub-project 4.1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From an orbit around any body, plan and fly the cheapest transfer to any other body, as a flyby or a capture, explaining every non-obvious burn. Two options: "Prepare only" (one game node at a time) and a resumable plan.

**Architecture:**
- **Pure code** (namespace `K2D2.Transfer`, folder `Assets/SkySpaceAgency/Code/Pilots/Transfer/`):
  - the body system and ephemeris;
  - Lambert;
  - hyperbolic departure and arrival;
  - a patched-conic propagator;
  - the transfer search;
  - correction targeting;
  - the per-stage Δv budget;
  - plan persistence.
- **Game glue:**
  - a snapshot of the game's bodies and vessel;
  - a generalised closed-loop burn, with staging during burns;
  - a `TransferPilot` state machine;
  - a new Transfer tab.
- **Later sub-projects (4.2–4.6)** reuse these foundations: persistence, staging during burns, the stage budget, Lambert, the propagator and the generic burn.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c (`Ksp2Redux.dll`), Newtonsoft.Json (the Unity package already used by the VAB tools).

**Spec:** `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`. This plan covers sections 1–3, 4.1, 5, 6 and 6b. Persistence, staging and the per-stage check are shared constraints.

## Global Constraints

- **Game and logs:**
  - KSP2 Redux 0.2.9 / 26w42c.
  - Logger: `K2D2_Plugin.logger`, or `LogManager.GetLogger("SkySpaceAgency.Transfer")`.
  - UI and logs in English, with the log prefix `[Transfer]`.
- **Any body to any body:**
  - The departure is wherever the vessel is.
  - Body data comes from the game at run time and is never hard-coded. Stock values appear only in test fixtures, labelled as external data (KSP wiki).
- **Δv first:**
  - A candidate's cost is departure + capture (0 for an encounter) + any orbit change + the reserve. The reserve is 2 % of the total, and at least 10 m/s.
  - The maximum trip duration is optional.
  - The search horizon is two synodic periods.
- **Default capture orbit:** low and circular, at atmosphere depth + 10 km, or at the highest terrain + 10 km. Ap, Pe and inclination can be changed.
- **Explain with numbers, before flying,** any orbit change, plane change or non-obvious burn.
- **Physics:**
  - Patched conics, two-body inside each SOI, inertial Zup frame (z = rotation axis), our `KeplerPropagator`.
  - The game's patches are only a cross-check, and they are logged.
- **Burns:**
  - Closed-loop, with `BurnWarp` for alignment and warp.
  - Warn when a burn is longer than 10 % of the orbit period.
  - Every stop leaves no burn running and the warp off.
- **Prepare only:**
  - One game node at a time, re-planned from the orbit reached.
  - No warp.
  - The same checks and explanations as a flown transfer.
- **Resume:**
  - The plan survives a save/reload, a scene change and a vessel switch, and is stored per vessel id.
  - It resumes only after the player clicks Resume.
  - It always re-plans from the actual state and never replays a burn.
- **Staging during burns:** empty stages are dropped by the mod, except a stage holding a docking port, a parachute or the command part.
- **Δv check per stage:** use `VesselDeltaV.StageInfo` (vacuum). The stages left at the arrival must cover the capture plus its reserve.
- **Refusals, each with a message:**
  - no closed orbit and no usable escape;
  - no engine;
  - not enough Δv;
  - no window within the cap;
  - the target is the current body;
  - the capture orbit is below the atmosphere or the terrain.
- **Regressions:** Orbit, Node, Lift, Land and the other tabs are unchanged. All existing tests stay green (427 on 2026-10-22).
- **Licence and repository:**
  - CC BY-SA, with a CHANGELOG entry for each visible change.
  - No GPL code.
  - Never commit game DLLs.
- **Test command:** never run two Unity jobs at once.
  ```
  S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad; rm -f "$S/results.xml"; "/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
  ```
  The run passes when `results.xml` shows every test passed and `tests.log` has 0 `error CS`.
- **Commits:**
  - Stage your own files only.
  - End each message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Push to `origin main`.

## Review Focus

1. **Retrograde, highly inclined or very eccentric starting orbit.** The departure planner must still find a burn point or refuse with a reason, never produce NaN. Tests in Task 3.
2. **Target that is not a sibling or a direct child.** Examples: a grandparent (Mun → Kerbol), a moon of another planet (Ike → Laythe), a body in the same parent SOI on a different branch. The case is classified from the hierarchy, never by name. Tests in Task 5.
3. **An unplanned SOI on the way,** such as the Mun crossing the escape from Kerbin. The propagator reports it, and the plan either corrects it or warns. Tests in Task 4 (the event list) and Task 5.
4. **Resume after the state no longer matches the plan.** Cases: the player burned or staged by hand, or the vessel now orbits another body. The plan re-plans from the actual state when it can, otherwise it refuses with "plan no longer matches the vessel: re-plan". Tests in Task 8.
5. **The search freezing the game.** The search runs on a worker thread, on a pure snapshot, and touches no game object. It takes a cancel token and reports progress. Tests in Task 5 (cancellation); the duration is logged in Task 11.

---

### Task 1: Body system and ephemeris

**Files:**
- Create: `Pilots/Transfer/BodySystem.cs` and `Pilots/Transfer/BodyNode.cs`.
- Create: `Assets/Tests/Transfer/SkySpaceAgency.Tests.Transfer.asmdef`, a copy of `Assets/Tests/Vab/SkySpaceAgency.Tests.Vab.asmdef` with its name changed. It already references Newtonsoft.
- Create: `Assets/Tests/Transfer/KerbolFixture.cs` and `Assets/Tests/Transfer/BodySystemTests.cs`.

**Interfaces:**
- **Produces `class BodyNode`:**
  - `string Name`, `int Index`, `int ParentIndex` (−1 for the root);
  - `double Mu`, `Radius`, `Soi`, `AtmosphereDepth`, `MaxTerrainHeight` (m, or m³/s² for Mu);
  - `Vector3d EpochPosition`, `EpochVelocity`: the state relative to the parent at `EpochUT`, in the inertial Zup frame shared by all bodies;
  - `double EpochUT`;
  - `double LowOrbitRadius => Radius + Math.Max(AtmosphereDepth, MaxTerrainHeight) + 10_000`.
- **Produces `class BodySystem`:**
  - constructor `BodySystem(IReadOnlyList<BodyNode> nodes)`;
  - indexers `BodyNode this[string name]` and `BodyNode this[int index]`;
  - `void StateRelativeToParent(int body, double ut, out Vector3d r, out Vector3d v)`, which propagates from the epoch with `KeplerPropagator.Propagate`;
  - `void StateRelativeTo(int body, int ancestor, double ut, out Vector3d r, out Vector3d v)`, which sums the states up the chain;
  - `int CommonAncestor(int a, int b)`;
  - `IReadOnlyList<int> PathToRoot(int body)`;
  - `bool IsAncestor(int ancestor, int body)`;
  - `double SynodicPeriod(int a, int b)`, for siblings;
  - `double OrbitalPeriod(int body)`.
- **Produces `static class KerbolFixture.Build()`** (tests only): a synthetic system of Kerbol, Moho, Eve, Kerbin, Mun, Minmus, Duna, Ike, Dres, Jool, Laythe, Vall and Tylo. It uses the stock semi-major axes, eccentricities, inclinations, μ, radii and SOIs from the KSP wiki, labelled as external data. The epoch state is built from the elements.

- [ ] **Step 1: Write the failing tests:**
  - `Period_Kerbin_MatchesKepler`: propagating Kerbin over one period returns to its epoch position within 1 m.
  - `StateRelativeTo_MunAroundKerbol_IsSumOfChain`.
  - `CommonAncestor_MunDuna_IsKerbol` and `CommonAncestor_MunMinmus_IsKerbin`.
  - `SynodicPeriod_KerbinDuna_About2Years`: 1.92 ± 0.1 Kerbin years (an external value).
- [ ] **Step 2:** Run the test command. Expected: FAIL, because the types are missing.
- [ ] **Step 3:** Implement `BodyNode` and `BodySystem`. Cache nothing that depends on UT.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Add the body system and ephemeris for transfers`.

### Task 2: Lambert solver

**Files:**
- Create: `Pilots/Transfer/Lambert.cs`.
- Test: `Assets/Tests/Transfer/LambertTests.cs`.

**Interfaces:**
- **Produces** `static bool Lambert.Solve(Vector3d r1, Vector3d r2, double tof, double mu, bool prograde, int revolutions, bool longPeriodBranch, out Vector3d v1, out Vector3d v2)`:
  - universal variables, with multi-revolution support (the revolution count and branch are needed later for the rendezvous);
  - prograde is relative to +z;
  - it returns false, never NaN, when there is no solution or the transfer angle is within 1e-6 rad of 0 or π.

- [ ] **Step 1: Write the failing tests:**
  - `Curtis_Example5_2`: r1 = (5000, 10000, 2100) km, r2 = (−14600, 2500, 7000) km, tof = 3600 s, μ = 398600 km³/s² (convert to SI). Expected v1 = (−5.9925, 1.9254, 3.2456) km/s and v2 = (−3.3125, −4.1966, −0.38529) km/s, each within 1e-3 km/s.
  - `RoundTrip_WithKeplerPropagator`: for random r1, r2 and tof, propagating (r1, v1) for tof lands within 1 m of r2. Run it with 0 and with 1 revolution.
  - `Collinear_ReturnsFalse`: the transfer angle is π.
  - `MultiRev_NoSolutionBelowMinimumTime_ReturnsFalse`.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement. Iterate on z with a bisection fallback, and cap the iterations at 200.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Add a Lambert solver`.

### Task 3: Hyperbolic departure and arrival

**Files:**
- Create: `Pilots/Transfer/Hyperbola.cs` and `Pilots/Transfer/DeparturePlanner.cs`.
- Test: `Assets/Tests/Transfer/HyperbolaTests.cs` and `DeparturePlannerTests.cs`.

**Interfaces:**
- **Consumes:** Task 1, `KeplerPropagator`, `OrbitMath` (`K2D2.OrbitPlanning`).
- **Produces in `Hyperbola`:**
  - `static bool VelocityForAsymptote(Vector3d r, Vector3d vInf, double mu, out Vector3d vA, out Vector3d vB)`: the two velocities at r whose outgoing asymptote is vInf. It returns false when r lies on the asymptote line.
  - `static Vector3d Asymptote(Vector3d r, Vector3d v, double mu)`: the outgoing excess velocity of a hyperbolic state.
  - `static double CaptureDeltaV(double vInf, double rPeriapsis, double targetApoapsisRadius, double mu)`: the burn at periapsis, `sqrt(vInf² + 2μ/rp) − sqrt(2μ/rp − 2μ/(rp + ra))`.
- **Produces `class DeparturePlan`:**
  - `bool Ok`, `string Refusal`;
  - `double BurnUT`, `Vector3d DeltaV`, `double DeltaVMagnitude`;
  - `bool PlaneChangeFirst`, `double PlaneChangeUT`, `Vector3d PlaneChangeDeltaV`, `double OutOfPlaneDeg`;
  - `string Explanation`.
- **Produces** `static DeparturePlan DeparturePlanner.Plan(Vector3d r0, Vector3d v0, double ut0, double mu, double soi, Vector3d vInf, int samples = 360)`. It keeps the cheaper of two plans:
  - **(a)** the best single burn over the current orbit: at each sample point, the cheaper of the two `VelocityForAsymptote` velocities minus the orbit velocity;
  - **(b)** a plane change first, at the node where the orbit's plane meets the plane that contains vInf, followed by a tangential departure in the new plane.

  When (b) wins, `Explanation` reads "Orbit plane change first: the departure direction is {X:F0}° out of your orbit plane, which saves {Y:F0} m/s". When (a) wins, it is empty.

- [ ] **Step 1: Write the failing tests:**
  - `VelocityForAsymptote_PropagatedToSoi_MatchesDirection`: on Kerbin, at the SOI radius, the direction matches within 0.2° and the magnitude within 1 %.
  - `CaptureDeltaV_KnownValue`: vInf = 1000 m/s, rp = 320 km + 420 km on Duna, circular target. The expected value is hand-computed and written in the test.
  - `Departure_CoplanarCircular_EqualsOberthFormula`: matches `sqrt(vInf² + 2μ/r) − sqrt(μ/r)` within 0.5 %.
  - `Departure_InclinedVinf_PrefersPlaneChangeWhenCheaper` and `Departure_SmallOutOfPlane_SingleBurn`, both also checking the explanation text.
  - For Review Focus 1: `Departure_RetrogradeOrbit_FindsBurnOrRefuses_NeverNaN`, `Departure_EccentricOrbit_Works` (e = 0.6) and `Departure_Hyperbolic_Refused`.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement. For (b), reuse the plane-change cost from `OrbitMath`.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Plan hyperbolic departures and captures`.

### Task 4: Patched-conic propagator

**Files:**
- Create: `Pilots/Transfer/PatchedPropagator.cs`.
- Test: `Assets/Tests/Transfer/PatchedPropagatorTests.cs`.

**Interfaces:**
- **Consumes:** Tasks 1 and 2.
- **Produces:**
  - `struct Impulse { double UT; Vector3d DeltaV; }`, with the Δv in the frame of the body the vessel orbits at that UT.
  - `class Patch`, with:
    - `int Body`, `double StartUT`, `EndUT`;
    - `Vector3d StartR`, `StartV`;
    - `PatchEnd End`, an enum: `Escape`, `Encounter`, `Impact`, `HorizonReached`, `Impulse`;
    - `int NextBody`;
    - `double PeriapsisRadius`, `PeriapsisUT`.
  - `static List<Patch> PatchedPropagator.Propagate(BodySystem s, int body, Vector3d r, Vector3d v, double ut, double untilUT, IReadOnlyList<Impulse> impulses, int maxPatches = 8)`.
- **Events:**
  - **SOI exit:** |r| ≥ Soi, found by root-finding on the Kepler state.
  - **SOI entry:** the distance to any child drops below that child's Soi. Check on a step of at most 1/200 of the current period, then refine by bisection.
  - **Impact:** the radius drops below `Radius + AtmosphereDepth`. Entering the atmosphere counts as the end.

- [ ] **Step 1: Write the failing tests:**
  - `Escape_FromKerbin_ExitsAtSoiWithAsymptote`;
  - `HohmannToMun_ReportsEncounter_ThenPeriapsis`;
  - `MunCrossesEscape_ReportsUnplannedEncounter` (Review Focus 3);
  - `Impulse_AppliedAtUT`;
  - `NoEvent_EndsAtHorizon`.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Add a patched-conic propagator`.

### Task 5: Transfer search

**Files:**
- Create: `Pilots/Transfer/TransferPlan.cs` and `Pilots/Transfer/TransferSearch.cs`.
- Test: `Assets/Tests/Transfer/TransferSearchTests.cs`, plus `BruteForceReference.cs` (tests only).

**Interfaces:**
- **Consumes:** Tasks 1–4.
- **Produces:**
  - `enum ArrivalKind { Encounter, Orbit }`.
  - `class TransferRequest`, with:
    - `int FromBody`, `ToBody`;
    - `Vector3d R0`, `V0`, `double UT0`: the vessel's state relative to `FromBody`;
    - `ArrivalKind Arrival`;
    - `double ArrivalPeriapsisRadius`, `ArrivalApoapsisRadius`: NaN means `LowOrbitRadius`, circular;
    - `double ArrivalInclinationDeg`: NaN means free;
    - `double MaxTripSeconds`: ∞ means no cap.
  - `enum StepKind { PlaneChange, OrbitChange, Injection, Escape, Correction, Capture }`.
  - `class PlanStep`, with `StepKind Kind`, `int Body`, `double UT`, `Vector3d DeltaV`, `double DeltaVMagnitude`, `string Explanation`. A correction step is a placeholder: its UT is planned, its Δv is 0, and Task 6 solves it in flight.
  - `class TransferPlan`, with:
    - `bool Ok`, `string Refusal`;
    - `List<PlanStep> Steps`;
    - `double DepartureUT`, `ArrivalUT`, `TotalDeltaV`, `ReserveDeltaV`;
    - `string CaseName`;
    - `TransferRequest Request`;
    - `double TripSeconds => ArrivalUT - DepartureUT`.
  - `static TransferPlan TransferSearch.Find(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress = null)`.
- **Cases**, classified from the hierarchy only:
  - **Child → parent:** an escape that puts the parent periapsis at the arrival radius.
  - **Parent → child:** a Lambert search inside the parent SOI, from the vessel's orbit to the child's ephemeris. The departure UT spans one vessel period × the child's period window, and the time of flight spans 0.5–1.5 × Hohmann.
  - **Siblings:** a porkchop at the common parent. The departure UT spans [UT0, UT0 + 2 synodic periods] and the time of flight 0.5–1.5 × Hohmann, on a coarse grid of at least 60 × 40. Then refine by coordinate descent or Nelder-Mead.
  - **Different branches:**
    1. Lambert at the common ancestor, between the two ancestor-children.
    2. Staged departure: from each lower level, escape into an orbit of the next level whose periapsis is that level's `LowOrbitRadius`, then make the hyperbolic burn there (Oberth).
    3. On arrival, the same staging in reverse.
- **Cost:**
  - The refined candidate uses `DeparturePlanner.Plan` from the actual state. The grid uses a circular approximation.
  - The reserve is `max(10, 0.02 × total)`.
  - Every non-obvious step carries an `Explanation`.
- **Corrections:** three placeholder steps, at the SOI exit + 1 % of the trip time, at mid-course, and at 90 % of the trip time.
- **Refusals:**
  - "The target is the body you are orbiting.";
  - "No window within the maximum trip duration.";
  - "Capture orbit below the atmosphere or terrain of {body}.";
  - "Not in a closed orbit.", except that a hyperbolic state already escaping toward the parent is allowed for child → parent.

- [ ] **Step 1: Write the failing tests:**
  - `KerbinToDuna_FromLko_WithinDvMap`: from a 100 km circular equatorial orbit, the injection is within ±10 % of 1060 m/s (an external community Δv-map estimate), and the trip time is 200 to 400 Kerbin days.
  - `KerbinToMun_FromLko_WithinDvMap`: within ±10 % of 860 m/s (an external estimate).
  - `MunToDuna_WithinTenPercentOfBruteForce`: `BruteForceReference` scans every free time on a fine grid and shares no refinement with the production search.
  - `MunToKerbin_ChildToParent_PeriapsisAtLowOrbit`.
  - `IkeToLaythe_DifferentBranches_HasStagedSteps` and `MunToKerbol_GrandparentTarget_Classified` (Review Focus 2).
  - `TargetIsCurrentBody_Refused`, `MaxTrip_TooShort_Refused`, `CaptureBelowAtmosphere_Refused`.
  - `Encounter_HasNoCaptureStep`.
  - `Cancel_StopsWithinOneSecond` (Review Focus 5).
  - `KerbinToDuna_UnplannedMunCrossing_Flagged`: a departure that crosses the Mun SOI makes the plan carry a warning step or shift to avoid it.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement. Use no game types: the search must be callable from a worker thread.
- [ ] **Step 4:** Run the test command. Expected: all green, with each `Find` under 3 s in the editor. Log the durations in the test output.
- [ ] **Step 5:** Commit: `Search the cheapest transfer to any body`.

### Task 6: Correction targeting (arrival periapsis)

**Files:**
- Create: `Pilots/Transfer/ArrivalTargeting.cs`.
- Test: `Assets/Tests/Transfer/ArrivalTargetingTests.cs`.

**Interfaces:**
- **Consumes:** Tasks 1, 4 and 5.
- **Produces** `static bool ArrivalTargeting.Solve(BodySystem s, int body, Vector3d r, Vector3d v, double ut, int targetBody, double wantedPeriapsisRadius, double wantedInclinationDeg, double horizonUT, out Vector3d deltaV, out double predictedPeriapsisRadius, out double predictedInclinationDeg)`:
  - differential correction on the Δv components through `PatchedPropagator`, with finite differences and damped Newton, at most 12 iterations;
  - when the inclination is NaN, it targets the periapsis only, with the minimum-norm Δv;
  - it returns false, with no Δv, when there is no encounter and none can be produced with up to 300 m/s.
- **Produces** `double ArrivalTargeting.SkipThreshold = 1.0` (m/s).

- [ ] **Step 1: Write the failing tests:**
  - `PerturbedKerbinDuna_CorrectedToPeriapsisWithin1Km`: a 5 m/s injection error, corrected at mid-course.
  - `InclinationTarget_Met_Within0_5Deg`.
  - `NoEncounter_Recovers_IfWithinBudget`.
  - `Unreachable_ReturnsFalse`.
  - `EndToEnd_KerbinToDuna_Capture`: the whole plan is simulated with impulses (injection, the 3 solved corrections, capture). The final Duna orbit is within 2 km of the low orbit, and the total Δv is within 5 % of the plan's total.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Target the arrival periapsis with corrections`.

### Task 7: Per-stage Δv budget and staging guard

**Files:**
- Create: `Pilots/Transfer/StageBudget.cs` and `Pilots/Transfer/StagingGuard.cs`.
- Test: `Assets/Tests/Transfer/StageBudgetTests.cs`.

**Interfaces:**
- **Produces `struct StageDv`:**
  - `int Stage`, in game order;
  - `double DeltaVVac`;
  - `bool HasDockingPort`, `HasParachute`, `HasCommandPart`, for the parts that stage drops.
- **Produces `class BudgetResult`:** `bool Ok`, `string Refusal`, `double Available`, `Needed`, `List<(int step, int stage)> Allocation`.
- **Produces** `static BudgetResult StageBudget.Check(IReadOnlyList<StageDv> stagesInFiringOrder, IReadOnlyList<double> stepDeltaV, int captureStepIndex, double reserve)`:
  - it allocates the steps to stages in order; a step may span stages;
  - it refuses with per-stage numbers: "Not enough Δv: needs {N} m/s, the vessel has {A} m/s.", or "The stages left at the arrival have {X} m/s, the capture needs {Y} m/s."
- **Produces** `static bool StagingGuard.MayDrop(StageDv stage, out string reason)`: false when the stage would drop a docking port, a parachute or the command part.

- [ ] **Step 1: Write the failing tests:**
  - enough Δv in total, but the last stage too small for the capture: refused;
  - a step spanning two stages;
  - a reserve that tips the plan into a refusal;
  - a guard refusal for each of the three part kinds.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Check transfer Δv per stage and guard staging`.

### Task 8: Plan persistence (pure)

**Files:**
- Create: `Pilots/Transfer/PlanStore.cs`.
- Test: `Assets/Tests/Transfer/PlanStoreTests.cs`.

**Interfaces:**
- **Consumes:** `TransferPlan` and `TransferRequest` (Task 5).
- **Produces** `enum TransferPhase { Planned, WaitWindow, PlaneChange, OrbitChange, Injection, Escape, Correction, Coast, Capture, Done, Failed }`.
- **Produces `class SavedPlan`:**
  - `int Version = 1`;
  - `string VesselId`;
  - `string Kind`: `"transfer"`, extensible for 4.3–4.5;
  - `TransferPlan Plan`, `TransferPhase Phase`, `int StepIndex`;
  - `double SavedUT`;
  - `bool PrepareOnly`;
  - `string FromBodyName`, `ToBodyName`: bodies are stored by name, not by index.
- **Produces `static class PlanStore`:**
  - `string ToJson(SavedPlan p)`;
  - `bool TryFromJson(string json, out SavedPlan p, out string error)`: refuses an unknown version or a missing field, and never throws;
  - `string FileName(string vesselId) => $"plan_{vesselId}.json"`;
  - `ResumeCheck Check(SavedPlan p, BodySystem s, int currentBody, Vector3d r, Vector3d v, double ut)`, which returns one of:
    - `Resume`;
    - `Replan`, with a reason;
    - `Mismatch`, with "plan no longer matches the vessel: re-plan". This applies when the vessel orbits a body the plan does not pass through, or when its orbit differs from the expected one by more than 5 % in energy with no step pending (Review Focus 4).
- **Storage:** files go in `<mod folder>/plans/`, written by the game glue in Task 11.

- [ ] **Step 1: Write the failing tests:**
  - a round trip through JSON;
  - a corrupt file;
  - version 2, refused;
  - a body renamed or missing: `Mismatch`;
  - a state burned by hand: `Replan`;
  - a nominal state: `Resume`.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Store and check resumable transfer plans`.

### Task 9: Game snapshot (bodies, vessel, stages)

**Files:** create `Pilots/Transfer/GameBodySystem.cs` and `Pilots/Transfer/VesselSnapshot.cs`. This is game glue, with no tests.

**Interfaces:**
- **Consumes:** Tasks 1 and 7.
- **Produces** `static BodySystem GameBodySystem.Build(double ut, out string error)`:
  - it reads `UniverseModel.GetAllCelestialBodies()`, `referenceBody`, `gravParameter`, `radius`, `sphereOfInfluence`, `hasAtmosphere`/`atmosphereDepth`, and the relative state of `Orbit` (an `IKeplerPatch`) through `GetRelativePositionAtUTZup` and `GetOrbitalVelocityAtUTZup`;
  - **`MaxTerrainHeight`:** find the game's maximum terrain height for a body in the XML or the IL, and note the member used in the report. If there is none, use 0 for bodies with an atmosphere, and sample `GetAltitudeFromTerrain` on a coarse grid for airless ones. Log the value for each body.
- **Produces** `static bool VesselSnapshot.TryRead(out int body, out Vector3d r, out Vector3d v, out double ut, out List<StageDv> stages, out string error)`:
  - the state comes from the same calls as `OrbitUI.TryReadState`;
  - the stages come from `VesselDeltaV.StageInfo`, in firing order (see `VabReader.cs:80`), with the three part flags of Task 7.
- **IL checks,** written into the report under "IL findings":
  - **(a)** whether the relative Zup vectors of different bodies share one orientation. Log at start the vessel's position relative to the root, computed two ways, and their difference;
  - **(b)** whether the vessel's real motion in Redux follows patched conics or n-body (`PatchedNBodyOrbit` usage). If it is n-body, log it at start: `[Transfer] game trajectory model: …`.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 3:** Commit: `Snapshot the game's bodies and vessel for transfers`.

### Task 10: Generic closed-loop burn and staging during burns

**Files:**
- Create: `Pilots/Nodes/BurnGuidance.cs`, with the interface and its implementations.
- Modify: `Pilots/Nodes/Controlers/TargetOrbitBurn.cs`, refactored to run on an `IBurnGuidance`. The existing `Start(TargetOrbit, …)` keeps its behaviour and its tests.
- Test: `Assets/Tests/OrbitPlanner/BurnGuidanceTests.cs`.

**Interfaces:**
- **Produces** `interface IBurnGuidance { Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double mu); string Describe(); }`, with three implementations:
  - `OrbitTargetGuidance`, which wraps today's `OrbitGuidance.DeltaVToOrbit`;
  - `AsymptoteGuidance(Vector3d vInf)`: `Hyperbola.VelocityForAsymptote` at the current r, taking the branch closest to the current velocity, minus v;
  - `FixedImpulseGuidance(Vector3d deltaV, Vector3d v0)`: the Δv to go is `(v0 + deltaV) − v`, with the impulse fixed in inertial space at ignition.
- **Produces** `TargetOrbitBurn.Start(IBurnGuidance g, double startUT, double doneDeltaV = DefaultDoneDeltaV)`.
- **Staging during the burn:** when thrust has been 0 for 0.5 s (all engines flamed out or deprived) and Δv is still to go, check `StagingGuard.MayDrop` for the next stage.
  - If allowed: stage once, and log `[Transfer] staged during the burn: stage {n}`.
  - If not: fail with "Burn stopped: the next stage would drop {reason}. Stage manually."
  - Never stage twice within 1 s.

- [ ] **Step 1: Write the failing tests:**
  - `AsymptoteGuidance_ZeroWhenOnTheHyperbola`;
  - `FixedImpulse_DecreasesAsVelocityChanges`;
  - `OrbitTargetGuidance_SameAsBefore`, against the old values.
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the test command. Expected: all green, with no fewer than the 427 existing tests.
- [ ] **Step 5:** Commit: `Run closed-loop burns on any guidance and stage when a burn runs dry`.

### Task 11: Transfer pilot (state machine, warp, Prepare only, resume)

**Files:**
- Create: `Pilots/Transfer/TransferPilot.cs`, game glue: a `Pilot` registered like `LandingPilot`.
- Modify: `K2D2_Plugin.cs` and `PilotsManager`, wherever the pilots are registered.

**Interfaces:**
- **Consumes:** Tasks 5–10.
- **Produces `TransferPilot`:**
  - `Plan(TransferRequest)`: runs the search with `Task.Run` and polls for the result;
  - `Start(bool prepareOnly)`, `Stop()`, `Resume()`, `CancelSaved()`;
  - `TransferPhase Phase`, `TransferPlan Current`, `string Status`.
- **Flight:** the phases follow `TransferPhase`, and the sequence is spec 4.1, steps 1–7.
  - **Warp:** through `WarpTo`. It stops before every burn, with a lead of `BurnWarp.AlignmentLead` + 30 s. It also stops before every SOI change: use the propagator's patch end, cross-checked with the game's patch `EndUT`.
  - **Corrections:** solved with `ArrivalTargeting.Solve` when each falls due. A correction under 1 m/s is skipped, with the log line `[Transfer] correction {n} skipped: {dv} m/s`.
  - **Capture:** an `OrbitTargetGuidance` toward the capture orbit, at the arrival periapsis.
- **Prepare only:**
  - create one node with `ManeuverCreator.CreateManeuverNodeAtUT` for the next step, then wait;
  - detect that the player flew it (the node is gone and the orbit has changed), then re-plan the next step;
  - never warp.
- **Persistence:**
  - write `PlanStore` at every phase change, and delete the file on Done and on Cancel;
  - when a vessel with a saved plan becomes active in flight, run `PlanStore.Check`. The tab then shows "Resume plan to {body} (next: {step} in {time})" with Resume and Cancel, or the mismatch message.
- **Failures:**
  - every failure stops the pilot with the warp off and no burn running, logs the cause, and keeps the saved plan in the `Failed` phase so it can be resumed;
  - a capture that fails after the encounter is flagged prominently: "Capture failed: you will fly past {body}".
- **Logs**, with the `[Transfer]` prefix:
  - the plan: case, steps, Δv, durations, and the search time in ms;
  - the IL findings checks (a) and (b);
  - each phase;
  - planned vs actual Δv for each burn;
  - predicted vs game-patch SOI times;
  - the arrival periapsis predicted at each correction;
  - the final orbit.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 3:** Commit: `Fly transfers to any body`.

### Task 12: Transfer tab

**Files:**
- Create: `Assets/UI/K2D2_UI/Transfer.uxml` and its `.meta`. Let Unity generate the GUID by importing in batch mode, or write one.
- Modify: `Assets/UI/K2D2_UI/K2D2_Window.uxml`. Add a `<ui:Template name="Transfer" …>` with the new file's GUID, an `<ui:Instance template="Transfer" name="transfer" style="display: none;" />`, and its tab button.
- Create: `Pilots/Transfer/TransferUI.cs`, a `K2Page` with `code = "transfer"`. Register it like `OrbitUI` (`K2D2Window.cs:161`) or through `RegisterPilot`.

**Interfaces:**
- **Consumes:** Task 11.
- **Controls:**
  - the target body: a dropdown grouped by parent, with indentation;
  - the arrival type;
  - Pe, Ap and inclination, defaulting to "Low circular";
  - Prepare only;
  - Use gravity assist: disabled, with the tooltip "Coming soon";
  - Max trip duration, in days (empty means none);
  - the buttons Plan, Start, Stop, Resume and Cancel saved plan.
- **Readouts:**
  - the case;
  - the departure date (Y/D/H) and the time to wait;
  - the trip duration and the arrival date;
  - a table of the steps: kind, when, Δv, explanation;
  - the total, the reserve and the vessel's Δv;
  - the per-stage result of `StageBudget.Check`, and the margin;
  - the status line;
  - the warnings: a burn longer than 10 % of the orbit period, an unplanned SOI.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Run the test command. Expected: 0 `error CS`.
- [ ] **Step 3:** `TabbedPage.BuildButtons` must find matching button and page counts: grep the build log for its warning.
- [ ] **Step 4:** Commit: `Add the Transfer tab`.

### Task 13: Build, install, document (controller)

- [ ] **Build** with ThunderKit "Deploy to Zip File". It always exits with code 1, so check instead: 0 `error CS`, 0 `logLevel: 2` in the latest log, and the zip present.
- [ ] **Install** into `mods/SkySpaceAgency`, keeping `skyspaceagency_*.json`, `vab/` and `plans/`. Compare the DLL with `cmp`.
- [ ] **Document:**
  - CHANGELOG: a Transfer tab entry;
  - README: the features and the limitations;
  - parent `CLAUDE.md`: a section with the IL findings and the log lines not yet checked in game.
- [ ] **In-game checklist,** added to the shared list for the six sub-projects:
  - Kerbin → Mun, with a capture;
  - Kerbin → Duna, with Prepare only;
  - a save and reload during the coast, then Resume;
  - a burn that runs a stage dry.
- [ ] **Commit and push** the submodule, then the parent pointer.
