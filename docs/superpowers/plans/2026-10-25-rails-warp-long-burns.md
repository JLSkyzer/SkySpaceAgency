# Rails warp for long burns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Speed up burns that last more than about 2 minutes by using the game's own burn prediction under rails warp (x10 or x50). The start and the end of each burn stay in physics warp (x4 at most), where our closed loop steers and corrects.

**Why:**
- Physics warp is capped at x4 by the game. Above that, only rails warp exists.
- On rails, the game precomputes the burn once, when the warp starts (`OrbiterComponent.HandleOrbitalPhysicsUnderThrustStart`, read in the IL on 2026-10-24). That precomputed burn:
  - keeps a fixed direction, the engines' summed thrust direction;
  - takes the active stage's flow and runs until that stage is empty;
  - ignores steering and throttle while the warp lasts;
  - can exit silently with no burn patches, while the fuel is still consumed.
- The game's own maneuver nodes use the same path.
- The user agreed to this on 2026-10-25.

**Architecture:**
- **Pure decision**, `Pilots/Nodes/RailsBurnPolicy.cs`. It decides when to enter rails warp, at which rate, and when to leave. Its inputs:
  - the remaining Δv and acceleration;
  - the stage's remaining Δv and burn time;
  - the aim drift, i.e. how fast the ideal direction turns;
  - the guard status read from the game.
- **Game wrapper**, `KSPService/RailsBurn.cs`. It reads the precomputed trajectory and the guard fields, starts and stops the rails warp, and reads the true state during it.
- **`TargetOrbitBurn`**. After its physics-warp phase (2026-10-25 plan), it hands long, steady stretches to the rails mode, then takes control back for the end.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c.

## Global Constraints

- **When rails warp may start.** All of these must hold:
  - more than 120 s of game-time burn left;
  - aligned within 1° for at least 1 s of real time, at a steady throttle;
  - in physics warp or at 1x;
  - not a landing or reentry burn. Those stay at physics x4 at most, per the user's rule. Use a burn flag `allow_rails_warp`, default true, which the Landing callers set to false.
- **Aim drift.** The rails burn has a fixed direction. Enter only when the predicted angle between that fixed direction and the ideal one stays under 5° until the planned exit; otherwise stay in physics warp. Compute it from the guidance: the Δv-to-go direction now and one step ahead.
- **Rate.** x10 or x50, indices 3 and 4 of the game's table. Take the highest rate that still leaves at least 15 s of real time before the planned exit.
- **Guard.** Within 2 real frames of entering, check all three:
  - `IsOrbitalPhysicsUnderThrustActive` is true;
  - `Orbiter.PatchedOrbit` is the burn trajectory, not `CurrentPatchedConicsOrbit`;
  - the precomputed burn has at least one patch (`_numBurnPatches > 0`, read by reflection and cached).

  If any check fails, drop to physics warp at once, log it, and never try rails again for this burn.
- **Exit.**
  - Leave rails before the remaining Δv falls under 15 s of burn at the current acceleration.
  - Also leave before the stage's remaining burn time falls under 10 s: the precomputed burn stops at stage burnout, and staging needs physics.
  - Then finish in physics warp, closed-loop, as today.
- **State during rails.** Read r and v from `Orbiter.PatchedOrbit`, the burn trajectory. The closed loop does not steer or throttle during rails; it only watches the exit conditions.
- **Never rails warp:**
  - within 2 km of a rendezvous target;
  - inside an atmosphere;
  - below the game's surface envelope (the game forbids it there anyway).
- **Logs.** Prefix `[TargetOrbitBurn] rails burn: …`. Log the entry, the guard result, the rate, the exit reason, and the Δv applied during rails, planned against actual.
- **Rules from 4.1.** The 4.1 plan's rules apply: tests, commits, licence, CHANGELOG.

## Review Focus

