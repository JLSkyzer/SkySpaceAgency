# Landing Modes (Legs and Parachutes) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two ways to land, chosen by the player and started by one button:
- **Propulsive (legs)**: the engines brake to a soft touchdown. The legs deploy by themselves, the vessel ends upright without drift, and the autopilot stops on contact.
- **Parachute**: no engine at all. The vessel holds retrograde during reentry, the parachutes are armed and the game opens them, then the legs deploy if there are any. The vessel comes down under canopy.

**Architecture:** The new math lives in two pure units under `Pilots/Landing/Braking/`, unit-tested in the editor:
- `FinalDescent`: aim direction below 50 m (vertical tilted against the drift, capped at 15°), thrust factor `max(0, cos(error))`, contact speed floor 0.5 m/s;
- `ParachuteFeasibility`: parachute-mode start refusals and their messages, the "Too fast under canopy" and "No parachute left" tests.

The game-facing code only gathers inputs and applies results:
- `KSPService/LandingGear` finds the legs, fires the Gear action group, re-reads every leg 3 s later and extends the stragglers one by one;
- `KSPService/Parachutes` counts the parachutes by state and arms the stowed ones;
- `ParachuteDescent` is the controller of the single `Mode.Parachute` phase;
- `LandingPilot` picks the mode at start, runs the matching checks, deploys the legs on entering Touch Down (or Brake), and ends both modes on `LandedOrSplashed`;
- `TouchDown` flies the final 50 m;
- `LandingSettings`, `LandingUI` and `Landing.uxml` add the mode selector, the "Deploy legs early" toggle, the legs/parachutes info rows, the button label and the alerts.

**Tech Stack:** Unity 6000.6.0f1, C# (mod assembly `SkySpaceAgency`), Unity Test Framework (NUnit, EditMode), KSP2 Redux 26w41a game API (`KSP.Sim`, `KSP.Sim.impl`, `KSP.Modules`).

**Spec:** `docs/superpowers/specs/2026-10-06-landing-modes-design.md` (read it first; this plan implements it). It builds on `docs/superpowers/specs/2026-10-05-landing-safety-design.md`, already implemented.

**Deliberate deviations from the spec:** each one is forced by the game API or keeps the spec's own behavior consistent.
1. **The per-leg fallback uses the leg's "Extend Part" toggle, not `Module_Deployable.Extend()`.** The API declares `protected virtual bool Extend()` (`API-Documentation/beta/AssemblyCSharp/KSP.Modules.Module_Deployable.html`), so the mod cannot call it. The public route is `Data_Deployable.toggleExtend.SetValue(true)`, which `Module_Deployable.OnToggleExtendChanged` handles ("Called when clicking on the deploy toggle", `Assembly-CSharp.xml`). A leg whose toggle is already on but which is still `Retracted` is left alone and logged: flipping the toggle off and on again could retract it if the toggle actually toggles.
2. **A leg is a part with `Data_Deployable` whose `DefaultActionGroup` includes `KSPActionGroup.Gear`, or that also carries a wheel/suspension module (`Data_WheelBase`, "rover wheels and landing legs").** `Module_Deployable` also drives solar panels and antennas (`Module_SolarPanel` derives from it), and the spec does not say how to tell legs apart. Every gear log line lists what was found, so a wrong filter shows up in the first in-game test.
3. **"No parachute on this vessel." is checked even inside the atmosphere, and counts only parachutes that are not `CUT`.** Only the "deorbit first" check is waived inside the atmosphere. Spec test 3 only requires the high-periapsis case to pass inside the atmosphere, and starting a parachute landing with no usable parachute would do nothing at all.
4. **The Touch Down button is hidden in parachute mode, and its click is ignored there.** It starts an engine descent, and the spec gives parachute mode a single "Land" button.
5. **The old altitude/fall-speed net (`altitude < 5 && fall speed < 1`) also ends a parachute landing.** The spec makes the touchdown end common to both modes; keeping one end path avoids a second, mode-specific copy.
6. **The gear log has a third line**, 3 s after a fallback, with the state the fallback reached. Without it, the log would not show whether the fallback worked.

## Global Constraints

- Unity editor: `D:/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe`. Use this exact version; never open the project with another one.
- Project path: `D:/KSPReduxModding/SkySpaceAgency`. Close any interactive Unity editor on this project before a batch run: batch mode fails on a locked project.
- Logs and test results go to the session scratchpad, never into the repository.
- Mod code lives in `Assets/SkySpaceAgency/Code` (assembly `SkySpaceAgency`, root namespace `K2D2`). The new pure code uses namespace `K2D2.Landing.Braking`, in folder `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/`. Every new type is `public` so the test assembly can reach it. The new game-facing helpers go to `Assets/SkySpaceAgency/Code/KSPService/` (namespace `K2D2.KSPService`), the new controller to `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/` (namespace `K2D2.Landing`).
- Frame: `FinalDescent` works in whatever frame its vectors share. The game code passes the `HorizonUp` frame of `SASTool.getTelemetry()`, as `TouchDown.checkDirection` already does, and applies the result through SAS StabilityAssist and `SetTargetOrientation` in that frame.
- Units: heights in m above the ground (`LandingPilot.altitude`, from `GetApproxAltitude`), speeds in m/s, gravity in m/s², angles in degrees at the pure units' interfaces.
- Line numbers in this plan are those of the files at commit `6112534` (start of the plan). Earlier tasks of this plan shift some of them by a few lines: always locate an edit by the quoted code.
- Constants (spec):

  | Constant | Value |
  |---|---|
  | Final descent height h_final | 50 m |
  | Tilt gain k in `θ = atan(k · v_h / g)` | 1.0 |
  | Tilt cap | 15° |
  | Contact speed floor (below h_final) | 0.5 m/s |
  | Gear re-check delay | 3 s of game time after the command |
  | "Too fast under canopy" | descent speed > 10 m/s, height < 500 m, at least one parachute `DEPLOYED` |

- UI and log text is in English, like the rest of the mod. Messages are exactly those of the spec:
  - `No atmosphere here: parachute landing needs one.`
  - `No parachute on this vessel.`
  - `Trajectory stays above the atmosphere: deorbit first.`
  - `Too fast under canopy`
  - `No parachute left`
  - `No legs`

  Gear log lines start with `[Landing] gear:`.
- Game API members used by this plan, all confirmed in `API-Documentation/beta/xrefmap.yml` and its HTML pages (docs of 2026-10-05):
  - `VesselComponent`: `LandedOrSplashed` (bool), `IsInAtmosphere` (bool), `SetActionGroup(KSPActionGroup, bool)`, `GetActionGroupState(KSPActionGroup) : KSPActionGroupState`, `SimulationObject.objVesselBehavior`;
  - `KSPActionGroup.Gear` (flags enum), `KSPActionGroupState { None, True, False, Mixed }` (namespace `KSP.Sim`);
  - `CelestialBodyComponent.hasAtmosphere` (bool), `atmosphereDepth` (double, m above the radius);
  - `VesselBehavior.parts : IEnumerable<PartBehavior>`, `PartBehavior.Model : PartComponent`, `PartBehavior.GetModule<T>() where T : class, IPartModule`;
  - `PartComponent.TryGetModuleData<T, U>(out U) where T : PartComponentModule where U : ModuleData`, with `KSP.Sim.impl.PartComponentModule_Deployable` / `KSP.Modules.Data_Deployable` and `KSP.Sim.impl.PartComponentModule_Parachute` / `KSP.Modules.Data_Parachute`;
  - `Data_Deployable.CurrentDeployState` (`EntityModuleProperty<DeployableData, Data_Deployable.DeployState>`), `toggleExtend` (`EntityModuleProperty<DeployableData, bool>`), `DefaultActionGroup` (`KSPActionGroup`); `Data_Deployable.DeployState { Retracted, Extended, Retracting, Extending, Broken }`;
  - `Data_Parachute.deployState` (`ModuleProperty<Data_Parachute.DeploymentStates>`); `DeploymentStates { STOWED, ARMED, SEMIDEPLOYED, DEPLOYED, CUT }`;
  - `Module_Parachute.ArmChute()` (public void; `Module_Parachute` implements `IPartModule`);
  - `GetValue()` (public virtual) and `SetValue(T)` (public), inherited by both property types from `KSP.Api.CoreTypes.PropertyReadonly<T>`.

  `Module_Deployable.Extend()` is **protected**: do not call it (deviation 1).
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
- All existing tests (Orbit, Landing, UI) must stay green. Note the `total` before Task 1: each task below says how many tests it adds.
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
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/FinalDescent.cs` | Final-descent aim, thrust factor, contact speed floor. |
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ParachuteFeasibility.cs` | Parachute-mode start refusals and messages; canopy alerts. |
| Create `Assets/SkySpaceAgency/Code/KSPService/LandingGear.cs` | Find the legs, deploy them (Gear action group), re-check at 3 s, per-leg fallback, `[Landing] gear:` log. |
| Create `Assets/SkySpaceAgency/Code/KSPService/Parachutes.cs` | Count the parachutes by state; arm the `STOWED` ones. |
| Create `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/ParachuteDescent.cs` | Controller of `Mode.Parachute`: retrograde, arming, SAS release and legs at canopy opening, alerts. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingSettings.cs` | `LandingMethod` enum, `land.mode` (atmo profile only), `land.deploy_legs_early` (both); their UI bindings. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs` | `Mode.Parachute`, mode-dependent start and checks, legs on Touch Down/Brake, common touchdown end, contact speed floor. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/TouchDown.cs` | Final 50 m: aim, thrust modulation instead of the alignment cut. |
| Modify `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs`, `Assets/UI/K2D2_UI/Landing.uxml` | Mode selector, "Deploy legs early" toggles, info rows, button label, alerts. |
| Create `Assets/Tests/Landing/FinalDescentTests.cs`, `Assets/Tests/Landing/ParachuteFeasibilityTests.cs` | EditMode tests (existing assembly `SkySpaceAgency.Tests.Landing`). |
| Modify `CHANGELOG.md`, `README.md` | Documentation. |

---

### Task 1: `FinalDescent`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/FinalDescent.cs`
- Test: `Assets/Tests/Landing/FinalDescentTests.cs`

