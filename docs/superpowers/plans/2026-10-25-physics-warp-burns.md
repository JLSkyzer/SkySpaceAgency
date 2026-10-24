# Physics time warp during burns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let closed-loop burns run faster under time warp, up to x4, while staying in real physics, so that steering, throttle and rotation keep working. Never use rails warp while thrusting.

**Why:**
- **In game, 2026-10-24.** Rails warp x4 during a transfer injection switched the vessel to Orbital. The game then precomputes a fixed-direction burn (`HandleOrbitalPhysicsUnderThrustStart`) that our steering cannot control. About 500 m/s of fuel was lost and the orbit stayed unchanged. Every burn has been held at 1x since (`TargetOrbitBurn.WarpWhileBurning = false`).
- **In the IL, 2026-10-25.**
  - The game has a KSP1-style physics warp. Set `TimeWarp.ToggleTimeWarpMode(true)` at 1x, then `SetRateIndex(1..2)` gives x2 or x4.
  - In physics warp the vessel stays RigidBody, SAS gains are unchanged, and the throttle works.
  - `MAX_PHYSICS_WARP_RATE_INDEX = 2`, and `UniverseModel.SetTimeScale` clamps physics warp to x4.
  - Without that mode, indices 1 and 2 are rails warp in vacuum.
  - The game forces physics mode itself in an atmosphere and near the surface.
- **The user.** Landing and reentry stay at 1x, or at most low warp.

**Architecture:**
- **Pure policy** (`Pilots/Nodes/PhysicsWarpPolicy.cs`). It decides the warp index wanted for the next frame from the BurnWarp cap, the alignment, the remaining burn time, and what the game reports: physics mode on, vessel RigidBody, current index.
- **Game wrapper** (`KSPService/PhysicsWarp.cs`). It enters physics-warp mode, sets the index, reads the game's state back, and leaves cleanly.
- **`TargetOrbitBurn.ManageBurnWarp`** uses both in place of the 1x hold. The rails warp before the burn, used to wait for the window, is unchanged.

**Tech Stack:** Unity 6000.6.0f1, C#, NUnit EditMode, KSP2 Redux 26w42c.

## Global Constraints

- **Never rails warp while the engines push.** Drop the warp to index 0 at once (instant), and log once, whenever:
  - the index is above 2;
  - physics mode is reported off;
  - the vessel is no longer RigidBody.
- **Order of operations.**
  - Entering: at index 0, call `ToggleTimeWarpMode(true)`, then raise the index (max 2).
  - Leaving: index 0 first, then `ToggleTimeWarpMode(false)`.
  - Restore the mode the player had before, at the end of the burn and on every stop or failure.
- **Caps.**
  - The existing BurnWarp rules stay:
    - at most 5 % of the remaining Δv per frame;
    - at least 10 s of real burn left;
    - 1x when not aligned.
  - New: x4 at most, and x2 when under about 30 s of game-time burn remains.
- **`allow_warp`** keeps its meaning. When it is off, the mod only lowers the warp the player chose. If the player warps on rails, the mod still forces physics mode or 1x.
- **Landing and reentry.** Their burns go through TargetOrbitBurn only for the deorbit and correction burns, which may use physics warp. The landing descent controllers are unchanged in this plan and stay at 1x.
- **Logs.** Prefix `[TargetOrbitBurn] physics warp: …` for entering, leaving, a refusal and a forced drop, with the game state read back.
- **From the 4.1 plan.** Its rules apply: test command, commits, licence, CHANGELOG.

## Review Focus

1. **The game refuses physics mode** (toggle ignored). The index must not rise; stay at 1x.
2. **The player already pressed rails warp before ignition.** Drop to index 0 first, then enter physics mode.
3. **Stop, failure, scene change or vessel switch during a physics-warped burn.** The warp goes to 0 and the player's mode is restored.
4. **Atmosphere or surface envelope, where the game forces physics mode.** On leaving, do not toggle physics mode off if the game owns it.
5. **Frame-rate drop at x4** (more physics steps per frame). The 5 % rule uses the real frame time; keep it.

