# Landing autopilot audit: physics and guidance algorithms

Date: 2026-10-05. Scope: the burn computations and guidance of the Landing pilot in SkySpaceAgency (remix of K2-D2), at the repo state on the audit date. Companion to `2026-10-05-landing-tab-audit.md` (UI and settings), which this report does not repeat.
Method: I read the code statically and ran a simplified simulator I wrote myself (Appendix A). I modified no file in the repo. I did not run Unity or the game, and I watched no flight.

**Evidence tags.** Each claim carries one of these tags:
- **[code]**: read directly in the source, including a grep showing something is absent.
- **[doc]**: taken from the Redux API XML docs in `API-Documentation/xml/9b15c3ff` (latest `beta` snapshot).
- **[sim]**: an output of my planar simulator. These are estimates, not game measurements.
- **[inferred]**: follows from the code, but depends on engine behaviour I did not observe.

Paths are relative to `Assets/SkySpaceAgency/Code/`.

Files read in full: `Pilots/Landing/LandingPilot.cs`, `LandingSettings.cs`, `LandingProfile.cs`, `LandingTargeting.cs`, `AtmosphericPredictor.cs`, `Controlers/TouchDown.cs`, `Controlers/DeorbitBurn.cs`, `Controlers/MidCourseCorrection.cs`, `Controlers/Circularize.cs`, `KSPService/BurndV.cs`, `KSPService/SASTool.cs`, `KSPService/ManeuverCreator/KeplerPropagator.cs`, `Pilots/Nodes/Controlers/WarpTo.cs`, `Pilots/Nodes/Controlers/BurnManeuvre.cs`, `Pilots/BaseControllers/ExecuteController.cs`, `Pilots/StagingPilot.cs`. Read in part: `KSPService/KSPVessel.cs`, `KSPService/ManeuverCreator/ManeuverCreator.cs`, `Pilots/Nodes/NodeExPilot.cs`, `Pilots/Landing/LandingUI.cs`, `K2D2_Plugin.cs`. I did not read `VesselAeroLookup.cs` in detail, because its only consumer, `AtmosphericPredictor`, is not wired into the pilot.

---

## 1. Flow map

### 1.1 State machine (`Pilots/Landing/LandingPilot.cs:79-93`)

| Mode | Entered when | What it computes or does |
|---|---|---|
| `Circularize` | Start, if `precision_landing` is on and the body has no atmosphere (`:221-222`) | Ap/Pe from state vectors. If \|Ap−Pe\| > 1500 m, a vis-viva circularize node at apoapsis (`Circularize.cs:115-153`). |
| `DeorbitBurn` | `Circularize` finished | A 36-sample search over one orbit for the burn time whose **ballistic terrain impact** is closest to the target, plus an optional normal "plane trim". It then creates the node and flies Turn → Warp → Burn (`DeorbitBurn.cs:87-215`, `LandingTargeting.cs:388-619`). |
| `MidCourseCorrection` | `DeorbitBurn` finished | The same search, rerun on the real post-burn orbit over 25 % of the remaining coast (`MidCourseCorrection.cs:95-174`). |
| `Pause` | `MidCourseCorrection` finished, or a non-precision Brake reached < 50 m/s above `start_touchdown_altitude` | 1 s at throttle 0 (`LandingPilot.cs:132-135, 603-610`). |
| `QuickWarp` | Start when precision landing is off, or after Pause | Warps (index ≤ 6) to `startSafeWarp_UT` − 10 s (`:136-146`, `WarpTo.cs:109-113`). |
| `RotationWarp` | QuickWarp finished | Warps (index ≤ 2) while checking surface-retrograde attitude, until `startBurn_UT` − 10 s (`:147-156`). |
| `Waiting` | RotationWarp finished | Real time until `startBurn_UT` (`:620-628`). |
| `Brake` | `startBurn_UT` reached | `TouchDown` executor. Without precision: `max_speed = 0`, i.e. full braking down to 50 m/s. With precision: `max_speed = compute_limit_speed(alt)`, then TouchDown below `start_touchdown_altitude` (`:629-668`). |
| `TouchDown` | Brake → TouchDown altitude, or "no collision found" while below that altitude (`:581-585`), or the Touch Down button | `max_speed = 0.05·alt + 2.5` with the defaults (`:669-674`, `LandingSettings.cs:201-206`). |
| stop | `alt < 5 m && descent rate < 1 m/s` (`:597-602`) | `isRunning = false` → throttle 0. |

Every frame (`Update`, `:546-684`), regardless of mode, the pilot does the following:
1. It computes `altitude = min(SeaLevel, GroundLevel) − BoundingSphere.radius` (`KSPVessel.cs:562-586`) [code].
2. It runs `compute_real_collision()`. This walks the **current patched-conic coast** forward in 20 s steps from now + 120 s, then bisects on `GetAltitudeFromTerrain` until the result is within 1 m (`:379-530`). It yields `adjusted_collision_UT` and the predicted lat/lon, which is not corrected for body rotation [code].
3. It computes `speed_collision = |v_inertial(t_impact)|` and `burn_duration = speed_collision / full_dv` (`:279-280`). `full_dv` is the full-throttle **acceleration**, not a Δv (`BurndV.cs:90-91`) [code].
4. It computes `startBurn_UT = t_impact − burn_duration − burn_before` (`:364`). Here `burn_before` is the slider (0-10 s), raised in precision mode to the time at which the coast crosses `start_touchdown_altitude + 8000 m` (`:340-362`) [code].

### 1.2 Braking and descent law (`Controlers/TouchDown.cs:243-263, 868-916`)