**Interfaces:**
- Produces:
  - the constants `FinalDescent.FinalHeight` (50), `TiltGain` (1.0), `MaxTiltDegrees` (15), `MinContactSpeed` (0.5);
  - `FinalDescent.IsFinal(double height) : bool` (`height < FinalHeight`);
  - `FinalDescent.TiltDegrees(double horizontalSpeed, double gravity) : double`;
  - `FinalDescent.AimDirection(Vector3d up, Vector3d surfaceVelocity, double gravity) : Vector3d` (unit vector; only the part of `surfaceVelocity` perpendicular to `up` counts; `up` need not be unit);
  - `FinalDescent.ThrustFactor(double alignmentErrorDegrees) : double` (`max(0, cos)`);
  - `FinalDescent.ContactSpeed(double touchDownSpeed) : double` (`max(v_td, 0.5)`).

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Landing/FinalDescentTests.cs`:

```csharp
using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using NUnit.Framework;

public class FinalDescentTests
{
    const double MunG = 1.63; // spec value

    static double AngleDeg(Vector3d a, Vector3d b)
    {
        double c = Vector3d.Dot(a, b) / (a.magnitude * b.magnitude);
        return Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180 / Math.PI;
    }

    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance)
    {
        Assert.AreEqual(expected.x, actual.x, tolerance, "x");
        Assert.AreEqual(expected.y, actual.y, tolerance, "y");
        Assert.AreEqual(expected.z, actual.z, tolerance, "z");
    }

    // Spec test 1, first case.
    [Test]
    public void NoHorizontalSpeed_AimsStraightUp()
    {
        var up = new Vector3d(0, 0, 1);
        AssertVector(up, FinalDescent.AimDirection(up, new Vector3d(0, 0, 0), MunG), 1e-12);
        // Falling straight down: only the horizontal part counts, so still straight up.
        AssertVector(up, FinalDescent.AimDirection(up, new Vector3d(0, 0, -5), MunG), 1e-12);
    }

    // Spec test 1, second case: atan(1 / 1.63) = 31.5°, capped at 15°, against the drift.
    [Test]
    public void OneMeterPerSecondOnTheMun_IsCappedAt15Degrees()
    {
        Assert.AreEqual(31.5, Math.Atan(1 / MunG) * 180 / Math.PI, 0.05); // uncapped value of the spec
        Assert.AreEqual(15.0, FinalDescent.TiltDegrees(1, MunG), 1e-12);

        var up = new Vector3d(0, 0, 1);
        var drift = new Vector3d(1, 0, 0);
        Vector3d aim = FinalDescent.AimDirection(up, drift, MunG);

        Assert.AreEqual(1.0, aim.magnitude, 1e-12);
        Assert.AreEqual(15.0, AngleDeg(aim, up), 1e-9);
        Assert.Less(Vector3d.Dot(aim, drift), 0.0); // leans against the drift
        Assert.AreEqual(0.0, aim.y, 1e-12);          // in the plane of up and the drift
    }

    // Spec test 1, third case: atan(0.1 / 1.63) = 3.5°, not capped.
    [Test]
    public void TenthOfMeterPerSecond_TiltsByAtan()
    {
        double expected = Math.Atan(0.1 / MunG) * 180 / Math.PI;
        Assert.AreEqual(3.5, expected, 0.05);
        Assert.AreEqual(expected, FinalDescent.TiltDegrees(0.1, MunG), 1e-12);

        var up = new Vector3d(0, 0, 1);
        // 0.1 m/s along +y while falling at 3 m/s.
        Vector3d aim = FinalDescent.AimDirection(up, new Vector3d(0, 0.1, -3), MunG);
        Assert.AreEqual(expected, AngleDeg(aim, up), 1e-9);
        Assert.Less(aim.y, 0.0);
        Assert.AreEqual(0.0, aim.x, 1e-12);
    }

    [Test]
    public void Aim_WorksInAnyFrame_AndUpNeedNotBeUnit()
    {
        var up = new Vector3d(3, 4, 0);          // |up| = 5, unit (0.6, 0.8, 0)
        var velocity = new Vector3d(-1.8, -2.4, 2); // 3 m/s down plus 2 m/s along +z
        Vector3d aim = FinalDescent.AimDirection(up, velocity, MunG);

        double c = Math.Cos(15 * Math.PI / 180), s = Math.Sin(15 * Math.PI / 180);
        AssertVector(new Vector3d(0.6 * c, 0.8 * c, -s), aim, 1e-12);
    }

    [Test]
    public void Tilt_BadInputs()
    {
        Assert.AreEqual(0.0, FinalDescent.TiltDegrees(double.NaN, MunG));
        Assert.AreEqual(0.0, FinalDescent.TiltDegrees(1, double.NaN));
        Assert.AreEqual(0.0, FinalDescent.TiltDegrees(-1, MunG));
        Assert.AreEqual(15.0, FinalDescent.TiltDegrees(1, 0), 1e-12);                       // no gravity: cap
        Assert.AreEqual(15.0, FinalDescent.TiltDegrees(double.PositiveInfinity, MunG), 1e-12);
    }

    // Spec test 2, thrust factor.
    [Test]
    public void ThrustFactor_IsCosineOfTheError()
    {
        Assert.AreEqual(1.0, FinalDescent.ThrustFactor(0), 1e-12);
        Assert.AreEqual(Math.Cos(Math.PI / 6), FinalDescent.ThrustFactor(30), 1e-12);
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(90), 1e-12);
    }

    [Test]
    public void ThrustFactor_IsZeroBeyond90Degrees()
    {
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(120));
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(180));
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(double.NaN));
    }

    // Spec test 2, contact speed floor: max(v_td, 0.5).
    [Test]
    public void ContactSpeed_NeverBelowHalfMeterPerSecond()
    {
        Assert.AreEqual(0.5, FinalDescent.ContactSpeed(0), 1e-12);
        Assert.AreEqual(0.5, FinalDescent.ContactSpeed(0.3), 1e-12);
        Assert.AreEqual(2.5, FinalDescent.ContactSpeed(2.5), 1e-12);
        Assert.AreEqual(0.5, FinalDescent.ContactSpeed(double.NaN), 1e-12);
    }

    [Test]
    public void IsFinal_Below50Metres()
    {
        Assert.IsTrue(FinalDescent.IsFinal(49.9));
        Assert.IsFalse(FinalDescent.IsFinal(50));
        Assert.IsFalse(FinalDescent.IsFinal(double.NaN));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0103` / `CS0246`, because `FinalDescent` does not exist yet, and no `results.xml`.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/FinalDescent.cs`:

```csharp
using System;
using KSP.Sim;

namespace K2D2.Landing.Braking
{
    // End of a propulsive landing, below FinalHeight: stand up, lean against the horizontal drift
    // to cancel it, and never cut the thrust only because SAS lags behind.
    public static class FinalDescent
    {
        public const double FinalHeight = 50;      // m above the ground
        public const double TiltGain = 1.0;        // k in atan(k * v_h / g)
        public const double MaxTiltDegrees = 15;
        public const double MinContactSpeed = 0.5; // m/s
        // Below this horizontal speed (m/s) there is no meaningful drift direction.
        const double MinHorizontalSpeed = 1e-6;

        public static bool IsFinal(double height) => height < FinalHeight;

        // Tilt off the vertical, in degrees: atan(k * v_h / g), at most MaxTiltDegrees.
        // 0 (straight up) on unknown inputs; the cap when there is no gravity.
        public static double TiltDegrees(double horizontalSpeed, double gravity)
        {
            if (double.IsNaN(horizontalSpeed) || double.IsNaN(gravity) || !(horizontalSpeed > MinHorizontalSpeed))
                return 0;
            if (!(gravity > 0))
                return MaxTiltDegrees;
            double degrees = Math.Atan(TiltGain * horizontalSpeed / gravity) * 180 / Math.PI;
            return Math.Min(degrees, MaxTiltDegrees);
        }

        // Unit thrust direction: up, tilted by TiltDegrees against the horizontal part of the
        // surface velocity. All vectors in the same frame; up need not be unit.
        public static Vector3d AimDirection(Vector3d up, Vector3d surfaceVelocity, double gravity)
        {
            Vector3d upUnit = up.normalized;
            Vector3d horizontal = surfaceVelocity - upUnit * Vector3d.Dot(surfaceVelocity, upUnit);
            double speed = horizontal.magnitude;
            double tilt = TiltDegrees(speed, gravity) * Math.PI / 180;
            if (tilt == 0)
                return upUnit;
            Vector3d drift = horizontal * (1 / speed);
            return upUnit * Math.Cos(tilt) - drift * Math.Sin(tilt);
        }

        // Share of the computed throttle to apply when the vessel points alignmentErrorDegrees
        // away from the aim: cos(error), 0 from 90° on (or when unknown).
        public static double ThrustFactor(double alignmentErrorDegrees)
        {
            if (double.IsNaN(alignmentErrorDegrees))
                return 0;
            return Math.Max(0, Math.Cos(alignmentErrorDegrees * Math.PI / 180));
        }

        // Target speed at contact: the player's touch-down speed, never below MinContactSpeed,
        // or a touch-down speed of 0 would hover instead of landing.
        public static double ContactSpeed(double touchDownSpeed)
        {
            if (double.IsNaN(touchDownSpeed))
                return MinContactSpeed;
            return Math.Max(touchDownSpeed, MinContactSpeed);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 9 more tests than before this task.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/FinalDescent.cs* Assets/Tests/Landing/FinalDescentTests.cs*
git status --short   # only these paths
git commit -m "Add final descent aim, thrust factor and contact speed floor

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: `ParachuteFeasibility`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ParachuteFeasibility.cs`
- Test: `Assets/Tests/Landing/ParachuteFeasibilityTests.cs`

**Interfaces:**
- Consumes: the existing `FeasibilityResult { bool Ok; string Error; string Warning; }` from `Braking/LandingFeasibility.cs`.
- Produces:
  - `ParachuteFeasibility.Check(bool hasAtmosphere, double atmosphereDepth, bool inAtmosphere, double periapsisAltitude, int usableParachutes) : FeasibilityResult`;
  - `ParachuteFeasibility.TooFastUnderCanopy(int deployed, double height, double descentSpeed) : bool`;
  - `ParachuteFeasibility.NoParachuteLeft(int usable, bool landedOrSplashed) : bool`;
  - the constants `CanopyAlertHeight` (500), `CanopyAlertSpeed` (10), and the message constants `NoAtmosphereMessage`, `NoParachuteMessage`, `DeorbitFirstMessage`, `TooFastMessage`, `NoParachuteLeftMessage`.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Landing/ParachuteFeasibilityTests.cs`:

```csharp
using K2D2.Landing.Braking;
using NUnit.Framework;

public class ParachuteFeasibilityTests
{
    const double Depth = 70000; // Kerbin's atmosphere

    static void AssertRefused(FeasibilityResult result, string message)
    {
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(message, result.Error);
    }

    [Test]
    public void NoAtmosphere_Refused()
    {
        AssertRefused(ParachuteFeasibility.Check(false, 0, false, 5000, 2),
            "No atmosphere here: parachute landing needs one.");
    }

    [Test]
    public void NoParachute_Refused()
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, false, 30000, 0),
            "No parachute on this vessel.");
    }

    [Test]
    public void NoParachute_RefusedEvenInsideTheAtmosphere()
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, true, 30000, 0),
            "No parachute on this vessel.");
    }

    [TestCase(80000.0)] // stable orbit above the atmosphere
    [TestCase(70000.0)] // periapsis right at the edge
    [TestCase(2e6)]     // escape trajectory with a high periapsis
    public void PeriapsisAboveTheAtmosphere_Refused(double periapsis)
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, false, periapsis, 2),
            "Trajectory stays above the atmosphere: deorbit first.");
    }

    [Test]
    public void UnknownPeriapsis_Refused()
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, false, double.NaN, 2),
            "Trajectory stays above the atmosphere: deorbit first.");
    }

    [Test]
    public void PeriapsisInsideTheAtmosphere_Accepted()
    {
        var result = ParachuteFeasibility.Check(true, Depth, false, 30000, 1);
        Assert.IsTrue(result.Ok);
        Assert.IsTrue(string.IsNullOrEmpty(result.Error));
        // Suborbital: the periapsis is below the surface.
        Assert.IsTrue(ParachuteFeasibility.Check(true, Depth, false, -400000, 1).Ok);
    }

    [Test]
    public void AlreadyInTheAtmosphere_AcceptsAHighPeriapsis()
    {
        Assert.IsTrue(ParachuteFeasibility.Check(true, Depth, true, 80000, 1).Ok);
    }

    [Test]
    public void TooFastUnderCanopy_Above10MetresPerSecondBelow500Metres()
    {
        Assert.IsTrue(ParachuteFeasibility.TooFastUnderCanopy(1, 400, 12));
        Assert.IsTrue(ParachuteFeasibility.TooFastUnderCanopy(2, 499, 10.1));
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(1, 400, 10));   // not above 10 m/s
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(1, 500, 20));   // not below 500 m
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(1, 2000, 40));
    }

    [Test]
    public void TooFastUnderCanopy_NeedsAnOpenParachute()
    {
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(0, 400, 40));
    }

    [Test]
    public void NoParachuteLeft_OnlyWhileFlying()
    {
        Assert.IsTrue(ParachuteFeasibility.NoParachuteLeft(0, false));
        Assert.IsFalse(ParachuteFeasibility.NoParachuteLeft(0, true));
        Assert.IsFalse(ParachuteFeasibility.NoParachuteLeft(1, false));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: a compile error, because `ParachuteFeasibility` does not exist yet.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ParachuteFeasibility.cs`:

```csharp
namespace K2D2.Landing.Braking
{
    // Parachute landing: start checks (no engine checks at all) and the two alerts shown under
    // canopy, where nothing can be done about them since there is no engine.
    public static class ParachuteFeasibility
    {
        public const double CanopyAlertHeight = 500; // m above the ground
        public const double CanopyAlertSpeed = 10;   // m/s down

        public const string NoAtmosphereMessage = "No atmosphere here: parachute landing needs one.";
        public const string NoParachuteMessage = "No parachute on this vessel.";
        public const string DeorbitFirstMessage = "Trajectory stays above the atmosphere: deorbit first.";
        public const string TooFastMessage = "Too fast under canopy";
        public const string NoParachuteLeftMessage = "No parachute left";

        // atmosphereDepth and periapsisAltitude in m above the body radius. usableParachutes:
        // parachutes that are not CUT. The first failure refuses the start, in the spec's order.
        // Inside the atmosphere, the trajectory no longer matters: only it is waived.
        public static FeasibilityResult Check(bool hasAtmosphere, double atmosphereDepth, bool inAtmosphere,
            double periapsisAltitude, int usableParachutes)
        {
            if (!hasAtmosphere)
                return Refuse(NoAtmosphereMessage);
            if (usableParachutes <= 0)
                return Refuse(NoParachuteMessage);
            // !(a < b) also refuses an unknown (NaN) periapsis.
            if (!inAtmosphere && !(periapsisAltitude < atmosphereDepth))
                return Refuse(DeorbitFirstMessage);
            return new FeasibilityResult { Ok = true };
        }

        // At least one parachute DEPLOYED, below CanopyAlertHeight, descending faster than
        // CanopyAlertSpeed.
        public static bool TooFastUnderCanopy(int deployed, double height, double descentSpeed)
        {
            return deployed > 0 && height < CanopyAlertHeight && descentSpeed > CanopyAlertSpeed;
        }

        // Every parachute is CUT or gone while the vessel still flies.
        public static bool NoParachuteLeft(int usable, bool landedOrSplashed)
        {
            return usable <= 0 && !landedOrSplashed;
        }

        static FeasibilityResult Refuse(string error) => new FeasibilityResult { Ok = false, Error = error };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0`, `failed="0"`, with 12 more tests than before this task.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Braking/ParachuteFeasibility.cs* Assets/Tests/Landing/ParachuteFeasibilityTests.cs*
git commit -m "Add parachute landing start checks and canopy alerts

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: `LandingGear`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/KSPService/LandingGear.cs`

**Interfaces:**
- Produces, in namespace `K2D2.KSPService`:
  - `class LandingGear`, with `const double RecheckDelay` (3);
  - `void Reset()`: forgets the previous run's request;
  - `void Deploy(VesselComponent vessel, string reason)`: only the first call after `Reset()` acts;
  - `void Update(VesselComponent vessel)`: call every frame; it runs the 3 s re-check and the fallback;
  - `static List<Data_Deployable> FindLegs(VesselComponent vessel)`;
  - `static string Describe(List<Data_Deployable> legs)`, which returns `"No legs"` or, for example, `"4 legs: 2 Extended, 2 Retracted"`;
  - `static string Summary(VesselComponent vessel)`.

No unit test: the class reads game objects. The test command is the compile check, and in-game scenarios 1 to 3 and 6 check the behavior.

- [ ] **Step 1: Write the class**

`Assets/SkySpaceAgency/Code/KSPService/LandingGear.cs`:

```csharp
using System.Collections.Generic;
using System.Text;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.KSPService
{
    // Landing legs. A leg is a part with a deployable module that is bound to the Gear action
    // group (Data_Deployable.DefaultActionGroup) or also carries a wheel/suspension module
    // (Data_WheelBase): Module_Deployable also drives solar panels and antennas, which this
    // leaves out.
    //
    // Deploy() fires the Gear action group, unless GetActionGroupState(Gear) already says True.
    // RecheckDelay seconds of game time later, Update() reads every leg back, and a leg still
    // Retracted is extended through its own "Extend Part" toggle (Data_Deployable.toggleExtend):
    // Module_Deployable.Extend() is protected. Nobody has checked in game yet that Gear deploys
    // stock legs, nor what GetActionGroupState(Gear) reports, so every step is logged as
    // "[Landing] gear: ...".
    public class LandingGear
    {
        public const double RecheckDelay = 3; // s of game time

        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.LandingGear");

        bool requested = false;
        double recheck_ut = -1;   // < 0: no re-check pending
        double final_log_ut = -1; // < 0: no post-fallback log pending

        // Forget the previous landing's request.
        public void Reset()
        {
            requested = false;
            recheck_ut = -1;
            final_log_ut = -1;
        }

        public static List<Data_Deployable> FindLegs(VesselComponent vessel)
        {
            var legs = new List<Data_Deployable>();
            if (vessel == null || vessel.SimulationObject == null)
                return legs;
            var vessel_behavior = vessel.SimulationObject.objVesselBehavior;
            if (vessel_behavior == null)
                return legs;

            foreach (PartBehavior part in vessel_behavior.parts)
            {
                if (part == null || part.Model == null)
                    continue;
                if (!part.Model.TryGetModuleData<PartComponentModule_Deployable, Data_Deployable>(out Data_Deployable data) || data == null)
                    continue;
                // Bound to Gear, or carrying a wheel/suspension module (Data_WheelBase covers "rover
                // wheels and landing legs"): either way it is landing gear, not a solar panel or an
                // antenna. The second test keeps legs whose default action group is not Gear.
                bool on_gear = (data.DefaultActionGroup & KSPActionGroup.Gear) != 0;
                bool has_wheel = part.Model.TryGetModuleData<PartComponentModule_WheelBase, Data_WheelBase>(out Data_WheelBase wheel) && wheel != null;
                if (!on_gear && !has_wheel)
                    continue;
                legs.Add(data);
            }
            return legs;
        }

        // "No legs", or for example "4 legs: 2 Extended, 2 Retracted".
        public static string Describe(List<Data_Deployable> legs)
        {
            if (legs.Count == 0)
                return "No legs";

            var counts = new SortedDictionary<string, int>();
            foreach (var leg in legs)
            {
                string state = leg.CurrentDeployState.GetValue().ToString();
                counts[state] = counts.TryGetValue(state, out int n) ? n + 1 : 1;
            }

            var text = new StringBuilder();
            text.Append(legs.Count).Append(legs.Count == 1 ? " leg: " : " legs: ");
            bool first = true;
            foreach (var pair in counts)
            {
                if (!first)
                    text.Append(", ");
                text.Append(pair.Value).Append(' ').Append(pair.Key);
                first = false;
            }
            return text.ToString();
        }

        public static string Summary(VesselComponent vessel) => Describe(FindLegs(vessel));

        // Deploys the legs once per run (until Reset). reason goes to the log.
        public void Deploy(VesselComponent vessel, string reason)
        {
            if (requested || vessel == null)
                return;
            requested = true;

            var legs = FindLegs(vessel);
            if (legs.Count == 0)
            {
                logger.LogInfo($"[Landing] gear: {reason}: no legs on this vessel, nothing to deploy");
                return;
            }

            string before = Describe(legs);
            KSPActionGroupState group = vessel.GetActionGroupState(KSPActionGroup.Gear);
            string path;
            if (group == KSPActionGroupState.True)
            {
                path = "Gear action group already True, not fired";
            }
            else
            {
                vessel.SetActionGroup(KSPActionGroup.Gear, true);
                path = $"Gear action group set to true (was {group})";
            }
            recheck_ut = GeneralTools.Current_UT + RecheckDelay;
            logger.LogInfo($"[Landing] gear: {reason}: {path}; before: {before}");
        }

        // Every frame: the re-check RecheckDelay after Deploy, then one more log line
        // RecheckDelay after a fallback.
        public void Update(VesselComponent vessel)
        {
            if (vessel == null)
                return;
            double now = GeneralTools.Current_UT;

            if (recheck_ut >= 0 && now >= recheck_ut)
            {
                recheck_ut = -1;
                var legs = FindLegs(vessel);
                int toggled = 0, stuck = 0;
                foreach (var leg in legs)
                {
                    if (leg.CurrentDeployState.GetValue() != Data_Deployable.DeployState.Retracted)
                        continue;
                    if (leg.toggleExtend.GetValue())
                    {
                        // Already asked to extend and still retracted: flipping the toggle off
                        // and on again could retract it instead. Leave it, the log says so.
                        stuck++;
                        continue;
                    }
                    leg.toggleExtend.SetValue(true);
                    toggled++;
                }

                string after = Describe(legs);
                if (toggled == 0 && stuck == 0)
                {
                    logger.LogInfo($"[Landing] gear: after {RecheckDelay:0} s: {after}; no fallback needed");
                }
                else
                {
                    logger.LogInfo($"[Landing] gear: after {RecheckDelay:0} s: {after}; fallback: {toggled} leg(s) " +
                        $"extended through their own toggle, {stuck} leg(s) toggled on but still retracted");
                    final_log_ut = now + RecheckDelay;
                }
            }

            if (final_log_ut >= 0 && now >= final_log_ut)
            {
                final_log_ut = -1;
                logger.LogInfo($"[Landing] gear: after fallback: {Summary(vessel)}");
            }
        }
    }
}
```

- [ ] **Step 2: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green.

If `TryGetModuleData<PartComponentModule_Deployable, Data_Deployable>` does not resolve, check the usings: `PartComponentModule_Deployable` is in `KSP.Sim.impl`, `Data_Deployable` in `KSP.Modules`. If `(data.DefaultActionGroup & KSPActionGroup.Gear) != 0` is rejected, write `!= KSPActionGroup.None`. `PartComponentModule_WheelBase` is in `KSP.Sim.impl`, `Data_WheelBase` in `KSP.Modules`.

- [ ] **Step 3: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/KSPService/LandingGear.cs*
git commit -m "Add landing gear deployment with a per-leg fallback

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: `Parachutes`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/KSPService/Parachutes.cs`

**Interfaces:**
- Produces, in namespace `K2D2.KSPService`:
  - `struct ParachuteCounts { int Stowed, Armed, SemiDeployed, Deployed, Cut; int Total; int Usable; }`, where `Usable` counts every state except `CUT`, and `ToString()` is used by the logs;
  - `static ParachuteCounts Parachutes.Count(VesselComponent vessel)`;
  - `static int Parachutes.ArmStowed(VesselComponent vessel)`, which returns how many parachutes it armed.

No unit test. The test command is the compile check, and in-game scenarios 4 and 5 check the behavior.

- [ ] **Step 1: Write the class**

`Assets/SkySpaceAgency/Code/KSPService/Parachutes.cs`:

```csharp
using System.Collections.Generic;
using KSP.Modules;
using KSP.Sim.impl;

namespace K2D2.KSPService
{
    public struct ParachuteCounts
    {
        public int Stowed, Armed, SemiDeployed, Deployed, Cut;

        public int Total => Stowed + Armed + SemiDeployed + Deployed + Cut;

        // Can still slow the vessel down: every state but CUT.
        public int Usable => Stowed + Armed + SemiDeployed + Deployed;

        public override string ToString() =>
            $"{Total} parachute(s): {Stowed} stowed, {Armed} armed, {SemiDeployed} semi-deployed, {Deployed} deployed, {Cut} cut";
    }

    // The vessel's parachutes, part by part (VesselBehavior.parts, as VesselAeroLookup walks them).
    // State from Data_Parachute.deployState; arming through Module_Parachute.ArmChute().
    public static class Parachutes
    {
        public static ParachuteCounts Count(VesselComponent vessel)
        {
            var counts = new ParachuteCounts();
            foreach (PartBehavior part in Parts(vessel))
            {
                if (!TryGetData(part, out Data_Parachute data))
                    continue;
                switch (data.deployState.GetValue())
                {
                    case Data_Parachute.DeploymentStates.STOWED: counts.Stowed++; break;
                    case Data_Parachute.DeploymentStates.ARMED: counts.Armed++; break;
                    case Data_Parachute.DeploymentStates.SEMIDEPLOYED: counts.SemiDeployed++; break;
                    case Data_Parachute.DeploymentStates.DEPLOYED: counts.Deployed++; break;
                    case Data_Parachute.DeploymentStates.CUT: counts.Cut++; break;
                }
            }
            return counts;
        }

        // Arms every STOWED parachute. ArmChute() is not documented as idempotent, so call this
        // once per landing. The game then opens each parachute by its own settings
        // (DeploymentMode, deployAltitude, minAirPressureToOpen, drogues before mains).
        public static int ArmStowed(VesselComponent vessel)
        {
            int armed = 0;
            foreach (PartBehavior part in Parts(vessel))
            {
                if (!TryGetData(part, out Data_Parachute data))
                    continue;
                if (data.deployState.GetValue() != Data_Parachute.DeploymentStates.STOWED)
                    continue;
                Module_Parachute module = part.GetModule<Module_Parachute>();
                if (module == null)
                    continue;
                module.ArmChute();
                armed++;
            }
            return armed;
        }

        static IEnumerable<PartBehavior> Parts(VesselComponent vessel)
        {
            if (vessel == null || vessel.SimulationObject == null)
                yield break;
            var vessel_behavior = vessel.SimulationObject.objVesselBehavior;
            if (vessel_behavior == null)
                yield break;
            foreach (PartBehavior part in vessel_behavior.parts)
            {
                if (part != null && part.Model != null)
                    yield return part;
            }
        }

        static bool TryGetData(PartBehavior part, out Data_Parachute data)
        {
            if (!part.Model.TryGetModuleData<PartComponentModule_Parachute, Data_Parachute>(out data))
                return false;
            return data != null;
        }
    }
}
```

- [ ] **Step 2: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green. `PartComponentModule_Parachute` is in `KSP.Sim.impl`; `Module_Parachute` and `Data_Parachute` are in `KSP.Modules`.

- [ ] **Step 3: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/KSPService/Parachutes.cs*
git commit -m "Add parachute counting and arming

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: Settings `land.mode` and `land.deploy_legs_early`

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingSettings.cs`: before the class comment (line 7), fields after line 98, constructor at lines 100-120
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs`: `settings` property at line 27, constructor at lines 64-65

**Interfaces:**
- Produces:
  - `enum LandingMethod { Propulsive, Parachute }` (namespace `K2D2.Landing`);
  - `LandingSettings.mode : EnumSetting<LandingMethod>`, key `land.mode`, default `Propulsive`, **null on the vacuum profile**;
  - `LandingSettings.deploy_legs_early : Setting<bool>`, key `land.deploy_legs_early`, default false, on both profiles;
  - constructor `LandingSettings(SettingsFile file, bool atmospheric)`;
  - `internal bool LandingPilot.parachute_mode`.

The UI bindings come with the UI elements in Task 8: binding a missing element throws.

- [ ] **Step 1: The enum**

In `LandingSettings.cs`, just before the class comment `// Atmo/Vacuum profile split: LandingPilot constructs TWO of these (settings_atmo/` (line 7), add:

```csharp
    // How to land on an atmospheric body (LandingSettings.mode, atmo profile only).
    public enum LandingMethod
    {
        Propulsive,
        Parachute,
    }

```

- [ ] **Step 2: Fields and constructor**

After `public ClampSetting<float> rcs_fine_correction_power;` (line 98), add:

```csharp

        // Landing mode. Atmo profile only: null on the vacuum profile, where the landing is
        // always propulsive (see LandingPilot.parachute_mode).
        public EnumSetting<LandingMethod> mode;

        // Deploy the legs on entering Brake instead of Touch Down. Both profiles.
        public Setting<bool> deploy_legs_early;
```

Replace the constructor signature `public LandingSettings(SettingsFile file)` (line 100) with:

```csharp
        public LandingSettings(SettingsFile file, bool atmospheric)
```

and, at the end of the constructor body, after `rcs_fine_correction_power = new("land.rcs_fine_correction_power", 0.5f, 0.05f, 1f, file);` (line 119), add:

```csharp

            deploy_legs_early = new("land.deploy_legs_early", false, file);
            if (atmospheric)
                mode = new("land.mode", LandingMethod.Propulsive, file);
```

- [ ] **Step 3: `LandingPilot`**

In the constructor, replace (lines 64-65):

```csharp
            settings_atmo = new LandingSettings(atmo_file);
            settings_vac = new LandingSettings(vac_file);
```

with:

```csharp
            settings_atmo = new LandingSettings(atmo_file, atmospheric: true);
            settings_vac = new LandingSettings(vac_file, atmospheric: false);
```

After `internal LandingSettings settings => LandingProfile.IsAtmospheric ? settings_atmo : settings_vac;` (line 27), add:

```csharp

        // Parachute landing is chosen on the atmosphere profile only; on an airless body the
        // landing is always propulsive.
        internal bool parachute_mode =>
            LandingProfile.IsAtmospheric && settings_atmo.mode.V == LandingMethod.Parachute;
```

- [ ] **Step 4: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green. `new LandingSettings(` has no other caller (checked: only `LandingPilot.cs:64-65`).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/LandingSettings.cs Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs
git commit -m "Add landing mode and early legs settings

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: `ParachuteDescent` and the pilot's two modes

**Files:**
- Create: `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/ParachuteDescent.cs`
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs`

**Interfaces:**
- Consumes:
  - Task 2: `ParachuteFeasibility.Check`, `TooFastUnderCanopy` and `NoParachuteLeft`;
  - Task 3: `LandingGear` (`Reset`, `Deploy`, `Update`);
  - Task 4: `Parachutes.Count` and `ArmStowed`, and `ParachuteCounts`;
  - Task 5: `LandingPilot.parachute_mode` and `settings.deploy_legs_early`;
  - existing: `LandingTargeting.OrbitalElementsFromStateVectors`, `SASTool.setAutoPilot`, and `KSPVessel.SetSpeedMode` / `SetThrottle`.
- Produces, for Task 8:
  - `LandingPilot.Mode.Parachute`, the last member of the enum;
  - `public ParachuteDescent LandingPilot.parachute_descent`;
  - `internal LandingGear LandingPilot.gear`;
  - `ParachuteDescent.counts` (`ParachuteCounts`), `too_fast_under_canopy` (bool) and `no_parachute_left` (bool).

No unit test, because the classes are all game state: compile check, then the in-game checklist.

- [ ] **Step 1: The controller**

`Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/ParachuteDescent.cs`:

```csharp
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing.Braking;
using KSP.Sim;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // Mode.Parachute, the only phase of a parachute landing, without any engine:
    // 1. hold surface retrograde; the time warp stays in the player's hands;
    // 2. arm every STOWED parachute once, at start; the game opens them by their own settings;
    // 3. once one is DEPLOYED, release SAS (StabilityAssist) and deploy the legs;
    // 4. LandingPilot ends the run on LandedOrSplashed: this controller never sets finished.
    public class ParachuteDescent : ExecuteController
    {
        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.ParachuteDescent");

        readonly LandingPilot landing;

        public ParachuteCounts counts;
        public bool too_fast_under_canopy = false;
        public bool no_parachute_left = false;
        bool canopy_open = false;

        public ParachuteDescent(LandingPilot landing)
        {
            this.landing = landing;
        }

        public override void Start()
        {
            base.Start();
            canopy_open = false;
            too_fast_under_canopy = false;
            no_parachute_left = false;

            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            counts = Parachutes.Count(vessel);
            int armed = Parachutes.ArmStowed(vessel);
            logger.LogInfo($"[Landing] parachute: start, {counts}; armed {armed} stowed parachute(s)");
            status_line = "Holding retrograde";
        }

        public override void Update()
        {
            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;

            counts = Parachutes.Count(vessel);

            if (!canopy_open)
            {
                current_vessel.SetSpeedMode(SpeedDisplayMode.Surface);
                SASTool.setAutoPilot(AutopilotMode.Retrograde);

                if (counts.Deployed > 0)
                {
                    canopy_open = true;
                    SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
                    logger.LogInfo($"[Landing] parachute: canopy open at {landing.altitude:n0} m, " +
                        $"{landing.current_falling_speed:n1} m/s down, SAS released; {counts}");
                    landing.gear.Deploy(vessel, "parachute open");
                }
            }

            bool too_fast = ParachuteFeasibility.TooFastUnderCanopy(counts.Deployed, landing.altitude, landing.current_falling_speed);
            if (too_fast && !too_fast_under_canopy)
                logger.LogWarning($"[Landing] parachute: too fast under canopy: {landing.current_falling_speed:n1} m/s " +
                    $"at {landing.altitude:n0} m; {counts}");
            too_fast_under_canopy = too_fast;

            bool none_left = ParachuteFeasibility.NoParachuteLeft(counts.Usable, vessel.LandedOrSplashed);
            if (none_left && !no_parachute_left)
                logger.LogWarning($"[Landing] parachute: no parachute left; {counts}");
            no_parachute_left = none_left;

            if (canopy_open)
                status_line = "Under canopy";
            else if (counts.Armed + counts.SemiDeployed > 0)
                status_line = "Holding retrograde, waiting for the parachutes to open";
            else
                status_line = "Holding retrograde";
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Canopy", canopy_open ? "Open" : "Not open yet");
        }
    }
}
```

- [ ] **Step 2: Fields, enum and constructor in `LandingPilot`**

After `public TouchDown brake;` (line 41), add:

```csharp

        // Mode.Parachute's controller (Controlers/ParachuteDescent.cs).
        public ParachuteDescent parachute_descent;

        // Landing legs (KSPService/LandingGear.cs): deployed on entering Touch Down (or Brake with
        // deploy_legs_early), or by ParachuteDescent once a canopy opens. One request per run.
        internal LandingGear gear = new LandingGear();
```

In the constructor, after `brake = new TouchDown(this, atmo_file, vac_file);` (line 66), add:

```csharp
            parachute_descent = new ParachuteDescent(this);
```

In `enum Mode`, replace (lines 94-96):

```csharp
            Brake,
            TouchDown
        }
```

with:

```csharp
            Brake,
            TouchDown,
            // Parachute landing (ParachuteDescent), entered straight from Off. Last on purpose:
            // nextMode() never runs from it, and TouchDown never falls through into it.
            Parachute
        }
