# Landing Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the landing autopilot unable to crash silently:
- it refuses an impossible landing;
- it starts braking from a forward simulation of the burn;
- it never descends faster than the active engines can stop;
- it counts only active engines;
- it stops when a phase fails.

**Architecture:** All the new physics lives in four pure-math units under `Pilots/Landing/Braking/`, unit-tested in the editor: `ThrustMath`, `DescentEnvelope`, `BodyRotation` and `BrakeSimulator`. A fifth pure unit, `LandingFeasibility`, holds the start checks.

The game-facing code only gathers inputs and applies results:
- `BurndV` exposes the thrust, mass and Isp of the active engines;
- `LandingPilot` runs the simulation at 2 Hz, checks feasibility when the player starts, and stops when a phase fails;
- `TouchDown` uses the envelope and guards against zero thrust;
- `LandingUI` shows errors persistently.

**Tech Stack:** Unity 6000.6.0f1, C# (mod assembly `SkySpaceAgency`), Unity Test Framework (NUnit, EditMode), KSP2 Redux 26w41a game API (`KSP.Sim`, `KSP.Sim.DeltaV`, `KSP.Modules`).

**Spec:** `docs/superpowers/specs/2026-10-05-landing-safety-design.md` (read it first; this plan implements it).

**Deliberate deviations from the spec:** each one is needed to meet the spec's own tests, or to leave other autopilots unchanged.
1. **Search precision is 0.01 s, with up to 40 iterations**, instead of 0.25 s and 30. At ~150 m/s, 0.25 s of start time moves the stop point by tens of metres, which makes the spec's "stops at 50 ± 5 m" unreachable. About 18 iterations are needed in practice.
2. **A run counts as stopped once the surface speed is at most `max(1 m/s, η·F/m · step)`**, that is, within one integration step of zero. With a plain 1 m/s threshold, RK4 chatters around zero speed (the thrust direction flips inside a step) and corrupts the stop altitude.
3. **Terrain is read every step while the vessel is less than 5 km above the last terrain reading**, rather than less than 5 km above the body radius. This is stricter on bodies whose terrain is high above the radius.
4. **`BurndV` leaves `full_dv` as it is, except for a NaN guard, and adds `active_*` fields.** Node, Lift, Docking and Drone also read `full_dv`; filtering it would change their burns, which is out of scope. Only landing code switches to the active values.
5. **The "Cannot stop" alert keeps the touchdown speed as slack.** It fires when `speed > √(2·max(a − g, 0)·h) + v_td`. The bare `v² > 2(a − g)h` would fire at every touchdown, where h → 0 while v ≈ v_td.
6. **The simulation-based start checks (braking possible, Δv) only run when a collision is predicted at start.** Precision landing starts in orbit, where no collision is predicted yet, and Circularize and DeorbitBurn come first. The engine and TWR checks always run.

An independent JavaScript prototype (session scratchpad `proto.mjs`, not committed) reproduced the test numbers below.

## Global Constraints

- Unity editor: `D:/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe`. Use this exact version; never open the project with another one.
- Project path: `D:/KSPReduxModding/SkySpaceAgency`. Close any interactive Unity editor on this project before a batch run: batch mode fails on a locked project.
- Logs and test results go to the session scratchpad, never into the repository.
- Mod code lives in `Assets/SkySpaceAgency/Code` (assembly `SkySpaceAgency`, root namespace `K2D2`). The new pure code uses namespace `K2D2.Landing.Braking`, in folder `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/`. Every new type is `public` so the test assembly can reach it.
- Physics frame: the game's `...Zup` vectors (`GetRelativePositionAtUTZup`, `GetOrbitalVelocityAtUTZup`), relative to the body, with z along the body's rotation axis.
- Units: thrust in kN, mass in t, so `thrust / mass` is in m/s² (same as the existing `BurndV`).
- Constants (spec):

  | Constant | Value |
  |---|---|
  | Thrust fraction η | 0.85 |
  | Safe stop altitude | 50 m |
  | Stop speed | 1 m/s |
  | RK4 step | 0.5 s |
  | Terrain reading interval | 2 s |
  | Close-to-terrain band | 5 km |
  | Maximum simulated burn | 3600 s |
  | g0 | 9.80665 |
  | TWR margin | 1.05 |
  | Δv margin | 1.10 |
  | Minimum speed difference for the rotation sign | 1 m/s |
  | Simulation refresh | every 0.5 s of real time |

- UI and log text is in English, like the rest of the mod. Messages are exactly those of the spec:
  - `No active engine: stage or activate your engines first.`
  - `Local TWR {0:0.00}: too low to stop safely.`
  - `Cannot stop before the ground with the current thrust.`
  - `Δv {0:0} m/s for ~{1:0} m/s needed.`
  - `Δv unknown: check your fuel.`
  - `Cannot stop before the ground!`

  Numbers are formatted with `CultureInfo.InvariantCulture`.
- Commits: English, imperative, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage only the files of the task, including the `.meta` files Unity generated for new files. The mod repo is `D:/KSPReduxModding/SkySpaceAgency` (remote `origin` = JLSkyzer/SkySpaceAgency). Push after each commit.

### Commands used by several tasks

Run EditMode tests (compiles everything first):

```bash
S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
rm -f "$S/results.xml"
"/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
echo "exit=$?"
grep -c "error CS" "$S/tests.log"
grep -oE 'total="[0-9]+" passed="[0-9]+" failed="[0-9]+"' "$S/results.xml" | head -1
grep -oE 'name="[A-Za-z_]+"[^>]*result="Failed"' "$S/results.xml"
```

- `exit=0` and `failed="0"` mean all green.
- A compile error shows up as `error CS` lines, and no `results.xml` is written.
- The 24 existing Orbit tests must stay green.
- Tasks that only touch game code have no unit test. For them, this same command is the compile check.

Build the release zip:

```bash
S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
P=D:/KSPReduxModding/SkySpaceAgency
rm -f "$P/Deploy/SkySpaceAgency.zip"
"/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "$P" -executeMethod ThunderKit.Core.Pipelines.Pipeline.BatchModeExecutePipeline "-pipeline=Assets/SkySpaceAgency/Pipelines/Deploy to Zip File.asset" "-manifest=Assets/SkySpaceAgency/Pipelines/Deploy to Zip File Manifest.asset" -logFile "$S/deploy.log"
grep -c "error CS" "$S/deploy.log"
L=$(ls -t "$P/Assets/ThunderKitSettings/Logs/Deploy to Zip File/"*.asset | head -1); grep -c "logLevel: 2" "$L"
ls -la "$P/Deploy/SkySpaceAgency.zip"
```

This method **always exits with code 1**. Success means 0 `error CS`, 0 `logLevel: 2`, and the zip exists.

---

## File Structure

| File | Responsibility |
|---|---|
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ThrustMath.cs` | NaN-free acceleration; combined Isp of several engines. |
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/DescentEnvelope.cs` | Speed limit derived from the thrust; "can still stop" test. |
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BodyRotation.cs` | Angular speed from the rotation period; rotation sign from the measured surface speed. |
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BrakeSimulator.cs` | Forward simulation of the braking burn; latest safe start. |
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/LandingFeasibility.cs` | Start checks and their messages. |
| Modify `Assets/SkySpaceAgency/Code/KSPService/BurndV.cs` | Active-engine thrust, mass and Isp; NaN guard. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/BaseControllers/ExecuteController.cs` | Failure reason on controllers, forwarded by `SingleExecuteController`. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/Circularize.cs`, `DeorbitBurn.cs` | Report refusals as failures. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs` | Simulation at 2 Hz, burn start from it, start checks, stop on failure, envelope. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/TouchDown.cs` | Active thrust, zero-thrust guard, "cannot stop" flag. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs` | Persistent error, warning and alert; button reset on refusal. |
| Create `Assets/Tests/Landing/SkySpaceAgency.Tests.Landing.asmdef` | Editor test assembly for the landing units (never shipped). |
| Create `Assets/Tests/Landing/*Tests.cs` | EditMode tests. |
| Modify `CHANGELOG.md`, `README.md` | Documentation. |

---

### Task 1: Test assembly, `ThrustMath` and `DescentEnvelope`

**Files:**
- Create: `Assets/Tests/Landing/SkySpaceAgency.Tests.Landing.asmdef`
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ThrustMath.cs`
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/DescentEnvelope.cs`
- Test: `Assets/Tests/Landing/ThrustMathTests.cs`, `Assets/Tests/Landing/DescentEnvelopeTests.cs`

**Interfaces:**
- Produces:
  - `ThrustMath.Acceleration(double thrustKN, double massT) : double`, which returns 0 when either input is ≤ 0, NaN or infinite;
  - `ThrustMath.CombinedIsp(IReadOnlyList<double> thrustsKN, IReadOnlyList<double> isps) : double`;
  - `DescentEnvelope.ThrustFraction` (0.85);
  - `DescentEnvelope.MaxSpeed(double height, double accel, double gravity, double touchDownSpeed, double playerLimit) : double`;
  - `DescentEnvelope.CanStop(double speed, double height, double accel, double gravity, double touchDownSpeed) : bool`.

- [ ] **Step 1: Create the test assembly**

`Assets/Tests/Landing/SkySpaceAgency.Tests.Landing.asmdef` (same as the Orbit one, under another name):

```json
{
  "name": "SkySpaceAgency.Tests.Landing",
  "references": [
    "SkySpaceAgency",
    "UnityEngine.TestRunner",
    "UnityEditor.TestRunner"
  ],
  "includePlatforms": [
    "Editor"
  ],
  "overrideReferences": true,
  "precompiledReferences": [
    "nunit.framework.dll",
    "Assembly-CSharp.dll"
  ],
  "autoReferenced": false,
  "defineConstraints": [
    "UNITY_INCLUDE_TESTS"
  ]
}
```

- [ ] **Step 2: Write the failing tests**