```
min_throttle = g · cos(tilt between the vessel's thrust axis and gravity) / a_max   (gravity_compensation)
wanted       = clamp01( (|v_surface| − max_speed) / a_max + min_throttle )
throttle     = MoveTowards(throttle, wanted, 5/s · dt)
```

This is a proportional speed controller with a 1 s time constant and a gravity feed-forward. It acts on the **magnitude** of surface velocity, with thrust along surface retrograde. In precision mode, the thrust is instead tilted by up to `steering_max_angle` (40°) in heading and `arc_extend` 80° / `arc_shorten` 10° in pitch. Thrust is fired only if:
- the velocity has a downward component (`:294`), and
- the attitude error is < `touch_down_max_angle` (30°, `:364`).

Otherwise the throttle is set to 0 [code].

---

## 2. Findings

Severity: **Critical** = can crash the vessel; **Important** = lands badly or wastes a lot of fuel; **Minor** = everything else.

### 2.1 Brake start and burn-time physics

**F1. The non-precision brake start ignores gravity and flight path angle. Critical.**
- *Code* (`LandingPilot.cs:279-280, 364`) [code]: `t_start = t_impact − V_impact / a_max − burn_before`. `V_impact` is the inertial speed where the coast meets the terrain. Altitude, vertical speed, flight path angle and gravity do not enter the formula.
- *Physics*: the formula is the time needed to cancel `V` with no gravity. For a **vertical** fall it is conservative. I derived in closed form that it stops `V²/(2a)` above the ground. For a **shallow** approach, which is what every deorbit from a low circular orbit produces, the horizontal speed takes `V/a` to cancel. During that time the vessel keeps falling, with gravity only partly offset by the shrinking centrifugal term. The burn then starts a few km up, with ~550 m/s horizontal speed and ~48 km of horizontal stopping distance at TWR 2.
- *Scenario* [sim]: Mun, 15 km circular orbit, Pe at datum −2 km, non-precision mode, defaults. The run brakes to < 50 m/s, then follows the Pause/re-predict cycle.

  | TWR | Brake start altitude | Touchdown (vertical / horizontal) |
  |---|---|---|
  | 1.5 | 4.7 km | 71 / 273 m/s |
  | 2 | 3.4 km | 59 / 247 m/s |
  | 3 | 2.1 km | 44 / 201 m/s |
  | 5 | 1.2 km | 27 / 120 m/s |

  Raising `burn_before` to its 10 s maximum changes almost nothing (TWR 2: 61 / 234 m/s). With Pe at −20 km, TWR 2 still crashes (61 / 76 m/s) but TWR ≥ 3 lands. With Pe at −60 km or deeper, TWR ≥ 2 lands. The rule therefore only works for steep approaches.
- *Fix*: compute the start by **forward-simulating the braking burn**. Use full thrust along surface retrograde, varying g, mass flow `ṁ = F/(Isp·g0)`, and terrain sampled along the simulated path. Bisect on the start time so that the simulated stop happens at `h_safe` (for example 50-100 m) with throttle margin `η ≈ 0.9`. This is what MechJeb's landing predictor does. A cheap but rough analytic stand-in: start when the vertical stopping distance `v_vert² / (2(η·a − g))`, plus the drop during the horizontal kill time `t_h = v_h/(η·a)`, reaches the terrain altitude ahead.

**F2. Burn duration uses inertial speed at the conic impact, and current mass. Minor.**
- *Code* (`:279-280`) [code]: `speed_collision` comes from `GetOrbitalVelocityAtUTZup` (inertial). Braking actually cancels **surface** velocity. `a_max = F/m_now` is measured now.
- *Effect*: for a prograde orbit, surface speed = inertial − ωR·cos(lat), so the burn time is overestimated (early start, safe). For a **retrograde** orbit it is underestimated by `2ωR/a`:
  - Mun (ωR ≈ 9 m/s, a = 3.26 m/s²): ≈ 5.5 s late.
  - Kerbin: 175 m/s, so tens of seconds.
- Mass loss makes `a` grow during the burn, so `V/a_now` overestimates the duration (conservative).
- *Fix*: use the surface-relative velocity at impact (subtract `ω × r`), and integrate `a(t) = F/(m0 − ṁt)` (Tsiolkovsky) inside the forward simulation of F1.

**F3. Turn time, ignition and alignment are not budgeted in the start time. Minor (Important at low TWR).**
- *Code* [code]:
  - RotationWarp gives 60 s to turn (`LandingSettings.cs:104`), but the start time does not depend on turn success.
  - In Brake, thrust is zero until the attitude error is < 30° (`TouchDown.cs:364`).
  - The throttle law does not divide by the cosine of that error. At 30° off, effective braking is 87 %.
  - In precision mode the commanded aim is itself up to 80° off retrograde, and the law still assumes thrust is anti-parallel to `v` (see F11).
- The throttle ramp is 0.2 s (`max_throttle_rate_per_sec` = 5); engine spool-up is not modelled. In KSP2, stock engines respond almost instantly [inferred].
- *Fix*: add the measured residual attitude error and a few seconds of margin to the forward simulation. Scale braking by `cos(angle between thrust and −v)` in the throttle law (`wanted ← wanted / max(cos θ, 0.3)`).

**F4. The extra time for the lateral correction is dimensionally wrong. Minor (masked by F5).**
- *Code* (`LandingPilot.cs:307-320`) [code]: `t = target_error_m / (speed_collision · sin θ_min)`. This treats `V·sin θ` as a lateral velocity obtained instantly.
- *Physics*: tilting thrust by θ gives a lateral acceleration `a·sin θ`. The distance closed is `½·a·sin θ·t²`, so `t = sqrt(2·err / (a·sin θ))`. Mun, err 5 km, a = 3.26, θ = 10°: physics needs 133 s; the code gives 52 s.
- In practice the 8000 m altitude floor (`:340-362`) dominates both.
- *Fix*: replace with the correct expression, or drop it once F1 or F5 is fixed.

