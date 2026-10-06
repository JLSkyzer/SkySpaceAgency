# Fix: circularization nodes start their burn at the apsis instead of centering it

## Root cause (verified)

- **The game's convention.** The API doc for `ManeuverNodeData.Time` says "Universal time at which the burn begins" (beta xml `9b15c3ff`). The node also carries `BurnDuration` and `CachedManeuverPatchEndUT`, so the game models a finite burn running from `Time` to `Time + BurnDuration`, and the Node executor's default start mode `precise` starts the burn at `Time` too (`NodeExPilot.cs`, `StartMode.precise`).
- **The mod's mistake.** Lift's `FinalCircularize`, the Node tab's "Circularize at AP/PE" buttons, Landing's `Circularize` and the Orbit tab's planned burns all set `Time` to the impulsive burn instant: the apsis, or the orbital node for the Orbit tab.
- **The effect on long burns.** The whole burn happens after the apsis. With a long burn the vessel is already falling while it burns, so both the game's prediction and the real execution end on a trajectory that still hits the ground. The user's log shows the case clearly:

  | | |
  |---|---|
  | Δv | 932 m/s |
  | `BurnDuration` | 1011 s |
  | time to apoapsis | 89 s |

  The saved node is exactly `{x 0, y 0, z 932.34}` at the apoapsis UT, so the Δv itself is correct; only the start time is wrong.
- **A second bug in the Node tab's "Circularize at AP".** When the vessel has just passed the apoapsis on a suborbital trajectory, `TimeToNextApoapsis` returns the next apoapsis, one period later, which comes after the impact. The log shows `T+932.6s` followed by a `NullReferenceException` in the game's `ManeuverPlanComponent.UpdateNodeDetails` while it added the node.

## Fix

### 1. Pure unit `Pilots/Orbit/BurnTiming.cs`

Namespace `K2D2.OrbitPlanning`, `public static class BurnTiming`, plus EditMode tests in `Assets/Tests/OrbitPlanner/BurnTimingTests.cs` (the existing Orbit test assembly). TDD.

- **`Duration`**: `public static double Duration(double deltaV, double thrustKN, double massT, double isp)`. Thrust in kN, mass in t, `g0 = 9.80665`.
  - With `isp > 0`: `T = m0·isp·g0/F · (1 − exp(−|Δv|/(isp·g0)))` (Tsiolkovsky, constant thrust). Mind the units: `F` in kN with `m` in t gives m/s², so use `m0/F` directly.
  - With `isp` ≤ 0 or unknown: `T = |Δv|·m0/F`.
  - Return `double.NaN` when `F` ≤ 0, `m0` ≤ 0 or any input is NaN.
- **`CenteredStart`**: `public static double CenteredStart(double impulseUT, double duration, double earliestUT)`. It returns `impulseUT − duration/2`, clamped to at least `earliestUT`. When `duration` is NaN it returns `impulseUT` unchanged, so with no thrust information the old behavior is kept.
- **Tests**:
  - Duration at constant mass: Δv 100, F 50 kN, m 10 t, no Isp, so T = 20 s.
  - Tsiolkovsky case: Δv 1000, F 50, m 10, Isp 300. Compare with the closed form, and check the result is shorter than the constant-mass value.
  - NaN for F = 0 or m = 0.
  - `CenteredStart`: plain centering, clamping to `earliestUT`, and NaN duration returning `impulseUT`.

### 2. `ManeuverCreator`: center nodes on their impulse time

Add `public double EstimateBurnDuration(double deltaV)` in `Assets/SkySpaceAgency/Code/KSPService/ManeuverCreator/ManeuverCreator.cs`. It reads the active vessel's active-engine thrust, mass and Isp, using a `BurndV` instance's `Compute_Thrust()` and its `active_thrust`, `mass` and `active_isp` fields. Fall back to `full_thrust.magnitude` when `active_thrust` is 0, because the engines may be shut down while coasting. Then it calls `BurnTiming.Duration`.

Add an overload or parameter so callers can pass the **impulse** UT and have the node `Time` centered:

```
Time = BurnTiming.CenteredStart(impulseUT, duration, now + MinLead)
```

`MinLead` is 5 s, so the executor has a moment to react. Log one line per centered node:

```
[ManeuverCreator] centered burn: impulse T+{..}s, duration {..}s, start T+{..}s
```

When the clamp to `now + MinLead` applies, add `(too long to center: starting now)` to that line.

Apply it to:
- **`RemoveAllNodesThenCreate`**, used by Lift `FinalCircularize`, the Node tab circularize buttons and Landing `Circularize`. Add a `bool centerOnImpulse` parameter defaulting to **false**, so callers not listed here keep their behavior. Pass true from:
  - Lift `Final.cs` (`FinalCircularize`);
  - `NodeExPilot.CreateCircularizeNode`, both AP and PE;
  - Landing `Controlers/Circularize.cs`.

  Do **not** pass it from Landing `DeorbitBurn` or `MidCourseCorrection`: their impact targeting assumes an impulse at the node time, which is sub-project 3's work.
- **`CreateNodes`**, the Orbit tab: center each burn on its `PlannedBurn.UT` with that burn's own duration, estimated from the current mass. This is a known approximation for later burns, which happen after mass has been lost. Keep the chronological order: if centering would make a node start before the previous node's start, clamp it to the previous start + 1 s.

### 3. Node tab "Circularize at AP" past the apoapsis on a suborbital trajectory

In `NodeExPilot.CreateCircularizeNode(true)`, refuse (set `circularize_error`, log, return without creating a node) when **both** of these hold:
- the vessel is descending: `Vector3d.Dot(r_now, v_now) < 0`;
- the periapsis radius is below the body radius plus its atmosphere depth (`body.radius + (body.hasAtmosphere ? body.atmosphereDepth : 0)`).

The message, in English like the rest of the UI: `"Already past apoapsis and falling: the next apoapsis comes after impact. Burn prograde now, or use Circularize at PE once in a stable orbit."`. The existing unbound-orbit refusal stays.

## Out of scope

- The Node executor's start modes are unchanged; `precise` is already consistent with the game.
- Landing DeorbitBurn and MCC.
- Finite-burn steering losses: a centered constant-attitude burn is the standard approximation.

## Verification

- EditMode tests green, with the new BurnTiming tests added to the existing 87.
- Build the release zip with the usual command, install it, and commit and push.
- **In game:**
  - Lift with auto-circularize: the planned node starts before the apoapsis, and the map shows a closed orbit after the node.
  - Node tab "Circularize at AP" on a suborbital arc before the apoapsis: same result.
  - The same button after the apoapsis: refusal message.
  - Read the `[ManeuverCreator] centered burn` lines.