`Assets/Tests/Landing/ThrustMathTests.cs`:

```csharp
using K2D2.Landing.Braking;
using NUnit.Framework;

public class ThrustMathTests
{
    [Test]
    public void Acceleration_IsThrustOverMass()
    {
        Assert.AreEqual(5.0, ThrustMath.Acceleration(50, 10), 1e-12);
    }

    [Test]
    public void Acceleration_IsZeroInsteadOfNaNOrInfinity()
    {
        Assert.AreEqual(0.0, ThrustMath.Acceleration(50, 0));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(0, 10));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(double.NaN, 10));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(50, double.NaN));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(-5, 10));
    }

    [Test]
    public void CombinedIsp_SameEngines_IsTheirIsp()
    {
        Assert.AreEqual(300.0, ThrustMath.CombinedIsp(new[] { 100.0, 100.0 }, new[] { 300.0, 300.0 }), 1e-9);
    }

    [Test]
    public void CombinedIsp_IsThrustOverTotalFlow()
    {
        // 400 / (100/200 + 300/400) = 400 / 1.25 = 320
        Assert.AreEqual(320.0, ThrustMath.CombinedIsp(new[] { 100.0, 300.0 }, new[] { 200.0, 400.0 }), 1e-9);
    }

    [Test]
    public void CombinedIsp_IgnoresEnginesWithoutThrust()
    {
        Assert.AreEqual(300.0, ThrustMath.CombinedIsp(new[] { 100.0, 0.0 }, new[] { 300.0, 0.0 }), 1e-9);
    }

    [Test]
    public void CombinedIsp_UnknownIspOrNoEngine_IsZero()
    {
        Assert.AreEqual(0.0, ThrustMath.CombinedIsp(new[] { 100.0, 100.0 }, new[] { 300.0, 0.0 }));
        Assert.AreEqual(0.0, ThrustMath.CombinedIsp(new[] { 100.0 }, new[] { double.NaN }));
        Assert.AreEqual(0.0, ThrustMath.CombinedIsp(new double[0], new double[0]));
    }
}
```

`Assets/Tests/Landing/DescentEnvelopeTests.cs`:

```csharp
using System;
using K2D2.Landing.Braking;
using NUnit.Framework;

public class DescentEnvelopeTests
{
    [Test]
    public void MaxSpeed_IsStoppingSpeedPlusTouchDownSpeed()
    {
        // sqrt(2 * (0.85*5 - 1.6) * 1000) + 2 = sqrt(5300) + 2
        double expected = Math.Sqrt(5300) + 2;
        Assert.AreEqual(expected, DescentEnvelope.MaxSpeed(1000, 5, 1.6, 2, 1000), 1e-9);
    }

    [Test]
    public void MaxSpeed_NeverAbovePlayerProfile()
    {
        Assert.AreEqual(20.0, DescentEnvelope.MaxSpeed(1000, 5, 1.6, 2, 20), 1e-12);
    }

    [Test]
    public void MaxSpeed_WeakThrust_IsTouchDownSpeed()
    {
        // 0.85 * 1.5 < 1.6: the engines cannot decelerate at all.
        Assert.AreEqual(2.0, DescentEnvelope.MaxSpeed(1000, 1.5, 1.6, 2, 1000), 1e-12);
    }

    [Test]
    public void MaxSpeed_BadInputs_IsTouchDownSpeed()
    {
        Assert.AreEqual(2.0, DescentEnvelope.MaxSpeed(-50, 5, 1.6, 2, 1000), 1e-12);
        Assert.AreEqual(2.0, DescentEnvelope.MaxSpeed(1000, double.NaN, 1.6, 2, 1000), 1e-12);
    }

    [Test]
    public void CanStop_UsesFullThrust()
    {
        // full thrust: sqrt(2 * (5 - 1.6) * 1000) + 2 = 84.46
        Assert.IsTrue(DescentEnvelope.CanStop(50, 1000, 5, 1.6, 2));
        Assert.IsTrue(DescentEnvelope.CanStop(84, 1000, 5, 1.6, 2));
        Assert.IsFalse(DescentEnvelope.CanStop(85, 1000, 5, 1.6, 2));
    }

    [Test]
    public void CanStop_NoThrust_OnlyAtTouchDownSpeed()
    {
        Assert.IsTrue(DescentEnvelope.CanStop(1.5, 1000, 0, 1.6, 2));
        Assert.IsFalse(DescentEnvelope.CanStop(3, 1000, 0, 1.6, 2));
        Assert.IsFalse(DescentEnvelope.CanStop(3, 1000, double.NaN, 1.6, 2));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0246` / `CS0103`, because `ThrustMath` and `DescentEnvelope` do not exist yet, and no `results.xml`.

- [ ] **Step 4: Implement**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ThrustMath.cs`:

```csharp
using System.Collections.Generic;

namespace K2D2.Landing.Braking
{
    // Engine arithmetic shared by BurndV and the braking simulation. Thrust in kN and mass in t,
    // so thrust / mass is in m/s², like the rest of the mod.
    public static class ThrustMath
    {
        // 0 instead of NaN or infinity when there is no thrust or no mass.
        public static double Acceleration(double thrustKN, double massT)
        {
            if (!(thrustKN > 0) || !(massT > 0) || double.IsInfinity(thrustKN) || double.IsInfinity(massT))
                return 0;
            return thrustKN / massT;
        }

        // Isp of several engines firing together: total thrust over total propellant flow.
        // Engines without thrust are ignored; a thrusting engine with an unknown Isp makes the
        // result unknown (0), and the caller then keeps the mass constant, which is pessimistic.
        public static double CombinedIsp(IReadOnlyList<double> thrustsKN, IReadOnlyList<double> isps)
        {
            double thrust = 0, flow = 0;
            for (int i = 0; i < thrustsKN.Count; i++)
            {
                double f = thrustsKN[i];
                if (!(f > 0))
                    continue;
                double isp = isps[i];
                if (!(isp > 0))
                    return 0;
                thrust += f;
                flow += f / isp;
            }
            return flow > 0 ? thrust / flow : 0;
        }
    }
}
```

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/DescentEnvelope.cs`:

```csharp
using System;

namespace K2D2.Landing.Braking
{
    // How fast the descent may go at a given height above the ground.
    public static class DescentEnvelope
    {
        // Share of the active thrust the planning counts on, leaving the rest as margin.
        public const double ThrustFraction = 0.85;

        // Speed from which ThrustFraction of the thrust still stops at the ground, plus the
        // touch-down speed, and never more than the player's own altitude/speed profile.
        public static double MaxSpeed(double height, double accel, double gravity, double touchDownSpeed, double playerLimit)
        {
            double envelope = StoppingSpeed(height, ThrustFraction * Sanitize(accel), gravity) + touchDownSpeed;
            return Math.Min(envelope, playerLimit);
        }

        // False when even full thrust can no longer stop before the ground. The touch-down speed
        // is slack: arriving at it is a landing, not a crash.
        public static bool CanStop(double speed, double height, double accel, double gravity, double touchDownSpeed)
        {
            return speed <= StoppingSpeed(height, Sanitize(accel), gravity) + touchDownSpeed;
        }

        // Speed that a net deceleration of (accel - gravity) cancels over the given height.
        static double StoppingSpeed(double height, double accel, double gravity)
        {
            double net = accel - gravity;
            if (!(net > 0) || !(height > 0))
                return 0;
            return Math.Sqrt(2 * net * height);
        }

        static double Sanitize(double accel) => accel > 0 && !double.IsInfinity(accel) ? accel : 0;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 36 tests in total (24 Orbit + 12 new).

- [ ] **Step 6: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/Tests/Landing.meta Assets/Tests/Landing Assets/SkySpaceAgency/Code/Pilots/Landing/Braking.meta Assets/SkySpaceAgency/Code/Pilots/Landing/Braking
git status --short   # only these paths
git commit -m "Add thrust math and descent envelope for landing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: `BodyRotation`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BodyRotation.cs`
- Test: `Assets/Tests/Landing/BodyRotationTests.cs`

**Interfaces:**
- Produces:
  - `BodyRotation.MinSpeedDifference` (1.0);
  - `BodyRotation.AngularSpeed(double rotationPeriod) : double` (rad/s, ≥ 0);
  - `BodyRotation.IsDecisive(Vector3d r, Vector3d v, double angularSpeed) : bool`;
  - `BodyRotation.ChooseSign(Vector3d r, Vector3d v, double angularSpeed, double measuredSurfaceSpeed) : int` (+1 or −1);
  - `BodyRotation.AngularVelocity(double angularSpeed, int sign) : Vector3d`, which returns `(0, 0, sign·ω)`;
  - `BodyRotation.SurfaceVelocity(Vector3d r, Vector3d v, Vector3d angularVelocity) : Vector3d`, which returns `v − ω×r`.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Landing/BodyRotationTests.cs`:

```csharp
using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using NUnit.Framework;

public class BodyRotationTests
{
    const double R = 200000;

    [Test]
    public void AngularSpeed_IsTwoPiOverPeriod()
    {
        Assert.AreEqual(2 * Math.PI / 21549.425, BodyRotation.AngularSpeed(21549.425), 1e-15);
        Assert.AreEqual(2 * Math.PI / 21549.425, BodyRotation.AngularSpeed(-21549.425), 1e-15);
    }

    [Test]
    public void AngularSpeed_NoPeriod_IsZero()
    {
        Assert.AreEqual(0.0, BodyRotation.AngularSpeed(0));
        Assert.AreEqual(0.0, BodyRotation.AngularSpeed(double.NaN));
        Assert.AreEqual(0.0, BodyRotation.AngularSpeed(double.PositiveInfinity));
    }