```

In `nextMode`, replace (lines 185-186):

```csharp
            var next = this.mode + 1;
            setMode(next);
```

with:

```csharp
            // TouchDown and Parachute are both final phases: the run ends on contact.
            if (mode == Mode.TouchDown || mode == Mode.Parachute)
                return;

            var next = this.mode + 1;
            setMode(next);
```

- [ ] **Step 3: `setMode`: the parachute phase, and legs on Brake / Touch Down**

Replace (lines 165-170):

```csharp
                case Mode.Brake:
                case Mode.TouchDown:
                    // Clear a stale flag from a previous descent.
                    brake.cannot_stop = false;
                    current_executor.setController(brake);
                    break;
```

with:

```csharp
                case Mode.Brake:
                case Mode.TouchDown:
                    // Clear a stale flag from a previous descent.
                    brake.cannot_stop = false;
                    // Legs out on entering Touch Down, or already on entering Brake with "Deploy
                    // legs early". LandingGear only acts on the first request of a run.
                    if (mode == Mode.TouchDown || settings.deploy_legs_early.V)
                        gear.Deploy(current_vessel.VesselComponent, $"entering {mode}");
                    current_executor.setController(brake);
                    break;
                case Mode.Parachute:
                    current_vessel.SetThrottle(0);
                    current_executor.setController(parachute_descent);
                    parachute_descent.Start();
                    break;
