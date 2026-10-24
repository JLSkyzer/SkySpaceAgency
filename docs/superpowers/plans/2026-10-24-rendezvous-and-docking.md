# Rendezvous and docking around the same body (sub-project 4.3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Dock tab becomes **Rendezvous**. The player picks a target vessel that orbits the same body.
- **Rendezvous.** The mod flies a plane match if needed, then phasing (Δv first), then an intercept to 200 m short of the target with one correction midway, and brakes at 200 m.
- **Docking, if asked.** The mod picks a compatible free pair of ports, which the player can change. It aligns on the target port's axis, holds at 50 m and then at 10 m, and makes the final approach at 0.2–0.5 m/s inside the capture cone.
- **Translation** uses RCS, with an axis mapping the mod measures itself, or the main engine when the vessel has no RCS.
- **On doubt** the mod aborts, backs off and retries once.

**Architecture:**
- **Pure code** in namespace `K2D2.Rendezvous`, folder `Pilots/Rendezvous/`, tested in `Assets/Tests/Rendezvous/`:
  - a rendezvous planner, built on `Lambert` (multi-rev) and `KeplerPropagator`;
  - a port-pair chooser;
  - the docking guidance (hold points, speed profile, cone, abort);
  - the RCS axis-mapping probe.
- **Game glue:**
  - reads of the ports, the RCS and the target;
  - a `RendezvousPilot` state machine;
  - a translation controller for RCS or the main engine;
  - the Rendezvous tab, which replaces `Dock.uxml`'s content and keeps its template slot.
- **K2-D2 Docking Assistant** (`Pilots/Docks/`): its controllers are replaced. Their files are removed only once the new tab covers what they did.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c.

**Spec:** `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`, sections 3, 4.3 and 6b. The user's answers also apply:
- ports are picked automatically and can be changed;
- without RCS, docking uses the main engine;
- the target vessel is never touched.

## Global Constraints

- **Inherited from the 4.1 plan.** Its Global Constraints apply here:
  - the game version, the logs, the licence, the test command and the commit rules;
  - closed-loop burns with `BurnGuidance`;
  - one pilot at a time;
  - persistence per vessel GUID in `<mod>/plans/`, resumed only on a click and always re-planned;
  - Prepare only creates one node at a time and never warps.
- **The target vessel is never acted on.** No switching to it, no SAS or port action on it. Its port axis is tracked as it moves.
- **Rendezvous:**
  - a plane match when the relative inclination exceeds 0.05°;
  - phasing chosen Δv first, at most 10 revolutions;
  - an intercept to 200 m short of the target on its orbit, with one closed-loop correction halfway, and braking at 200 m;
  - Prepare only applies to the rendezvous burns, never to docking.
- **Port selection:**
  - Both ports must be `Data_DockingNode.CurrentState == Ready` and compatible: their `NodeTypes` overlap, or `PartComponentModule_DockingNode.IsCompatibleWith` says so.
  - Prefer the pair that needs the least rotation of our vessel, with the target's port on the side facing us.
- **Before the approach:** "control from here" on our port, then set the target to the target's port with `SetTargetByID(part)`.
- **Port axes:** read from the view-side docking transform, so both vessels must be loaded (physics range). The aim point is the port face.
- **Approach:**
  - align on the target port's axis at 50 m;
  - hold at 50 m, then at 10 m;
  - final approach at 0.2–0.5 m/s, inside `CaptureMinFwdDot` and `CaptureMaxRvel`;
  - align the roll when `MinRollDot` requires it.
- **RCS:**
  - turn the RCS action group on, and require at least one `Operational` thruster;
  - measure the X/Y/Z mapping with a probe, never assume it;
  - restore the RCS state at the end.