    [Test]
    public void SurfaceVelocity_RemovesTheGroundSpeed()
    {
        var r = new Vector3d(R, 0, 0);
        var v = new Vector3d(0, 500, 0);
        var vs = BodyRotation.SurfaceVelocity(r, v, BodyRotation.AngularVelocity(1e-3, 1));
        // omega x r = (0, 0, 1e-3) x (R, 0, 0) = (0, 200, 0)
        Assert.AreEqual(300.0, vs.y, 1e-9);
        Assert.AreEqual(0.0, vs.x, 1e-9);
    }

    [TestCase(1)]
    [TestCase(-1)]
    public void ChooseSign_MatchesMeasuredSurfaceSpeed(int actualSign)
    {
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(0, 550, 0);
        double omega = 1e-3;
        double measured = BodyRotation.SurfaceVelocity(r, v, BodyRotation.AngularVelocity(omega, actualSign)).magnitude;
        Assert.IsTrue(BodyRotation.IsDecisive(r, v, omega));
        Assert.AreEqual(actualSign, BodyRotation.ChooseSign(r, v, omega, measured));
    }

    [Test]
    public void ChooseSign_SlowRotation_KeepsPlusOne()
    {
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(0, 550, 0);
        double omega = 1e-7; // ground speed ~0.02 m/s: both signs predict the same speed
        Assert.IsFalse(BodyRotation.IsDecisive(r, v, omega));
        Assert.AreEqual(1, BodyRotation.ChooseSign(r, v, omega, 400));
    }