---

### Task 1: Physics warp policy and game wrapper

**Files:**
- Create: `Pilots/Nodes/PhysicsWarpPolicy.cs` (pure), `KSPService/PhysicsWarp.cs` (glue)
- Test: `Assets/Tests/OrbitPlanner/PhysicsWarpPolicyTests.cs`

**Interfaces:**
- **`PhysicsWarpPolicy`** produces `static WarpStep Decide(WarpInputs i)`.
  - `WarpInputs`: `CurrentIndex`, `PhysicsModeOn`, `VesselRigidBody`, `CapRate`, `Aligned`, `BurnLeftGameSeconds`, `AllowRaise`, `PlayerModeWasPhysics`.
  - `WarpStep`: `WantedIndex`, `SetPhysicsMode` (true, false, or null for no change), `Reason`, `ForcedDrop`.
- **`PhysicsWarp`** produces `bool TryEnter()`, `void Leave(bool restorePlayerMode)`, `bool IsPhysicsMode`, `bool VesselIsRigidBody`, `int CurrentIndex`, `void SetIndex(int index, bool instant)`.
  - It calls `TimeWarp.ToggleTimeWarpMode`, `IsPhysicsTimeWarp` and `TimeWarp.SetRateIndex` (through `TimeWarpTools`).
  - It reads the vessel physics mode from `VesselComponent`; find the member in the IL.
  - Use reflection only where a member is not public. Cache it, and log a failure once.

- [ ] **Step 1: Write the failing tests:**
  - `RailsIndexWhileThrusting_ForcedToZero`
  - `EnterOrder_ModeAtZeroThenRaise`
  - `LeaveOrder_ZeroThenModeOff`
  - `PhysicsRefused_StaysAtOne`
  - `NotRigidBody_ForcedDrop`
  - `CapRespected_MaxIndex2`
  - `UnderThirtySeconds_X2Max`
  - `NotAligned_One`
  - `GameOwnedPhysics_NotToggledOff`
  - `AllowRaiseFalse_OnlyLowers`
- [ ] **Step 2:** Run the test command. Expected: FAIL.
- [ ] **Step 3:** Implement the policy and the wrapper. Record the IL members used in the report.
- [ ] **Step 4:** Run the test command. Expected: all green.
- [ ] **Step 5:** Commit: `Decide and drive physics time warp for burns`.

### Task 2: Use physics warp in TargetOrbitBurn

**Files:**
- Modify `Pilots/Nodes/Controlers/TargetOrbitBurn.cs`:
  - in `ManageBurnWarp`, replace the `WarpWhileBurning` 1x hold with the policy, and update its comment;
  - apply the warp exit on every stop, failure and end path.
- Modify `CHANGELOG.md`.

**Behaviour:**
- Each frame while burning: read the state, call `Decide`, apply the result.
- On every end path (done, stop, fail, or an `OnDisable`-like reset): leave physics mode and restore the player's mode.
- The pilots that use TargetOrbitBurn need no change: Transfer, Rendezvous, Lift circularize, Node, Orbit and the Landing deorbit.
- Logs: entering, the highest index reached, every forced drop with its reason, and leaving.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Run the test command. Expected: 0 `error CS`, all green, existing BurnWarp tests included.
- [ ] **Step 3:** Commit: `Burn under physics time warp, never on rails`.

### Task 3: Build, install, document (controller)

- [ ] Build and install as before, keeping `plans/`.
- [ ] Update the note in the parent `CLAUDE.md`.
- [ ] Add a checklist item:
  - a long transfer injection should go to x4, with `physics warp` log lines and the vessel steering normally;
  - pressing rails warp by hand during the burn must bring the vessel back to physics.
- [ ] Commit and push.