```

- [ ] **Step 4: Start in the chosen mode**

In the `isRunning` setter, replace (lines 235-238):

```csharp
                    if (settings.precision_landing.V && !LandingProfile.IsAtmospheric)
                        setMode(Mode.Circularize);
                    else
                        setMode(Mode.QuickWarp);
```

with:

```csharp
                    gear.Reset();
                    if (parachute_mode)
                        setMode(Mode.Parachute);
                    else if (settings.precision_landing.V && !LandingProfile.IsAtmospheric)
                        setMode(Mode.Circularize);
                    else
                        setMode(Mode.QuickWarp);
```

- [ ] **Step 5: Mode-dependent start checks**

In `CheckCanStart`, replace (lines 717-723):

```csharp
            if (current_vessel?.VesselComponent == null || current_vessel.VesselVehicle == null)
            {
                last_error = "No active vessel.";
                return false;
            }

            burn_dV.Compute_Thrust();
```

with:

```csharp
            if (current_vessel?.VesselComponent == null || current_vessel.VesselVehicle == null)
            {
                last_error = "No active vessel.";
                return false;
            }

            // Refresh the profile now: the mode, and so the checks, depend on it. The engine,
            // TWR and Δv checks below do not apply to a parachute landing.
            LandingProfile.Update(current_vessel.currentBody());
            if (parachute_mode)
                return CheckCanStartParachute();

            burn_dV.Compute_Thrust();