### 2.2 Speed profile (the core of Brake in precision mode and of TouchDown)

**F5. The speed limit `v_max = (ratio/10)·h + v_td` is linear and independent of vessel capability. Critical.**
- *Code* (`LandingSettings.cs:201-206`; defaults `ratio = 0.5`, `v_td = 2.5`, start at 1500 m, `:107-109`) [code]. Used in Brake when precision is on (`LandingPilot.cs:644`) and in TouchDown (`:672`).
- *Physics*: with constant thrust, the speed from which you can still stop before the ground is `v_safe(h) = sqrt(2(a−g)h)`. It is a square root, while the profile is linear (`k·h`, with `k = ratio/10`). The profile exceeds the stopping envelope above `h* = 2(a−g)/k²`. With the defaults that is `h* = 800(a−g)`:
  - Mun, TWR 2: 1300 m.
  - Mun, TWR 1.5: 650 m.
  - Minmus, TWR 2: 390 m.

  TouchDown starts at 1500 m, so it starts inside the unsafe zone. Below 1500 m the controller **cuts the throttle** whenever the vessel is slower than the profile (`wanted` < 0). It lets the vessel accelerate up to the profile, and the profile then allows speeds that cannot be stopped. With `touch_down_ratio` at its slider maximum of 3 (`k = 0.3`), `h* = 22(a−g)`, which is ~70 m on the Mun at TWR 3.
- *Scenario* [sim, vertical TouchDown phase from 1500 m, defaults]:

  | Case | Touchdown speed |
  |---|---|
  | Mun TWR 1.5, starting at rest | 25 m/s |
  | Mun TWR 1.5, starting at 50 m/s | 41 m/s |
  | Mun TWR 1.2 | 38-53 m/s |
  | Ike TWR 1.5 | 28-46 m/s |
  | Minmus TWR 2 | 19-44 m/s |
  | Mun / Ike / Tylo, TWR ≥ 2-3 | lands at 2.6 m/s |
  | Mun with `ratio = 3`, TWR 1.5 / 2 / 3 | 78 / 75 / 70 m/s |
  | Tylo with `ratio = 3` | 74-124 m/s |

  The cases that crash are low-TWR landers: heavy or nuclear landers, or any lander where the player raised the ratio.
- *Fix*: derive the limit from measured capability, recomputed each frame:
  ```
  a_net   = η·a_max·cosθ − g_local                    (η ≈ 0.8-0.9, θ = expected tilt)
  v_max(h) = min( sqrt(2·a_net·max(h − h_final, 0)) + v_td ,  user_ratio·h/10 + v_td )
  ```
  Keep the user's linear profile only as an extra cap. If `a_net ≤ 0.2·g`, refuse to start or warn (low TWR). Apply the limit to **vertical** speed, and command horizontal speed separately (see F12).

**F6. The P controller is acceptable; no oscillation risk found. Minor (observation).**
- *Code*: gain `1/a_max`, so a speed error `Δv` produces a corrective acceleration of `Δv` per second (time constant 1 s). Gravity feed-forward is `g·cos(tilt)/a_max`, which is correct for **speed magnitude** when thrust is aligned with `−v`. Throttle slew is 5/s [code].
- There is no integral term. Feed-forward plus profile tracking gives a small steady-state error (≈ `k·v·τ`), and nothing that would cause a hover oscillation [inferred].
- High TWR (Minmus, TWR 10): `min_throttle` ≈ 0.1, which is fine. If an engine has a high minimum throttle, KSP2 clamps it [inferred, not checked].

### 2.3 Terrain

**F7. Terrain and lat/lon at a future time are evaluated with the body's *current* orientation. Important.**
- *Code* [code]: every future sample builds `new Position(body.SimulationObject.transform.celestialFrame, r_future)`. This happens at `LandingPilot.cs:422-428, 518-521`, `LandingTargeting.cs:270-273, 293-297, 331-334` and `AtmosphericPredictor.cs:206-208, 377-379`. The docs say `celestialFrame` is the *celestial* frame, distinct from the *body-fixed* `bodyFrame` [doc: `ITransformModel.celestialFrame` / `bodyFrame`]. `GetAltitudeFromTerrain` and `GetLatLonAltFromRadius` therefore map an inertial point valid at `t` onto the ground using the rotation at "now".
- Consequences:
  - The predicted impact point is offset eastward by `ωR·cos(lat)·Δt`.
  - The terrain height is read at the **wrong place**. At a grazing approach this is amplified. With a deorbit from 15 km to Pe −2 km on the Mun, the flight path angle at impact is ≈ 1.5° (my vis-viva calculation), so a terrain height error `Δh` moves the impact by ≈ 38·Δh.
  - `compute_real_collision` applies **no** correction (`:518-521`). The Target Error readout and the TouchDown steering input therefore carry a bias that shrinks with time to impact:
    - Mun, 9 m/s at the equator (stock value, not re-checked in Redux): 2.7 km at 300 s, 0.5 km at 60 s.
    - Kerbin: 175 m/s.
  - `PredictImpactLongitude` corrects the longitude only, by `rotation × 1.307` (`LandingTargeting.cs:302-304`). The correct factor is exactly 1. An empirical 1.307 means the factor is absorbing other errors: lands short (F9), late node burn (F14), terrain read at the wrong place [inferred]. Those errors depend on body, orbit and TWR, so a constant will not hold elsewhere. The terrain *height* used to find the crossing is not corrected at all.
