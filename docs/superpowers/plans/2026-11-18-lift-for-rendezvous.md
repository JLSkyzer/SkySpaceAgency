# Lift for rendezvous (sub-project 4.4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the goal "Rendezvous with [vessel]" to the Lift tab. On the pad, the mod picks the launch pass that both puts the vessel in the target's plane and gives the cheapest rendezvous after the ascent. It then:
1. waits in warp (throttle held at 0) and launches on the solver's heading, steering toward the plane;
2. reaches a parking orbit 10 km under the target's periapsis (at least the body's low orbit);
3. hands over to the 4.3 Rendezvous pilot (rendezvous, then docking if asked), whose plane match takes out what the launch could not reach.

**Architecture:**
- **Pure code**, tested in `Assets/Tests/Transfer/` (it needs `KerbolFixture`):
  - `LaunchWindow` gains the insertion point of a pass and an enumeration of the next passes (shared with 4.2, unchanged otherwise);
  - a new `RendezvousLaunch` (namespace `K2D2.Lift`): parking radius, target checks, predicted insertion state, and the pass choice, each pass scored by `RendezvousPlanner.Plan` (4.3) from the predicted insertion state;
  - `LiftGoalRules` gains the rendezvous decisions and texts.
- **Game glue:** the rendezvous branch of `WaitLaunch` (new partial file), a `Handoff` status with its controller, the `LiftTransferPlan` generalised to carry either goal, and the Lift tab's selector extended (one option, the vessel list in the existing target dropdown, one toggle).

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w46a in game (project still on the 26w42c template).

**Spec:** `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`, sections 4.4 and 6b. Builds on the 4.2 plan (`docs/superpowers/plans/2026-10-24-lift-for-transfer.md`) and the 4.3 plan (`docs/superpowers/plans/2026-10-24-rendezvous-and-docking.md`).

## Global Constraints