```

Then, right after the end of `CheckCanStart` (the `return true;` and `}` at lines 746-747, just before the `// One line per engine, …` comment of `LogEngines`), add:

```csharp

        // Parachute landing start checks (ParachuteFeasibility). Periapsis from the state
        // vectors, the same way Circularize.CheckOrbit computes it (also valid for an escape
        // trajectory).
        bool CheckCanStartParachute()
        {
            var vessel = current_vessel.VesselComponent;
            IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter,
                out _, out _, out double periapsis_radius);
            double periapsis_alt = periapsis_radius - body.radius;
            ParachuteCounts chutes = Parachutes.Count(vessel);

            var check = ParachuteFeasibility.Check(body.hasAtmosphere, body.atmosphereDepth,
                vessel.IsInAtmosphere, periapsis_alt, chutes.Usable);
            logger.LogInfo($"[Landing] parachute start check: ok={check.Ok} atmosphere {body.hasAtmosphere} " +
                $"(depth {body.atmosphereDepth:n0} m), in atmosphere {vessel.IsInAtmosphere}, Pe {periapsis_alt:n0} m, {chutes}" +
                (check.Ok ? "" : $" -> {check.Error}"));

            if (!check.Ok)
            {
                last_error = check.Error;
                return false;
            }
            return true;
        }
```