    [Test]
    public void ChooseSign_PolarOrbit_KeepsPlusOne()
    {
        // Velocity along the rotation axis: omega x r is perpendicular to v, so both signs give
        // the same |v - omega x r|.
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(0, 0, 550);
        Assert.IsFalse(BodyRotation.IsDecisive(r, v, 1e-3));
        Assert.AreEqual(1, BodyRotation.ChooseSign(r, v, 1e-3, 100));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: a compile error, because `BodyRotation` does not exist yet.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BodyRotation.cs`:

```csharp
using System;
using KSP.Sim;

namespace K2D2.Landing.Braking
{
    // Body rotation in the game's Zup frame (z = rotation axis). The direction of rotation along z
    // is not documented, so the sign is calibrated against the surface speed the game measures.
    public static class BodyRotation
    {
        // Below this gap between the two predictions (m/s), the sign cannot be told apart.
        public const double MinSpeedDifference = 1.0;

        public static double AngularSpeed(double rotationPeriod)
        {
            double period = Math.Abs(rotationPeriod);
            if (!(period > 0) || double.IsInfinity(period))
                return 0;
            return 2 * Math.PI / period;
        }

        public static Vector3d AngularVelocity(double angularSpeed, int sign)
        {
            return new Vector3d(0, 0, sign * angularSpeed);
        }

        public static Vector3d SurfaceVelocity(Vector3d r, Vector3d v, Vector3d angularVelocity)
        {
            return v - Vector3d.Cross(angularVelocity, r);
        }

        public static bool IsDecisive(Vector3d r, Vector3d v, double angularSpeed)
        {
            Predict(r, v, angularSpeed, out double plus, out double minus);
            return Math.Abs(plus - minus) >= MinSpeedDifference;
        }

        // +1 or -1: the sign whose predicted surface speed is closer to the measured one; +1 when
        // the two predictions are too close to tell.
        public static int ChooseSign(Vector3d r, Vector3d v, double angularSpeed, double measuredSurfaceSpeed)
        {
            Predict(r, v, angularSpeed, out double plus, out double minus);
            if (Math.Abs(plus - minus) < MinSpeedDifference)
                return 1;
            return Math.Abs(plus - measuredSurfaceSpeed) <= Math.Abs(minus - measuredSurfaceSpeed) ? 1 : -1;
        }

        static void Predict(Vector3d r, Vector3d v, double angularSpeed, out double plus, out double minus)
        {
            plus = SurfaceVelocity(r, v, AngularVelocity(angularSpeed, 1)).magnitude;
            minus = SurfaceVelocity(r, v, AngularVelocity(angularSpeed, -1)).magnitude;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0`, `failed="0"`.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BodyRotation.cs* Assets/Tests/Landing/BodyRotationTests.cs*
git commit -m "Add body rotation sign calibration for landing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: `BrakeSimulator`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BrakeSimulator.cs`
- Test: `Assets/Tests/Landing/BrakeSimulatorTests.cs`

**Interfaces:**
- Consumes:
  - `DescentEnvelope.ThrustFraction` and `BodyRotation.SurfaceVelocity`;
  - the existing `KSP2FlightAssistant.MathLibrary.KeplerPropagator.Propagate(Vector3d r0, Vector3d v0, double mu, double dt, out Vector3d r1, out Vector3d v1)`, in `Assets/SkySpaceAgency/Code/KSPService/ManeuverCreator/KeplerPropagator.cs`.
- Produces:
  - `enum BrakeStatus { Ok, TooLate, Impossible }`;
  - `struct BrakeInput`, with fields `Position`, `Velocity`, `UT`, `Mu`, `BodyRadius`, `AngularVelocity`, `ThrustKN`, `MassT`, `Isp`, `ImpactUT` and `Func<Vector3d, double> TerrainHeight`;
  - `struct BrakeRun { bool Stopped; double StopAltitude; double BurnDuration; double DeltaV; Vector3d StopPosition; }`;
  - `struct BrakeResult { BrakeStatus Status; double StartUT; double BurnDuration; double DeltaVNeeded; double StopAltitude; Vector3d StopPosition; }`;
  - `BrakeSimulator.Simulate(BrakeInput input, double startUT) : BrakeRun`;
  - `BrakeSimulator.FindStart(BrakeInput input) : BrakeResult`;
  - the constants `SafeAltitude`, `StopSpeed`, `Step`, `TerrainInterval`, `CloseToTerrain`, `MaxBurnTime`, `StartPrecision`, `MaxIterations` and `G0`.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Landing/BrakeSimulatorTests.cs`:

```csharp
using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class BrakeSimulatorTests
{
    // Mun.
    const double Mu = 6.5138e10;
    const double R = 200000;
    const double Eta = 0.85;
    const double G0 = 9.80665;
    static double SurfaceGravity => Mu / (R * R);

    static Vector3d Zero => new Vector3d(0, 0, 0);

    static BrakeInput Input(Vector3d r, Vector3d v, double thrustKN, double massT, double isp = 0,
        Func<Vector3d, double> terrain = null, Vector3d? omega = null)
    {
        var input = new BrakeInput
        {
            Position = r,
            Velocity = v,
            UT = 1000,
            Mu = Mu,
            BodyRadius = R,
            AngularVelocity = omega ?? Zero,
            ThrustKN = thrustKN,
            MassT = massT,
            Isp = isp,
            TerrainHeight = terrain ?? (p => 0),
        };
        input.ImpactUT = ImpactUT(input);
        return input;
    }

    // First UT at which the unpowered trajectory reaches the terrain, at most 0.5 s late (the
    // search only needs an upper bound that is unsafe). input.UT when it never does (orbits used
    // by Simulate-only tests).
    static double ImpactUT(BrakeInput input)
    {
        for (double dt = 0.5; dt < 20000; dt += 0.5)
        {
            KeplerPropagator.Propagate(input.Position, input.Velocity, input.Mu, dt, out var r, out _);
            if (r.magnitude - R <= input.TerrainHeight(r))
                return input.UT + dt + 0.5;
        }
        return input.UT;
    }

    static void AssertRelative(double expected, double actual, double tolerance, string what)
    {
        Assert.That(Math.Abs(actual - expected) / Math.Abs(expected), Is.LessThan(tolerance),
            $"{what}: expected {expected}, got {actual}");
    }

    // Spec test 1: vertical fall, no rotation, constant mass.
    [Test]
    public void VerticalFall_StopDistanceMatchesConstantDeceleration()
    {
        double h0 = 2000, v0 = 60, thrust = 50, mass = 10; // a = 5 m/s²
        var input = Input(new Vector3d(R + h0, 0, 0), new Vector3d(-v0, 0, 0), thrust, mass);

        BrakeRun run = BrakeSimulator.Simulate(input, input.UT);

        Assert.IsTrue(run.Stopped);
        double g = Mu / ((R + h0) * (R + h0));
        double expected = (v0 * v0 - 1) / (2 * (Eta * thrust / mass - g)); // ~678 m
        AssertRelative(expected, h0 - run.StopAltitude, 0.02, "stop distance");
    }

    [Test]
    public void VerticalFall_LatestStartStopsAtSafeAltitude()
    {
        var input = Input(new Vector3d(R + 10000, 0, 0), new Vector3d(-60, 0, 0), 50, 10);

        BrakeResult result = BrakeSimulator.FindStart(input);

        Assert.AreEqual(BrakeStatus.Ok, result.Status);
        Assert.That(result.StartUT, Is.GreaterThan(input.UT + 30)); // prototype: ~57 s of free fall
        Assert.That(result.StopAltitude, Is.InRange(50.0, 55.0));   // prototype: 51.4 m
        Assert.That(result.BurnDuration, Is.GreaterThan(0));
        Assert.That(result.DeltaVNeeded, Is.GreaterThan(0));
    }

    // Spec test 2: the audit's shallow approach (15 km orbit lowered to a -2 km periapsis), TWR 2.
    [Test]
    public void ShallowApproach_StopsAboveTerrain()
    {
        double ra = R + 15000, rp = R - 2000;
        double va = Math.Sqrt(Mu * (2 / ra - 2 / (ra + rp)));
        double mass = 10, thrust = 2 * SurfaceGravity * mass;
        var input = Input(new Vector3d(ra, 0, 0), new Vector3d(0, va, 0), thrust, mass);

        BrakeResult result = BrakeSimulator.FindStart(input);

        Assert.AreEqual(BrakeStatus.Ok, result.Status);
        Assert.That(result.StartUT, Is.GreaterThan(input.UT + 100)); // prototype: ~302 s
        Assert.That(result.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
        BrakeRun check = BrakeSimulator.Simulate(input, result.StartUT);
        Assert.IsTrue(check.Stopped);
        Assert.That(check.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
    }

    // Spec test 3.
    [Test]
    public void ThrustBelowGravity_IsImpossible()
    {
        double mass = 10, thrust = 1.1 * SurfaceGravity * mass; // 0.85 * 1.1 g < g
        var input = Input(new Vector3d(R + 10000, 0, 0), new Vector3d(-60, 0, 0), thrust, mass);

        Assert.AreEqual(BrakeStatus.Impossible, BrakeSimulator.FindStart(input).Status);
    }

    [Test]
    public void NoThrust_IsImpossible()
    {
        var input = Input(new Vector3d(R + 10000, 0, 0), new Vector3d(-60, 0, 0), 0, 10);
        Assert.AreEqual(BrakeStatus.Impossible, BrakeSimulator.FindStart(input).Status);
    }

    [Test]
    public void AlreadyTooLow_BrakesNow()
    {
        // 100 m up at 30 m/s: stopping takes ~170 m, so even braking now ends below 50 m (or crashes).
        var input = Input(new Vector3d(R + 100, 0, 0), new Vector3d(-30, 0, 0), 50, 10);
        BrakeResult result = BrakeSimulator.FindStart(input);
        Assert.That(result.Status, Is.EqualTo(BrakeStatus.TooLate).Or.EqualTo(BrakeStatus.Impossible));
        Assert.AreEqual(input.UT, result.StartUT);
    }

    // Spec test 4: mass loss.
    [Test]
    public void MassLoss_StartsLaterAndFollowsTsiolkovsky()
    {
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(-60, 0, 0);
        double thrust = 50, mass = 10, isp = 300;
        BrakeResult constant = BrakeSimulator.FindStart(Input(r, v, thrust, mass));
        BrakeResult losing = BrakeSimulator.FindStart(Input(r, v, thrust, mass, isp));

        Assert.AreEqual(BrakeStatus.Ok, losing.Status);
        Assert.That(losing.StartUT, Is.GreaterThan(constant.StartUT)); // prototype: +0.7 s

        double massEnd = mass - Eta * thrust / (isp * G0) * losing.BurnDuration;
        double expected = isp * G0 * Math.Log(mass / massEnd);
        AssertRelative(expected, losing.DeltaVNeeded, 0.01, "delta-v");
    }

    // Spec test 5: a 3 km plateau under the whole approach.
    [Test]
    public void Plateau_StopsAbovePlateau()
    {
        double ra = R + 15000, rp = R - 2000;
        double va = Math.Sqrt(Mu * (2 / ra - 2 / (ra + rp)));
        double mass = 10, thrust = 2 * SurfaceGravity * mass;
        Func<Vector3d, double> plateau = p => p.y > 0 ? 3000 : 0;
        var input = Input(new Vector3d(ra, 0, 0), new Vector3d(0, va, 0), thrust, mass, terrain: plateau);

        BrakeResult result = BrakeSimulator.FindStart(input);

        Assert.AreEqual(BrakeStatus.Ok, result.Status);
        Assert.That(result.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
        Assert.That(result.StopPosition.magnitude - R, Is.GreaterThanOrEqualTo(3000 + BrakeSimulator.SafeAltitude - 1e-6));
    }

    // Spec test 6: rotation. Fast rotation (ground speed 210 m/s) to make the effect obvious.
    [Test]
    public void Rotation_RetrogradeOrbitBrakesLonger()
    {
        double r0 = R + 10000, vc = Math.Sqrt(Mu / r0);
        double mass = 10, thrust = 3 * SurfaceGravity * mass;
        var omega = new Vector3d(0, 0, 1e-3);
        var prograde = Input(new Vector3d(r0, 0, 0), new Vector3d(0, vc, 0), thrust, mass, omega: omega);
        var retrograde = Input(new Vector3d(r0, 0, 0), new Vector3d(0, -vc, 0), thrust, mass, omega: omega);

        BrakeRun pro = BrakeSimulator.Simulate(prograde, prograde.UT);
        BrakeRun retro = BrakeSimulator.Simulate(retrograde, retrograde.UT);

        Assert.IsTrue(pro.Stopped);   // stopped = surface speed down to the stop threshold
        Assert.IsTrue(retro.Stopped);
        Assert.That(retro.BurnDuration, Is.GreaterThan(pro.BurnDuration * 1.5)); // prototype: 203 s vs 88 s
    }

    [Test]
    public void Rotation_TerrainIsReadInTheBodyOrientationAtStart()
    {
        double r0 = R + 10000, vc = Math.Sqrt(Mu / r0);
        double mass = 10, thrust = 3 * SurfaceGravity * mass, w = 1e-3;
        Vector3d last = Zero;
        // 6 km plateau: closer than CloseToTerrain, so the terrain is read at every step,
        // including the step where the run ends.
        Func<Vector3d, double> terrain = p => { last = p; return 6000; };
        var input = Input(new Vector3d(r0, 0, 0), new Vector3d(0, vc, 0), thrust, mass, terrain: terrain, omega: new Vector3d(0, 0, w));

        BrakeRun run = BrakeSimulator.Simulate(input, input.UT);

        Assert.IsTrue(run.Stopped);
        // The body has turned by w * BurnDuration: the reading must undo that turn.
        double angle = -w * run.BurnDuration;
        Vector3d p = run.StopPosition;
        Assert.AreEqual(p.x * Math.Cos(angle) - p.y * Math.Sin(angle), last.x, 1e-3);
        Assert.AreEqual(p.x * Math.Sin(angle) + p.y * Math.Cos(angle), last.y, 1e-3);
        Assert.AreEqual(p.z, last.z, 1e-3);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: a compile error, because `BrakeSimulator`, `BrakeInput`, `BrakeRun`, `BrakeResult` and `BrakeStatus` do not exist yet.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BrakeSimulator.cs`:

```csharp
using System;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Braking
{
    public enum BrakeStatus
    {
        Ok,         // StartUT is the latest start that stops SafeAltitude above the terrain
        TooLate,    // braking now stops, but below SafeAltitude: brake now
        Impossible, // braking now still hits the ground (or there is no thrust)
    }

    // Everything the braking simulation needs. Vectors are in the game's Zup frame relative to
    // the body (z = rotation axis), inertial. Plain data, so tests can build it.
    public struct BrakeInput
    {
        public Vector3d Position;          // m, at UT
        public Vector3d Velocity;          // m/s, at UT
        public double UT;
        public double Mu;                  // m³/s²
        public double BodyRadius;          // m
        public Vector3d AngularVelocity;   // rad/s, along z (BodyRotation.AngularVelocity)
        public double ThrustKN;            // active engines at full throttle
        public double MassT;
        public double Isp;                 // s; 0 = unknown, mass kept constant (pessimistic)
        public double ImpactUT;            // unpowered impact: upper bound of the search
        // Terrain height above BodyRadius (m) under a position given in the body's orientation at UT.
        public Func<Vector3d, double> TerrainHeight;
    }

    // One simulated braking burn.
    public struct BrakeRun
    {
        public bool Stopped;
        public double StopAltitude;   // m above the terrain where the run ended (<= 0: crash)
        public double BurnDuration;   // s
        public double DeltaV;         // m/s spent
        public Vector3d StopPosition; // inertial, Zup
    }

    public struct BrakeResult
    {
        public BrakeStatus Status;
        public double StartUT;
        public double BurnDuration;
        public double DeltaVNeeded;
        public double StopAltitude;
        public Vector3d StopPosition;
    }

    // Forward simulation of the braking burn, like MechJeb's landing prediction: coast on the
    // Kepler orbit until the start time, then burn ThrustFraction of the active thrust against the
    // surface velocity (RK4), with varying gravity, mass loss, body rotation and the terrain
    // under the path. FindStart bisects the latest start that still stops SafeAltitude up.
    public static class BrakeSimulator
    {
        public const double SafeAltitude = 50;     // m
        public const double StopSpeed = 1;         // m/s
        public const double Step = 0.5;            // s
        public const double TerrainInterval = 2;   // s between terrain readings
        public const double CloseToTerrain = 5000; // m: closer than this, read the terrain every step
        public const double MaxBurnTime = 3600;    // s
        public const double StartPrecision = 0.01; // s
        public const int MaxIterations = 40;
        public const double G0 = 9.80665;
        // Numeric floor only (real dry mass is unknown): keeps a very long burn from dividing by ~0.
        const double MinMassFraction = 0.05;

        public static BrakeResult FindStart(BrakeInput input)
        {
            if (!(input.ThrustKN > 0) || !(input.MassT > 0))
                return new BrakeResult { Status = BrakeStatus.Impossible, StartUT = input.UT };

            BrakeRun now = Simulate(input, input.UT);
            if (!now.Stopped)
                return ToResult(BrakeStatus.Impossible, input.UT, now);
            if (now.StopAltitude < SafeAltitude)
                return ToResult(BrakeStatus.TooLate, input.UT, now);

            // lo is always a verified safe start, hi an unsafe one (the impact itself).
            double lo = input.UT, hi = Math.Max(input.ImpactUT, input.UT);
            BrakeRun best = now;
            for (int i = 0; i < MaxIterations && hi - lo > StartPrecision; i++)
            {
                double mid = 0.5 * (lo + hi);
                BrakeRun run = Simulate(input, mid);
                if (run.Stopped && run.StopAltitude >= SafeAltitude)
                {
                    lo = mid;
                    best = run;
                }
                else
                {
                    hi = mid;
                }
            }
            return ToResult(BrakeStatus.Ok, lo, best);
        }

        public static BrakeRun Simulate(BrakeInput input, double startUT)
        {
            Vector3d r = input.Position, v = input.Velocity;
            double coast = startUT - input.UT;
            if (coast > 1e-6)
                KeplerPropagator.Propagate(input.Position, input.Velocity, input.Mu, coast, out r, out v);

            double thrust = DescentEnvelope.ThrustFraction * input.ThrustKN;   // kN
            double flow = input.Isp > 0 ? thrust / (input.Isp * G0) : 0;      // t/s
            double minMass = input.MassT * MinMassFraction;
            double m = input.MassT;
            double t = startUT, burn = 0;
            double terrain = 0, nextTerrainUT = double.NegativeInfinity;

            while (true)
            {
                double aboveRadius = r.magnitude - input.BodyRadius;
                if (t >= nextTerrainUT || aboveRadius - terrain < CloseToTerrain)
                {
                    terrain = TerrainUnder(input, r, t);
                    nextTerrainUT = t + TerrainInterval;
                }
                double altitude = aboveRadius - terrain;

                double speed = BodyRotation.SurfaceVelocity(r, v, input.AngularVelocity).magnitude;
                // Within one step of zero speed: going on would only let RK4 chatter around zero.
                bool stopped = speed <= Math.Max(StopSpeed, thrust / m * Step);

                if (altitude <= 0 || stopped || burn >= MaxBurnTime)
                {
                    return new BrakeRun
                    {
                        Stopped = stopped && altitude > 0,
                        StopAltitude = altitude,
                        BurnDuration = burn,
                        DeltaV = input.Isp > 0 ? input.Isp * G0 * Math.Log(input.MassT / m) : thrust / input.MassT * burn,
                        StopPosition = r,
                    };
                }

                RK4Step(input, thrust, flow, ref r, ref v, ref m);
                if (m < minMass)
                    m = minMass;
                t += Step;
                burn += Step;
            }
        }

        static BrakeResult ToResult(BrakeStatus status, double startUT, BrakeRun run)
        {
            return new BrakeResult
            {
                Status = status,
                StartUT = startUT,
                BurnDuration = run.BurnDuration,
                DeltaVNeeded = run.DeltaV,
                StopAltitude = run.StopAltitude,
                StopPosition = run.StopPosition,
            };
        }

        // Terrain under r at time t. A point fixed to a body turning at +omega_z around z is, at
        // t, rotated by +omega_z * (t - UT) from where it was at UT: undo that turn to read the
        // terrain in the body's orientation at UT (the only one TerrainHeight knows).
        static double TerrainUnder(BrakeInput input, Vector3d r, double t)
        {
            if (input.TerrainHeight == null)
                return 0;
            double angle = -input.AngularVelocity.z * (t - input.UT);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var bodyFixed = new Vector3d(r.x * c - r.y * s, r.x * s + r.y * c, r.z);
            return input.TerrainHeight(bodyFixed);
        }

        static void RK4Step(BrakeInput input, double thrust, double flow, ref Vector3d r, ref Vector3d v, ref double m)
        {
            double h = Step;
            Vector3d a1 = Acceleration(input, thrust, r, v, m);
            Vector3d v1 = v;
            Vector3d v2 = v + a1 * (h / 2);
            Vector3d a2 = Acceleration(input, thrust, r + v1 * (h / 2), v2, m - flow * h / 2);
            Vector3d v3 = v + a2 * (h / 2);
            Vector3d a3 = Acceleration(input, thrust, r + v2 * (h / 2), v3, m - flow * h / 2);
            Vector3d v4 = v + a3 * h;
            Vector3d a4 = Acceleration(input, thrust, r + v3 * h, v4, m - flow * h);

            r = r + (v1 + v2 * 2 + v3 * 2 + v4) * (h / 6);
            v = v + (a1 + a2 * 2 + a3 * 2 + a4) * (h / 6);
            m -= flow * h; // linear in time: exact
        }

        // Gravity, plus thrust against the surface velocity.
        static Vector3d Acceleration(BrakeInput input, double thrust, Vector3d r, Vector3d v, double m)
        {
            double rm = r.magnitude;
            Vector3d accel = r * (-input.Mu / (rm * rm * rm));
            Vector3d vs = BodyRotation.SurfaceVelocity(r, v, input.AngularVelocity);
            double speed = vs.magnitude;
            if (speed > 1e-9)
                accel = accel + vs * (-thrust / (m * speed));
            return accel;
        }
    }
}
```

`Vector3d` is `KSP.Sim.Vector3d`. `OrbitMath.cs` already uses it with `Cross`, `Dot`, `+`, `-` and scalar products. If `vector * double` does not compile, write the product the way `OrbitMath.cs` writes it (for example `double * vector`). Do not add a helper.

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0`, `failed="0"`.

If `VerticalFall_LatestStartStopsAtSafeAltitude` reports a stop altitude above 55 m, check two things:
- the bisection uses the exact start time, with no rounding of `mid`;
- `Simulate` propagates the coast with `KeplerPropagator` in one call, not in fixed steps (the prototype had exactly this bug).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/BrakeSimulator.cs* Assets/Tests/Landing/BrakeSimulatorTests.cs*
git commit -m "Add braking burn simulation for landing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: `LandingFeasibility`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/LandingFeasibility.cs`
- Test: `Assets/Tests/Landing/LandingFeasibilityTests.cs`

**Interfaces:**
- Consumes: `BrakeResult` and `BrakeStatus` (Task 3); `DescentEnvelope.ThrustFraction` (Task 1).
- Produces:
  - `struct FeasibilityResult { bool Ok; string Error; string Warning; }`;
  - `LandingFeasibility.Check(double accel, double surfaceGravity, BrakeResult? brake, double deltaVRemaining) : FeasibilityResult`;
  - the constants `TwrMargin` (1.05) and `DeltaVMargin` (1.10);
  - the message constants `NoEngineMessage`, `CannotStopMessage` and `DeltaVUnknownMessage`.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Landing/LandingFeasibilityTests.cs`:

```csharp
using K2D2.Landing.Braking;
using NUnit.Framework;

public class LandingFeasibilityTests
{
    const double G = 1.62845; // Mun surface gravity

    static BrakeResult Brake(BrakeStatus status, double dv = 500)
    {
        return new BrakeResult { Status = status, DeltaVNeeded = dv };
    }

    [Test]
    public void NoEngine_Refused()
    {
        var result = LandingFeasibility.Check(0, G, null, 1000);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("No active engine: stage or activate your engines first.", result.Error);
        Assert.IsFalse(LandingFeasibility.Check(double.NaN, G, null, 1000).Ok);
    }

    [Test]
    public void LowTwr_Refused()
    {
        // 0.85 * 1.2 g = 1.02 g <= 1.05 g
        var result = LandingFeasibility.Check(1.2 * G, G, null, 1000);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Local TWR 1.20: too low to stop safely.", result.Error);
    }

    [Test]
    public void EnoughTwr_NoCollisionYet_Accepted()
    {
        // 0.85 * 1.3 g = 1.105 g > 1.05 g; no simulation (precision landing starting in orbit).
        var result = LandingFeasibility.Check(1.3 * G, G, null, 0);
        Assert.IsTrue(result.Ok);
        Assert.IsTrue(string.IsNullOrEmpty(result.Error));
        Assert.IsTrue(string.IsNullOrEmpty(result.Warning));
    }

    [Test]
    public void SimulationImpossible_Refused()
    {
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Impossible), 1000);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Cannot stop before the ground with the current thrust.", result.Error);
    }

    [Test]
    public void NotEnoughDeltaV_Refused()
    {
        // 1.10 * 500 = 550 > 540
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Ok, 500), 540);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Δv 540 m/s for ~500 m/s needed.", result.Error);
    }

    [Test]
    public void EnoughDeltaV_Accepted()
    {
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Ok, 500), 560);
        Assert.IsTrue(result.Ok);
        Assert.IsTrue(string.IsNullOrEmpty(result.Warning));
    }

    [Test]
    public void TooLate_IsNotARefusal()
    {
        Assert.IsTrue(LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.TooLate, 100), 1000).Ok);
    }

    [TestCase(0.0)]
    [TestCase(double.NaN)]
    public void UnknownDeltaV_WarnsWithoutRefusing(double remaining)
    {
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Ok, 500), remaining);
        Assert.IsTrue(result.Ok);
        Assert.AreEqual("Δv unknown: check your fuel.", result.Warning);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: a compile error, because `LandingFeasibility` does not exist yet.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/LandingFeasibility.cs`:

```csharp
using System.Globalization;

namespace K2D2.Landing.Braking
{
    public struct FeasibilityResult
    {
        public bool Ok;
        public string Error;   // why the start is refused (Ok == false)
        public string Warning; // shown without refusing
    }

    // Checks run when the player starts the landing autopilot. The first failure refuses the
    // start; the order matches the spec's table.
    public static class LandingFeasibility
    {
        public const double TwrMargin = 1.05;
        public const double DeltaVMargin = 1.10;

        public const string NoEngineMessage = "No active engine: stage or activate your engines first.";
        public const string CannotStopMessage = "Cannot stop before the ground with the current thrust.";
        public const string DeltaVUnknownMessage = "Δv unknown: check your fuel.";

        // accel: active thrust / mass (m/s²). brake: braking simulation, null when no collision
        // is predicted yet. deltaVRemaining: the game's Δv for the whole vessel (<= 0 or NaN = unknown).
        public static FeasibilityResult Check(double accel, double surfaceGravity, BrakeResult? brake, double deltaVRemaining)
        {
            if (!(accel > 0))
                return Refuse(NoEngineMessage);

            if (DescentEnvelope.ThrustFraction * accel <= TwrMargin * surfaceGravity)
                return Refuse(string.Format(CultureInfo.InvariantCulture,
                    "Local TWR {0:0.00}: too low to stop safely.", accel / surfaceGravity));

            if (brake == null)
                return new FeasibilityResult { Ok = true };

            if (brake.Value.Status == BrakeStatus.Impossible)
                return Refuse(CannotStopMessage);

            if (!(deltaVRemaining > 0))
                return new FeasibilityResult { Ok = true, Warning = DeltaVUnknownMessage };

            double needed = brake.Value.DeltaVNeeded;
            if (DeltaVMargin * needed > deltaVRemaining)
                return Refuse(string.Format(CultureInfo.InvariantCulture,
                    "Δv {0:0} m/s for ~{1:0} m/s needed.", deltaVRemaining, needed));

            return new FeasibilityResult { Ok = true };
        }

        static FeasibilityResult Refuse(string error) => new FeasibilityResult { Ok = false, Error = error };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0`, `failed="0"`. If the `Δv` strings fail, make sure both `.cs` files are saved as UTF-8 so the `Δ` survives.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/LandingFeasibility.cs* Assets/Tests/Landing/LandingFeasibilityTests.cs*
git commit -m "Add landing start feasibility checks

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: Active engines in `BurndV`

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/KSPService/BurndV.cs`: fields at lines 29-33, `Engine_Running` at lines 46-49, `Compute_Thrust` at lines 69-92

**Interfaces:**
- Consumes: `ThrustMath.Acceleration` and `ThrustMath.CombinedIsp` (Task 1).
- Produces these new public members on `BurndV`, updated in `Compute_Thrust()`:
  - `float active_thrust`: kN of the engines that can thrust now, at full throttle;
  - `float active_dv`: m/s², `active_thrust / mass`, 0 when nothing is active;
  - `float active_isp`: s, combined, 0 when unknown;
  - `double mass`: t;
  - `static bool IsActive(DeltaVEngineInfo)`.
- The existing `actual_dv` and `full_dv` keep their meaning, but can no longer be NaN or infinite.

No unit test: the class reads game objects. The test command doubles as the compile check, and in-game scenario 4 checks the behavior.

- [ ] **Step 1: Add the fields**

In `BurndV.cs`, after `public float full_dv;` (line 33), add:

```csharp

        // Engines that can thrust right now (ignited, operational, not starved), at full
        // throttle. Landing uses these; full_dv still counts every engine (other autopilots).
        public float active_thrust;   // kN
        public float active_dv;       // m/s², 0 when nothing is active
        public float active_isp;      // s, combined; 0 when unknown
        public double mass;           // t

        readonly List<double> active_thrusts = new List<double>();
        readonly List<double> active_isps = new List<double>();
```

- [ ] **Step 2: Add the engine filter**

After `Engine_Running` (lines 46-49), add:

```csharp

        // Can this engine thrust if the throttle is raised? Staged (ignited), not broken or
        // shut down, and fed.
        public static bool IsActive(DeltaVEngineInfo engine_info)
        {
            var engine = engine_info.Engine;
            return engine != null && engine.EngineIgnited && engine.IsOperational && !engine.IsPropellantStarved;
        }
```

- [ ] **Step 3: Rewrite `Compute_Thrust`**

Replace the whole `Compute_Thrust` method (lines 69-92) with:

```csharp
        public void Compute_Thrust()
        {
            if (current_vessel.VesselComponent == null) return;
            VesselDeltaVComponent delta_v = current_vessel.VesselComponent.VesselDeltaV;
            if (delta_v == null) return;

            mass = current_vessel.VesselComponent.totalMass;

            actual_thrust = Vector3.zero;
            full_thrust = Vector3.zero;
            Vector3 active_vector = Vector3.zero;
            active_thrusts.Clear();
            active_isps.Clear();

            List<DeltaVEngineInfo> engineInfos = delta_v.EngineInfo;
            for (int i = 0; i < engineInfos.Count; i++)
            {
                DeltaVEngineInfo engineInfo = engineInfos[i];

                Vector3 vector = ((engineInfo.Engine != null) ? engineInfo.Engine.ThrustDirRelativePartWorldSpace : (1f * Vector3.back));

                float full = compute_full_thrust(engineInfo);
                actual_thrust += vector * engineInfo.Engine.FinalThrustValue;
                full_thrust += vector * full;

                if (IsActive(engineInfo))
                {
                    active_vector += vector * full;
                    active_thrusts.Add(full);
                    active_isps.Add(engineInfo.IspActual > 0 ? engineInfo.IspActual : engineInfo.IspVac);
                }
            }

            actual_dv = (float)K2D2.Landing.Braking.ThrustMath.Acceleration(actual_thrust.magnitude, mass);
            full_dv = (float)K2D2.Landing.Braking.ThrustMath.Acceleration(full_thrust.magnitude, mass);

            active_thrust = active_vector.magnitude;
            active_dv = (float)K2D2.Landing.Braking.ThrustMath.Acceleration(active_thrust, mass);
            active_isp = (float)K2D2.Landing.Braking.ThrustMath.CombinedIsp(active_thrusts, active_isps);
        }
```

`IspVac` is listed in `API-Documentation/beta/xrefmap.yml` as `KSP.Sim.DeltaV.DeltaVEngineInfo.IspVac`. If it does not compile, use `IspActual` alone.

- [ ] **Step 4: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/KSPService/BurndV.cs
git commit -m "Track active engine thrust, mass and Isp in BurndV

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: Failed controllers stop the landing

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/BaseControllers/ExecuteController.cs`
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/Circularize.cs:86-110`
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/DeorbitBurn.cs:87-129`

**Interfaces:**
- Produces:
  - `ExecuteController.failure_reason` (`string`, null when not failed);
  - `ExecuteController.Fail(string reason)` (protected);
  - `SingleExecuteController.failed` (`bool`);
  - `SingleExecuteController.failure_reason` (`string`).

- [ ] **Step 1: `ExecuteController` and `SingleExecuteController`**

In `ExecuteController` (class at line 53), replace:

```csharp
        public bool finished = false;
        public string status_line = "";

        // called everytime the Pilot shoudl start
        public virtual void Start()
        {
            finished = false;
        }
```

with:

```csharp
        public bool finished = false;
        public string status_line = "";

        // Set (with finished) by a controller that cannot do its task; null otherwise. The pilot
        // running it stops and shows the reason instead of moving on to its next phase.
        public string failure_reason = null;

        // called everytime the Pilot shoudl start
        public virtual void Start()
        {
            finished = false;
            failure_reason = null;
        }

        protected void Fail(string reason)
        {
            failure_reason = reason;
            status_line = reason;
            finished = true;
        }
```

In `SingleExecuteController`, after the `finished` property (lines 15-24), add:

```csharp

        public bool failed => sub_controler != null && sub_controler.failure_reason != null;

        public string failure_reason => sub_controler?.failure_reason;
```

- [ ] **Step 2: `Circularize.Start`**

`Circularize.Start` sets `finished = false` itself and does not call `base.Start()`, so it has to clear the failure itself too. In `Circularize.cs`:

- After `finished = false;` (line 88), add `failure_reason = null;`.
- Replace `if (current_vessel == null) { finished = true; return; }` (line 92) with:

  ```csharp
              if (current_vessel == null) { Fail("No active vessel."); return; }
  ```

- In the `TooHigh` branch (lines 104-110), keep the `logger.LogInfo` line and replace the `status_line = …; finished = true;` pair with:

  ```csharp
                  Fail($"Starting orbit too high for precision landing (Ap {apoapsisAlt_m / 1000:n0}km / Pe {periapsisAlt_m / 1000:n0}km, max {max_starting_altitude_m / 1000:n0}km) - circularize to a lower orbit first.");
  ```

The `AlreadyCircular` branch keeps its plain `finished = true`: it is not a failure.

- [ ] **Step 3: `DeorbitBurn.Start`**

In `DeorbitBurn.cs`:

- After `finished = false;` (line 89), add `failure_reason = null;`.
- In the vessel-null block (lines 93-97), replace the body `finished = true; return;` with:

  ```csharp
                  Fail("No active vessel.");
                  return;
  ```

- In `if (!found)` (lines 124-129), replace the `status_line = …; finished = true;` pair with:

  ```csharp
                  Fail("Couldn't find a safe deorbit window - try again in a moment.");
  ```

`MidCourseCorrection` is not touched: per the spec, its "no window" and "nothing to do" outcomes are skips, not failures.

- [ ] **Step 4: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/BaseControllers/ExecuteController.cs Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/Circularize.cs Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/DeorbitBurn.cs
git commit -m "Report Circularize and DeorbitBurn refusals as failures

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: `LandingPilot`: simulation, start checks, failure stop, envelope

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs`

**Interfaces:**
- Consumes:
  - Task 1: `DescentEnvelope.MaxSpeed`.
  - Task 2: the `BodyRotation` functions.
  - Task 3: `BrakeSimulator.FindStart`, `BrakeInput`, `BrakeResult`, `BrakeStatus`.
  - Task 4: `LandingFeasibility.Check`.
  - Task 5: `BurndV.active_thrust`, `active_dv`, `active_isp`, `mass` and `Compute_Thrust()`.
  - Task 6: `SingleExecuteController.failed` and `failure_reason`.
- Produces, for Task 9 (`LandingUI`):
  - `internal string last_error` (empty when there is none);
  - `internal string last_warning`;
  - `internal BrakeResult brake_result`;
  - `internal bool brake_result_valid`.

No unit test, because the class is all game state: compile check, then the in-game checklist.

- [ ] **Step 1: Usings and fields**

Add `using K2D2.Landing.Braking;` after `using K2D2.Node;` (line 8).

After the `target_error_m` field (line 260), add:

```csharp

        // Braking simulation (Braking/BrakeSimulator.cs). It runs dozens of simulated burns, so it
        // is refreshed every BrakeSimInterval seconds of real time, not every frame.
        internal BrakeResult brake_result;
        internal bool brake_result_valid = false;
        float next_brake_sim_time = 0;
        const float BrakeSimInterval = 0.5f;
        BrakeStatus last_logged_brake_status = (BrakeStatus)(-1);
        float next_brake_log_time = 0;

        // Sign of the body rotation in the Zup frame (BodyRotation.ChooseSign), calibrated once
        // per body against the surface speed the game measures.
        int rotation_sign = 1;
        string rotation_sign_body = null;

        // Why the last start was refused, or why the last run stopped. Shown until the next
        // successful start.
        internal string last_error = "";
        // Start-check warning that does not refuse (unknown Δv). Shown while running.
        internal string last_warning = "";
```

- [ ] **Step 2: Simulation helpers**

After `HaversineDistanceMeters` (which ends at line 544), add:

```csharp

        // Runs the braking simulation from the current state, at most every BrakeSimInterval
        // (unless forced). brake_result_valid is false when there is no predicted collision.
        void UpdateBrakeSimulation(bool force)
        {
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (!force && now_real < next_brake_sim_time)
                return;
            next_brake_sim_time = now_real + BrakeSimInterval;

            brake_result_valid = false;
            if (!collision_detected || current_vessel?.VesselComponent == null)
                return;

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            Vector3d r0 = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v0 = orbit.GetOrbitalVelocityAtUTZup(now);
            double omega = BodyRotation.AngularSpeed(body.rotationPeriod);
            UpdateRotationSign(body, r0, v0, omega);

            var input = new BrakeInput
            {
                Position = r0,
                Velocity = v0,
                UT = now,
                Mu = body.gravParameter,
                BodyRadius = body.radius,
                AngularVelocity = BodyRotation.AngularVelocity(omega, rotation_sign),
                ThrustKN = burn_dV.active_thrust,
                MassT = burn_dV.mass,
                Isp = burn_dV.active_isp,
                ImpactUT = adjusted_collision_UT,
                TerrainHeight = p => TerrainHeightAt(body, p),
            };
            brake_result = BrakeSimulator.FindStart(input);
            brake_result_valid = true;
            LogBrakeResult(now);
        }

        void UpdateRotationSign(CelestialBodyComponent body, Vector3d r0, Vector3d v0, double omega)
        {
            if (rotation_sign_body == body.Name)
                return;
            rotation_sign = 1;
            // Too slow, or a polar orbit: both signs predict the same speed. Keep +1, retry later.
            if (!BodyRotation.IsDecisive(r0, v0, omega))
                return;

            double measured = current_vessel.VesselVehicle.SurfaceSpeed;
            rotation_sign = BodyRotation.ChooseSign(r0, v0, omega, measured);
            rotation_sign_body = body.Name;
            logger.LogInfo($"[Landing] rotation sign for {body.Name}: {rotation_sign} " +
                $"(measured surface speed {measured:n1} m/s, ω {omega:e3} rad/s)");
        }

        // Terrain height above the body radius under p (Zup, body-relative, current body
        // orientation), with the same Zup -> Yup swap and frame as compute_real_collision.
        static double TerrainHeightAt(CelestialBodyComponent body, Vector3d p_zup)
        {
            Vector3d p_yup = new Vector3d(p_zup.x, p_zup.z, p_zup.y);
            Position ps = new Position(body.SimulationObject.transform.celestialFrame, p_yup);
            body.GetAltitudeFromTerrain(ps, out double above_terrain, out double scenery_offset);
            return p_zup.magnitude - body.radius - above_terrain;
        }

        void LogBrakeResult(double now)
        {
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (brake_result.Status == last_logged_brake_status && now_real < next_brake_log_time)
                return;
            last_logged_brake_status = brake_result.Status;
            next_brake_log_time = now_real + 5;
            logger.LogInfo($"[Landing] brake sim: {brake_result.Status} start in {brake_result.StartUT - now:n1}s, " +
                $"burn {brake_result.BurnDuration:n1}s, Δv {brake_result.DeltaVNeeded:n0} m/s, stop {brake_result.StopAltitude:n0} m above terrain | " +
                $"thrust {burn_dV.active_thrust:n1} kN, mass {burn_dV.mass:n2} t, Isp {burn_dV.active_isp:n0} s, rotation sign {rotation_sign}");
        }

        // Speed the descent must not exceed at this height: the player's profile, capped by what
        // the active engines can still stop (DescentEnvelope).
        float limit_speed(float height)
        {
            double gravity = current_vessel.VesselComponent.graviticAcceleration.magnitude;
            return (float)DescentEnvelope.MaxSpeed(height, burn_dV.active_dv, gravity,
                settings.touch_down_speed.V, settings.compute_limit_speed(height));
        }

        // Start checks (LandingFeasibility), on fresh data. Sets last_error / last_warning.
        bool CheckCanStart()
        {
            last_warning = "";
            if (current_vessel?.VesselComponent == null)
            {
                last_error = "No active vessel.";
                return false;
            }

            burn_dV.Compute_Thrust();
            var body = current_vessel.VesselComponent.Orbit.referenceBody;
            double surface_gravity = body.gravParameter / (body.radius * body.radius);

            collision_detected = compute_real_collision();
            UpdateBrakeSimulation(force: true);
            BrakeResult? brake = brake_result_valid ? brake_result : (BrakeResult?)null;

            var delta_v = current_vessel.VesselComponent.VesselDeltaV;
            double dv_remaining = delta_v != null ? delta_v.TotalDeltaVActual : double.NaN;

            var check = LandingFeasibility.Check(burn_dV.active_dv, surface_gravity, brake, dv_remaining);
            logger.LogInfo($"[Landing] start check: ok={check.Ok} accel {burn_dV.active_dv:n2} m/s², surface g {surface_gravity:n2}, " +
                $"collision {collision_detected}, sim {(brake.HasValue ? brake.Value.Status.ToString() : "none")}, Δv {dv_remaining:n0} m/s" +
                (check.Ok ? "" : $" -> {check.Error}"));

            if (!check.Ok)
            {
                last_error = check.Error;
                return false;
            }
            last_warning = check.Warning ?? "";
            return true;
        }

        void StopWithError(string reason)
        {
            logger.LogWarning($"[Landing] stopped: {reason}");
            isRunning = false;
            last_error = reason;
        }
```

- [ ] **Step 3: Use the checks in the `isRunning` setter**

In the setter's start branch (line 201), replace:

```csharp
                else
                {
                    // Start total burn counter
                    burn_dV.reset();
```

with:

```csharp
                else
                {
                    // Refuse an impossible landing before touching anything: _active stays
                    // false, and LandingUI shows last_error and resets its button.
                    if (!CheckCanStart())
                    {
                        logger.LogInfo($"[Landing] start refused: {last_error}");
                        return;
                    }
                    last_error = "";

                    // Start total burn counter
                    burn_dV.reset();
```

- [ ] **Step 4: Feed the simulation into `computeValues` and `compute_startBurn`**

In `computeValues`, replace (lines 279-282):

```csharp
            speed_collision = orbit.GetOrbitalVelocityAtUTZup(adjusted_collision_UT).magnitude;
            burn_duration = (speed_collision / burn_dV.full_dv);

            compute_startBurn();
```

with:

```csharp
            speed_collision = orbit.GetOrbitalVelocityAtUTZup(adjusted_collision_UT).magnitude;
            // Legacy estimate, still used by precision landing's floor; 0 (not infinity) without thrust.
            burn_duration = burn_dV.active_dv > 0 ? speed_collision / burn_dV.active_dv : 0;

            UpdateBrakeSimulation(force: false);
            compute_startBurn();
```

In `compute_startBurn`, replace the whole block from `startBurn_UT = adjusted_collision_UT - burn_duration - burn_before;` (line 364) down to and including `startBurn_UT = now_ut;` (line 374), including the "Backstop" comment between them, with:

```csharp
            startBurn_UT = adjusted_collision_UT - burn_duration - burn_before;
            double now_ut = GeneralTools.Game.UniverseModel.UniverseTime;

            // The braking simulation gives the real latest safe start (gravity, approach angle,
            // mass loss, terrain, body rotation); the player's "burn before" stays an extra margin.
            // Precision landing keeps the earlier of the two, so its correction margins above
            // still apply. TooLate / Impossible: brake now, at full thrust.
            if (brake_result_valid)
            {
                if (brake_result.Status == BrakeStatus.Ok)
                {
                    double simulated = brake_result.StartUT - settings.burn_before.V;
                    startBurn_UT = settings.precision_landing.V ? Math.Min(startBurn_UT, simulated) : simulated;
                }
                else
                {
                    startBurn_UT = now_ut;
                }
                burn_duration = brake_result.BurnDuration;
            }

            // Backstop: whatever combination of the floors above, never schedule the burn as
            // already overdue. WarpTo silently no-ops the instant its target time is in the past,
            // so an overshot start wouldn't just start the burn a bit early, it would skip the
            // warp entirely and force a real-time wait for however long was actually left.
            // Clamping here means the worst case is "start braking immediately".
            if (startBurn_UT < now_ut)
                startBurn_UT = now_ut;
```

- [ ] **Step 5: Envelope and failure stop in `Update`**

In `Update`, replace both occurrences of `brake.max_speed = settings.compute_limit_speed(altitude);` (precision Brake at line 644, TouchDown at line 672) with `brake.max_speed = limit_speed(altitude);`.

Then replace (lines 679-683):

```csharp
            if (current_executor.finished)
            {
                // auto next
                nextMode();
            }
```

with:

```csharp
            // A phase that cannot do its job (Circularize too high, no deorbit window) stops
            // the landing instead of falling through to the next phase.
            if (current_executor.failed)
            {
                StopWithError(current_executor.failure_reason);
                return;
            }

            if (current_executor.finished)
            {
                // auto next
                nextMode();
            }
```

- [ ] **Step 6: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green. If `CelestialBodyComponent` is not found, it is `KSP.Sim.impl.CelestialBodyComponent`, which `using KSP.Sim.impl;` already imports.

- [ ] **Step 7: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs
git commit -m "Plan the landing burn from the braking simulation and check feasibility at start

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: `TouchDown`: active thrust, zero-thrust guard, "cannot stop" flag

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/TouchDown.cs`: `Start` at lines 212-223, `compute_Throttle` at lines 243-263, `Update` at lines 868-916

Only the landing `TouchDown` (namespace `K2D2.Landing`) changes. `Pilots/Lift/TouchDown.cs` is a different class and is not touched.

**Interfaces:**
- Consumes:
  - `DescentEnvelope.CanStop` (Task 1);
  - `BurndV.active_dv` (Task 5);
  - the existing `LandingPilot.altitude` and `LandingPilot.settings.touch_down_speed`.
- Produces: `public bool cannot_stop` on `TouchDown`, used by Task 9.

- [ ] **Step 1: Field, using and reset**

- Add `using K2D2.Landing.Braking;` after `using K2D2.Controller;` (line 9).
- After `public float max_speed = 0;` (line 22), add:

  ```csharp

          // True when even full thrust can no longer stop before the ground (DescentEnvelope.CanStop).
          // The landing tab shows it as an alert; braking goes on at full throttle anyway.
          public bool cannot_stop = false;
  ```
- In `Start()` (line 212), add `cannot_stop = false;` after `finished = false;`.

- [ ] **Step 2: `compute_Throttle`**

Replace the whole `compute_Throttle` method (lines 243-263) with:

```csharp
        void compute_Throttle()
        {
            // Only engines that can thrust right now (BurndV.active_dv). Without any, idle
            // instead of dividing by zero: the landing tab shows the "Cannot stop" alert.
            float accel = burn_dV.active_dv;
            delta_speed = current_speed - max_speed;
            if (!(accel > 0))
            {
                wanted_throttle = 0;
                return;
            }

            float min_throttle = 0;
            if (gravity_compensation && gravity_direction_factor != 0)
                min_throttle = gravity_direction_factor * gravity / accel;

            float remaining_full_burn_time = delta_speed / accel;
            wanted_throttle = Mathf.Clamp(remaining_full_burn_time + min_throttle, 0, 1);
        }
```

- [ ] **Step 3: Compute `cannot_stop` in `Update`**

Add this method just before `public override void Update()`:

```csharp
        void UpdateCannotStop()
        {
            if (landing == null)
            {
                cannot_stop = false;
                return;
            }
            double gravity_now = current_vessel.VesselComponent.graviticAcceleration.magnitude;
            bool now = !DescentEnvelope.CanStop(current_speed, landing.altitude, burn_dV.active_dv,
                gravity_now, landing.settings.touch_down_speed.V);
            if (now && !cannot_stop)
                logger.LogWarning($"[TouchDown] cannot stop before the ground: speed {current_speed:n1} m/s, " +
                    $"height {landing.altitude:n0} m, thrust accel {burn_dV.active_dv:n2} m/s², gravity {gravity_now:n2} m/s²");
            cannot_stop = now;
        }
```

Then, in `Update`, right after `current_speed = (float)current_vessel.VesselVehicle.SurfaceSpeed;` (line 873), call it:

```csharp
            UpdateCannotStop();
```

`landing.settings` is `internal` on `LandingPilot`. `TouchDown` is in the same assembly, so the access compiles.

- [ ] **Step 4: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/TouchDown.cs
git commit -m "Brake on active thrust only and flag when touchdown cannot stop

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: `LandingUI`: persistent messages, button reset

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs`: listeners at lines 86-96, `onUpdateUI` at lines 335-398

**Interfaces:**
- Consumes:
  - `LandingPilot.last_error` and `last_warning` (Task 7);
  - `LandingPilot.brake` (existing field, type `TouchDown`) and its `cannot_stop` (Task 8).

- [ ] **Step 1: Reset the buttons on refusal**

Replace (lines 86-96):

```csharp
            run_button.listeners += v =>
            {
                pilot.isRunning = v;
                run_button.label = v ? "Stop" : "Brake";
            };

            touch_down.listenClick(() =>
            {
                pilot.isRunning = true;
                pilot.setMode(LandingPilot.Mode.TouchDown);
            });
```

with:

```csharp
            run_button.listeners += v =>
            {
                pilot.isRunning = v;
                if (v && !pilot.isRunning)
                {
                    // Refused by the start checks (pilot.last_error is shown below): put the
                    // toggle back. This re-enters the listener with false, which sets the label.
                    run_button.Value = false;
                    return;
                }
                run_button.label = v ? "Stop" : "Brake";
            };

            touch_down.listenClick(() =>
            {
                pilot.isRunning = true;
                if (pilot.isRunning)
                    pilot.setMode(LandingPilot.Mode.TouchDown);
            });
```

- [ ] **Step 2: Show the error, the warning and the alert**

In `onUpdateUI`, make three edits.

First, in the running branch, replace the Brake and TouchDown cases:

```csharp
                    case LandingPilot.Mode.Brake:
                        status_bar.Warning($"Brake !");
                        break;
                    case LandingPilot.Mode.TouchDown:
                        status_bar.Warning($"Touch Down...");
                        break;
```

with:

```csharp
                    case LandingPilot.Mode.Brake:
                        if (pilot.brake.cannot_stop)
                            status_bar.Error("Cannot stop before the ground!");
                        else
                            status_bar.Warning($"Brake !");
                        break;
                    case LandingPilot.Mode.TouchDown:
                        if (pilot.brake.cannot_stop)
                            status_bar.Error("Cannot stop before the ground!");
                        else
                            status_bar.Warning($"Touch Down...");
                        break;
```

Second, still inside `if (pilot.isRunning)`, right after the line `if (pilot.current_executor != null && …) status_bar.Console(pilot.current_executor.status_line);`, add:

```csharp

                if (!string.IsNullOrEmpty(pilot.last_warning))
                    status_bar.Console(pilot.last_warning);
```

Third, in the idle branch, keep the comment and replace:

```csharp
                status_bar.Status("Landing autopilot not enabled");
```

with:

```csharp
                // A refused start or a failed phase stays visible until the next successful
                // start; status_bar.Reset() wipes everything else every tick.
                if (!string.IsNullOrEmpty(pilot.last_error))
                    status_bar.Error(pilot.last_error);
                else
                    status_bar.Status("Landing autopilot not enabled");
```

- [ ] **Step 3: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green.

- [ ] **Step 4: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs
git commit -m "Show landing refusals, failures and the cannot-stop alert persistently

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 10: Build, install, document

The controller does this task itself, not a subagent, because it touches the game folder and the parent repository.

**Files:**
- Modify: `CHANGELOG.md` (section `## Unreleased (SkySpaceAgency)`) and `README.md` (Known limitations)
- Parent repo: `D:/KSPReduxModding/CLAUDE.md` (SkySpaceAgency section) and the submodule pointer

- [ ] **Step 1: Build the zip**

Run the build command. Expected: 0 `error CS`, 0 `logLevel: 2`, and `Deploy/SkySpaceAgency.zip` present (~6.3 MB).

- [ ] **Step 2: Install in the game**

```bash
G="/i/SteamLibrary/steamapps/common/Kerbal Space Program 2/mods/SkySpaceAgency"
S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
rm -rf "$S/zip" && mkdir -p "$S/zip" && unzip -q -o /d/KSPReduxModding/SkySpaceAgency/Deploy/SkySpaceAgency.zip -d "$S/zip"
ls "$S/zip"
```

Copy the unzipped mod folder over `"$G"`, keeping the `skyspaceagency_*.json` settings files already there. Then compare the size and date of `SkySpaceAgency.dll` in the game folder with the build output.

- [ ] **Step 3: CHANGELOG and README**

In `CHANGELOG.md`, under `## Unreleased (SkySpaceAgency)` → `### Changed`, add:

```markdown
- **Landing autopilot safety**:
  - **Refused starts**: Start (Brake or Touch Down) is refused, with a message that stays in the tab, when:
    - no engine is active;
    - the local TWR is too low to stop safely (the plan counts on 85 % of the thrust);
    - even braking now would hit the ground;
    - the vessel has less than 110 % of the Δv the braking burn needs.

    When the game reports no Δv, the tab warns instead of refusing.
  - **Braking start**: braking starts at the latest moment that still stops 50 m above the terrain. A simulation of the burn finds it, accounting for gravity, the approach angle, mass loss, body rotation and the terrain under the path. "Burn before" is now an extra margin on top.
  - **Descent speed**: the speed limit in Brake (precision) and Touch Down is now capped by what the active engines can stop. The altitude/speed profile still applies when it is slower.
  - **Engines**: only active engines (staged, working, fed) count for landing. With no thrust, the throttle goes to 0 instead of NaN. The tab shows "Cannot stop before the ground!" whenever even full thrust is no longer enough.
  - **Failed phases**: a Circularize or deorbit phase that cannot do its job now stops the landing and shows why, instead of moving on to the next phase.
```

In `README.md` → "Known limitations", add:

```markdown
- Landing's braking plan ignores atmospheric drag (which only helps) and assumes the active engines stay as they are: auto-staging during the landing burn is not planned for.
```

- [ ] **Step 4: Commit and push the mod docs, then bump the parent**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add CHANGELOG.md README.md
git commit -m "Document landing safety changes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

Then, in `D:/KSPReduxModding/CLAUDE.md`, add a **Landing safety** bullet to the SkySpaceAgency section, after the Orbit bullet. It should give the spec and plan paths, and say that the pure code in `Pilots/Landing/Braking/` is tested by `Assets/Tests/Landing`. It should also list what is **not verified in game**:
- what `EngineIgnited`, `IsOperational` and `IsPropellantStarved` mean at zero throttle;
- the rotation sign (logged as `[Landing] rotation sign for …`);
- the terrain height that `GetAltitudeFromTerrain` returns far from the vessel.

```bash
cd /d/KSPReduxModding
git add SkySpaceAgency CLAUDE.md
git commit -m "Bump SkySpaceAgency: landing safety

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 5: Hand the in-game checklist to the user**

Give the user the spec's six "En jeu" scenarios, and ask them to send back these log lines:
- `[Landing] start check`
- `[Landing] brake sim`
- `[Landing] rotation sign`
- `[TouchDown] cannot stop`
- `[Landing] stopped`