- **Inherited:** the Global Constraints of the 4.1, 4.2 and 4.3 plans (logs in English with the `[Lift]` prefix, licence and CHANGELOG, stage your own files only, commit message ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`, push `origin main`, never commit game DLLs).
- **Test command** (never two Unity jobs at once; `S` = your session's scratchpad):
  ```
  rm -f "$S/results.xml"; "/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
  ```
  Passes when `results.xml` shows every test passed and `tests.log` has 0 `error CS`.
- **Another session commits in this submodule:** `git status` and `git diff` before each `git add`; stage only the files of the task.
- **Goals Orbit and Prepare transfer stay exactly as today**, settings, behaviour and tests included. `LiftGoal.Rendezvous` already exists in `Pilots/Lift/LiftGoal.cs` (hidden until now).
- **Launch time:** the site passes under the target's plane **and** the predicted rendezvous after the ascent is the cheapest among the passes within the horizon (Δv first; a phasing wait in orbit is fine). Within 5 m/s of the cheapest, the earliest pass wins.
- **Parking orbit:** circular at the target's periapsis − 10 km, never under the body's `BodyNode.LowOrbitRadius` (atmosphere or highest terrain + 10 km), **and never above `LowOrbitRadius + 150 km`** (user, 2026-11-18): for a high target, Lift stops at that cap and 4.3's phasing raises the rest. Add `const double MaxAboveLow = 150_000;` to `RendezvousLaunch`, a third `ParkingRadius` reason `"capped at the low orbit + 150 km: the 4.3 phasing raises the rest"`, and a test `ParkingRadius_HighTarget_CappedAtLowPlus150`. The player's Ap setting is ignored for this goal (logged); the saved setting is never written (`LiftSettings.SetApOverride`).
- **Plane out of reach** (target inclination below the site latitude): `LaunchWindow` already gives the closest plane and its residual; the residual is shown with its Δv before launch, and 4.3's plane match flies it.
- **No Lift plane trim for this goal** (decision, see "Decisions"): after the circularisation, the hand-over to 4.3, whose closed-loop plane match fires above 0.05° (`RendezvousPlanner.PlaneMatchThresholdDeg`), tighter than 6b's 0.1°.
- **Chain:** at the end, `RendezvousPilot.Instance.Start(RvMode.RendezvousAndDock or RvMode.RendezvousOnly, targetId, prepareOnly: false)`. Prepare only does not apply to 4.4 (6b).
- **Failure in the chain (6b):** stop safely (throttle 0, warp 1×, current orbit), the tab says the step, the reason and what is left; no automatic re-plan. Once 4.3 has started, its own save and Resume apply.
- **Frames:** the site, the vessel and the target are all read from orbits around the launch body, so they share that body's celestial frame (`CelestialFrames`, 2026-11-18). The rendezvous branch never converts them with `ToCommon`/`ToLocal` and never mixes them with `BodySystem` vectors; only scalars come from `BodyNode`.
- **The target vessel is never acted on.** Setting it as our vessel's game target (`RendezvousSnapshot.SetTarget(ours, …)`) is an action on our vessel, so allowed: it pre-selects it in the Rendezvous tab.
- **UI rule (user):** no new interface in the old K2-D2 style. Only: one more goal label, the existing `transfer_target` dropdown filled with vessels for this goal, one `K2Toggle` "Dock after the rendezvous", rows in the existing `transfer_infos` table.

## Decisions taken in this plan (the user can correct them)

1. **No Lift plane trim for the rendezvous goal.** 6b asks for a trim under 0.1° in 4.2 and 4.4. Here the very next pilot (4.3) opens with a closed-loop plane match at the relative node above 0.05°: a Lift trim would be the same burn twice, or a trim then a second tiny match. The 0.1° promise is kept, by 4.3's burn.
2. **Pass horizon:** 4 rotations of the body, at most 12 passes (Kerbin: 24 h, 8 passes). Each pass is scored by a full `RendezvousPlanner.Plan` on the worker thread (the plan time is logged). Values are builder estimates.
3. **Missed launch time:** for this goal a missed pass re-plans every pass (the phase has changed), where 4.2 only re-solves the next window.
4. **No Δv refusal on the pad.** The rendezvous Δv (plane match included) is shown before launch; the stage-by-stage check is 4.3's, at its start, when the vessel is already safe in a closed orbit (anti-goal "stranding" holds).
5. **No persistence of the Lift phase** (pad wait, ascent), as in 4.2: a reload on the pad means Start again, which re-plans. The roadmap's "Lift resume after reload" covers it later.

## Review Focus

1. **Equatorial target from an almost equatorial site** (KSC at −0.097°, station at 0°): the plane is out of reach by 0.1°, and `LaunchWindow` returns `Pass = Any` launches. The enumeration must step through time, not return "now" twelve times, and the residual must show as a ~4 m/s plane change. (Tasks 1, 2)
2. **Eccentric or very high target:** parking at Pe − 10 km; a target whose Pe is inside the atmosphere or under the terrain is refused; a target under the low orbit gives a parking orbit above its Pe, and a planner refusal on every pass becomes a clear refusal, never a launch. (Task 2)
3. **The player warps past the launch time, or the launch does not lift off:** the whole pass choice is planned again; the old phase is never flown. (Tasks 3, 4)
4. **Target gone or changed before the hand-over** (destroyed, recovered, docked elsewhere: new GUID), or landed when planned: the chain stops with "what is left", 4.3 is never started on a stale id. (Tasks 2, 3, 4)
5. **Frame mixing** (lesson 2026-11-18): the plan must depend only on vectors of the same frame; turning the whole scene about the rotation axis must not change the launch time or the Δv. (Task 2)

---

### Task 1: Insertion point and pass enumeration in `LaunchWindow`

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Transfer/LaunchWindow.cs`
- Test: `Assets/Tests/Transfer/LaunchWindowTests.cs` (add tests; use its helpers `Find`, `Ksc`, `Omega`, `Normal`, `Rotate`)

**Interfaces:**
- Produces, in `LaunchWindowResult`: `public Vector3d InsertionPoint;` (unit, inertial, the `q` already computed in `Candidate` at `LaunchUT + lead`) and `public double InsertionUT;` (`LaunchUT + lead`). Set on every result, refusals included (`InsertionUT = LaunchUT`, `InsertionPoint = zero`).
- Produces `public static List<LaunchWindowResult> LaunchWindow.Passes(double bodyRadius, double mu, Vector3d bodyAngularVelocity, Vector3d siteAtUT0, double ut0, Vector3d targetPlaneNormal, double orbitRadius, double ascentLeadSeconds, double ascentDownrangeRad, double horizonSeconds, int maxPasses, double anyTimeStepSeconds)`:
  - calls `Find` from `ut0`, then from the next start time with the site turned about z by ω·(t − ut0);
  - next start = `LaunchUT + anyTimeStepSeconds` when the result is `Pass == Any` launched at its own start time (every moment is a launch: equatorial plane, body not turning), else `LaunchUT + MinPassGapSeconds` (`public const double MinPassGapSeconds = 600`);
  - stops at `maxPasses` results or past `ut0 + horizonSeconds`; only `Ok` results are returned; an empty list when the first `Find` refuses.

- [ ] **Step 1: write the failing tests**
  - `InsertionPoint_LiesInTheReachedPlane`: KSC, plane 28.5°, lead 150 s, downrange 5°: `|Dot(InsertionPoint, ReachedPlaneNormal)| < 1e-9`, `InsertionUT == LaunchUT + 150`.
  - `InsertionPoint_IsTheSiteTurnedAndMovedDownrange`: equatorial site, equatorial plane, lead 100 s, downrange 3°: the angle between `InsertionPoint` and `Rotate(site, Omega(), LaunchUT + 100)` is 3° ± 1e-6.
  - `Passes_Kerbin6Deg_TwoPerRotation_Increasing`: 2 days horizon → 4 passes, strictly increasing, gaps alternating, Ascending/Descending alternating.
  - `Passes_AnyTime_StepsThroughTime`: equatorial site and plane, step 300 s, max 5 → launch times `ut0, ut0+300, …, ut0+1200`.
  - `Passes_OutOfReach_OnePerRotation`: site latitude 10°, plane 5°: passes one rotation apart (± 1 s), all `PlanePass.Any`, each `PlaneErrorDeg` ≈ 5°.
  - `Passes_FirstRefused_Empty` (tilted ω) and `Passes_MaxPassesHonoured`.
- [ ] **Step 2:** run the test command. Expected: FAIL (`InsertionPoint`, `Passes` missing).
- [ ] **Step 3:** implement. `Find`'s results are unchanged apart from the two new fields: every existing `LaunchWindowTests` test stays green.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5: commit** `Give launch windows their insertion point and list the next passes`.

### Task 2: `RendezvousLaunch`, the pass choice (pure)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Lift/RendezvousLaunch.cs` (namespace `K2D2.Lift`)
- Test: `Assets/Tests/Transfer/RendezvousLaunchTests.cs`

**Interfaces:**
- Consumes: `LaunchWindow.Passes` (Task 1); `RendezvousPlanner.Plan(rV, vV, rT, vT, mu, ut0, bodyLowOrbitRadius, standoff = 200, maxRevolutions = 10, ct)` and `RvPlan` (`K2D2.Rendezvous`); `KeplerPropagator.Propagate(r0, v0, mu, dt, out r1, out v1)` (`KSP2FlightAssistant.MathLibrary`); `OrbitMath.Elements` (`K2D2.OrbitPlanning`).
- Produces:
  ```csharp
  public class RendezvousLaunchRequest
  {
      public double BodyRadius, Mu, LowOrbitRadius, ClearRadius;   // ClearRadius: radius + max(atmosphere, terrain)
      public Vector3d Omega, SiteAtUT0;                            // body's celestial frame
      public double UT0;
      public Vector3d TargetR, TargetV;                            // at UT0, same frame
      public double LeadSeconds, DownrangeRad;
  }
  public class RendezvousPass { public LaunchWindowResult Window; public RvPlan Rendezvous; public double DeltaV /* NaN if refused */; public double PhaseDeg; }
  public class RendezvousLaunchPlan
  {
      public bool Ok; public string Refusal;
      public double ParkingRadius; public string ParkingReason;
      public Vector3d TargetPlaneNormal;           // unit, (rT × vT)
      public LaunchWindowResult Window;            // the chosen pass
      public RvPlan Rendezvous;                    // planned from that pass's predicted insertion
      public List<RendezvousPass> Passes;          // every pass scored, for the log
      public int Chosen;
      public string Explanation;
  }
  ```
- Produces `public static class RendezvousLaunch` with:
  - `const double BelowTarget = 10_000, TieDeltaV = 5, HorizonRotations = 4; const int MaxPasses = 12;`
  - `static string TargetRefusal(bool exists, bool landed, string targetBody, string launchBody, OrbitElements target, double clearRadius)`: null when fine; texts:
    `"Choose the rendezvous target."`, `"The target is landed: a rendezvous needs a vessel in orbit."`, `"The target orbits {targetBody}, not {launchBody}: a rendezvous around another body is not built yet (sub-project 4.5)."`, `"The target is not on a closed orbit."`, `"The target's periapsis ({km} km) is inside the atmosphere or under the terrain of {launchBody}."`
  - `static double ParkingRadius(double targetPeriapsisRadius, double lowOrbitRadius, out string reason)`: `max(Pe − 10 km, low)`; reason `"10 km under the target's periapsis"` or `"the low orbit ({km} km): the target's periapsis is too low for 10 km under it"`.
  - `static void Insertion(LaunchWindowResult w, double parkingRadius, double mu, out Vector3d r, out Vector3d v)`: `r = R·InsertionPoint`, `v = sqrt(μ/R)·(ReachedPlaneNormal × InsertionPoint).normalized`.
  - `static double PhaseDeg(Vector3d r, Vector3d rT, Vector3d planeNormal)`: signed angle from us to the target in our direction of motion, (−180, 180].
  - `static int Choose(IReadOnlyList<RendezvousPass> passes)`: lowest finite `DeltaV`; any pass within `TieDeltaV` of it and earlier wins; −1 when none.
  - `static RendezvousLaunchPlan Plan(RendezvousLaunchRequest q, CancellationToken ct)`: elements of the target → `TargetRefusal`-style checks on the orbit (closed, clear) → `ParkingRadius` → `Passes(…, horizon = HorizonRotations·2π/|ω|, MaxPasses, anyTimeStep = target period / 12)` (non-rotating body: horizon = 2 target periods) → for each pass: `Insertion`, the target propagated to `InsertionUT`, `RendezvousPlanner.Plan(r, v, rT', vT', μ, InsertionUT, LowOrbitRadius, ct: ct)`; `DeltaV = Ok ? TotalDeltaV : NaN` (the plane match of a residual is inside it, never added twice) → `Choose`. No pass: refusal `"No launch pass within {h}: {first window refusal}"`; every pass refused by the planner: `"No rendezvous from any of the {n} launch passes: {first planner refusal}"`.
  - `Explanation`: `"Launch on pass {k} of {n} (in {T}): rendezvous for {dv} m/s ({revs} revolution(s), arrival {date}); the first pass would cost {dv1} m/s."`, plus, when `Window.PlaneErrorDeg > 0.05`: `" The target's plane ({i}°) is below the site's latitude ({φ}°): the closest plane is flown, then a {e}° plane match ({dvp} m/s) in orbit."`

- [ ] **Step 1: write the failing tests** (Kerbin from `KerbolFixture`: R 600 km, μ 3.5316e12, low orbit 80 km, clear 670 km; KSC site)
  - `ParkingRadius_10kmUnderPe` (target 120 km circular → 110 km) and `ParkingRadius_NeverUnderLowOrbit` (target Pe 85 km → 80 km, reason says the low orbit).
  - `TargetRefusals` — one assert per text above, null for a 120 km circular target.
  - `Insertion_IsCircularInTheReachedPlane`: |r| = R, |v| = sqrt(μ/R), `r × v` parallel to `ReachedPlaneNormal` (1e-9).
  - `PhaseDeg_TargetAhead_Positive_Behind_Negative`.
  - `Choose_LowestDeltaV`, `Choose_TieWithin5ms_EarliestWins`, `Choose_AllRefused_MinusOne`.
  - `StationAt120km_28Deg_PicksCheapestOfThePasses_AndBeatsTheFirst`: `Plan` returns `Ok`, `Rendezvous.TotalDeltaV` equals the minimum over `Passes` within 5 m/s, and is ≤ the first pass's.
  - `ChosenPass_SimulatedInsertion_RendezvousWithin200m`: propagate the chosen predicted insertion with the plan's impulses (as `RendezvousPlannerTests.IntercepSimulated_EndsWithin200mAndUnder1mps`) → within 200 m and 1 m/s.
  - `EquatorialTargetFromKsc_OutOfReach_ShowsResidual` (Review Focus 1): `Window.PlaneErrorDeg` ≈ 0.097°, explanation names the plane match, the plan has a `PlaneMatch` step, pass launch times all distinct.
  - `TurnedAboutTheAxis_SameLaunchAndDeltaV` (Review Focus 5): site, target state and ω all turned 73° about z → same `Window.LaunchUT` (1e-3 s) and `TotalDeltaV` (1e-6).
  - `TargetUnderLowOrbit_EveryPassRefused_ClearRefusal` (Review Focus 2): target circular at 75 km (above the 70 km clear radius, under the 80 km low orbit). `RendezvousPlanner` refuses any intercept whose lowest radius is under the 80 km low orbit, and the aim point is at 75 km, so every pass is refused → `Ok == false`, `Refusal` starts with "No rendezvous from any of the", and `ParkingRadius` is the low orbit. (4.3 would refuse the same target in orbit: consistent.)
  - `Cancelled_Throws` (token cancelled before the call → `OperationCanceledException`).
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green. Note the per-pass planner time in the test output (`TestContext.Out`).
- [ ] **Step 5: commit** `Choose the launch pass for a rendezvous`.

### Task 3: Lift goal rules and the plan for both goals

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Lift/LiftGoal.cs`
- Modify (replace `p.Parking.PlaneNormal` by `p.PlaneNormal`, no other change): `Pilots/Lift/Controlers/Ascent.cs` (line ~376), `Pilots/Lift/Controlers/PlaneTrim.cs` (~58, ~124), `Pilots/Lift/Controlers/WaitLaunch.cs` (~256)
- Test: `Assets/Tests/Transfer/LiftGoalTests.cs` (add)

**Interfaces:**
- Consumes: `RendezvousLaunchPlan` (Task 2); `LiftPilot.LiftStatus` (adds `Handoff` after `PlaneTrim`, done in Task 4 but declared here so the rules compile: add the member now).
- Produces, in `LiftTransferPlan` (name kept, it now carries either goal):
  - `public LiftGoal Goal = LiftGoal.PrepareTransfer;`
  - `public RendezvousLaunchPlan Rendezvous; public string TargetId; public bool Dock;`
  - `public Vector3d PlaneNormal` → `Goal == Rendezvous ? Rendezvous.TargetPlaneNormal : Parking.PlaneNormal`
  - `Ready` → `Window != null && Window.Ok && (Goal == Rendezvous ? Rendezvous != null && Rendezvous.Ok : Parking != null && Parking.Ok)`
- Produces, in `LiftGoalRules`:
  - `static LiftPilot.LiftStatus NextStatus(LiftPilot.LiftStatus finished, LiftGoal goal)`: `finished.Next()`, except `Circularize` with `Rendezvous` → `Handoff`.
  - `enum ResolveMode { NextPass, ReplanAll }` and `static ResolveMode OnMissedLaunch(LiftGoal goal)`: `Rendezvous` → `ReplanAll`, else `NextPass`.
  - `static string ChainStopped(LiftPilot.LiftStatus step, string target, string why)`: `"Rendezvous with {target} stopped during {step name}: {why}. Left to do: {left}"`; left = `"press Start again on the pad: the launch is planned again"` (WaitLaunch), `"reach orbit by hand, then start the rendezvous from the Rendezvous tab (the target is set)"` (Ascent, Coasting, Adjust), `"circularize, then start the rendezvous from the Rendezvous tab (the target is set)"` (Circularize), `"start the rendezvous from the Rendezvous tab (the target is set)"` (Handoff).
  - `static bool HandoffMessage(bool started, string target, bool dock, string rendezvousStatus, out string text)`: started → true, `"In the parking orbit: the Rendezvous pilot takes over ({rendezvous + docking | rendezvous only} with {target}), see the Rendezvous tab"`; else false, `ChainStopped(Handoff, target, rendezvousStatus)`.
  - `Readout(p, now)`: for `Goal == Rendezvous` the rows are `Launch in`, `Azimuth`, `Target plane` (`{i:F2}°`), `Plane change left` (`{e:F2}° ({dv:F0} m/s)`), `Parking orbit` (`{km} km ({ParkingReason})`), `Rendezvous` (`{dv:F0} m/s, arrival {date}`), `Launch pass` (`{k} of {n}, first pass {dv1:F0} m/s`); while planning `("Rendezvous plan", "planning with {target}...")`; refused `("Rendezvous plan", refusal)`. The transfer rows are unchanged.

- [ ] **Step 1: write the failing tests**
  - `NextStatus_OrbitAndTransfer_Unchanged`: for every `LiftStatus` and both goals, equals `Next()`.
  - `NextStatus_RendezvousAfterCircularize_Handoff`.
  - `MissedLaunch_RendezvousReplansAll_TransferNextPass` (Review Focus 3).
  - `ChainStopped_TextPerStep` (one assert per step above).
  - `Handoff_Started_And_Refused_Texts` (Review Focus 4): started → true and the text names the target and the mode; refused with the status `"No target vessel."` → false, and the text equals `ChainStopped(Handoff, target, "No target vessel.")`.
  - `PlaneNormal_And_Ready_PerGoal`.
  - `Readout_RendezvousRows` and `Readout_TransferRows_Unchanged` (compare with the existing expected rows).
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement; replace the three `p.Parking.PlaneNormal` uses.
- [ ] **Step 4:** run the test command. Expected: all green, existing `LiftGoalTests` included.
- [ ] **Step 5: commit** `Lift goal rules for a rendezvous launch`.

### Task 4: Pad wait, ascent and hand-over (game glue)

**Files:**
- Modify: `Pilots/Lift/LiftSettings.cs`: `public Setting<string> rendezvous_target = new("lift.rendezvous_target", "");` (vessel GUID, `"D"` format) and `public Setting<bool> rendezvous_dock = new("lift.rendezvous_dock", true);`
- Create: `Pilots/Lift/Controlers/WaitLaunch.Rendezvous.cs` (make `WaitLaunch` `partial`)
- Modify: `Pilots/Lift/Controlers/WaitLaunch.cs`: `BeginPlanning` branches to `BeginRendezvousPlanning()` when `settings.goal.V == LiftGoal.Rendezvous`; `Update` also polls `rv_job`; `Start`'s "back from the ascent" and `PadAction.Resolve` go through `LiftGoalRules.OnMissedLaunch` (`ReplanAll` → `BeginRendezvousPlanning`, logging `[Lift] rendezvous: re-planning every pass ({why})`); `Cancel` also cancels `rv_job`; the refusal text for a non-landed vessel names the goal.
- Create: `Pilots/Lift/Controlers/RendezvousHandoff.cs` (`ExecuteController`)
- Modify: `Pilots/Lift/LiftPilot.cs`: `LiftStatus.Handoff` wired in the status setter; `OnStartController` enters `WaitLaunch` for `PrepareTransfer` **or** `Rendezvous`; `Update` uses `status = LiftGoalRules.NextStatus(before, settings.goal.V)` instead of `NextMode()`; `EndLiftPilot(false, why)` with a rendezvous plan rewrites `end_status` with `LiftGoalRules.ChainStopped(status, target, why)` (status read before it is set to Off); `Off` also gives the warp back to 1× when leaving `Handoff`.
- Modify: `Pilots/Lift/Controlers/Final.cs`: no change needed (it already leaves the end to the next status when `lift.transfer != null`); check it.

**Interfaces:**
- Consumes: Tasks 1–3; `RendezvousSnapshot.FindVessel(id)`, `RendezvousSnapshot.SetTarget(ours, id, what)`, `RendezvousPilot.Instance.Start(RvMode, string targetId, bool prepareOnly)` and `RendezvousPilot.Instance.Status`; `GameBodySystem.GameBodies()`, `GameBodySystem.Build`, `GameBodySystem.IndexOf`; `BodyNode.Radius`, `.Mu`, `.LowOrbitRadius`, `.AtmosphereDepth`, `.MaxTerrainHeight`, `.Name`; `BodyRotation.AngularSpeed/ChooseSign/IsDecisive/AngularVelocity`; `LiftGoalRules.AscentLead`; `settings.MeasuredLead(body)`.

**`BeginRendezvousPlanning` (main thread reads, then `Task.Run(() => RendezvousLaunch.Plan(q, token))`):**
1. Landed check, target by `settings.rendezvous_target.V` (`FindVessel`), `TargetRefusal` with the target's elements from its own orbit; any refusal → `refusal` (ended in `Update`, as today).
2. `r`, `v` of our vessel and `TargetR`, `TargetV` all from `vessel.Orbit` / `target.Orbit` `GetRelativePositionAtUTZup` / `GetOrbitalVelocityAtUTZup` at the same UT. **No frame conversion** (Global Constraints).
3. ω and its sign as the transfer branch (`BodyRotation`), lead and downrange from `LiftGoalRules.AscentLead`.
4. `SetTarget(ours, target.GlobalId, "the rendezvous target")`; fill `lift.transfer = new LiftTransferPlan { Goal = Rendezvous, TargetName, TargetId, Dock = settings.rendezvous_dock.V, BodyName, BodyRadius, Mu, OrbitRadius = <set when planned>, Omega, Lead… }` and `lift.ascent.transfer_plan`.
5. When the job ends: `p.Rendezvous = plan`, `p.Window = plan.Window`, `p.OrbitRadius = plan.ParkingRadius`, `settings.SetApOverride(floor((ParkingRadius − R)/1000))`, `PlannedLaunchUT`.

**`RendezvousHandoff`:** `Start` only logs and sets `status_line` (never ends the pilot from `Start`). First `Update`: read the orbit, log the hand-over line, `lift.EndLiftPilot(...)` is **not** used for success; instead `LiftPilot.ChainToRendezvous()` (new, internal): ends the Lift run (`isRunning = false`), then calls `RendezvousPilot.Instance.Start(p.Dock ? RvMode.RendezvousAndDock : RvMode.RendezvousOnly, p.TargetId, false)`, then sets `result_ok`/`end_status` from `LiftGoalRules.HandoffMessage`. Ending Lift first avoids `RendezvousPilot.Begin` → `ResetControllers` resetting a running Lift from inside its own update.

**Logs to add** (`[Lift]`, invariant culture):
- `[Lift] rendezvous: planning with {name} ({id}) from {body}: target Pe {km} km, Ap {km} km, inclination {i:F3}° (game orbit.inclination {raw}), parking {km} km ({reason}); player's Ap {km} km ignored; site latitude {φ:F3}°, ω sign {s}{, NOT decisive}, lead {s} s, downrange {deg}° ({measured|estimate})`
- one per pass: `[Lift] rendezvous: pass {k}/{n} UT {t:F0} ({pass}), azimuth {az:F2}°, plane change {e:F3}° ({dv:F1} m/s), phase at insertion {ph:F1}°, rendezvous {dv:F0} m/s ({revs} rev, arrival UT {t:F0})` or `… refused: {why}`
- `[Lift] rendezvous: chosen pass {k} at UT {t:F1} (in {s} s): {dv:F0} m/s, first pass {dv1:F0} m/s, {n} pass(es) planned in {ms} ms`
- the existing launch / liftoff / ascent-lead / `plane error` lines, unchanged (the plane error now reads against the target's plane).
- `[Lift] rendezvous: in orbit Ap {km} Pe {km} km (parking {km}), plane error to the target {e:F3}°, phase {ph:F1}° (predicted {ph0:F1}°), distance {km} km; handing over ({mode})`
- `[Lift] rendezvous: hand-over {started | refused: <RendezvousPilot.Status>}`
- `[Lift] rendezvous stopped during {step}: {why}` (from `EndLiftPilot`).

- [ ] **Step 1:** implement the settings, the partial `WaitLaunch`, `RendezvousHandoff`, `LiftPilot` changes and the log lines.
- [ ] **Step 2:** check by reading that the Orbit and Prepare transfer paths are untouched apart from `NextStatus` (which returns `Next()` for them, Task 3 test).
- [ ] **Step 3:** run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 4: commit** `Lift into a parking orbit under the target and hand over to the rendezvous`.

### Task 5: Lift tab selector

**Files:**
- Modify: `Assets/UI/K2D2_UI/Lift.uxml`: inside `transfer_goal_group`, after `transfer_target`, add `<K2UI.K2Toggle label="Dock after the rendezvous" name="rendezvous_dock" tooltip="After the rendezvous, dock with a free compatible port (Rendezvous tab rules)" />`; update the `goal` tooltip to name the third goal.
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Lift/LiftUI.cs`
- Test: `Assets/Tests/UI/WindowLayoutTests.cs`: extend `LiftTab_HasTheTransferGoalElements` with `rendezvous_dock` (a `K2Toggle`).

**UI behaviour:**
- `GoalLabels = { "Orbit", "Prepare transfer", "Rendezvous" }`, `GoalLabel` maps all three.
- Rendezvous goal: `transfer_target` lists `RendezvousReadout.Targets` built from `RendezvousSnapshot.VesselsByBody(ours)` keeping only the group of our body (refreshed every 2 s like the body list), pre-selected with `RendezvousReadout.PreSelect(choices, game target id, settings.rendezvous_target.V)`; a header cannot be picked (Id null → ignored). The callback writes `rendezvous_target` for this goal and `transfer_target` for the transfer goal.
- `rendezvous_dock` bound to `settings.rendezvous_dock`, shown only for the rendezvous goal; disabled while running, like the dropdowns.
- Heading hidden for both non-Orbit goals. Hint: `"Choose the target vessel."` or `"Start picks the launch pass for the cheapest rendezvous with {name}, waits on the pad, lifts to {km} km under it and hands over to the Rendezvous pilot."`. Rows from `LiftGoalRules.Readout`.
- No other element, no new style (UI rule).

- [ ] **Step 1:** extend the layout test; run it. Expected: FAIL (no `rendezvous_dock`).
- [ ] **Step 2:** implement.
- [ ] **Step 3:** run the test command. Expected: all green.
- [ ] **Step 4: commit** `Add the rendezvous goal to the Lift tab`.

### Task 6: Build, install, document (controller)

- [ ] **Build** the zip (pipeline `Deploy to Zip File`, exit code 1 is normal: judge on the ThunderKit log and the zip). The project compiles against 26w42c; check the DLL against 26w46a as for the 2026-11-18 build before installing. **Install** only if the game is closed (`tasklist | grep -i ksp2`), keep `plans/` and the settings json.
- [ ] **CHANGELOG** (`### Added`, Unreleased): "**Lift tab: goal "Rendezvous with [vessel]"** (sub-project 4.4; not yet flown in game). On the pad, the mod scores the next launch passes (up to 12 within 4 turns of the body) by the rendezvous each would leave after the ascent, and launches on the cheapest (within 5 m/s, the earliest). It lifts into a circular orbit 10 km under the target's periapsis (at least the low orbit), then hands over to the Rendezvous pilot: rendezvous, then docking when "Dock after the rendezvous" is on. A target plane the site cannot reach is shown with its plane change, flown by the Rendezvous pilot's plane match. A missed launch time plans every pass again. A stop says which step failed and what is left; the Rendezvous tab then has the target pre-selected."
- [ ] **README:** the Lift line names the third goal. **Parent `CLAUDE.md`:** a 4.4 bullet under "Transferts, rendez-vous et amarrage" (plan path, the five decisions, "non testé en jeu", log lines). **`tasks/skyspaceagency/todo.md`** (parent repo): tick 4.4 as built.
- [ ] **Checklist:** append section 21 below to `docs/testing/2026-10-24-transfers-in-game-checklist.md`.
- [ ] **Commit and push** the submodule, then the parent pointer (`git add SkySpaceAgency`), own files only.

#### Section 21 to append (French)

```markdown
## 21. Lift « Rendezvous » depuis le pas de tir (4.4)

Préparer une station en orbite circulaire vers 120 km, inclinée d'environ 6° (une inclinaison de 0° teste le cas 4).
Lire `Player.log`, lignes `[Lift] rendezvous:` puis `[Rendezvous]`.

1. **Plan sur le pas de tir.** Onglet Lift, objectif Rendezvous, cible la station, « Dock after the rendezvous » coché, Start.
   - Une ligne `planning with …` : Pe et Ap de la cible, `inclination` comparée à `game orbit.inclination` (même valeur, en
     degrés ou en radians : noter l'unité), orbite d'attente 110 km.
   - Une ligne par passage (`pass k/n`), puis `chosen pass` : le Δv choisi est le plus bas à 5 m/s près. Noter le temps
     `planned in … ms` (le jeu ne doit pas geler).
   - Le tableau affiche le passage, l'azimut, le plan de la cible, le changement de plan restant et le Δv du rendez-vous.
2. **Attente et lancement.** Warp jusqu'à T-30 s, gaz à 0 pendant l'attente, premier étage tiré à T-0 (comme la section 6).
   La ligne `plane error` décroît pendant la montée.
3. **Passage manqué.** Sur une autre tentative, warper soi-même au-delà de l'heure de lancement : la ligne
   `re-planning every pass` apparaît et un nouveau passage est choisi, jamais un lancement en retard.
4. **Plan hors d'atteinte.** Station à 0° d'inclinaison depuis le KSC : `plane change` d'environ 0,1° (quelques m/s)
   affiché avant le lancement, puis un `PlaneMatch` au début du plan `[Rendezvous]`.
5. **Passage de relais.** Après la circularisation, sans trim Lift : ligne `in orbit … handing over`, puis
   `hand-over started`. Comparer la phase réelle à la phase prédite. L'onglet Rendezvous montre le plan (plane match
   éventuel, phasage, interception) puis l'amarrage (sections 9 et 10).
6. **Arrêt dans la chaîne.** Arrêter Lift pendant l'attente : message « stopped during … Left to do: … ». Supprimer ou
   récupérer la cible pendant la montée : au relais, `hand-over refused`, aucun burn lancé, warp à 1×.
7. **Cible refusée.** Une cible posée au sol, ou en orbite autour de la Mun : refus immédiat avec le message, rien n'est lancé.
```

---

## Not verifiable outside the game

- The real ascent lead and downrange toward a non-default altitude: the phase at insertion depends on them. The stored lead is per body, measured by any non-Orbit ascent, so a 110 km and a 250 km parking share it (log `predicted` against real phase).
- `orbit.inclination`'s unit and the agreement of the target's plane with ours in the body's frame (the cross-check line).
- That `RendezvousPilot.Start` called right after Lift ends, in the same frame, plans from the fresh orbit without a reset race.
- The planner's time for 8 to 12 passes on the player's machine (worker thread, logged).
- That the game target set on the pad survives the launch (staging, scene) and still pre-selects the vessel in the Rendezvous tab.

## Answers from the user (2026-11-18)

1. **Very high targets:** cap the direct ascent at the low orbit + 150 km; 4.3's phasing raises the rest (see Global Constraints, parking orbit).
2. **Plane trim:** decision 1 confirmed: no Lift trim for this goal, 4.3's plane match does it.
3. **Pass horizon:** keep 4 rotations of the body and 12 passes.