- [ ] **Step 6: No braking simulation in parachute mode**

In `UpdateBrakeSimulation`, replace (lines 606-611):

```csharp
        // detected collision, then brake_result_valid goes false. Unforced calls do nothing in
        // TouchDown, which does not read the result.
        void UpdateBrakeSimulation(bool force)
        {
            if (!force && mode == Mode.TouchDown)
                return;
```

with:

```csharp
        // detected collision, then brake_result_valid goes false. Unforced calls do nothing in
        // TouchDown and Parachute, which do not read the result.
        void UpdateBrakeSimulation(bool force)
        {
            if (!force && (mode == Mode.TouchDown || mode == Mode.Parachute))
                return;
```

- [ ] **Step 7: Common touchdown end**

After `StopWithError` (lines 773-778), add:

```csharp

        // End of a landing, whichever the mode: no thrust, SAS holding attitude, autopilot off.
        // Nothing relights the engines afterwards, even on a bounce or a slide.
        void FinishLanding(bool landed_or_splashed)
        {
            logger.LogInfo($"[Landing] touchdown in {mode}: " +
                (landed_or_splashed ? "LandedOrSplashed" : "altitude/fall-speed net") +
                $", altitude {altitude:n1} m, fall speed {current_falling_speed:n2} m/s");
            current_vessel.SetThrottle(0);
            SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
            isRunning = false;
        }
```

In `Update`, replace (lines 815-836):

```csharp
                if (isRunning)
                {
                    if (altitude < settings.start_touchdown_altitude.V)
                        setMode(Mode.TouchDown);
                }
                else
                {
                    // no more collision
                    isRunning = false;
                }
            }

            if (!isRunning)
                return;

            // landing detection....
            if (altitude < 5 && current_falling_speed < 1)
            {
                //current_vessel.SetThrottle(0);
                isRunning = false;
                return;
            }
```

with:

```csharp
                if (isRunning)
                {
                    // A parachute landing has no Touch Down phase to fall back to.
                    if (mode != Mode.Parachute && altitude < settings.start_touchdown_altitude.V)
                        setMode(Mode.TouchDown);
                }
                else
                {
                    // no more collision
                    isRunning = false;
                }
            }

            // Leg re-check and fallback (LandingGear). Before the isRunning test, so a check
            // still pending at touchdown completes while the tab stays open.
            gear.Update(current_vessel.VesselComponent);

            if (!isRunning)
                return;

            // Landing detection, common to both modes: the game's own LandedOrSplashed, with the
            // old altitude/fall-speed test kept as a net.
            bool landed = current_vessel.VesselComponent.LandedOrSplashed;
            if (landed || (altitude < 5 && current_falling_speed < 1))
            {
                FinishLanding(landed);
                return;
            }
```

`Mode.Parachute` needs no branch of its own in the `if (mode == Mode.Pause) … else if (mode == Mode.TouchDown)` chain: `base.Update()` runs `ParachuteDescent` through `current_executor`, which never reports `finished` or `failed`.

- [ ] **Step 8: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green.

`SASTool` is in namespace `K2D2`, and `AutopilotMode` / `SpeedDisplayMode` are in `KSP.Sim`; both resolve from `K2D2.Landing` with the existing usings. `LandingTargeting` is in `K2D2.Landing` (`Pilots/Landing/LandingTargeting.cs`).

- [ ] **Step 9: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/ParachuteDescent.cs* Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs
git status --short   # only these paths
git commit -m "Add parachute landing mode, leg deployment and touchdown on contact

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: `TouchDown`: the final 50 m

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/TouchDown.cs`: field at line 289, `Start` at lines 217-229, `checkDirection` at lines 343-374, `Update` at line 938, `UpdateInfoRows` at lines 952-955
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs`: the `Mode.TouchDown` branch of `Update` (lines 906-911)

Only the landing `TouchDown` (namespace `K2D2.Landing`) changes. `Pilots/Lift/TouchDown.cs` is a different class and is not touched.

**Interfaces:**
- Consumes:
  - Task 1: `FinalDescent.IsFinal`, `AimDirection`, `ThrustFactor`, `ContactSpeed`;
  - existing: `LandingPilot.mode`, `LandingPilot.altitude`, and `settings.touch_down_speed`.
- Produces: nothing new for other tasks (the "Final Descent" info row is internal).

- [ ] **Step 1: Fields and reset**

After `float retrograde_angle;` (line 289), add:

```csharp

        // Final descent (FinalDescent: below FinalHeight, in TouchDown only), set by
        // checkDirection. thrust_factor scales the throttle there; it is 1 everywhere else.
        bool final_descent = false;
        float final_tilt_deg = 0;
        float thrust_factor = 1;
```

In `Start()`, replace:

```csharp
            finished = false;
            cannot_stop = false;
```

with:

```csharp
            finished = false;
            cannot_stop = false;
            final_descent = false;
            thrust_factor = 1;
```

- [ ] **Step 2: Aim in `checkDirection`**

Replace (lines 343-358):

```csharp
            if (steering)
            {
                var autopilot = current_vessel.Autopilot;
                autopilot.Enabled = true;
                // Guard the mode switch like SASTool.setAutoPilot does elsewhere - re-calling
                // SetMode every single tick even while already in StabilityAssist risks resetting
                // SAS's own internal state each frame instead of letting it settle onto the target.
                if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                    autopilot.SetMode(AutopilotMode.StabilityAssist);
                autopilot.SAS.lockedMode = false;
                autopilot.SAS.SetTargetOrientation(new Vector(HorizonUp.coordinateSystem, aim_dir), false);
            }
```

with:

```csharp
            // Final descent: aim at the local vertical tilted against the horizontal drift
            // (FinalDescent), instead of pure or steered retrograde. Applied through the same SAS
            // target as the steering below.
            final_descent = landing != null && landing.mode == LandingPilot.Mode.TouchDown
                && FinalDescent.IsFinal(landing.altitude);
            if (final_descent)
            {
                double gravity_now = current_vessel.VesselComponent.graviticAcceleration.magnitude;
                // SurfaceMovementRetrograde is a direction: the surface velocity is minus it, at
                // the current surface speed.
                Vector3d surface_velocity = retro_dir.vector.normalized * (-current_speed);
                aim_dir = FinalDescent.AimDirection(HorizonUp.vector, surface_velocity, gravity_now);
                final_tilt_deg = (float)Vector3d.Angle(aim_dir, HorizonUp.vector);
            }

            if (steering || final_descent)
            {
                var autopilot = current_vessel.Autopilot;
                autopilot.Enabled = true;
                // Guard the mode switch like SASTool.setAutoPilot does elsewhere - re-calling
                // SetMode every single tick even while already in StabilityAssist risks resetting
                // SAS's own internal state each frame instead of letting it settle onto the target.
                if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                    autopilot.SetMode(AutopilotMode.StabilityAssist);
                autopilot.SAS.lockedMode = false;
                autopilot.SAS.SetTargetOrientation(new Vector(HorizonUp.coordinateSystem, aim_dir), false);
            }
```

The `else { SASTool.setAutoPilot(AutopilotMode.Retrograde); }` that follows stays as it is.

- [ ] **Step 3: Thrust factor instead of the alignment cut**

At the end of `checkDirection`, replace (lines 371-374):

```csharp
            retrograde_angle = (float)Vector3d.Angle(aim_dir, forward_direction);
            status_line = $"Waiting for Vessel rotation\nAngle = {retrograde_angle:n2}°";

            return retrograde_angle < touch_down_max_angle.V;
```

with:

```csharp
            retrograde_angle = (float)Vector3d.Angle(aim_dir, forward_direction);

            if (final_descent)
            {
                // Below FinalHeight the thrust is never cut because SAS lags: Update scales it by
                // how well the vessel points instead (FinalDescent.ThrustFactor).
                thrust_factor = (float)FinalDescent.ThrustFactor(retrograde_angle);
                status_line = $"Final descent\nTilt = {final_tilt_deg:n1}°, Angle = {retrograde_angle:n2}°";
                return true;
            }

            thrust_factor = 1;
            status_line = $"Waiting for Vessel rotation\nAngle = {retrograde_angle:n2}°";
            return retrograde_angle < touch_down_max_angle.V;
```

