# One button for a target around another body (sub-project 4.5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the Rendezvous tab, a target vessel that orbits another body offers **"Transfer + rendezvous + dock"**. One press:
1. plans a 4.1 transfer whose arrival is a circular parking orbit under the target, in the plane closest to the target's that the arrival allows (the B-plane aim of the corrections does the plane match, no separate burn);
2. adds the rendezvous (worst case over the target's phase at the arrival) and the docking reserve, shows every leg and the total Δv in the tab's plan table, and refuses when the vessel's stages cannot pay for it;
3. flies the transfer (or Prepare only), then, in the parking orbit, hands over to the 4.3 Rendezvous pilot, which plans the phase **from the state reached at the arrival**, then docks.

**Architecture:**
- **Pure code**, tested in `Assets/Tests/Transfer/` (it needs `KerbolFixture` and `RendezvousPlanner`):
  - `ArrivalTargeting` gains a **plane aim**: a wanted orbit normal, in the target body's own frame;
  - the transfer search carries that normal from `TransferRequest` to the final target of every leg (`PlanStep.TargetPlaneNormal`), and keeps the request of a staged arrival's last leg;
  - a new `FarRendezvous` (namespace `K2D2.Rendezvous`): target checks, request, arrival asymptote, plane residual, rendezvous estimate, combined budget, rows and texts. The transfer search is injected: **the 4.6 hook**;
  - `SavedChain` in `PlanStore`.
- **Game glue:** `TransferPilot.Chain.cs` (the chain lives in the Transfer pilot: plan, flight, save, resume, hand-over), and the Rendezvous tab (one button, the chain's rows in the existing `steps` table).
- **Task 0** first: the three small defects 4.4 left in `Pilots/Lift/`.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w46a in game (project still on the 26w42c template).

**Spec:** `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`, sections 4.5, 5, 6 and 6b. Builds on the plans `2026-10-22-transfer-to-a-body.md` (4.1), `2026-10-24-rendezvous-and-docking.md` (4.3) and `2026-11-18-lift-for-rendezvous.md` (4.4, hand-over model: `Pilots/Lift/Controlers/RendezvousHandoff.cs`, `LiftPilot.ChainToRendezvous`).

## Global Constraints

- **Inherited:** the Global Constraints of the 4.1, 4.3 and 4.4 plans: logs in English with the pilot's prefix (`[Transfer]`, `[Rendezvous]`, `[Lift]`), invariant culture in numbers, licence CC BY-SA and a CHANGELOG entry, K2D2 namespaces kept, stage only your own files, commit messages ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, push `origin main`, never commit game DLLs.
- **Test command** (never two Unity jobs at once; `S` = your session's scratchpad):
  ```
  rm -f "$S/results.xml"; "/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
  ```
  Passes when `results.xml` shows every test passed and `tests.log` has 0 `error CS`. All 1057 existing tests stay green.
- **Another session commits in the parent repo:** `git status` and `git diff` before each `git add`; stage only the files of the task.
- **Transfer tab, Lift goals Orbit / Prepare transfer and the 4.3 buttons stay exactly as today.** Every new field defaults to "absent" (`Vector3d.zero` normal, null chain), and a plan file without the new keys reads as before (`PlanStore.CurrentVersion` stays 1, new keys optional).
- **Parking orbit at the arrival:** circular at `RendezvousLaunch.ParkingRadius(targetPe, lowOrbit, radius, out reason)` (4.4's rule: target periapsis − 10 km, never under `BodyNode.LowOrbitRadius`, never above `LowOrbitRadius + 150 km`), around the target's body.
- **Plane:** the arrival hyperbola's plane must contain its incoming asymptote Ŝ, so the aim is the plane closest to the target's: normal ĥ = normalize(n̂ − (n̂·Ŝ)Ŝ). What is left, asin(|n̂·Ŝ|), is flown by 4.3's plane match (above `RendezvousPlanner.PlaneMatchThresholdDeg` = 0.05°) and **budgeted in the plan**.
- **Frames (lesson 2026-11-18):** the target vessel's `GetRelativePositionAtUTZup` / `GetOrbitalVelocityAtUTZup` are in its body's own celestial frame. The target's normal, Ŝ and the rendezvous estimate are all in **that body's own frame**; the vessel's transfer vectors stay in the system's common frame, turned with `CelestialFrames.ToLocal(S[body].FrameToCommon, …)` only where they meet. Every frame-sensitive test also runs with a turned frame (an identity proves nothing).
- **The target moves during the transfer:** the phase is never fixed at departure. The plan budgets the worst of `FarRendezvous.PhaseSamples` = 12 phases; the real rendezvous is planned by `RendezvousPilot.Start` from the parking orbit reached.
- **Chain failure (6b):** stop safely (no burn, warp 1×, current orbit); the tab says the step, the reason and what is left; the plan is kept and Resume re-plans from there; the mod never re-plans on its own. Once 4.3 has started, its own save and Resume apply.
- **Prepare only** (the tab's existing toggle) covers the transfer and the rendezvous burns; the docking is always flown (6b).
- **The target vessel is never acted on.** Setting it as our game target is an action on our vessel, so allowed.
- **UI rule (user, memory `ui-follow-the-mockups`):** no new interface in the old K2-D2 style. Only: one button in `Dock.uxml`, the chain's rows in the existing `steps` / `totals`, and the existing status, Resume and Stop.
- **4.6 hook only:** nothing of the gravity assist is built (see Task 3, "4.6 hook").

## Decisions taken in this plan (the user can correct them)

1. **One press plans, then flies**, like 4.3's buttons. The plan's rows stay in the tab during the wait for the window, and Stop works at any time; a refusal flies nothing.
2. **Arrival orbit:** 4.4's parking rule (Pe − 10 km, at least the low orbit, at most low + 150 km); 4.3's phasing raises the rest for a high target.
3. **Plane match folded into the arrival correction:** B-plane aim at the closest plane (above); the residual is a 4.3 plane match, shown on its own row with its Δv.
4. **Rendezvous budget = the worst of 12 target phases** at the predicted arrival, from a circular parking orbit in the aimed plane, plus `RendezvousFlight.DockReserve` (10 m/s). A phase the planner refuses is left out of the worst; all 12 refused is a refusal.
5. **The chain lives in `TransferPilot`** (plan, flight, save, resume), which calls `RendezvousPilot.Instance.Start` after it has ended itself, as `LiftPilot.ChainToRendezvous` does. The Rendezvous pilot is idle during the transfer leg.
6. **Target lost during the transfer** (gone, landed, now around another body): the transfer goes on to the parking orbit, which is a closed, safe orbit; the hand-over then stops with the reason. The last known plane is kept for the corrections.
7. **The target's plane is read again at each arrival correction** (it may have manoeuvred); a change above 0.01° is logged.
8. **Staged arrivals** (a capture around the target's parent, then a leg to a moon) keep their last leg's request (`TransferPlan.FinalLegRequest`), so the asymptote can be predicted. When no prediction works, the plan is refused ("cannot predict the arrival geometry"), never budgeted blind.

## Review Focus

1. **Turned frames** (target around Minmus or Ike, whose frame is turned; lesson 2026-11-18): the plane aim and the estimate must not depend on the frame. Turning the target body's frame, with the target's state given in it, must leave the Δv and the plane error unchanged. (Tasks 1, 3)
2. **Asymptote almost along the target's normal** (|n̂·Ŝ| ≈ 1, e.g. a polar station seen from a near-polar approach): ĥ degenerates; the aim falls back to the periapsis only and the residual (~90°) is budgeted, never a NaN. (Tasks 1, 3)
3. **Target that cannot be met there:** landed, on an escape path, around the star, periapsis inside the atmosphere or under the terrain, around our own body (that is 4.3's buttons), or our vessel already in the target body's SOI: a clear refusal before any burn. (Tasks 3, 6)
4. **Resume after a reload in the middle of the cruise:** the chain must come back with the plan (file written by 0.x without the key reads as a plain transfer), the budget must still count the rendezvous, and the hand-over must still happen. (Tasks 2, 4, 5)
5. **The vessel cannot pay for the rendezvous although it can pay for the transfer:** refused with the per-stage numbers, the capture check still applied before the rendezvous steps. (Tasks 3, 5)

---

### Task 0: The small defects left by 4.4 (Lift)

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Lift/LiftGoal.cs` (`LiftGoalRules`)
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Lift/LiftPilot.cs` (`status` setter `Off` branch, `onReset`, `EndLiftPilot`)
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Lift/Controlers/WaitLaunch.cs` (`Update`, the refusal path) and `WaitLaunch.Rendezvous.cs` (`BeginRendezvousPlanning`, `OnRendezvousPlanned`, `OnMissedLaunch`)
- Modify: `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs` (`ResetControllers`, `ValidScene`, the `VesselChangedMessage` handler)
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Lift/RendezvousLaunch.cs` (`TargetRefusal`, other-body text)
- Test: `Assets/Tests/Transfer/LiftGoalTests.cs`, `Assets/Tests/Transfer/RendezvousLaunchTests.cs`

**The three defects (from commits `8bc1954`..`7b64400`):**
1. **A planner refusal says "press Start again".** `WaitLaunch.Update` ends every refusal with `lift.EndLiftPilot(false, why)` while the status is `WaitLaunch`, so `LiftGoalRules.ChainStopped` adds "Left to do: press Start again on the pad: the launch is planned again". A refusal from `RendezvousLaunch.TargetRefusal` or from `RendezvousLaunch.Plan` (`!plan.Ok`) gives the same answer on every Start.
2. **A scene change is reported as "Stop pressed, or another pilot started"** (`LiftPilot.status` setter, `Off` branch): `K2D2_Plugin.ResetControllers` is also called by `ValidScene` (scene left, mod disabled in the campaign) and by the `VesselChangedMessage` handler.
3. **The first Lift "Rendezvous" plan does not force 1× warp:** only `OnMissedLaunch` calls `TimeWarpTools.SetRateIndex(0, false)` before `BeginRendezvousPlanning`; `Start` → `BeginRendezvousPlanning(null)` does not, and the passes are planned for seconds of game time under the player's warp (`RendezvousStartDelaySeconds` = 60 s is not enough at ×1000).

**Interfaces:**
- Produces in `K2D2_Plugin.cs`: a top-level `public enum ResetCause { AnotherPilot, SceneChange, VesselChange, ModDisabled }` (next to the class, so the pure rules do not touch `K2D2_Plugin`); in the class, `public static ResetCause? CurrentResetCause { get; private set; }` (set for the duration of the call, in a `try/finally`); `public static void ResetControllers(ResetCause cause = ResetCause.AnotherPilot)`. `ValidScene` passes `ModDisabled` (the `Enabled` check) or `SceneChange`; the vessel-change handler passes `VesselChange`. Every other caller keeps the default.
- Produces in `LiftGoalRules`:
  - `static string StopReason(ResetCause? cause)`: null → `"Stop pressed"`, `AnotherPilot` → `"another pilot started"`, `SceneChange` → `"the scene changed (flight left)"`, `VesselChange` → `"the active vessel changed"`, `ModDisabled` → `"the mod is off in this campaign"`.
  - `static string PlanRefused(string target, string why)`: `"Rendezvous with {target} refused on the pad: {why without its final dot}. Nothing was launched."`
- `LiftPilot`: `onReset` stores `stop_cause = K2D2_Plugin.CurrentResetCause` before `isRunning = false`; the `Off` branch uses `LiftGoalRules.StopReason(stop_cause)` in both the end text and its log line, then clears `stop_cause`. `EndLiftPilot(bool result_ok, string end_status, bool planRefused = false)`: with `planRefused`, the rendezvous goal gets `PlanRefused(target, end_status)` instead of `ChainStopped`.
- `WaitLaunch`: a field `bool refusal_is_plan`, set with the refusal by the `TargetRefusal` branch and by `OnRendezvousPlanned` when `!plan.Ok`; `Update` passes it to `EndLiftPilot` and clears it. A faulted job, `StagedRefusal` and the other refusals keep `ChainStopped`.
- `BeginRendezvousPlanning` sets the warp to 1× at its start when `TimeWarpTools.CurrentRateIndex != 0`, logging `[Lift] rendezvous: warp set to 1x while the passes are planned (was index {i})`; the call in `OnMissedLaunch` goes (now redundant).
- `RendezvousLaunch.TargetRefusal`, other body: `"The target orbits {targetBody}, not {launchBody}: lift to orbit with the Orbit goal, then use Transfer + rendezvous + dock in the Rendezvous tab."`

- [ ] **Step 1: write the failing tests**
  - `StopReason_PerCause`: one assert per text above, null included.
  - `PlanRefused_SaysNothingWasLaunched`: `PlanRefused("Station", "The target is landed: a rendezvous needs a vessel in orbit.")` equals `"Rendezvous with Station refused on the pad: The target is landed: a rendezvous needs a vessel in orbit. Nothing was launched."`; it does not contain `"press Start again"`.
  - `ChainStopped_WaitLaunch_Unchanged`: the existing text for an interruption on the pad still says "press Start again".
  - In `RendezvousLaunchTests.TargetRefusals`: the other-body assert takes the new text.
- [ ] **Step 2:** run the test command. Expected: FAIL (`StopReason`, `PlanRefused` missing; old text).
- [ ] **Step 3:** implement the interfaces above.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Fix the Lift rendezvous stop texts and force 1x while its passes are planned`.

### Task 1: Plane aim in `ArrivalTargeting`

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Transfer/ArrivalTargeting.cs`
- Test: `Assets/Tests/Transfer/ArrivalTargetingTests.cs` (use its helpers `FromLko`, `KerbinToDuna`, `Start`, `FlyBurnsTo`, `Arrival`, `Horizon`, `MidCourse`)

**Interfaces:**
- Produces `public static Vector3d IncomingAsymptote(Vector3d r, Vector3d v, double mu)`: the unit incoming asymptote Ŝ of an open conic, the formula already in `Problem.Arrival` (`eHat`, `qHat`, `sHat`), moved out and used by it; `Vector3d.zero` for a closed conic.
- Produces `public static Vector3d ClosestPlaneNormal(Vector3d sHat, Vector3d targetNormal)`: normalize(n̂ − (n̂·Ŝ)Ŝ); `Vector3d.zero` when that length is under 1e-6 (Review Focus 2).
- Produces `public static double PlaneResidualDeg(Vector3d sHat, Vector3d targetNormal)`: asin(min(1, |n̂·Ŝ|)) in degrees.
- Produces a new most-general overload; the existing most general one (the one with `maxDeltaV`) delegates to it with `Vector3d.zero`:
  ```csharp
  public static bool Solve(BodySystem s, int body, Vector3d r, Vector3d v, double ut, int targetBody,
      double wantedPeriapsisRadius, double wantedInclinationDeg, Vector3d wantedPlaneNormal,
      double horizonUT, double plannedArrivalUT, double captureApoapsisRadius, double maxDeltaV,
      out Vector3d deltaV, out double predictedPeriapsisRadius, out double predictedInclinationDeg,
      out double predictedArrivalUT, out double predictedCaptureDeltaV, out bool converged)
  ```
  `wantedPlaneNormal`: unit, in the **target body's own frame** (where `Arrival` already turns the patch with `CelestialFrames.ToLocal`); zero = no plane. A plane takes precedence over an inclination.
- In `Problem.Arrival`, a plane residual (same `Kind = 2` as the inclination residual, so `Better` compares them on one scale): ĥ = `ClosestPlaneNormal(sHat, WantedPlaneNormal)`; zero → the periapsis-only residual (`Kind = 1`); else `error = bVec − bWanted · Cross(sHat, ĥ)` (B̂ = Ŝ × ĥ, as `bVec = Cross(sHat, h) / vInf`), residual = its `tHat` and `rHat` components. No `branch`.
- Produces `public static double ArrivalPlaneErrorDeg(BodySystem s, int body, Vector3d r, Vector3d v, double ut, Vector3d deltaV, int targetBody, Vector3d targetNormal, double horizonUT)`: propagates with `deltaV`, takes the first patch around `targetBody`, and returns the angle between its orbit normal (in the target's own frame) and `targetNormal`, in degrees; NaN without an arrival. For the logs and the tests.

- [ ] **Step 1: write the failing tests**
  - `IncomingAsymptote_MatchesTheVelocityFarOut`: a hyperbola around Duna started at 0.9 SOI inbound; `IncomingAsymptote` within 0.5° of the velocity direction at that point, and zero on a circular orbit.
  - `ClosestPlaneNormal_IsPerpendicularToTheAsymptote_AndNearestToTheTarget`: random Ŝ, n̂ (seeded): Dot(ĥ, Ŝ) < 1e-12, |ĥ| = 1, and angle(ĥ, n̂) equals `PlaneResidualDeg` within 1e-9; n̂ = Ŝ → zero.
  - `PlaneTarget_KerbinDuna_ArrivesInTheClosestPlane`: `KerbinToDuna`, flown to the first correction; target normal of an orbit at 35° inclination, node 70° (Duna frame); `Solve` with that normal → converged, arrival periapsis within 1 km of the low orbit, and `ArrivalPlaneErrorDeg` within 0.2° of `PlaneResidualDeg(Ŝ of the arrival, n̂)`; the Δv stays under 150 m/s.
  - `PlaneTarget_InTheAsymptotePlane_ExactPlane`: n̂ chosen ⊥ the uncorrected arrival's Ŝ → `ArrivalPlaneErrorDeg` < 0.05°.
  - `PlaneTarget_TurnedTargetFrame_SameBurn` (Review Focus 1): the same solve with `_s[_duna].FrameToCommon = q` (q = 30° about x then 50° about z) and the normal `CelestialFrames.ToLocal(q, n̂_common)`, against identity with `n̂_common`: the two Δv within 1e-3 m/s.
  - `PlaneTarget_AlongTheAsymptote_PeriapsisOnly` (Review Focus 2): n̂ = Ŝ → a periapsis-only solve (same Δv as `Solve` with no plane, 1e-6), no NaN.
  - `NoPlane_Unchanged`: the existing tests stay green (they go through the old overloads).
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Aim a transfer's arrival at the plane closest to a given orbit`.

### Task 2: The plane normal through the transfer plan and its file

**Files:**
- Modify: `Pilots/Transfer/TransferPlan.cs`: `TransferRequest.ArrivalPlaneNormal` (`Vector3d`, default zero, "unit, the target body's own frame; zero: none"), `PlanStep.TargetPlaneNormal` (same), `TransferPlan.FinalLegRequest` (`TransferRequest`, null when the last leg starts from `Request`; an estimate, never flown).
- Modify: `Pilots/Transfer/TransferSearch.cs`: `Target` gets `public Vector3d Plane` (a 5-argument constructor; the 4-argument one sets zero), `ApplyTo` writes `TargetPlaneNormal`; `Search` reads `ArrPlane = Q.ArrivalPlaneNormal`; `FinalTarget()` passes it.
- Modify: `Pilots/Transfer/TransferSearch.Descend.cs`: the staged sub-request copies `ArrivalPlaneNormal`; the `Arrival` class gets `public TransferRequest FinalLegRequest`, set to the sub-plan's own `FinalLegRequest ?? sub` where the staged leg is built, and carried by every `Arrival` built from another (`BuildArrival` recursion); `BudgetDirect` passes `step.TargetPlaneNormal` to the new `Solve` overload.
- Modify: `Pilots/Transfer/TransferSearch.Cases.cs`: where `plan.Steps.AddRange(arr.Steps)` (two places), also `plan.FinalLegRequest = arr.FinalLegRequest`.
- Modify: `Pilots/Transfer/TransferSearch.Existing.cs`: the new `Solve` overload with `ArrPlane`.
- Modify: `Pilots/Transfer/TransferSearch.InTarget.cs`: when `ArrInc` is NaN and `ArrPlane` is not zero, `OrbitTargets.InclinationDeg = acos(ArrPlane.z)` in degrees (OrbitPlanner aims an inclination only; 4.3's plane match does the node).
- Modify: `Pilots/Transfer/TransferPilot.cs`: `CopyRequest` copies `ArrivalPlaneNormal`.
- Modify: `Pilots/Transfer/PlanStore.cs`: optional keys `arrivalPlaneNormal` (request), `targetPlaneNormal` (step), `finalLegRequest` (plan), written always, read with `ContainsKey` (absent → zero / null).
- Test: `Assets/Tests/Transfer/TransferSearchTests.cs`, `Assets/Tests/Transfer/PlanStoreTests.cs`

**Interfaces:**
- Consumes: Task 1's `Solve` overload.
- Produces: `PlanStep.TargetPlaneNormal` on every step whose `TargetBody == Request.ToBody` (corrections of the final leg, the final capture, the in-target orbit changes); zero on the others. `TransferPlan.FinalLegRequest` on staged plans.

- [ ] **Step 1: write the failing tests**
  - `KerbinToDuna_PlaneNormal_OnTheFinalTargetSteps`: request with `ArrivalPlaneNormal = n̂`: every step with `TargetBody == duna` carries n̂ (1e-12), the others zero; `TotalDeltaV` equal to the same search without a normal (1e-6: the plane costs nothing at the search).
  - `KerbinToIke_StagedArrival_KeepsTheLastLegRequest`: the case of `KerbinToIke_ExpensiveCorrection_SwitchesToStagedArrival` with a normal: `FinalLegRequest` not null, `FromBody == duna`, `ToBody == ike`, its `ArrivalPlaneNormal` is n̂, and the Ike steps carry n̂; the direct cases give null.
  - `TargetIsCurrentBody_PlaneNormal_GivesItsInclination`: `TargetIsCurrentBody_PlansTheOrbitThere`'s setup with n̂ at 40° → the last orbit change's `TargetInclinationDeg` is 40 (1e-9).
  - `RoundTrip_KeepsThePlaneNormalsAndTheLastLegRequest` (PlanStore).
  - `OldFile_WithoutTheNewKeys_ReadsAsBefore`: the JSON of `RoundTrip_KeepsEveryField` with the three keys removed → zero normals, null `FinalLegRequest`, no error (Review Focus 4).
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green, every existing `TransferSearchTests` and `PlanStoreTests` included.
- [ ] **Step 5: commit** `Carry the arrival plane through the transfer plan and its file`.

### Task 3: `FarRendezvous`, the chain's plan (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Rendezvous/FarRendezvous.cs` (namespace `K2D2.Rendezvous`)
- Test: `Assets/Tests/Transfer/FarRendezvousTests.cs`

**Interfaces:**
- Consumes: `TransferSearch.Find(BodySystem, TransferRequest, CancellationToken, IProgress<float>)`, `TransferPlan`, `PatchedPropagator.Propagate`, `Impulse`, `BodySystem.StateRelativeToParent`, `CelestialFrames.ToLocal`, Task 1's `ArrivalTargeting.IncomingAsymptote` / `ClosestPlaneNormal` / `PlaneResidualDeg`, `RendezvousLaunch.ParkingRadius`, `RendezvousPlanner.Plan`, `RvPlan`, `RendezvousFlight.DockReserve`, `StageBudget.Check`, `KeplerPropagator.Propagate`, `OrbitMath.Elements`, `TransferReadout.Steps`, `RendezvousReadout.StepRow`.
- Produces:
  ```csharp
  public delegate TransferPlan TransferFinder(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress);

  public class FarRendezvousRequest
  {
      public int FromBody, TargetBody;     // indices in the BodySystem
      public Vector3d R0, V0;              // our vessel, relative to FromBody, common frame
      public double UT0;
      public Vector3d TargetR, TargetV;    // the target vessel at UT0, relative to TargetBody, ITS body's own frame
      public string TargetName = "";
      public bool Dock = true;
      public double MaxTripSeconds = double.PositiveInfinity;
  }

  public class FarRendezvousPlan
  {
      public bool Ok; public string Refusal = "";
      public TransferPlan Transfer;
      public double ParkingRadius; public string ParkingReason = "";
      public Vector3d TargetPlaneNormal;               // target body's own frame
      public Vector3d ArrivalAsymptote;                // Ŝ, same frame
      public string AsymptoteSource = "";              // "propagated", "last leg", "closest approach"
      public double PlaneResidualDeg;
      public RvPlan Rendezvous;                        // the worst phase sample
      public double RendezvousBest, RendezvousWorst;   // m/s, over the phases planned
      public int PhasesPlanned;                        // of PhaseSamples
      public double DockReserve;
      public double TotalDeltaV => (Transfer?.TotalDeltaV ?? 0) + RendezvousWorst + DockReserve;
      public string Explanation = "";
  }
  ```
- Produces `public static class FarRendezvous` with:
  - `const int PhaseSamples = 12; const double PlaneChangeLogDeg = 0.01;`
  - `static string TargetRefusal(BodySystem s, int ourBody, int targetBody, bool exists, bool landed, OrbitElements target)`: null when fine; texts, in this order: `"Choose a target vessel."`, `"The target is landed: a rendezvous needs a vessel in orbit."`, `"The target orbits {body}, like us: use Rendezvous + dock."`, `"The target orbits {body}: a rendezvous around the star is not planned."` (root body), `"The target is not on a closed orbit of {body}."` (open, or apoapsis beyond the SOI), `"The target's periapsis ({km} km) is inside the atmosphere or under the terrain of {body}."` (Pe ≤ radius + max(atmosphere, terrain)).
  - `static TransferRequest TransferRequestFor(FarRendezvousRequest q, double parkingRadius)`: `Arrival = Orbit`, Pe = Ap = parking, `ArrivalInclinationDeg = NaN`, `ArrivalPlaneNormal = Cross(TargetR, TargetV).normalized`, R0/V0/UT0/FromBody/ToBody/MaxTrip copied.
  - `static bool ArrivalAsymptote(BodySystem s, TransferPlan plan, out Vector3d sHatLocal, out string source)`: start = `plan.FinalLegRequest ?? plan.Request`; impulses = the steps with `UT ≥ start.UT0` and a non-zero `DeltaV`; `Propagate(s, start.FromBody, start.R0, start.V0, start.UT0, plan.ArrivalUT + 0.25·max(0, ArrivalUT − start.UT0) + 3600, impulses, 12)`; the first patch around `ToBody` gives `IncomingAsymptote(ToLocal(F, StartR), ToLocal(F, StartV), μ)` (a closed arrival: its start velocity, normalized). Without one: the closest approach in the patches around `ToBody`'s parent (vessel velocity − `StateRelativeToParent(ToBody, t)` velocity at that time, turned to local). `source` = `"propagated"`, `"last leg"` (when `FinalLegRequest` was used) or `"closest approach"`. False when none.
  - `static RvPlan EstimateRendezvous(Vector3d sHat, Vector3d targetNormal, double parkingRadius, double mu, double arrivalUT, Vector3d targetR, Vector3d targetV, double targetUT, double lowOrbitRadius, CancellationToken ct, out double best, out int planned)`: ĥ = `ClosestPlaneNormal(sHat, n̂)` (zero → n̂); p̂ = normalize(Cross(sHat, ĥ)) (the B side, where the periapsis lies); our state r = R·p̂, v = sqrt(μ/R)·normalize(Cross(ĥ, p̂)); for k in 0..11: the target propagated from `targetUT` to `arrivalUT + k·P_T/12`, then `RendezvousPlanner.Plan(r, v, rTk, vTk, μ, arrivalUT, lowOrbitRadius, ct: ct)`. Returns the Ok plan of highest `TotalDeltaV` (its plane match is inside, never added twice), `best` the lowest, `planned` the Ok count; null when none.
  - `static FarRendezvousPlan Plan(BodySystem s, FarRendezvousRequest q, TransferFinder find, CancellationToken ct, IProgress<float> progress = null)`: target elements → `TargetRefusal` (exists and landed are checked by the glue: pass true/false) → `ParkingRadius` → `find(…)` (refusal `"No transfer to {body}: {refusal}"`) → `ArrivalAsymptote` (refusal `"Cannot predict the arrival geometry at {body}: plan the transfer in the Transfer tab, then the rendezvous there."`) → residual → `EstimateRendezvous` (refusal `"No rendezvous with {target} from the {km} km orbit at {body}: {first planner refusal}"`) → `DockReserve = q.Dock ? RendezvousFlight.DockReserve : 0`.
  - `Explanation`: `"Transfer to {body} into a {km} km orbit ({ParkingReason}), aimed at {target}'s plane ({i:F2}°): {res:F2}° left after the arrival correction ({pm:F0} m/s plane match). Rendezvous planned at the arrival from the actual phase: {best:F0} to {worst:F0} m/s over {n} phases, the worst budgeted."` (" Docking reserve 10 m/s." when docking).
  - `static BudgetResult Budget(TransferPlan transfer, int from, double rendezvousDeltaV, double dockReserve, IReadOnlyList<StageDv> stages)`: `StageBudget.Check` on the transfer steps from `from` (captures flagged as in `TransferFlight.RemainingBudget`), then one step of `rendezvousDeltaV`; reserve `transfer.ReserveDeltaV + dockReserve`.
  - `static List<RendezvousReadout.StepRow> Rows(FarRendezvousPlan p, BodySystem s, double now, int transferDoneBefore)`: `TransferReadout.Steps(p.Transfer, s, now, transferDoneBefore)` mapped field by field, then `"Plane match left ({body})"` (only above 0.05°: `{res:F2}°`, Δv of the worst plan's `PlaneMatch` step, When `"after the arrival"`), `"Rendezvous with {target} (planned at the arrival)"` (Δv `"up to {worst:F0} m/s"`, explanation `"The target's phase at the arrival is not known yet: {best:F0} to {worst:F0} m/s over {n} phases."`), `"Docking"` (Δv `"10 m/s reserve"`) when docking.
  - `static string Totals(FarRendezvousPlan p, BudgetResult budget)`: `"Total {T:F0} m/s (transfer {a:F0}, rendezvous up to {b:F0}, docking reserve {c:F0})"` + the budget's `"; the vessel has {X} m/s, {Y} m/s left"` or `"short by {Y} m/s"`, as `RendezvousReadout.Totals`.
- **4.6 hook:** `Plan` never calls `TransferSearch.Find` itself: the glue passes `TransferSearch.Find` as `find`. When 4.6 exists, it passes its assist-aware search; the plane rides on the request's final target (`FinalTarget()`), `ArrivalAsymptote` follows any steps with vectors, and `Rows` shows any steps. Nothing else is prepared for 4.6.

- [ ] **Step 1: write the failing tests** (`KerbolFixture`; our vessel in a 100 km circular equatorial Kerbin orbit, as `ArrivalTargetingTests.FromLko`)
  - `TargetRefusals`: one assert per text, null for a 200 km circular Duna orbit at 30°.
  - `TransferRequestFor_CircularParkingInTheTargetsPlane`: target 200 km circular → Pe = Ap = 190 km above Duna's radius, normal = rT × vT normalized; target 75 km above a 50 km-atmosphere Duna (Pe below the 70 km low orbit + 10) → the low orbit.
  - `ArrivalAsymptote_KerbinDuna_Propagated`: `source == "propagated"`, unit, and within 2° of the Ŝ of the arrival patch flown with the real corrections (`FlyBurnsTo` style: re-use `ArrivalTargetingTests`' way of flying, or compare with the uncorrected arrival patch).
  - `ArrivalAsymptote_KerbinIke_Staged_UsesTheLastLeg`.
  - `Estimate_WorstOfTwelve_AndPlaneMatchInside`: a known geometry (Ŝ, n̂ 20° apart): `planned == 12`, worst ≥ best, the worst plan has a `PlaneMatch` step whose Δv ≈ 2·v_c·sin(Δi/2) with Δi = `PlaneResidualDeg` (2 %).
  - `Plan_KerbinToDunaStation_Ok`: target 200 km, 30°, node 70°: `Ok`, `TotalDeltaV` = transfer total + worst + 10 (1e-6), explanation names the parking orbit and the residual.
  - `Plan_TurnedTargetFrame_SameTotal` (Review Focus 1): Duna's `FrameToCommon = q`, the target's state given as `ToLocal(q, …)` → same `TotalDeltaV` (1e-3) and `PlaneResidualDeg` (1e-6).
  - `Plan_AsymptoteAlongTheNormal_NoNaN` (Review Focus 2): a fake `find` returning a canned plan whose arrival Ŝ equals n̂ → finite residual ~90°, finite total.
  - `Plan_UsesTheInjectedSearch` (4.6 hook): a fake `find` that records its request and returns a refusal → `Refusal` starts with "No transfer to Duna", the recorded request has the normal and the parking Pe.
  - `Budget_CaptureCheckedBeforeTheRendezvous` (Review Focus 5): stages that pay the transfer but not the rendezvous → `Ok == false`, `Refusal` starts with "Not enough Δv"; stages short at the capture → the capture message.
  - `Rows_TransferThenPlaneThenRendezvousThenDocking` and `Totals_Text`.
  - `Cancelled_Throws`.
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green. Write the plan time of `Plan_KerbinToDunaStation_Ok` to `TestContext.Out` (search + 12 rendezvous plans).
- [ ] **Step 5: commit** `Plan a transfer, rendezvous and docking with a target around another body`.

### Task 4: Chain rules, texts and the saved chain (pure)

**Files:**
- Modify: `Pilots/Transfer/PlanStore.cs`: `public class SavedChain { public string TargetId, TargetName, TargetBodyName, Mode; public Vector3d PlaneNormal; public double ParkingRadius, RendezvousDeltaV, DockReserve; }`, `SavedPlan.Chain` (null by default), optional key `chain` (written only when not null, read with `ContainsKey`; `Mode` must parse as an `RvMode`, else a format error).
- Modify: `Pilots/Rendezvous/FarRendezvous.cs` (the texts and rules below)
- Test: `Assets/Tests/Transfer/FarRendezvousTests.cs`, `Assets/Tests/Transfer/PlanStoreTests.cs`

**Interfaces (in `FarRendezvous`):**
- `static bool IsFar(string ourBody, string targetBody)`: both known and different (ordinal).
- `static string FarHint(string target, string targetBody)`: `"{target} orbits {targetBody}: Transfer + rendezvous + dock plans the transfer, the rendezvous and the docking."`
- `static string HandoffRefusal(bool exists, bool landed, bool aroundArrivalBody, string arrivalBody)`: null, or `"the target vessel no longer exists (recovered, destroyed, or docked elsewhere)"`, `"the target is landed"`, `"the target no longer orbits {arrivalBody}"`.
- `static string ChainStopped(string stepName, string target, string why, bool afterArrival)`: `"Transfer + rendezvous with {target} stopped during {stepName}: {why without final dot}. Left to do: {left}"`; left = `"press Resume (Rendezvous or Transfer tab): the transfer is planned again from here, then the rendezvous follows"` before the arrival, `"the vessel is in its parking orbit: choose a target in the Rendezvous tab and start the rendezvous there"` after it.
- `static string HandoffStarted(string target, bool dock, bool prepareOnly)`: `"In the parking orbit: the Rendezvous pilot takes over ({rendezvous + docking | rendezvous only} with {target}{, Prepare only}), see the Rendezvous tab"`.
- `static string ResumeOffer(string transferOffer, string target)`: `transferOffer + " Then the rendezvous with " + target + "."`
- `static bool PlaneChanged(Vector3d before, Vector3d now, out double deg)`: angle > `PlaneChangeLogDeg`.

- [ ] **Step 1: write the failing tests**: one per text above; `IsFar` with null and equal names; `PlaneChanged` at 0.005° and 0.02°; `RoundTrip_KeepsTheChain`; `Chain_UnknownMode_Refused`; `NoChainKey_ChainNull` (Review Focus 4).
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Chain texts and the saved chain for a far rendezvous`.

### Task 5: The chain in the Transfer pilot (game glue)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Transfer/TransferPilot.Chain.cs` (partial `TransferPilot`)
- Modify: `Pilots/Transfer/TransferPilot.cs`: `Plan(TransferInput)` clears the chain; `OnPlanned`, `Start`, `Resume` (its `default` and `Replan` branches, `CorrectExistingPath`) take their budget from `BudgetFor(plan, from)`; `Save` sets `sp.Chain`; `RefreshOffer` reads `saved.Chain` and wraps the offer with `FarRendezvous.ResumeOffer`; `Stop` and `onReset` write `FarRendezvous.ChainStopped(…, afterArrival: false)` as the status when a chain is set.
- Modify: `Pilots/Transfer/TransferPilot.Flight.cs`: the correction solve (around `double wanted = step.TargetPeriapsisRadius, inc = …`) uses Task 1's overload with `step.TargetPlaneNormal` (after `ReadTargetPlane`), and its log adds `plane error predicted {e:F3}°` (`ArrivalPlaneErrorDeg`, computed on the worker); `Finish` calls `HandOver(body, r, v)` after its own end when a chain is set; `Fail` writes `ChainStopped` when a chain is set; the leg re-plan after `MergeLeg` checks `BudgetFor`.

**Interfaces:**
- Consumes: Tasks 1–4; `RendezvousSnapshot.FindVessel(id)`, `RendezvousSnapshot.SetTarget(ours, id, what)`, `RendezvousPilot.Instance.Start(RvMode, string, bool)` and `.Status`; `GameBodySystem.IndexOf`; `PilotJob`.
- Produces on `TransferPilot`:
  - `public SavedChain Chain { get; private set; }` and `public FarRendezvousPlan ChainPlan { get; private set; }` (null when none);
  - `public void PlanChain(string targetId, bool prepareOnly)`: snapshot (`TakeSnapshot`), target read on the main thread (`FindVessel`, its `Orbit.referenceBody`, `GetRelativePositionAtUTZup`/`GetOrbitalVelocityAtUTZup` at the snapshot UT, **no frame conversion**), `FarRendezvous.TargetRefusal`; `SetTarget(ours, target.GlobalId, "the rendezvous target")`; then `FarRendezvous.Plan(system, q, TransferSearch.Find, ct, progress)` on the worker (`JobKind.Search`, so `Searching` and the progress bar work). On the result: `Current = plan.Transfer`, `ChainPlan`, `Chain` (from the plan), `Budget = BudgetFor(Current, 0)`; refused or short → `Status` with the reason, nothing flown; else `Begin(Current, 0, prepareOnly, snap, "start (transfer + rendezvous)")`.
  - `BudgetResult BudgetFor(TransferPlan plan, int from)`: `Chain == null` → `TransferFlight.RemainingBudget`; else `FarRendezvous.Budget(plan, from, Chain.RendezvousDeltaV, Chain.DockReserve, stages)`.
  - `Vector3d ReadTargetPlane(PlanStep step)`: when a chain is set and `step.TargetBody == Request.ToBody`: the target's normal now, in its body's frame, if it exists, is not landed and orbits that body; a change above 0.01° is logged and written to the step and to `Chain.PlaneNormal`; otherwise the stored normal, logged once.
  - `void HandOver(int body, Vector3d r, Vector3d v)`: after `Finish` has ended the run (`isRunning == false`, saved plan deleted): `FarRendezvous.HandoffRefusal` on the target → refused: `Status = ChainStopped(…, afterArrival: true)`; else `RendezvousPilot.Instance.Start(Mode, TargetId, PrepareOnly)` and `Status = HandoffStarted(…)` or `ChainStopped(…, "the Rendezvous pilot refused: " + rv.Status, true)`. Then `Chain = null`, `ChainPlan = null`. The transfer ends first, so the Rendezvous pilot's `ResetControllers` never resets a running transfer (as 4.4).
  - Resume with a chain: `Replan` goes through the chain search when the target can be read (estimate and budget again), else through the plain search with the saved normal and a status `"{target} is gone: the transfer goes on to the {km} km orbit, the rendezvous will not follow."` (decision 6); `Continue` keeps `Chain` and its saved `RendezvousDeltaV`.

**Logs to add** (`[Transfer] chain:`, invariant culture):
- `planning transfer + rendezvous{ + dock} with {name} ({id}) around {body}: target Pe {km} km, Ap {km} km, inclination {i:F3}° (game orbit.inclination {raw}), normal ({x:F5}, {y:F5}, {z:F5}) in {body}'s frame; parking {km} km ({reason}); {Prepare only | flown}`
- `arrival asymptote ({x:F4}, {y:F4}, {z:F4}) ({source}), plane left after the arrival correction {res:F3}°`
- `rendezvous estimate at UT {t:F0} from a circular {km} km orbit: {n}/12 phases planned, {best:F0} to {worst:F0} m/s (plane match {pm:F1} m/s in the worst); planned in {ms} ms`
- `total {T:F0} m/s = transfer {a:F0} + rendezvous {b:F0} + docking reserve {c:F0}; stage budget {ok | refusal}`
- `correction {n}: target plane {re-read, changed {d:F3}° | re-read, unchanged | not readable ({why}): stored plane kept}`
- `in orbit {OrbitText}, plane error to {target} {e:F3}°, distance {km} km; handing over ({mode}{, Prepare only})`
- `hand-over {started | refused: {why}}`
- `stopped during {step}: {why}` and `resume: target {found | gone}, {Replan | Continue}`

- [ ] **Step 1:** implement the file and the modifications, with the log lines.
- [ ] **Step 2:** check by reading that a plan without a chain goes through exactly the old paths (`BudgetFor` returns `RemainingBudget`; the correction passes a zero normal, which Task 1 tested as unchanged).
- [ ] **Step 3:** run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 4: commit** `Fly a transfer into a target's plane and hand over to the rendezvous`.

### Task 6: The Rendezvous tab

**Files:**
- Modify: `Assets/UI/K2D2_UI/Dock.uxml`: after the `rendezvous_dock` button, `<ui:Button name="transfer_rendezvous_dock" text="Transfer + rendezvous + dock" class="big" style="height: 30px; display: none;" tooltip="The target orbits another body: transfer into its plane, then rendezvous and dock" />`. Nothing else.
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Rendezvous/RendezvousUI.cs`
- Test: `Assets/Tests/UI/WindowLayoutTests.cs` (`RendezvousTab_TakesTheDockSlot_WithEveryElementItsPageUses`: add the button to the list)

**UI behaviour:**
- `Elements` gains `"transfer_rendezvous_dock"`.
- `UpdateRelative`: when `FarRendezvous.IsFar(our body, target body)`, `far = true`, `relative_label` shows `FarRendezvous.FarHint(…)` instead of the `TryRead` error.
- `UpdateControls`: `transfer = TransferPilot.Instance`; `busy` also counts `transfer.isRunning || transfer.Searching` when `transfer.Chain != null`. The far button is shown only when `far`, enabled when not busy; the three 4.3 buttons are disabled when `far`. Its click: `transfer.PlanChain(selected_target, RendezvousSettings.prepare_only.V)`.
- Stop: when the chain is in its transfer leg (`transfer.Chain != null` and running or searching) → `transfer.Stop()`, else `pilot.Stop()`. Resume / Cancel saved: when the Rendezvous pilot has no saved plan and `TransferPilot` offers a chain resume → `transfer.Resume()` / `transfer.CancelSaved()`; `resume_offer` shows `transfer.ResumeOffer` then.
- Status: during the chain's transfer leg (or its search, or its refusal), the status shows `transfer.Status`; the progress bar `transfer.SearchProgress` while searching.
- `UpdatePlan`: when `transfer.ChainPlan != null` and the Rendezvous pilot has no plan → rows `FarRendezvous.Rows(transfer.ChainPlan, transfer.System, now, transfer.isRunning ? transfer.StepIndex : 0)` through the existing `StepElement`, totals `FarRendezvous.Totals(transfer.ChainPlan, transfer.Budget)` (coral when not Ok, as today), stages as today.
- No other element, no new style (UI rule).

- [ ] **Step 1:** extend the layout test; run it. Expected: FAIL (no `transfer_rendezvous_dock`).
- [ ] **Step 2:** implement.
- [ ] **Step 3:** run the test command. Expected: all green.
- [ ] **Step 4: commit** `Add Transfer + rendezvous + dock to the Rendezvous tab`.

### Task 7: Build, install, document (controller)

- [ ] **Build** the zip (pipeline `Deploy to Zip File`; exit code 1 is normal: judge on the ThunderKit log and the zip). The project compiles against 26w42c; check the DLL against 26w46a as for the 2026-11-18 build before installing. **Install** only if the game is closed (`tasklist | grep -i ksp2`), keeping `plans/` and the settings json.
- [ ] **CHANGELOG** (Unreleased), see below. **README:** the Rendezvous line names the new button. **Parent `CLAUDE.md`:** a 4.5 bullet under "Transferts, rendez-vous et amarrage" (plan path, the eight decisions, "non testé en jeu", the log lines). **`tasks/skyspaceagency/todo.md`** (parent repo): tick 4.5 as built and add section 22 to the installed-not-tested list.
- [ ] **Checklist:** append section 22 below to `docs/testing/2026-10-24-transfers-in-game-checklist.md`.
- [ ] **Commit and push** the submodule, then the parent pointer (`git add SkySpaceAgency`), own files only.

#### CHANGELOG to add

Under `### Added`:

"**Rendezvous tab: "Transfer + rendezvous + dock" for a target around another body** (sub-project 4.5; not yet flown in game). One press plans a transfer into a circular orbit 10 km under the target's periapsis (at least the low orbit, at most the low orbit + 150 km), in the plane closest to the target's that the arrival allows: the arrival corrections aim the plane, no separate burn. The rest of the plane is flown by the Rendezvous pilot's plane match, and budgeted. The rendezvous itself is planned at the arrival, from the target's actual phase; the plan budgets the worst of 12 phases, plus 10 m/s for the docking. The tab's plan table shows every transfer step, the plane match left, the rendezvous and the docking, and the total; the start is refused, with the numbers per stage, when the vessel cannot pay for it. Then the mod flies the transfer (or makes its nodes with Prepare only), and in the parking orbit hands over to the Rendezvous pilot: rendezvous, then docking. A stop says which step and what is left; Resume, in the Rendezvous or the Transfer tab, plans the transfer again from where the vessel is, and the rendezvous still follows. If the target disappears during the transfer, the vessel still reaches the parking orbit and the hand-over stops with the reason."

Under `### Fixed`:

"**Lift "Rendezvous" messages and warp** (follow-ups of 4.4, not yet flown in game): a refusal on the pad no longer advises "press Start again" (it says nothing was launched); a stop caused by leaving the flight scene, a vessel change or the mod being off in the campaign says so, instead of "Stop pressed, or another pilot started"; the first plan of the launch passes now sets the warp to 1×, like the re-plans. A target around another body points to the Rendezvous tab's new button."

#### Section 22 to append (French)

```markdown
## 22. Transfert + rendez-vous + amarrage vers une cible autour d'un autre corps (4.5)

Préparer une station en orbite circulaire d'environ 200 km autour de la Mun (puis, si possible, autour de Minmus, dont le
repère est tourné), inclinée d'environ 20°, avec un port d'amarrage libre. Le vaisseau part d'une orbite basse de Kerbin avec
un port compatible. Sauvegarde rapide avant chaque essai. Lire `Player.log`, lignes `[Transfer] chain:`, `[Transfer]` puis
`[Rendezvous]` et `[Docking]`.

1. **Plan.** Onglet Rendezvous, choisir la station : le texte dit qu'elle orbite la Mun, seul le bouton « Transfer +
   rendezvous + dock » est actif. Appuyer.
   - Ligne `planning transfer + rendezvous` : Pe, Ap, `inclination` comparée à `game orbit.inclination` (noter l'unité),
     orbite d'attente 190 km.
   - Lignes `arrival asymptote` (source `propagated`), `rendezvous estimate` (12 phases, fourchette de Δv, temps en ms :
     le jeu ne doit pas geler) et `total … = transfer … + rendezvous … + docking reserve 10`.
   - Le tableau du plan montre chaque étape du transfert, « Plane match left » si plus de 0,05°, « Rendezvous … (planned at
     the arrival) » avec « up to … m/s », « Docking », puis le total et la marge.
2. **Refus faute de Δv.** Avec un vaisseau trop juste pour le rendez-vous mais assez pour le transfert : refus « Not enough
   Δv … Per stage … », aucun burn.
3. **Corrections.** À chaque correction, une ligne `target plane re-read` et `plane error predicted … °` dans la ligne de
   correction. Comparer à `plane left after the arrival correction`.
4. **Arrivée et relais.** Après la capture : `in orbit … plane error to … °` (proche de la prévision), puis `hand-over
   started`. Le pilote Rendezvous planifie depuis la phase réelle (`[Rendezvous]`, plane match éventuel en premier), puis
   l'amarrage (sections 9 et 10). Comparer le Δv du rendez-vous réel à la fourchette prévue.
5. **Prepare only.** Même essai avec Prepare only : un nœud à la fois pour le transfert, puis pour le rendez-vous ;
   l'amarrage reste piloté.
6. **Arrêt et reprise.** Stop pendant la croisière : message « stopped during … Left to do: press Resume … ». Recharger la
   sauvegarde rapide : l'onglet Rendezvous propose de reprendre (« … Then the rendezvous with … »). Resume : le transfert
   reprend (`resume: target found`) et le relais a lieu à l'arrivée.
7. **Cible perdue.** Pendant la croisière, récupérer ou supprimer la station : le transfert continue jusqu'à l'orbite
   d'attente (`not readable … stored plane kept`), puis `hand-over refused` avec la raison, aucun burn, warp à 1×.
8. **Cibles refusées.** Une cible posée, une cible autour du Soleil, une cible autour de notre propre corps (ce sont alors
   les boutons de la section 9) : refus immédiat avec le message.
9. **Repère tourné.** Station autour de Minmus : `plane error to …` à l'arrivée du même ordre que la prévision (pas
   d'écart de dizaines de degrés).
10. **Correctifs 4.4.** Lift « Rendezvous » : une cible refusée sur le pas de tir dit « Nothing was launched » ; quitter le
    vol pendant l'attente donne « the scene changed (flight left) » ; lancer le plan sous warp : ligne `warp set to 1x
    while the passes are planned`.
```

---

## Only verifiable in game

- The unit of `orbit.inclination` and the agreement of the target's normal (its body's frame) with the frame of our transfer patches turned by `FrameToCommon`, on a turned moon (Minmus): lines `plane error predicted` against `plane error to … at the arrival`.
- The real plane error after the arrival corrections and the capture, against the predicted residual.
- The real rendezvous Δv at the arrival against the worst-of-12 budget.
- That `RendezvousPilot.Start` called from `TransferPilot.Finish`, in the same frame, plans from the fresh orbit without a reset race (as 4.4's hand-over).
- The plan time (search + 12 rendezvous plans) on the player's machine, on the worker thread.
- Resume of a chain after a save and reload, and the Rendezvous tab's resume offer.
- The three 4.4 fixes (texts and warp).

## Answers from the user (2026-11-18)

1. **Rendezvous budget:** keep the worst of the 12 target phases (+10 m/s docking reserve).
2. **Target lost during the transfer:** keep flying to the parking orbit, then stop the hand-over and say why.
3. **Buttons:** two buttons, "Transfer + rendezvous" and "Transfer + rendezvous + dock", like the 4.3 buttons (Task 6 adds both; the chain carries `RvMode.RendezvousOnly` or `RvMode.RendezvousAndDock`).