- *Fix*: express every future sample in the body-fixed frame at `t`. The repo already contains `ReprojectToNow` (`LandingTargeting.cs:237-247`), which is never called [code]. Equivalent: rotate `r_future` by `−ω·(t − now)` about the spin axis before building the `Position`. Then remove `empirical_calibration` and re-validate in game.

**F8. Terrain ahead and the radar altitude. Important.**
- *Code* [code]:
  - The collision search walks in **20 s** steps (`LandingPilot.cs:407`). `PredictImpactLongitude` and `PredictUTAtAltitude` use 60 s (`LandingTargeting.cs:263, 324`).
  - The search only bisects after one sample is found under the ground. A ridge the conic clips for less than one step is skipped. At 550 m/s, 20 s is 11 km of ground track.
  - The speed profile uses the radar altitude **directly below** (`KSPVessel.cs:562-586`).
  - No safety check samples terrain along the **powered** path.
- *Scenario*: a grazing approach in precision mode (F9 shows the vessel cruising tens of km at low altitude). Flying over a valley toward a 3 km ridge, the profile allows ~150 m/s at 3 km of radar altitude, and the ridge is never seen [inferred].
- *Fix*: inside the forward simulation (F1), sample terrain every ≤ 1 s of predicted path and take the minimum clearance. Shrink the coarse step when the conic is shallow, for example `step = clamp(h / |v_vert| / 4, 1, 20)`.

**F9. Effect of Redux 26w40a on the terrain sampler. Important (to re-test).**
- [doc]: `PQS.GetSurfaceHeight` now "matches the rendered terrain, quirks included". It is backed by `Redux.Planets.TerrainHeightSampler`, about 2 cm mean and < 1 m worst case on Kerbin. The stock CPU sampler `PQSJobUtil.HeightSample` was "about 225 m off on average". `GetStockSurfaceHeight` keeps the old behaviour.
- [inferred]: I could not check whether `CelestialBodyComponent.GetAltitudeFromTerrain`, and the `GroundLevel` altitude from `TelemetryDataProvider`, go through the new sampler. If they do, predictions now agree with the drawn ground and the colliders, which is an improvement. But constants tuned before 26w40a are suspect, given the ×38 amplification at a grazing approach (225 m ≈ 8 km of impact shift on the Mun). Those constants are `empirical_calibration = 1.307`, `periapsis_safety_margin = 2000` and `min_correction_altitude_margin = 8000`. If only one of the two paths changed, the prediction (conic vs. `GetAltitudeFromTerrain`) and the radar altitude can disagree by tens or hundreds of metres.
- *Test*: on a landed vessel, compare `AltitudeFromTerrain`, `GetAltitudeFromTerrain(vessel position)` and the HUD ground altitude, then recalibrate after fixing F7.

**F10. Contact point and vessel size. Minor.**
- *Code* [code]: `compute_real_collision` reads `BoundingSphere.radius`, but the subtraction is commented out (`LandingPilot.cs:418, 429`). The predicted collision is that of the vessel's reference point.
- The radar altitude does subtract the full radius (`KSPVessel.cs:568`). For a tall lander (radius 6 m) this can read negative or early, which is why the stop rule `alt < 5` trips (F13).
- *Fix*: use a measured "lowest point below CoM" offset, taken once at start (raycast or part bounds).

### 2.4 Precision targeting (vacuum)

**F11. The deorbit targets the *ballistic* impact point, not the powered landing point. Important (lots of fuel; can crash at low TWR).**
- *Code* [code]: `FindBestDeorbitBurn` scores each candidate by the conic's terrain impact (`LandingTargeting.cs:621-646`). `TouchDown` steers on `predicted_landing_lat/lon` from `compute_real_collision`, which is also ballistic (`TouchDown.cs:434-436`). The braking burn's displacement is never modelled. The vessel brakes from ~11-13 km of altitude (the 8000 m floor, minus `V/a`, F1/F4) and stops much shorter than the ballistic point.
- *Scenario* [sim]: Mun, Pe −2 km, precision on, steering off.

  | Orbit | TWR | Lands short of target | Touchdown |
  |---|---|---|---|
  | 15 km | 2-5 | 211 km | 2.6 m/s |
  | 30 km | 2-5 | 126 km | 2.6 m/s, except TWR 2: 15.8 m/s |

  With the arc steering on (my own planar copy of `ComputeSteeredDirection`), the steering does close the gap: miss 0.1 km. The cost is **816-821 m/s of Δv instead of 676-723 m/s** (+100 to +140 m/s, ≈ +15-20 %), because the vessel "extends" with thrust tilted up to 80° toward vertical, i.e. it flies on its engine. At TWR 1.3 the steered descent crashes at 18-21 m/s vertical and 8-11 m/s horizontal.
- *Fix*: predict the **powered landing point** with the forward simulation of F1, and use it in three places:
  1. as the deorbit search's objective;
  2. as the input to `MidCourseCorrection`;
  3. as `predicted_landing_lat/lon` for the descent steering.

  Better still, use real descent guidance (§3).