The "Waiting for speed Down" early return at the top of `checkDirection` (vessel moving up) still cuts the throttle in the final descent too. That is intended: after a bounce, no thrust.

In `Update`, replace:

```csharp
            compute_Throttle();

            // Rate-limit the actual applied throttle instead of snapping straight to the freshly
```

with:

```csharp
            compute_Throttle();
            // 1 outside the final descent; there, the throttle follows how well the vessel points.
            wanted_throttle *= thrust_factor;

            // Rate-limit the actual applied throttle instead of snapping straight to the freshly
```

- [ ] **Step 4: Info row**

In `UpdateInfoRows`, after `addRow("Delta Speed", $"{delta_speed:n2} m/s");` (line 955), add:

```csharp
            if (final_descent)
                addRow("Final Descent", $"tilt {final_tilt_deg:n1}°, thrust x{thrust_factor:n2}");
```

- [ ] **Step 5: Contact speed floor in `LandingPilot`**

In `LandingPilot.Update`, replace (lines 906-911):

```csharp
            else if (mode == Mode.TouchDown)
            {
                TimeWarpTools.SetRateIndex(0, false);
                brake.max_speed = limit_speed(altitude);
                brake.gravity_compensation = true;
            }
```

with:

```csharp
            else if (mode == Mode.TouchDown)
            {
                TimeWarpTools.SetRateIndex(0, false);
                brake.max_speed = limit_speed(altitude);
                // Final descent: never aim below the contact speed floor, or a Touch-Down speed
                // of 0 would hover instead of landing.
                if (FinalDescent.IsFinal(altitude))
                    brake.max_speed = Math.Max(brake.max_speed, (float)FinalDescent.ContactSpeed(settings.touch_down_speed.V));
                brake.gravity_compensation = true;
            }
```

`limit_speed` is never below the touch-down speed (both terms of `DescentEnvelope.MaxSpeed` add it), so this is the spec's `max(v_td, 0.5)` floor.

- [ ] **Step 6: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green. `TouchDown.cs` and `LandingPilot.cs` already have `using K2D2.Landing.Braking;`.

- [ ] **Step 7: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/Pilots/Landing/Controlers/TouchDown.cs Assets/SkySpaceAgency/Code/Pilots/Landing/LandingPilot.cs
git commit -m "Fly the final 50 m upright against the drift, without cutting thrust

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: UI: mode selector, legs toggle, info rows, button label, alerts

**Files:**
- Modify: `Assets/UI/K2D2_UI/Landing.uxml`: `landing_mode_group_atmo` (lines 32-34), end of `landing_mode_group_vac` (lines 60-61)
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingSettings.cs`: `setupUI` (line 141), `setupBasicUI` (lines 181-183)
- Modify: `Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs`: usings, listeners at lines 87-105, `updateContext` at lines 312-313, `onUpdateUI` at lines 368-398

**Interfaces:**
- Consumes:
  - Task 5: `LandingSettings.mode` and `deploy_legs_early`, `LandingPilot.parachute_mode`;
  - Task 6: `Mode.Parachute` and `ParachuteDescent.too_fast_under_canopy` / `no_parachute_left`;
  - Task 2: `ParachuteFeasibility.TooFastMessage` / `NoParachuteLeftMessage`;
  - Tasks 3-4: `LandingGear.Summary` and `Parachutes.Count`;
  - existing: `InlineEnum.Bind` and `K2Toggle.Bind`.

- [ ] **Step 1: UXML**

In `Landing.uxml`, replace (lines 32-34):

```xml
            <ui:VisualElement name="landing_mode_group_atmo" style="flex-grow: 1; background-color: rgba(0, 0, 0, 0); margin-top: 8px; display: none;">
                <ui:Label tabindex="-1" text="Precision landing isn't available on atmospheric bodies yet - Brake/Touch Down still work as usual." display-tooltip-when-elided="true" name="atmo_precision_note" style="color: rgb(180, 180, 180); font-size: 12px; white-space: normal; margin-top: 2px; margin-bottom: 4px;" />
            </ui:VisualElement>
```

with:

```xml
            <ui:VisualElement name="landing_mode_group_atmo" style="flex-grow: 1; background-color: rgba(0, 0, 0, 0); margin-top: 8px; display: none;">
                <!-- Landing mode (LandingSettings.mode, atmo profile only): Propulsive brakes on the
                     engines, Parachute lands without them (ParachuteDescent.cs). -->
                <K2UI.InlineEnum name="land_mode_atmo" labels="Propulsive;Parachute" />
                <K2UI.K2Toggle label="Deploy legs early" name="deploy_legs_early_atmo" tooltip="Propulsive landing: deploy the legs when braking starts instead of at Touch Down" />
                <ui:Label tabindex="-1" text="Precision landing isn't available on atmospheric bodies yet - Brake/Touch Down still work as usual." display-tooltip-when-elided="true" name="atmo_precision_note" style="color: rgb(180, 180, 180); font-size: 12px; white-space: normal; margin-top: 2px; margin-bottom: 4px;" />
            </ui:VisualElement>