- **Without RCS:** use the main engine (turn, short burn, turn back), with hold points farther out and wider tolerances. The tab says "No RCS: docking with the main engine, less precise".
- **Abort:** when the lateral error at a distance is outside the cone, or the closing speed is over the limit for that distance, back off along the axis to 20 m and retry once.
- **Refuse when:**
  - the target spins faster than 1°/s around the approach axis;
  - the target is out of physics range at the docking stage;
  - the target orbits another body (that is 4.5's job);
  - there is no free compatible port.
- **End:** `VesselDockedMessage` or `CurrentState == Docked` → "Docked". SAS goes back to Stability Assist, and RCS to its previous state.
- **Logs:** prefixes `[Rendezvous]` and `[Docking]`.
- **Staging:** never stage during the docking approach. During the rendezvous burns, the 4.1 staging guard applies.

## Review Focus

1. **Target ahead versus behind**, and a shared orbit with a phase angle near 0 or 180°. Phasing must pick the cheaper direction and never divide by a zero synodic rate. (Task 1)
2. **A very close start (under 2 km) with a large relative speed.** The plan must brake first, not phase. (Task 1)
3. **A port on the far side of the target.** The guidance must not fly through the target: it goes around via the hold point on the port's axis. (Task 2)
4. **RCS that cannot translate along an axis** (asymmetric blocks). The probe must detect the dead axis, then fall back to main-engine mode for that axis or refuse. (Task 3)
5. **A vessel change at the moment of docking.** The merged vessel gets a new id, and the pilot must end in Docked, not fail. (Task 6)

---

### Task 1: Rendezvous planner

**Files:**
- Create `Pilots/Rendezvous/RendezvousPlanner.cs`.
- Test: `Assets/Tests/Rendezvous/RendezvousPlannerTests.cs`.
- Create `Assets/Tests/Rendezvous/SkySpaceAgency.Tests.Rendezvous.asmdef`, a copy of the Transfer test asmdef.

**Interfaces:**
- Consumes: `Lambert.Solve` (multi-rev, both branches), `KeplerPropagator`, `OrbitMath`.
- Produces:
  - `enum RvStepKind { PlaneMatch, Phasing, Intercept, Correction, Brake }`;
  - `class RvStep { RvStepKind Kind; double UT; Vector3d DeltaV; double DeltaVMagnitude; string Explanation; }`;
  - `class RvPlan { bool Ok; string Refusal; List<RvStep> Steps; double TotalDeltaV; double ArrivalUT; int Revolutions; }`;
  - `static RvPlan RendezvousPlanner.Plan(Vector3d rV, Vector3d vV, Vector3d rT, Vector3d vT, double mu, double ut0, double bodyLowOrbitRadius, double standoff = 200, int maxRevolutions = 10, CancellationToken ct = default)`.

**Method:**
1. A plane match at the relative node, if the relative inclination exceeds 0.05°.
2. Search the intercept over:
   - the departure UT, across the next N vessel periods;
   - the time of flight, from 0.3 to 1.5 × the target's period;
   - the revolutions, from 0 to maxRevolutions;
   - both branches.
3. The arrival point is the target's position at arrival minus 200 m along its velocity.
4. Each candidate's periapsis must clear `bodyLowOrbitRadius`.
5. Keep the candidate with the lowest intercept + braking Δv.
6. Add a correction placeholder midway.
7. Close start (under 2 km): a single Brake.

- [ ] **Step 1: write the failing tests:**
  - `TargetAheadSameOrbit_PhasesCheapest`
  - `TargetBehind_PhasesOtherWay`
  - `PhaseNear0_NoDivideByZero`
  - `PhaseNear180_PicksCheaper`
  - `Inclined_AddsPlaneMatch_WithExplanation`
  - `CloseStart_FastRelative_BrakesFirst`
  - `IntercepSimulated_EndsWithin200mAndUnder1mps`: propagate the plan with impulses.
  - `PeriapsisBelowLowOrbit_CandidateRejected`
  - `MaxRevolutions_Capped`
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5:** commit `Plan rendezvous with a target vessel`.

### Task 2: Docking guidance

**Files:**
- Create `Pilots/Rendezvous/DockingGuidance.cs`.
- Test: `Assets/Tests/Rendezvous/DockingGuidanceTests.cs`.

**Interfaces:**
- Produces:
  - `enum DockPhase { Align, Hold50, Approach10, Hold10, Final, Backoff, Docked, Aborted }`;
  - `struct DockState { Vector3d RelPos; Vector3d RelVel; Vector3d TargetAxis; Vector3d TargetAxisRate; double Time; }`, inertial and relative to the target port's face;
  - `struct DockLimits { double HoldFar = 50; double HoldNear = 10; double FinalSpeedMin = 0.2; double FinalSpeedMax = 0.5; double CaptureMinFwdDot; double CaptureMaxRvel; double MinRollDot; double ConeHalfAngleDeg; bool MainEngineMode; }`;
  - `class DockingGuidance` with:
    - `DockCommand Step(DockState s, double dt)`. A `DockCommand` holds the wanted relative velocity, the wanted attitude (forward = −TargetAxis, plus roll) and a `Phase`.
    - `bool AbortNeeded(out string reason)`, with one retry: back off to 20 m, then retry.

**Behaviour:**
- Speed profile: `v = min(vmax, k·sqrt(d))`, capped at the final speed under 10 m.
- The lateral error is driven toward the axis.
- When our vessel is behind the target relative to the port, it goes to the hold point on the axis first, routing around a keep-out sphere of radius = the target's radius + 10 m (Review Focus 3).
- Main-engine mode: holds at 100 m and 20 m, a final speed of 0.3 m/s, and a wider cone.

- [ ] **Step 1: write the failing tests.** Use a simulated double integrator with thrust limits, and a target port axis rotating at up to 1°/s.
  - `Rcs_FromOffAxis50m_DocksInsideCone`
  - `Rcs_TargetRotating_TracksAxis`
  - `PortOnFarSide_RoutesAroundKeepOut`
  - `TooFastClosing_Aborts_BacksOff_RetriesOnce`
  - `SecondAbort_Fails`
  - `MainEngine_FromOffAxis_DocksWithWiderTolerance`
  - `NeverCommandsNaN`
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5:** commit `Guide the docking approach`.

### Task 3: RCS axis probe and port chooser

**Files:**
- Create `Pilots/Rendezvous/RcsProbe.cs` and `Pilots/Rendezvous/PortChooser.cs`.
- Tests: `Assets/Tests/Rendezvous/RcsProbeTests.cs` and `PortChooserTests.cs`.

**Interfaces:**
- Produces `class RcsMapping`:
  - fields: `bool Ok`, `string Refusal`, `int[] AxisOf` (X/Y/Z input → vessel axis), `int[] Sign`, `bool[] Dead`;
  - `static RcsMapping FromResponses(Vector3d dvX, Vector3d dvY, Vector3d dvZ, Vector3d vesselRight, Vector3d vesselUp, Vector3d vesselForward, double minResponse)`;
  - behaviour: each input is matched to the closest vessel axis by its measured Δv. An axis whose response is below `minResponse` is dead (Review Focus 4). Two inputs on the same axis are refused.
- Produces `struct PortInfo { string PartId; string[] NodeTypes; bool Free; bool Gendered; bool Female; Vector3d Position; Vector3d Axis; double Size; }`.
- Produces `static (PortInfo ours, PortInfo theirs, string reason) PortChooser.Choose(IReadOnlyList<PortInfo> ours, IReadOnlyList<PortInfo> theirs, Vector3d ourForward, Vector3d fromTargetToUs)`:
  - compatible means the NodeTypes overlap, both ports are free, and gendered ports are of opposite gender;
  - the cost is our rotation angle, plus a penalty when the target port faces away.

- [ ] **Step 1: write the failing tests:**
  - `Probe_PermutedAndFlippedAxes_Mapped`
  - `Probe_DeadAxis_Marked`
  - `Probe_TwoInputsSameAxis_Refused`
  - `Ports_NoCompatible_Refused`
  - `Ports_PrefersFacingAndLeastRotation`
  - `Ports_GenderedPairing`
- [ ] **Step 2:** run the test command. Expected: FAIL.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** run the test command. Expected: all green.
- [ ] **Step 5:** commit `Map RCS axes and choose docking ports`.

### Task 4: Game reads for rendezvous and docking

**Files:**
- Create `Pilots/Rendezvous/RendezvousSnapshot.cs` and `Pilots/Rendezvous/DockingParts.cs` (game glue), plus pure helpers with tests.

**Interfaces:**
- **Target:** the game's target (`VesselComponent.TargetObject`, `.IsVessel` / `.IsPart`); the list of vessels grouped by body; `SetTargetByID`.
- **States:** our state and the target's, at the same UT, in the same body's Zup frame (as in 4.1 Task 9).
- **Ports:**
  - from `PartComponentModule_DockingNode` / `Data_DockingNode`: state, `NodeTypes`, gender, capture limits;
  - the axis and position from the view `Module_DockingNode.GetDockingTransform()` when loaded. Check its access level in the IL, and use reflection if it is not public.
- **Control:** `OnControlFromHere` on our port. Record the previous control part so that Stop can restore it.
- **RCS:** the action group state and setter; `Data_RCS.ThrustStatus` per thruster.
- **Other reads:** the target vessel's angular velocity (spin); whether both vessels are loaded; the `VesselDockedMessage` subscription.

**IL findings to report:**
- the access level and frame of `GetDockingTransform`;
- whether `SetTargetByID(part)` keeps the vessel targeted;
- the id of the merged vessel after docking.

- [ ] **Step 1:** implement.
- [ ] **Step 2:** run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 3:** commit `Read targets, ports and RCS for rendezvous`.

### Task 5: Translation controller (RCS or main engine)

**Files:**
- Create `Pilots/Rendezvous/Translate.cs`, an ExecuteController.
- Test its pure part in `Assets/Tests/Rendezvous/TranslateTests.cs`: allocating a wanted Δv to the RCS inputs through `RcsMapping`, and the main-engine sequence (turn, burn, turn back).

**Interfaces:**
- Consumes: `RcsMapping`, `DockCommand`, `KSPVessel.X/Y/Z`, the SAS target orientation, the throttle.
- At the start of docking, pulse the probe: three 0.5 s pulses. Log the mapping as `[Docking] rcs mapping …`.

- [ ] **Step 1: write the failing pure tests:** allocation through the mapping, fallback on a dead axis, main-engine sequencing.
- [ ] **Step 2:** implement.
- [ ] **Step 3:** run the test command. Expected: all green.
- [ ] **Step 4:** commit `Translate with RCS or the main engine`.

### Task 6: Rendezvous pilot

**Files:**
- Create `Pilots/Rendezvous/RendezvousPilot.cs` and a pure `Pilots/Rendezvous/RendezvousFlight.cs` (phase decisions, tested).
- Modify: the pilot registration; `PlanStore`, to add the kind `"rendezvous"` by extending `SavedPlan` additively.

**Interfaces:**
- Consumes: Tasks 1–5; from 4.1, `TargetOrbitBurn` with `FixedImpulseGuidance`, `WarpTo`, `PlanStore`, `StagingGuard`.
- **Phases:** Plan → PlaneMatch → PhasingWait → Intercept → Correction → Brake → (if docking) Dock (Align … Final) → Docked, plus Failed.
- **Warp:** between burns, stopping before each one. Never within 2 km.
- **Correction:** solved closed-loop toward the 200 m point, with a fresh Lambert from the actual state.
- **Prepare only:** for the rendezvous burns.
- **Persistence:** a resume always re-plans.
- **Docking detection:** a vessel change whose new vessel contains both our part and the target's parts means Docked (Review Focus 5).
- **Stop:** throttle 0, warp off, RCS restored, control part restored.
- **Logs:**
  - the plan;
  - each burn, planned against flown;
  - the miss distance and relative speed after the intercept;
  - the docking trace every 2 s: distance, lateral error, closing speed, phase.

- [ ] **Step 1: write the failing `RendezvousFlightTests`,** including the rule for a vessel change on docking.
- [ ] **Step 2:** implement.
- [ ] **Step 3:** run the test command. Expected: all green.
- [ ] **Step 4:** commit `Fly rendezvous and docking`.

### Task 7: Rendezvous tab

**Files:**
- Modify `Assets/UI/K2D2_UI/Dock.uxml`: replace its content, keeping its GUID and its template slot.
- Replace `Pilots/Docks/DockingUI.cs` with `Pilots/Rendezvous/RendezvousUI.cs`. Keep the page code `dock`, or rename it consistently in `K2D2_Window.uxml`.
- Remove the old `Pilots/Docks/*` controllers once nothing references them.

**UI:**
- **Target list:** vessels grouped by body, with the game's target pre-selected.
- **Port pair:** "auto", plus two lists.
- **Buttons:** Rendezvous + dock, Rendezvous only, Dock only (enabled within 2 km), Stop, Resume / Cancel saved plan.
- **Prepare only.**
- **Readouts:**
  - distance, relative speed, relative inclination;
  - the steps, with Δv and explanations;
  - the vessel's Δv per stage;
  - RCS or main-engine mode, with its note;
  - the docking phase and its errors.
- **Tests:** WindowLayoutTests extended.

- [ ] **Step 1:** implement.
- [ ] **Step 2:** run the test command. Expected: all green.
- [ ] **Step 3:** commit `Replace the Dock tab with the Rendezvous tab`.

### Task 8: Build, install, document (controller)

- [ ] **Build and install:** as in 4.1 Task 13, keeping `plans/`.
- [ ] **Documentation:**
  - CHANGELOG: the Rendezvous tab, and the Dock tab it replaces;
  - README: update the Docking line;
  - parent `CLAUDE.md`.
- [ ] **Checklist:** add these cases to `docs/testing/2026-10-24-transfers-in-game-checklist.md`:
  - a rendezvous with a station in LKO, starting 50 km lower and 30° behind;
  - Dock only, from 1 km, with RCS;
  - the same without RCS;
  - a port on the far side;
  - the docked message and the merged vessel.
- [ ] **Commit and push:** the submodule, then the parent pointer.