1. **Guard fails** (the game does not start its precomputed burn). Leave rails within 2 frames, and never retry.
2. **The stage runs out during rails.** Exit early enough to stage in physics.
3. **Large aim drift** (low orbit, long burn). Stay in physics warp.
4. **The player changes the warp by hand or presses Stop during rails.** Leave cleanly and restore the player's mode.
5. **A maneuver node on the vessel during rails.** The game's node solver shares the trajectory list. The mod must not create or modify nodes while on rails; Prepare-only pilots never use rails warp.

---

### Task 1: Rails burn policy and game wrapper

**Files:**
- Create: `Pilots/Nodes/RailsBurnPolicy.cs` (pure) and `KSPService/RailsBurn.cs` (glue).
- Test: `Assets/Tests/OrbitPlanner/RailsBurnPolicyTests.cs`.

**Interfaces:**
- `RailsBurnPolicy` produces `static RailsStep Decide(RailsInputs i)`.
  - `RailsInputs` holds:
    - `BurnLeftGameSeconds`, `StageBurnLeftSeconds`;
    - `AlignedSteadyRealSeconds`, `AimDriftDegAtExit`;
    - `RailsAllowed`, `InAtmosphereOrEnvelope`, `NearTarget`;
    - `CurrentlyOnRails`, `GuardOk` (bool?), `FramesSinceRailsEntry`;
    - `Rates` (the game's table), `RealFrameSeconds`.
  - `RailsStep` holds `Action` (Stay / EnterRails / LeaveRails / DropGuard), `WantedIndex` and `Reason`.
- `RailsBurn` produces `bool TryEnter(int index)`, `bool GuardOk(out string detail)`, `void Leave()`, `bool TryReadState(double ut, out Vector3d r, out Vector3d v)` (from `Orbiter.PatchedOrbit`) and `bool OnRails`.
  - Record the reflection target, `_numBurnPatches`, and the members used. Verify all of them in the IL with `scratchpad/recon/cecil.ps1` before relying on them.

- [ ] **Step 1: Write the failing tests**
  - `ShortBurn_StaysPhysics`
  - `LongSteadyBurn_EntersRailsAtHighestSafeRate`
  - `NotSteadyYet_Waits`
  - `AimDriftTooLarge_StaysPhysics`
  - `LandingFlag_NeverRails`
  - `Atmosphere_NeverRails`
  - `NearTarget_NeverRails`
  - `GuardFailsWithin2Frames_DropsAndNeverRetries`
  - `ExitBeforeFifteenSecondsOfBurn`
  - `ExitBeforeStageBurnout`
  - `RateLeavesFifteenRealSeconds`
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Decide and drive rails warp for long burns`.

### Task 2: Use the rails burn in TargetOrbitBurn

**Files:**
- Modify `Pilots/Nodes/Controlers/TargetOrbitBurn.cs`:
  - add the `allow_rails_warp` flag;
  - wire the policy into `ManageBurnWarp`;
  - read the state from `RailsBurn` while on rails;
  - leave rails on every end path.
- Modify the Landing callers (`LandingPilotPrecision`, `LandingPilotReentry`, `PrecisionPhase`) to set `allow_rails_warp = false`.
- Modify the Rendezvous pilot to set it to false within 2 km of the target.
- Modify `CHANGELOG.md`.

**Behaviour:**
- Each frame of the burn: run the physics-warp policy (2026-10-25 plan), then the rails policy.
- On rails:
  - send no steering or throttle commands;
  - read the state from the burn trajectory;
  - watch the exit conditions.
- On exit: go to index 0, then re-enter physics warp on the next frame through the existing policy. The closed loop resumes.
- The safety net `PhysicsWarp.ReleaseIfAbandoned` also covers rails warp left on by an abandoned burn.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Run the test command. Expected: 0 `error CS`, all green.
- [ ] **Step 3:** Commit: `Speed up long burns with the game's burn prediction`.

### Task 3: Build, install, document (controller)

- [ ] Build and install.
- [ ] Update the parent `CLAUDE.md`.
- [ ] Add to the in-game checklist a burn of more than 2 minutes on a low-thrust stage. Check:
  - the `rails burn` entry and the guard result;
  - the exit before the end;
  - the Δv applied, planned against actual.
- [ ] Commit and push.