```

Then, at the end of `landing_mode_group_vac`, replace (lines 60-61):

```xml
                </ui:VisualElement>
            </ui:VisualElement>

            <!-- Brake/Touch Down restyled to the same bevel look as the reset buttons (see
```

with:

```xml
                </ui:VisualElement>
                <K2UI.K2Toggle label="Deploy legs early" name="deploy_legs_early_vac" tooltip="Deploy the landing legs when braking starts instead of at Touch Down" />
            </ui:VisualElement>

            <!-- Brake/Touch Down restyled to the same bevel look as the reset buttons (see
```

`.inline_enum` already has its style in `Assets/Runtime/K2UI/USS/ToggleButton.uss`, which `Landing.uxml` loads.

- [ ] **Step 2: Bindings in `LandingSettings`**

In `setupUI`, after `target_settings.Q<FloatField>("target_longitude_vac").Bind(target_longitude);` (line 141), add:

```csharp

            // LEGS
            root.Q<K2Toggle>("deploy_legs_early_vac").Bind(deploy_legs_early);
```

In `setupBasicUI`, replace (lines 181-183):

```csharp
        public void setupBasicUI(VisualElement root)
        {
            root.Q<K2Toggle>("auto_warp_atmo").Bind(auto_warp);
```

with:

```csharp
        public void setupBasicUI(VisualElement root)
        {
            // LANDING MODE (this is the atmo profile, so mode is not null)
            root.Q<InlineEnum>("land_mode_atmo").Bind(mode);
            root.Q<K2Toggle>("deploy_legs_early_atmo").Bind(deploy_legs_early);

            root.Q<K2Toggle>("auto_warp_atmo").Bind(auto_warp);
```

`InlineEnum` is in namespace `K2UI`, already imported by `LandingSettings.cs`.

- [ ] **Step 3: `LandingUI`: usings, label and listeners**

Add `using K2D2.KSPService;` after `using K2D2.Landing.Braking;` (line 2).

After `VisualElement vac_settings_panel;` (line 49), add:

```csharp

        // "Land" for a parachute landing (one button, no engine), "Brake" otherwise.
        string IdleLabel => pilot.parachute_mode ? "Land" : "Brake";
```

Replace (lines 97-105):

```csharp
                run_button.label = v ? "Stop" : "Brake";
            };

            touch_down.listenClick(() =>
            {
                pilot.isRunning = true;
                if (pilot.isRunning)
                    pilot.setMode(LandingPilot.Mode.TouchDown);
            });
```

with:

```csharp
                run_button.label = v ? "Stop" : IdleLabel;
            };

            touch_down.listenClick(() =>
            {
                // Hidden in parachute mode (onUpdateUI); a parachute landing never goes to Touch Down.
                if (pilot.parachute_mode)
                    return;
                pilot.isRunning = true;
                if (pilot.isRunning && pilot.mode != LandingPilot.Mode.Parachute)
                    pilot.setMode(LandingPilot.Mode.TouchDown);
            });
```

- [ ] **Step 4: Info rows**

In `updateContext`, after (lines 312-313):

```csharp
            AddInfoRow("Fall Speed", $"{pilot.current_falling_speed:n2} m/s");
            AddInfoRow("Altitude", StrTool.DistanceToString(pilot.altitude));
```

add:

```csharp

            // Legs and parachutes, shown even before starting: "No legs" tells the player the
            // landing will go ahead without any.
            var vessel = pilot.current_vessel?.VesselComponent;
            if (vessel != null)
            {
                AddInfoRow("Legs", LandingGear.Summary(vessel));
                if (LandingProfile.IsAtmospheric)
                {
                    ParachuteCounts chutes = Parachutes.Count(vessel);
                    AddInfoRow("Parachutes", chutes.Total == 0 ? "None"
                        : $"{chutes.Deployed} deployed, {chutes.SemiDeployed} semi, {chutes.Armed} armed, {chutes.Stowed} stowed");
                }
            }
```

- [ ] **Step 5: Button visibility, label and alerts in `onUpdateUI`**

Replace (line 368):

```csharp
            touch_down.Show(pilot.mode != LandingPilot.Mode.TouchDown);
```

with:

```csharp
            touch_down.Show(pilot.mode != LandingPilot.Mode.TouchDown
                && pilot.mode != LandingPilot.Mode.Parachute && !pilot.parachute_mode);
            if (!pilot.isRunning)
                run_button.label = IdleLabel; // follows the mode selector
```

In the running `switch`, replace (lines 393-398):

```csharp
                    case LandingPilot.Mode.TouchDown:
                        if (pilot.brake.cannot_stop)
                            status_bar.Error("Cannot stop before the ground!");
                        else
                            status_bar.Warning($"Touch Down...");
                        break;
```

with:

```csharp
                    case LandingPilot.Mode.TouchDown:
                        if (pilot.brake.cannot_stop)
                            status_bar.Error("Cannot stop before the ground!");
                        else
                            status_bar.Warning($"Touch Down...");
                        break;
                    case LandingPilot.Mode.Parachute:
                        // Alerts only: without engines, nothing can be done about them.
                        if (pilot.parachute_descent.no_parachute_left)
                            status_bar.Error(ParachuteFeasibility.NoParachuteLeftMessage);
                        else if (pilot.parachute_descent.too_fast_under_canopy)
                            status_bar.Error(ParachuteFeasibility.TooFastMessage);
                        else
                            status_bar.Warning("Parachute descent...");
                        break;
```

- [ ] **Step 6: Compile**

Run the EditMode test command. Expected: 0 `error CS`, all tests green. The UXML is only checked at runtime: if a `Q<…>(…)` returns null, `Bind` throws in `LandingUI.onInit` and the whole window fails to initialize (see the long comment above `setupUI(panel)`). Check that the three new names match exactly in both files: `land_mode_atmo`, `deploy_legs_early_atmo`, `deploy_legs_early_vac`.

- [ ] **Step 7: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/UI/K2D2_UI/Landing.uxml Assets/SkySpaceAgency/Code/Pilots/Landing/LandingSettings.cs Assets/SkySpaceAgency/Code/Pilots/Landing/LandingUI.cs
git commit -m "Add landing mode selector, early legs toggle, legs and parachute readouts

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: Build, install, document

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

In `CHANGELOG.md`, under `## Unreleased (SkySpaceAgency)` → `### Added`, add (create the `### Added` heading if the section has none):

```markdown
- **Landing modes (legs and parachutes)**:
  - **Mode selector** (atmospheric bodies): Propulsive or Parachute. On airless bodies the landing is always propulsive. The start button reads "Land" in parachute mode and "Brake" otherwise.
  - **Legs**: the landing legs deploy on entering Touch Down, or when braking starts with "Deploy legs early". The tab shows their state, or "No legs"; a vessel without legs still lands.
  - **Final descent**: below 50 m the vessel stands upright, leaning up to 15° against its horizontal drift, and the throttle follows how well it points instead of cutting out. It never aims below 0.5 m/s, so a Touch-Down speed of 0 no longer hovers.
  - **Contact**: the autopilot stops as soon as the game reports the vessel landed or splashed. Throttle goes to 0 and SAS holds attitude; the engines do not relight on a bounce.
  - **Parachute mode**: no engine at all. The vessel holds surface retrograde, every stowed parachute is armed once, and the game opens them. Once a canopy is open, SAS is released and the legs deploy. The start is refused without an atmosphere, without a parachute, or when the trajectory stays above the atmosphere (deorbit first). Under canopy the tab warns "Too fast under canopy" (over 10 m/s below 500 m) and "No parachute left".
```

In `README.md` → "Known limitations", add:

```markdown
- Parachute landing does not deorbit, does not guide the reentry, and leaves the time warp to the player. Legs are the deployable parts bound to the Gear action group by default.
```

- [ ] **Step 4: Commit and push the mod docs, then bump the parent**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add CHANGELOG.md README.md
git commit -m "Document landing modes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

Then, in `D:/KSPReduxModding/CLAUDE.md`, add a **Landing modes** bullet to the SkySpaceAgency section, after the "Sécurité de l'atterrissage" bullet. It should:
- call it sub-project 2 of 3 of the Landing work, and give the spec and plan paths;
- say that `FinalDescent` and `ParachuteFeasibility` (in `Pilots/Landing/Braking/`) are tested by `Assets/Tests/Landing`, and give the new total test count read from `results.xml`;
- say that legs are the parts whose `Data_Deployable.DefaultActionGroup` includes Gear, and that `Module_Deployable.Extend()` is protected, so the fallback goes through `Data_Deployable.toggleExtend`.

It should also list what is **not verified in game** at that date, to read in the log:
- whether `KSPActionGroup.Gear` deploys stock legs, and what `GetActionGroupState(Gear)` reports (`[Landing] gear: …`);
- whether stock legs carry Gear as their default action group;
- whether `toggleExtend.SetValue(true)` extends a retracted leg (`[Landing] gear: after fallback`);
- that `ArmChute()` arms and the game then opens the parachutes (`[Landing] parachute: canopy open`);
- when `LandedOrSplashed` turns true (`[Landing] touchdown in …`).

Then update the "Suite du chantier" line: only sub-project 3 (precision) remains.

```bash
cd /d/KSPReduxModding
git add SkySpaceAgency CLAUDE.md
git commit -m "Bump SkySpaceAgency: landing modes

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 5: Hand the in-game checklist to the user**

Give the user the spec's six in-game scenarios:
1. Mun, propulsive mode, with legs: the legs come out in Touch Down, the end is upright and without drift, the autopilot stops on contact, and the vessel does not bounce.
2. Same with "Deploy legs early": the legs come out as soon as Brake starts.
3. Lander without legs: it lands, and the tab shows "No legs".
4. Kerbin, capsule with a parachute, suborbital trajectory: retrograde, parachutes armed then opened by the game, legs if there are any, stop on contact.
5. Parachute mode without a parachute: refused. In a stable orbit: refused with "deorbit first".
6. Log: the `[Landing] gear: …` lines. They tell whether Gear deploys stock legs.

Ask them to send back these log lines:
- `[Landing] gear:`
- `[Landing] parachute` (start check, start, canopy open, alerts)
- `[Landing] touchdown in`
- `[Landing] start check` (propulsive mode, unchanged)

---

## Self-review

**Spec coverage:**

| Spec item | Task |
|---|---|
| `land.mode` in the atmo profile only; always propulsive on airless bodies | 5 (`mode` null on vac, `parachute_mode`), 8 (selector in the atmo panel) |
| `land.deploy_legs_early` in both profiles | 5, 8 |
| "Land" / "Brake" button label | 8 |
| Legs on entering Touch Down, or Brake with the early option | 6 Step 3 |
| Gear action group unless `GetActionGroupState(Gear)` already True; re-read at 3 s; per-leg fallback; `[Landing] gear:` log with before, after and path | 3 (fallback via `toggleExtend`, deviation 1) |
| No legs: "No legs", nothing refused | 3 (`Describe`), 8 (info row) |
| Below 50 m in TouchDown: `θ = atan(k·v_h/g)`, k = 1, cap 15°, via StabilityAssist + `SetTargetOrientation` in HorizonUp | 1, 7 Step 2 |
| Thrust × `max(0, cos(error))` instead of the `touch_down_max_angle` cut | 1, 7 Step 3 |
| Contact speed floor 0.5 m/s | 1, 7 Step 5 |
| End on `LandedOrSplashed`, old test as net; throttle 0, StabilityAssist, autopilot off; no relight | 6 Step 7 |
| Parachute refusals with exact messages; waived trajectory check inside the atmosphere; no engine/TWR/Δv checks | 2, 6 Step 5 (deviation 3) |
| `Mode.Parachute` appended last; single phase `ParachuteDescent`; never `nextMode()` | 6 Steps 1-2 |
| Retrograde, no warp control; arm STOWED once; SAS release and legs at first DEPLOYED; StabilityAssist at contact | 6 Step 1, Step 7 |
| Alerts "Too fast under canopy" (10 m/s, 500 m, DEPLOYED) and "No parachute left" | 2, 6 Step 1, 8 Step 5 |
| Info rows (legs, parachutes) | 8 Step 4 |
| Tests 1-3 (EditMode) | 1, 2 |
| In-game tests 1-6 | 9 Step 5 |
| Out of scope (retracting legs, rover wheels, cutting chutes, deorbit, guided reentry): nothing implements them | — |

**Placeholders:** none. Every code step gives the full code; every replaced block is quoted.

**Names checked across tasks:**
- `FinalDescent.IsFinal`, `AimDirection`, `ThrustFactor` and `ContactSpeed` (Task 1) are used in Task 7.
- `ParachuteFeasibility.Check`, `TooFastUnderCanopy` and `NoParachuteLeft` (Task 2) are used in Task 6, and `TooFastMessage` / `NoParachuteLeftMessage` in Task 8.
- `LandingGear.Reset`, `Deploy(VesselComponent, string)`, `Update(VesselComponent)` and `Summary` (Task 3) are used in Tasks 6 and 8.
- `ParachuteCounts` (`Usable`, `Deployed`, `SemiDeployed`, `Armed`, `Stowed`, `Total`) and `Parachutes.Count` / `ArmStowed` (Task 4) are used in Tasks 6 and 8.
- `LandingMethod`, `LandingSettings.mode` and `deploy_legs_early`, and `LandingPilot.parachute_mode` (Task 5) are used in Tasks 6 and 8.
- `LandingPilot.gear`, `parachute_descent`, `Mode.Parachute`, and `ParachuteDescent.too_fast_under_canopy` / `no_parachute_left` (Task 6) are used in Task 8.
- `LandingPilot.altitude` (public) and `current_falling_speed` (internal) are read by `ParachuteDescent`, in the same assembly.
- The UXML names `land_mode_atmo`, `deploy_legs_early_atmo` and `deploy_legs_early_vac` match between Task 8 Steps 1 and 2.

**Order:** Task 5 comes before Task 6, which reads `deploy_legs_early` and `parachute_mode`. The UI bindings come in Task 8, together with their UXML elements, so no intermediate build binds a missing element.