**F12. Steering authority persists down to contact. Important.**
- *Code* (`TouchDown.cs:447, 495-496, 584`) [code]: `taper = clamp01(alt / start_touchdown_altitude)`. It is 1 above 1500 m and decreases **linearly to 0 at the ground**. At 750 m, up to 40° of pitch "extend" and 20° of heading are still allowed. The comment "extending only ever ADDS vertical braking margin" (`TouchDown.cs:44-47`) is true for the vertical axis only. Tilting up keeps horizontal speed. Since the throttle law tracks `|v|` assuming thrust along `−v` (F3), the vessel can reach the ground with a residual horizontal speed and a tilted attitude.
- *Fix*: set steering to zero below a "final approach" altitude, for example `max(200 m, 10 s × |v_vert|)`. Below it, switch to horizontal-velocity nulling (see F15). Convert target error into a velocity command (ZEM/ZEV, §3) rather than a fixed angle.

**F13. The deorbit search: resolution and refinement. Minor.**
- *Code* (`LandingTargeting.cs:395, 445-487`) [code]: 36 samples over one orbit, a step of 68 s on the Mun at 15 km (~35 km of ground track). Then **one** parabola fitted through three points. The haversine error is V-shaped around the minimum, not parabolic. My calculation gives a vertex-estimate residual of up to ~0.09 step, about 6 s or ~3 km on the Mun, and the refinement is not iterated. Mid-course correction repairs most of it.
- *Fix*: minimize the **signed along-track error**, which is smooth and changes sign, with secant or Brent to < 1 s. Handle the cross-track error separately with the normal component (F16).

**F14. Deorbit, MCC and Circularize burns start at node time, not half a burn earlier. Important.**
- *Code* [code]: these phases call `burn.StartManeuver(node)`, which sets `UT = node.Time` (`BurnManeuvre.cs:64-68`). The burn starts at that UT (`:104-123`). `NodeExPilot` instead sets `burn.UT = node.Time − BurnDuration/2` every frame (`NodeExPilot.cs:393-402`). The landing phases never do (`DeorbitBurn.cs:197-202`, `MidCourseCorrection.cs:193-198`, `Circularize.cs:174-179`).
- The burn centroid is late by `T_burn/2`. The search's model, an impulse at `candidateUT` (`LandingTargeting.cs:628-629`), assumes it is on time.
- *Scenario*: Mun, 15 km orbit, deorbit Δv ≈ 11.4 m/s (vis-viva).
  - TWR 2: 3.5 s, so 1.75 s late, ≈ 0.9 km downrange.
  - Low TWR (a = 0.8 m/s²): 7 s late, ≈ 3.6 km.
- *Fix*: set `burn.UT = node.Time − node.BurnDuration/2` (and `warp.UT` to match) in all three phases, as NodeExPilot does.

**F15. Shallow deorbit (Pe = datum −2 km). Important.**
- *Code* (`DeorbitBurn.cs:59`, `MidCourseCorrection.cs:78`) [code].
- *Physics*: a 1.5° impact angle (15 km Mun orbit). The 9.5 km crossing comes ≈ 71° of orbit, ≈ 250 km, before the ballistic impact (my vis-viva calculation). The result is a long cruise at low altitude (F8), extreme sensitivity of the impact point to terrain (×38, F7/F9), and the long "extend" phase of F11.
- *Fix*: choose a periapsis that gives a flight path angle of 5-15° at the start of braking, for example `Pe = R − 0.1 R…0.3 R` or derived from TWR. Then target the powered point (F11).

**F16. Plane trim and the sign of the normal. Minor (unverified).**
- *Code* [code]: the prediction adds `dvNormal · normalize(Cross(r0, v0))` in the **Zup** frame (`LandingTargeting.cs:631-635`). The node takes `BurnVector.y = +normalDeltaV` raw (`ManeuverCreator.cs:332, 417`).
- *Analysis*: swapping Y and Z is a reflection (det = −1), so `Cross` in Zup = `−P·Cross` in Yup. The sign therefore depends on which frame the game's "normal" is defined in. In KSP1, `node.DeltaV.y > 0` is along `Cross(r_zup, v_zup)` (MechJeb, `DeltaVAndTimeForNodeUT`), so the assumption is plausible, but not verified in KSP2.
- The Orbit tab goes through `GameNormalSign` (`ManeuverCreator.cs:427-430, 479`), but the Landing path does not. If one is flipped after an in-game test, the other will not be.
- *Fix*: pass the Landing call through `GameNormalSign`, and test once: trim +X m/s, then read the new inclination.

**F17. MCC reuses a deorbit Δv formula that is only valid at an apsis. Minor.**
- *Code* (`LandingTargeting.cs:221-226`, used at `:628`) [code]: `newSMA = (r0 + rP)/2` assumes the burn point becomes the apoapsis. On a descending trajectory that is false: the radial speed is nonzero and a tangential Δv does not create an apsis. For MCC, the Δv amplitude is therefore an arbitrary function of time (for example ≈ −2 m/s at r = 212 km on the Mun, my vis-viva calculation), not a free variable. The impact is still evaluated exactly by propagation, but the search explores only a 1-DOF family plus trim.
- *Fix*: at a fixed MCC time, solve the 2-DOF problem `(Δv_prograde, Δv_normal)` with Newton on the signed (along-track, cross-track) error, using a finite-difference Jacobian (4 propagations per iteration).

### 2.5 Final descent and touchdown

**F18. No contact detection; cutoff at `alt < 5 && sink < 1`. Important.**
- *Code* (`LandingPilot.cs:597-602`) [code]: neither `VesselComponent.Landed` nor `Situation` is used, although both exist [doc: `VesselComponent.Landed` "is currently landed", `Situation`].
- Cases [my calculation, F10]:
  - Hovering slowly at 5 m (for example `touch_down_speed = 0`, which the slider allows, `LandingSettings.cs:109`): the vessel is dropped from up to 5 m plus the bounding radius. That is 4 m/s on the Mun and 8.9 m/s on Tylo.
  - After contact while bouncing or sliding (descent ≥ 1 m/s), TouchDown keeps firing.
  - On a slope, retrograde can drop below the horizon: throttle 0, then it fires again.
- After the stop, SAS stays on Retrograde or a fixed target. With nearly zero velocity, retrograde is ill-defined [inferred].
- *Fix*:
  - stop on `Landed || Splashed` (or `Situation`), or on radar < `h_contact` with `|v| < 0.5`;
  - throttle 0;
  - SAS → StabilityAssist or Radial Out;
  - optional timeout.

  With `v_td = 0`, use a floor: `max(v_td, 0.5 m/s)`.

**F19. Attitude near the ground: pure surface retrograde, no horizontal nulling. Important.**
- *Code* (`TouchDown.cs:347`, plus steering) [code]. Example: with 1 m/s horizontal and 2.5 m/s vertical, the vessel is tilted 22° at contact, and more under precision steering (F12). Tall landers tip over. The 30° gate (`:364`) cuts the throttle when SAS lags, including in the last seconds [inferred].
- *Fix*: below `h_final`, command a vertical attitude ("up") plus a small tilt `θ = atan(k_h · v_h / g)` capped at 10-15°, to cancel horizontal speed. Do not cut the throttle because of the alignment gate below that altitude: modulate it.

**F20. Landing legs are never deployed. Important.**
- *Code* [code]: a grep for `gear|ActionGroup|LandingGear` under `Code/` shows nothing in Landing (also see the companion audit, §2).
- [doc]: `VesselComponent.SetActionGroup(KSPActionGroup, bool)`, `GetActionGroupState(KSPActionGroup)` and `KSPActionGroup.Gear` exist. I did not check in game whether `Gear` deploys KSP2 legs.
- *Fix*: when entering TouchDown, or at `t_impact − max(5 s, deploy time + 2 s)`, call `SetActionGroup(Gear, true)` if `GetActionGroupState(Gear) != On`. Nothing to do if there is no gear or it is already deployed. Retracting is not needed after a landing. An option to deploy on Brake would cover descents that are too fast.

### 2.6 Atmospheric bodies

**F21. The active landing path has no drag model; AtmosphericPredictor is not wired. Important.**
- *Code* [code]: `AtmosphericPredictor` declares itself "NOT WIRED" (`:24`), and no call site exists (grep). On Kerbin, Duna, Eve and Laythe, `compute_real_collision` uses the **drag-free** conic. It therefore predicts an earlier impact than reality, and a `speed_collision` equal to the orbital speed instead of the terminal speed. The result is an early start and wasted fuel; the Pause/re-predict loop cleans up behind it.
- Drag does help braking, which is conservative. The thrust used is `MaxThrustOutputAtm` at the **current** pressure (`BurndV.cs:54-58`). On Eve, a vacuum engine loses a large fraction of its thrust between altitude and the surface. `a_max` measured high up overestimates `a` near the ground, which makes the start late (F1) [inferred].
- No chute logic (deploy, safe deploy speed) and no heating logic.
- Issues in AtmosphericPredictor itself [code]:
  1. Drag uses the **inertial** velocity, ignoring atmosphere co-rotation (`:28-32, 245-247`). That is 175 m/s at Kerbin's equator, comparable to terminal speeds at low altitude.
  2. Semi-implicit Euler with a 1 s step, and the terrain test is not refined. The impact is off by up to `v·1 s` (~200-300 m) (`:204-219, 255-257`).
  3. A fixed angle of 90° off retrograde (`:54`).
  4. No thrust term. It can predict a coast, but not plan a braking burn.
  5. Stale comments: `LandingPilot.calibrated_k_retrograde_by_vessel` (`:265`) and "DeorbitBurn.cs turns this on" (`:302`) refer to code that does not exist.
- *Fix*:
  - Drag against `v − ω×r`.
  - RK4 at 0.5-1 s, with a final bisection at terrain crossing.
  - Plug it into the same forward simulator as F1, with thrust and drag.
  - Chute logic: deploy below a safe dynamic-pressure or Mach threshold, then a propulsive burn computed with chute drag.
  - Thrust as a function of the pressure along the predicted path: `MaxThrustOutputAtm(p(h))`.

### 2.7 Numerical robustness and execution

**F22. `full_dv` sums every engine with no ignition or fuel filter. Critical if confirmed.**
- *Code* [code]: `Compute_Thrust` adds `MaxThrustOutputVac` or `MaxThrustOutputAtm` for **every** `EngineInfo` entry (`BurndV.cs:79-91`). The `Engine_Running` helper (`:46-49`) is never called (grep). [doc]: `EngineInfo` = "engine info records tracked by the engine stage set", which does not say whether inactive stages are included.
- If unstaged engines are included (for example an ascent engine above the descent stage, or a not-yet-activated stage), `a_max` is overestimated. That means a late start (F1), insufficient `min_throttle`, and the P gain too low. The same applies to a fuel-starved engine [inferred].
- *Test*: on a two-stage lander, compare `full_dv·m` with the stock TWR readout.
- *Fix*: keep only `Engine.EngineIgnited && !IsPropellantStarved` (or the active stage), and fall back to `actual_thrust/throttle` while burning.

**F23. Division by `full_dv = 0` gives NaN throttle. Minor.**
- *Code* (`LandingPilot.cs:280`, `TouchDown.cs:254, 261`) [code]: with no thrust available (engines not activated, air-breathing in vacuum `→ 0`, `BurndV.cs:59-62`), we get `x/0 = ±∞`, then `∞ − ∞ = NaN`. `Mathf.Clamp(NaN)` returns NaN [inferred from Unity's implementation], and `SetThrottle(NaN)` follows. `burn_duration = ∞` gives `startBurn_UT = −∞`, clamped to now, i.e. an immediate brake.
- *Fix*: if `full_dv < 1e-3`, show a status "no available thrust", set throttle 0, and do not start.

**F24. No fuel or Δv check, and no abort. Important.**
- *Code* [code]: no check of `VesselDeltaV.TotalDeltaVActual`/`StageInfo` against what the descent needs.
- Auto-staging, if enabled (`StagingPilot.cs:14`, off by default), freezes **all** pilots for 1 s (`:16`, `K2D2_Plugin.cs:268-274`). During that freeze the throttle stays at its last value.
- *Fix*: before Brake, compare the remaining Δv with the simulated Δv (F1) plus a margin, and warn or refuse. During TouchDown, do not freeze the descent controller during a staging event (or zero the throttle cleanly).

**F25. State machine: failures fall through to the next phase. Minor (Important for "Couldn't find a deorbit window").**
- *Code* [code]: `SingleExecuteController.finished` only means "the controller has finished" (`ExecuteController.cs:15-24`). `DeorbitBurn` sets `finished = true` when it fails (`:124-129`), and so does `Circularize` with TooHigh (`:104-110`). `LandingPilot.nextMode()` then moves on to the next phase.
- After a failed deorbit, the vessel goes to MCC → Pause → QuickWarp with no collision. `startBurn_UT` has been clamped to "now" (`LandingPilot.cs:372-374`), so the sequence runs straight on to Brake. Without precision that is a full retrograde burn in orbit, as soon as the velocity points slightly downward (`TouchDown.cs:294`) [inferred].
- *Fix*: add a `Failed` state (or `finished` + `success` flag) that stops the pilot with a message.

**F26. Time step, warp and cost. Minor.**
- *Code* [code]: the controllers run in `Update`. The rate limits (throttle 5/s, correction 8°/s) use `Time.deltaTime`, consistent with the frame rate. Warp is cancelled as soon as a burn is needed (`TouchDown.cs:300, 877-878`).
- The collision search plus `PredictUTAtAltitude` can make up to ~450 terrain queries per frame (`LandingPilot.cs:408`, `LandingTargeting.cs:325`). 26w40a says the sampler is "cheap enough to do per query" [doc], but I did not measure the per-frame cost.
- *Fix*: compute the prediction at 5-10 Hz, or incrementally from the previous solution.

---

## 3. What a good lander has that this one lacks

| Function | MechJeb / literature | Here |
|---|---|---|
| Prediction of the **powered** landing point (simulating the braking burn, varying g, mass, terrain) | MechJeb landing predictor | Ballistic only (F1, F11) |
| A speed envelope derived from TWR, `v ≤ sqrt(2(ηa−g)h)` | Standard (MechJeb "suicide burn" / vertical-speed profile) | Linear profile independent of TWR (F5) |
| Body-fixed frame at the future time | Standard | Current rotation + 1.307 fudge (F7) |
| Final vertical phase: horizontal nulling, upright attitude, gear, cutoff on contact | MechJeb final descent | Retrograde to contact, no gear, heuristic cutoff (F18-F20) |
| Terminal precision guidance | ZEM/ZEV `a = 6·ZEM/t_go² − 2·ZEV/t_go`, Apollo polynomial guidance (quadratic in time), G-FOLD (convex, fuel-optimal) | Retrograde tilts with fixed caps, chasing the ballistic point (F11, F12) |
| Targeting with 2 degrees of freedom (time + normal, or prograde + normal) solved by Newton | Standard | 36-sample grid search + one parabola (F13, F17) |
| Checks: available Δv, low TWR, no thrust | MechJeb warns | None (F22-F24) |
| Atmosphere: co-rotating drag, chutes, pressure-dependent thrust | MechJeb (drag simulation) | Not wired (F21) |

A suggested path, in order of benefit/cost:
1. **A fixed-step forward simulator (0.1-0.5 s) of the braking burn**: retrograde thrust at `η·a_max(m)`, varying g, terrain sampled in the body-fixed frame at `t`. It returns the stop point and stop altitude. On its own it fixes F1, F2, F7 (if the frame is right), F8 and F11, and it supplies the Δv for F24.
2. **A TWR-derived envelope** (F5) and a **final phase** (F18-F20).
3. Optional: ZEM/ZEV guidance on the powered point to replace the fixed-cap tilts (F12). G-FOLD is overkill for KSP, but ZEM/ZEV or a quadratic polynomial (Apollo) is a few dozen lines.

---

## 4. Ranked top 10

1. **F5, Critical.** The speed profile `0.05·h + 2.5` is not derived from TWR. Above `h* = 800(a−g)` it allows speeds that cannot be stopped. TouchDown crashes at TWR ≤ 1.5 (Mun 25-41 m/s, sim), and with `touch_down_ratio = 3` (70-124 m/s).
2. **F1, Critical.** The non-precision brake start, `t_impact − V/a`, ignores gravity and approach angle. It crashes on shallow approaches: Mun Pe −2 km, TWR 2, 59 m/s vertical and 247 m/s horizontal (sim).
3. **F22, Critical if confirmed.** `full_dv` sums every engine's maximum thrust with no ignition or fuel filter. If unstaged engines count, `a_max` is overestimated, which means braking late and too weak.
4. **F11, Important.** Deorbit and steering aim at the ballistic impact. The real stop point is 126-211 km short (Mun, sim). Steering recovers it at +100-140 m/s (+15-20 %), and fails at TWR 1.3 (18-21 m/s).
5. **F7, Important.** Future terrain and lat/lon are evaluated with the current rotation. The steering bias is `ωR·Δt` (Mun: 2.7 km at 300 s). The 1.307 fudge cannot hold elsewhere, and terrain is read at the wrong place (×38 at a grazing approach).
6. **F18 + F20, Important.** No gear deployment, no `Landed` detection. The cutoff at `alt < 5 && sink < 1` can drop the vessel (Tylo 8.9 m/s), and SAS stays on retrograde.
7. **F12 + F19, Important.** Steering authority persists down to contact (40° at 750 m), and the attitude stays retrograde with no horizontal nulling, which risks tipping. The 30° gate cuts the throttle in the last seconds.
8. **F14 + F15, Important.** Deorbit/MCC/Circularize burns start at `node.Time` instead of `−T/2`. The deorbit to datum −2 km gives a 1.5° approach: a long low-altitude cruise and extreme sensitivity to terrain.
9. **F8 + F9, Important.** No terrain-ahead check along the powered path, a coarse 20-60 s step that can skip a ridge, and radar altitude only directly below. The constants (1.307, 2000 m, 8000 m) need re-validating after 26w40a (terrain sampler ~225 m closer to the drawn ground on Kerbin).
10. **F21 + F24, Important.** Atmospheres: drag-free conic, AtmosphericPredictor not wired (and itself flawed: inertial drag, 1 s Euler, no thrust), no chutes, pressure-dependent thrust ignored. No Δv check, no failure state (F25).

## 5. Verdict

The descent is a closed loop on surface speed with gravity feed-forward. It is robust in the common case of a reasonably powerful lander (local TWR ≥ 2-3) on a steep approach, and the precision steering can absorb even very large errors (sim). But the two computations that decide survival, the burn start and the speed envelope, are heuristics that are not derived from physics: no gravity, no approach angle, no TWR-dependent envelope. They crash low-TWR landers and shallow approaches. The precision chain aims at the wrong point (the ballistic impact) with uncorrected rotation errors, papered over by an empirical factor. It works through costly corrections rather than through planning.

---

## Appendix A: simulator (estimates, not the game)

Model:
- planar, two-body, `μ` and `R` from stock values (Mun R = 200 km, μ = 6.5138e10; Minmus R = 60 km, μ = 1.7658e9; Ike R = 130 km, μ = 1.8568e10; Tylo R = 600 km, μ = 2.82528e12);
- **spherical body** (no relief), **no rotation**, **instantaneous** attitude;
- Isp 320 s; TWR defined at the surface;
- step 0.02 s, explicit Euler for the powered phase, Verlet at 0.5 s for the conics.

Reproduced from the code:
- the throttle law, `min_throttle`, the 5/s slew and the "speed down" gate (TouchDown);
- the profile (`LandingSettings.compute_limit_speed`);
- the start `t_impact − V/a − burn_before`, and in precision mode `crossing(1500 + 8000 m) − V/a`;
- the Brake → < 50 m/s → Pause/re-predict cycle;
- the `alt < 5 && sink < 1` stop;
- for the steered runs, the along-track (pitch) part of `ComputeSteeredDirection`: 80°/10° caps, linear taper, `descent_margin_factor` at 1.5 m/s, `(err/50)²` authority, 8°/s, on an analytic ballistic impact.

Not modelled: heading, RCS, terrain, rotation, SAS lag, `touch_down_max_angle`, staging.

The 1D TouchDown core, enough to reproduce §2.2:

```python
def touchdown_1d(g, twr, h, v, ratio=0.5, vtd=2.5, dt=0.02, isp=320, slew=5.0):
    m, T, thr, cut = 1.0, twr * g, 0.0, False          # v > 0 = descending
    while h > 0:
        a = T / m
        if not cut:
            if h < 5 and v < 1: cut = True              # LandingPilot.cs:597
            vmax = h * ratio / 10 + vtd                 # LandingSettings.cs:205
            want = min(1, max(0, (v - vmax) / a + g / a)) if v >= 0 else 0
            thr += max(-slew * dt, min(slew * dt, want - thr))
        else:
            thr = 0
        v += (g - thr * a) * dt; h -= v * dt; m -= thr * T / (isp * 9.80665) * dt
    return v                                            # touchdown speed
```

## Appendix B: what was and was not verified

- **Verified by reading** [code]: everything tagged [code], in particular:
  - the formulas F1, F4, F5, F6;
  - the absence of rotation correction in `compute_real_collision`;
  - the non-use of `ReprojectToNow`, `Engine_Running`, `GameNormalSign` (Landing path), `VesselComponent.Landed` and the Gear action group;
  - the start at `node.Time` in the landing phases;
  - the fact that `AtmosphericPredictor` is not wired.
- **From the docs** [doc]: `celestialFrame` vs `bodyFrame`; the 26w40a sampler and its ~225 m on Kerbin; the existence of `SetActionGroup`/`KSPActionGroup.Gear`/`Landed`/`Situation`; the wording of `EngineInfo`.
- **Not verified** (needs an in-game test):
  - whether `GetAltitudeFromTerrain` and the HUD ground altitude use the 26w40a sampler;
  - the contents of `EngineInfo` (inactive stages?);
  - the sign of the KSP2 normal;
  - the axes of `VesselVehicle.Up.coordinateSystem` used for `current_falling_speed`;
  - KSP2 behaviour with a NaN throttle;
  - whether `Gear` deploys legs;
  - every absolute number tagged [sim], which come from a simplified model (no terrain, no rotation, perfect attitude).
- Body constants (radius, μ, rotation) are the stock KSP1/KSP2 values, not re-checked in Redux.
