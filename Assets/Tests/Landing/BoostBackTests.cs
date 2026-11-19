using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SkySpaceAgency;
using SkySpaceAgency.Landing;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// IanMealworm's boost-back (K2-D2 1.4.0), carried for the K2-D2 flavour and tested here in ours
// (ModIdentity.IsK2D2 false): the start rule in both flavours, the burn sizing and direction search
// on a fake body (also turned), the entry steering on a fake prediction, the heat limit, and the
// settings keys, verbatim from upstream.
public class BoostBackTests
{
    const double Mu = 3.5316e12;       // Kerbin
    const double R = 600000;
    const double KerbinSpin = 2 * Math.PI / 21549.425;

    // A spherical body turning about `pole`, prime meridian along `meridian` (any directions: the
    // whole frame may be turned).
    class FakeBody : IImpactBody
    {
        readonly Vector3d pole, meridian, east;
        readonly double spin_rate;

        public FakeBody(Vector3d pole, Vector3d meridian, double spin_rate)
        {
            this.pole = pole.normalized;
            this.meridian = meridian.normalized;
            east = Vector3d.Cross(this.pole, this.meridian);
            this.spin_rate = spin_rate;
        }

        public double Mu => BoostBackTests.Mu;
        public double Radius => R;
        public double AtmosphereDepth => 70000;
        public Vector3d Spin => pole * spin_rate;

        public void LatLon(Vector3d body_fixed, out double latitude, out double longitude)
        {
            Vector3d d = body_fixed.normalized;
            latitude = Math.Asin(Vector3d.Dot(d, pole)) * 180 / Math.PI;
            longitude = Math.Atan2(Vector3d.Dot(d, east), Vector3d.Dot(d, meridian)) * 180 / Math.PI;
        }

        public Vector3d SurfaceUp(double latitude, double longitude)
        {
            double la = latitude * Math.PI / 180, lo = longitude * Math.PI / 180;
            return (meridian * Math.Cos(lo) + east * Math.Sin(lo)) * Math.Cos(la) + pole * Math.Sin(la);
        }

        public Vector3d East(double longitude)
        {
            double lo = longitude * Math.PI / 180;
            return -meridian * Math.Sin(lo) + east * Math.Cos(lo);
        }

        public double TerrainRadius(double latitude, double longitude) => R;
    }

    static Vector3d Rotate(Vector3d v, Vector3d axis, double angle)
    {
        axis = axis.normalized;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + Vector3d.Cross(axis, v) * s + axis * (Vector3d.Dot(axis, v) * (1 - c));
    }

    static readonly Vector3d TurnAxis = new Vector3d(0.3, -0.8, 0.52);
    const double TurnAngle = 2.1;
    static Vector3d Turn(Vector3d v) => Rotate(v, TurnAxis, TurnAngle);

    static FakeBody Kerbin(double spin, bool turned = false) => turned
        ? new FakeBody(Turn(new Vector3d(0, 0, 1)), Turn(new Vector3d(1, 0, 0)), spin)
        : new FakeBody(new Vector3d(0, 0, 1), new Vector3d(1, 0, 0), spin);

    // A booster just after separation (K2-D2's replays: 33-38 km up, 620-660 m/s downrange and
    // 480-590 m/s up), 4° (42 km) east of the pad, heading east. Velocity inertial: the surface
    // velocity plus the body's turn.
    static void Separation(FakeBody body, out Vector3d r, out Vector3d v)
    {
        const double lon = 4;
        Vector3d up = body.SurfaceUp(0, lon);
        r = up * (R + 36000);
        v = body.East(lon) * 640 + up * 520 + Vector3d.Cross(body.Spin, r);
    }

    static double MissAfter(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, Vector3d dv)
    {
        Assert.IsTrue(BoostBackBurn.Miss(model, site, r, v, 0, dv, out double a, out double b), "the coast comes down");
        return Math.Sqrt(a * a + b * b);
    }

    // ---- Start rule, both flavours ----

    static BoostBackState GoodState() => new BoostBackState
    {
        atmospheric_profile = true,
        method = LandingMethod.Propulsive,
        precision = true,
        landed = false,
        staged = false,
        suborbital = true,
        altitude_m = 36000,
        atmosphere_depth_m = 70000,
        redux_settings_problem = null,
    };

    static IEnumerable<BoostBackState> AllStates()
    {
        foreach (bool atmo in new[] { false, true })
        foreach (LandingMethod method in new[] { LandingMethod.Propulsive, LandingMethod.Parachute })
        foreach (bool precision in new[] { false, true })
        foreach (bool landed in new[] { false, true })
        foreach (bool suborbital in new[] { false, true })
        foreach (double altitude in new[] { 36000.0, 90000.0 })
        foreach (string problem in new[] { null, "Set Assumed attitude to Current attitude." })
            yield return new BoostBackState
            {
                atmospheric_profile = atmo, method = method, precision = precision, landed = landed, staged = false,
                suborbital = suborbital, altitude_m = altitude, atmosphere_depth_m = 70000, redux_settings_problem = problem,
            };
    }

    [Test]
    public void BoostBack_IsOnInTheK2D2FlavourOnly()
    {
        // K2-D2 keeps its boost-back; the sibling flavour of this code never flies it.
        Assert.AreEqual(ModIdentity.ModId == "K2D2", ModIdentity.IsK2D2);
    }

    [Test]
    public void NonK2D2Flavour_NeverBoostsBack()
    {
        int n = 0;
        foreach (var s in AllStates())
        {
            n++;
            Assert.AreEqual(BoostBackDecision.NotBoostBack, BoostBackStart.Check(s, false, out string reason));
            Assert.IsNull(reason);
            Assert.IsFalse(BoostBackStart.Requested(false, s.atmospheric_profile, s.method, s.precision));
        }
        Assert.AreEqual(128, n);
        // And the flavour this mod compiles with.
        Assert.AreEqual(ModIdentity.IsK2D2 ? BoostBackDecision.BoostBack : BoostBackDecision.NotBoostBack,
            BoostBackStart.Check(GoodState(), ModIdentity.IsK2D2, out _));
    }

    [Test]
    public void K2D2Flavour_SuborbitalInTheAir_BoostsBack()
    {
        Assert.AreEqual(BoostBackDecision.BoostBack, BoostBackStart.Check(GoodState(), true, out string reason));
        Assert.IsNull(reason);
    }

    [Test]
    public void K2D2Flavour_ParachuteModeOrNoPrecisionOrVacuum_IsNotABoostBack()
    {
        var s = GoodState();
        s.method = LandingMethod.Parachute;
        Assert.AreEqual(BoostBackDecision.NotBoostBack, BoostBackStart.Check(s, true, out _), "Parachute: our parachute return");
        s = GoodState();
        s.precision = false;
        Assert.AreEqual(BoostBackDecision.NotBoostBack, BoostBackStart.Check(s, true, out _), "no precision: a regular landing");
        s = GoodState();
        s.atmospheric_profile = false;
        Assert.AreEqual(BoostBackDecision.NotBoostBack, BoostBackStart.Check(s, true, out _), "vacuum profile: our precision");
    }

    [Test]
    public void K2D2Flavour_FromOrbit_RefusedSayingSo()
    {
        var s = GoodState();
        s.suborbital = false;
        s.altitude_m = 90000;
        Assert.AreEqual(BoostBackDecision.Refused, BoostBackStart.Check(s, true, out string reason));
        Assert.AreEqual(BoostBackStart.FromOrbitMessage, reason);
        StringAssert.Contains("in orbit", reason);
    }

    [Test]
    public void K2D2Flavour_SuborbitalAboveTheAtmosphere_RefusedWithTheHeights()
    {
        var s = GoodState();
        s.altitude_m = 85000;
        Assert.AreEqual(BoostBackDecision.Refused, BoostBackStart.Check(s, true, out string reason));
        StringAssert.Contains("above the atmosphere", reason);
        StringAssert.Contains("85.0 km", reason);
        StringAssert.Contains("70.0 km", reason);
    }

    [Test]
    public void K2D2Flavour_OnThePad_Refused()
    {
        var s = GoodState();
        s.landed = true;
        s.altitude_m = 70;
        Assert.AreEqual(BoostBackDecision.Refused, BoostBackStart.Check(s, true, out string reason));
        Assert.AreEqual(BoostBackStart.LandedMessage, reason);
    }

    [Test]
    public void K2D2Flavour_ReduxSettingsWrong_RefusedNamingTheSetting()
    {
        var s = GoodState();
        s.redux_settings_problem = "Set Redux's Assumed attitude (impact prediction) to Current attitude.";
        Assert.AreEqual(BoostBackDecision.Refused, BoostBackStart.Check(s, true, out string reason));
        StringAssert.Contains("Redux's impact prediction settings", reason);
        StringAssert.Contains("Assumed attitude", reason);
    }

    [Test]
    public void K2D2Flavour_ChosenStage_Refused()
    {
        var s = GoodState();
        s.staged = true;
        Assert.AreEqual(BoostBackDecision.Refused, BoostBackStart.Check(s, true, out string reason));
        Assert.AreEqual(BoostBackStart.StagedMessage, reason);
    }

    [Test]
    public void PrecisionToggle_PropulsiveOnlyInTheK2D2Flavour()
    {
        Assert.IsTrue(LandingSettings.PrecisionAllowed(LandingMethod.Parachute));
        Assert.AreEqual(ModIdentity.IsK2D2, LandingSettings.PrecisionAllowed(LandingMethod.Propulsive));
    }

    // ---- Burn sizing and direction ----

    [Test]
    public void TargetSite_Offset_IsTheDistanceAlongTheGround()
    {
        var model = new ImpactModel(Kerbin(0), 0);
        var site = new LandingTargeting.TargetSite(model, 0, 0, 0);
        Assert.AreEqual(R, site.radius, 1e-6);
        site.Offset(model.SurfaceUp(0, 1) * R, out double a, out double b);
        Assert.AreEqual(R * Math.PI / 180, Math.Sqrt(a * a + b * b), 1e-3);
    }

    [Test]
    public void GroundTrackBack_PointsBackAlongTheGround()
    {
        var body = Kerbin(0);
        Separation(body, out Vector3d r, out Vector3d v);
        Assert.IsTrue(BoostBackBurn.GroundTrackBack(r, v, out Vector3d back));
        Assert.AreEqual(-1, Vector3d.Dot(back, body.East(4)), 1e-9);
        Assert.IsFalse(BoostBackBurn.GroundTrackBack(r, r.normalized * 300, out _), "straight up: no ground track");
    }

    static void SolveBack(FakeBody body, double target_lat, out ImpactModel model, out LandingTargeting.TargetSite site,
        out Vector3d r, out Vector3d v, out Vector3d dv, out double pitch)
    {
        model = new ImpactModel(body, 0);
        site = new LandingTargeting.TargetSite(model, target_lat, 0, 0);
        Separation(body, out r, out v);
        Assert.IsTrue(BoostBackBurn.GroundTrackBack(r, v, out Vector3d back));
        double start = (v - r.normalized * Vector3d.Dot(v, r.normalized)).magnitude;
        Assert.IsTrue(BoostBackBurn.SolveByDirection(model, site, r, v, 0, back, double.NaN, start, out dv, out pitch), "solved");
    }

    [Test]
    public void SolveByDirection_DownrangeMiss_LandsOnThePad()
    {
        SolveBack(Kerbin(0), 0, out var model, out var site, out var r, out var v, out var dv, out double pitch);
        Assert.Greater(MissAfter(model, site, r, v, Vector3d.zero), 40000, "without the burn it lands far beyond the pad");
        Assert.Less(MissAfter(model, site, r, v, dv), BoostBackBurn.solved_m, "with it, on the pad");
        Assert.Greater(dv.magnitude, 600);
        Assert.Less(dv.magnitude, 1500);
        Assert.Less(Vector3d.Dot(dv, Kerbin(0).East(4)), 0, "the burn points back");
        Assert.GreaterOrEqual(pitch, BoostBackBurn.min_pitch_deg);
        Assert.LessOrEqual(pitch, BoostBackBurn.max_pitch_deg);
    }

    [Test]
    public void SolveByDirection_KeepsTheSmallestBurn()
    {
        var body = Kerbin(0);
        SolveBack(body, 0, out var model, out var site, out var r, out var v, out var dv, out _);
        BoostBackBurn.GroundTrackBack(r, v, out Vector3d back);
        Vector3d side = Vector3d.Cross(r, v).normalized;
        double start = (v - r.normalized * Vector3d.Dot(v, r.normalized)).magnitude;
        Assert.IsTrue(BoostBackBurn.Size(model, site, r, v, 0, back, side, start, out Vector3d flat), "straight back, level");
        Assert.LessOrEqual(dv.magnitude, flat.magnitude + 1e-6);
    }

    [Test]
    public void SolveByDirection_CrossrangeMiss_LandsOnTheTarget()
    {
        // The target 0.2° (2 km) north of the ground track.
        SolveBack(Kerbin(0), 0.2, out var model, out var site, out var r, out var v, out var dv, out _);
        Assert.Less(MissAfter(model, site, r, v, dv), BoostBackBurn.solved_m);
        Vector3d north = new Vector3d(0, 0, 1);
        Assert.Greater(Vector3d.Dot(dv, north), 0, "part of the burn goes north");
    }

    [Test]
    public void SolveByDirection_TurningBody_LandsOnTheTarget()
    {
        SolveBack(Kerbin(KerbinSpin), 0.1, out var model, out var site, out var r, out var v, out var dv, out _);
        Assert.Less(MissAfter(model, site, r, v, dv), BoostBackBurn.solved_m);
    }

    // Lesson 2026-11-18: a frame-sensitive function is tested in a turned frame.
    [Test]
    public void SolveByDirection_TurnedFrame_SameBurnTurned()
    {
        SolveBack(Kerbin(KerbinSpin), 0.1, out _, out _, out _, out _, out Vector3d dv, out double pitch);
        SolveBack(Kerbin(KerbinSpin, turned: true), 0.1, out var model, out var site, out var r, out var v, out Vector3d dv_turned,
            out double pitch_turned);
        // The site's ground axes are not turned with the frame, so Newton takes another path to
        // the same answer (within solved_m): close, not identical.
        Assert.AreEqual(pitch, pitch_turned, BoostBackBurn.pitch_step_deg + 1e-9);
        Assert.AreEqual(dv.magnitude, dv_turned.magnitude, 5.0, "the same size");
        Assert.Less(Vector3d.Angle(Turn(dv), dv_turned), 6.0, "the same direction, turned");
        Assert.Less(MissAfter(model, site, r, v, dv_turned), BoostBackBurn.solved_m);
    }

    [Test]
    public void SolveByDirection_LaterSolves_StayNearTheLastAnswer()
    {
        SolveBack(Kerbin(0), 0, out var model, out var site, out var r, out var v, out var dv, out double pitch);
        BoostBackBurn.GroundTrackBack(r, v, out Vector3d back);
        Assert.IsTrue(BoostBackBurn.SolveByDirection(model, site, r, v, 0, back, pitch, Vector3d.Dot(dv, back), out Vector3d again,
            out double pitch_again));
        Assert.LessOrEqual(Math.Abs(pitch_again - pitch), BoostBackBurn.pitch_window_deg);
        Assert.Less(MissAfter(model, site, r, v, again), BoostBackBurn.solved_m);
    }

    [Test]
    public void SolveDirect_CancelsWhatIsLeft()
    {
        SolveBack(Kerbin(KerbinSpin), 0.1, out var model, out var site, out var r, out var v, out var dv, out _);
        // 97 % of the burn flown: a few hundred metres left to fix.
        Vector3d v_now = v + dv * 0.97;
        double before = MissAfter(model, site, r, v_now, Vector3d.zero);
        Assert.Greater(before, 200);
        Assert.IsTrue(BoostBackBurn.SolveDirect(model, site, r, v_now, 0, out Vector3d rest));
        Assert.Less(rest.magnitude, 0.1 * dv.magnitude);
        Assert.Less(MissAfter(model, site, r, v_now, rest), 0.1 * before);
    }

    [Test]
    public void Throttle_PoweredFlipBurnsWhileTurning_UnpoweredWaits()
    {
        const double accel = 20, left = 500;
        // Pointing the wrong way: powered keeps the gimbals working, unpowered waits.
        Assert.AreEqual(BoostBackBurn.flip_throttle, BoostBackBurn.Throttle(true, 150, left, accel), 1e-9);
        Assert.AreEqual(0, BoostBackBurn.Throttle(false, 150, left, accel));
        Assert.AreEqual(0, BoostBackBurn.Throttle(false, BoostBackBurn.pause_beyond_deg + 1, left, accel));
        // Lined up: full thrust either way.
        Assert.AreEqual(1, BoostBackBurn.Throttle(true, 0, left, accel), 1e-9);
        Assert.AreEqual(1, BoostBackBurn.Throttle(false, 3, left, accel), 1e-9);
        // Powered: grows as the nose comes round.
        Assert.Greater(BoostBackBurn.Throttle(true, 30, left, accel), BoostBackBurn.Throttle(true, 60, left, accel));
        // Taper over the last taper_s of full thrust, never under min_throttle.
        Assert.AreEqual(0.5, BoostBackBurn.Throttle(false, 0, accel * BoostBackBurn.taper_s / 2, accel), 1e-9);
        Assert.AreEqual(BoostBackBurn.min_throttle, BoostBackBurn.Throttle(false, 0, 0.01, accel), 1e-9);
        // Unpowered ignition rule.
        Assert.IsTrue(BoostBackBurn.LinedUp(4.9));
        Assert.IsFalse(BoostBackBurn.LinedUp(5.1));
    }

    [Test]
    public void BudgetProblem_RefusesShortDeltaV_WithTheNumbers()
    {
        Assert.IsNull(BoostBackBurn.BudgetProblem(800, 2000));
        Assert.IsNull(BoostBackBurn.BudgetProblem(800, 0), "no figure from the game: not refused");
        string problem = BoostBackBurn.BudgetProblem(900, 1200);
        Assert.IsNotNull(problem);
        StringAssert.Contains("900 m/s", problem);
        StringAssert.Contains("600 m/s", problem);
        StringAssert.Contains("1200 m/s", problem);
    }

    // ---- Entry steering, on a fake prediction ----

    // Redux's landing point moves k metres along per unit of vertical lift, and k across per unit
    // of side lift (the same lift, turned).
    // Done within tolerance_m (steps under min_lift_change are not made: k x 0.03 is the finest).
    static int SteerUntilOnTarget(EntryLiftSteering steering, double along0, double across0, double k, int max_steps,
        out double along, out double across, double tolerance_m = 200)
    {
        double now = 0;
        along = along0;
        across = across0;
        for (int i = 0; i < max_steps; i++)
        {
            along = along0 + k * steering.vertical_lift;
            across = across0 + k * steering.side_lift;
            if (Math.Abs(along) < tolerance_m && Math.Abs(across) < tolerance_m)
                return i;
            steering.Steer(along, across, 0, now, 10);
            now += 6;
        }
        return max_steps;
    }

    [TestCase(3000)]
    [TestCase(10000)]
    [TestCase(40000)]
    public void EntrySteering_ConvergesOnTheTarget(double k)
    {
        var steering = new EntryLiftSteering();
        double tolerance = 100 + 1.2 * EntryLiftSteering.min_lift_change * k;
        int steps = SteerUntilOnTarget(steering, 0.6 * k, -0.3 * k, k, 40, out double along, out double across, tolerance);
        Assert.Less(steps, 40, $"still {along:0} m along, {across:0} m across");
        Assert.LessOrEqual(steering.lift_scale, 1);
    }

    [Test]
    public void EntrySteering_MoreLiftUpWhenShort_DownWhenLong()
    {
        var steering = new EntryLiftSteering();
        steering.Steer(-3000, 0, 0, 0, 10);
        Assert.Greater(steering.vertical_lift, 0, "short: stretch the flight");
        steering = new EntryLiftSteering();
        steering.Steer(3000, 0, 0, 0, 10);
        Assert.Less(steering.vertical_lift, 0, "long: shorten it");
        steering = new EntryLiftSteering();
        steering.Steer(0, 3000, 0, 0, 10);
        Assert.Less(steering.side_lift, 0, "landing toward the side axis: push the other way");
    }

    [Test]
    public void EntrySteering_AimsBeyondTheTargetByTheBurnShortfall()
    {
        // Redux lands 2 km beyond, the landing burn will make the flight 2 km shorter: on target.
        var steering = new EntryLiftSteering();
        var step = steering.Steer(2000, 0, 2000, 0, 10);
        Assert.AreEqual(0, step.along, 1e-9);
        Assert.IsFalse(step.changed);
    }

    [Test]
    public void EntrySteering_Secant_MeasuresTheRangePerLift()
    {
        var steering = new EntryLiftSteering();
        SteerUntilOnTarget(steering, 0.8 * 40000, 0, 40000, 40, out _, out _);
        Assert.Greater(steering.range_per_lift_m, 20000, "moved from 10 km toward the real 40 km");
    }

    [Test]
    public void EntrySteering_OutOfReach_BoostsTheAngleOfAttack()
    {
        var steering = new EntryLiftSteering();
        double now = 0;
        for (int i = 0; i < 20; i++, now += 6)
            steering.Steer(5000 + 1000 * steering.vertical_lift, 0, 0, now, 10);
        Assert.AreEqual(EntryLiftSteering.max_angle_of_attack_deg - 10, steering.aoa_boost_deg, 1e-9);
        Assert.AreEqual(EntryLiftSteering.max_angle_of_attack_deg, steering.WantedAoa(10), 1e-9);
        Assert.IsTrue(steering.overshoot_out_of_reach, "lift fully down and still long");
    }

    [Test]
    public void EntrySteering_LiftDirection_FlipsAfterThreeAgreeingMeasurements()
    {
        var steering = new EntryLiftSteering();
        Assert.AreEqual(180, steering.tilt_offset_deg);
        // Too little angle of attack: not measured.
        Assert.IsFalse(steering.CheckLiftDirection(-1, 0, 5, 2, out _));
        // Lift measured down while aiming it up (bank 0): the vessel lifts the other way.
        Assert.IsFalse(steering.CheckLiftDirection(-1, 0, 5, 10, out _));
        Assert.IsFalse(steering.CheckLiftDirection(-1, 0.1, 5, 10, out _));
        Assert.IsTrue(steering.CheckLiftDirection(-1, -0.1, 5, 10, out double bank));
        Assert.AreEqual(180, Math.Abs(bank), 10);
        Assert.AreEqual(0, steering.tilt_offset_deg, 1e-9);
        Assert.IsTrue(steering.lift_direction_checked);
        Assert.IsFalse(steering.CheckLiftDirection(-1, 0, 5, 10, out _), "checked once");

        var same = new EntryLiftSteering();
        for (int i = 0; i < 3; i++)
            same.CheckLiftDirection(1, 0, 5, 10, out _);
        Assert.AreEqual(180, same.tilt_offset_deg, "as expected: unchanged");
        Assert.IsTrue(same.lift_direction_checked);
    }

    [Test]
    public void SplitMiss_AlongTheFlowAndTowardTheSide()
    {
        var target = new Vector3d(1, 0, 0);
        var flow = new Vector3d(0, 1, 0);
        var side = new Vector3d(0, 0, 1);
        // Predicted a little along +y (beyond) and toward +z (the side).
        var predicted = new Vector3d(1, 0.001, 0.0005).normalized;
        AtmoEntryGuidance.SplitMiss(target, predicted, R, flow, side, out double along, out double across);
        Assert.AreEqual(600, along, 1);
        Assert.AreEqual(300, across, 1);
        AtmoEntryGuidance.SplitMiss(target, predicted, R, flow, -side, out _, out double across_flipped);
        Assert.AreEqual(-300, across_flipped, 1);
    }

    // ---- Entry heat limit ----

    static PartHeat[] Parts(double fraction, bool shield = false) =>
        new[] { new PartHeat("engine", "engine", fraction * 2000, 2000, shield), new PartHeat("tank", "tank", 300, 2000, false) };

    [Test]
    public void EntryHeatLimit_CoolParts_KeepTheLimit()
    {
        var heat = new EntryHeatLimit();
        heat.Reset(25);
        Assert.AreEqual(25, heat.Update(Parts(0.5), 0, 10, 25));
        Assert.AreEqual(25, heat.Update(Parts(0.5), 1, 10, 25));
        Assert.AreEqual(0.5, heat.hottest_fraction, 1e-9);
        Assert.AreEqual("engine", heat.hottest_part);
    }

    [Test]
    public void EntryHeatLimit_HeatingFast_StepsDownFromWhatIsFlown()
    {
        var heat = new EntryHeatLimit();
        heat.Reset(25);
        heat.Update(Parts(0.5), 0, 10, 25);
        // +4 % of the maximum a second: 54 % now, 94 % expected in 10 s.
        double limit = heat.Update(Parts(0.54), 1, 10, 25);
        Assert.AreEqual(0.94, heat.hottest_predicted, 1e-9);
        Assert.AreEqual(10 - EntryHeatLimit.step_deg, limit, 1e-9);
        // Within the check interval nothing is read again.
        Assert.AreEqual(limit, heat.Update(Parts(0.99), 1.5, 10, 25));
    }

    [Test]
    public void EntryHeatLimit_HeatShieldsDoNotCount()
    {
        var heat = new EntryHeatLimit();
        heat.Reset(25);
        heat.Update(Parts(0.5, shield: true), 0, 10, 25);
        Assert.AreEqual(25, heat.Update(Parts(0.8, shield: true), 1, 10, 25));
        Assert.AreEqual("tank", heat.hottest_part);
    }

    [Test]
    public void EntryHeatLimit_RateIsSmoothed()
    {
        var heat = new EntryHeatLimit();
        heat.Reset(25);
        heat.Update(Parts(0.5), 0, 10, 25);
        heat.Update(Parts(0.52), 1, 10, 25);          // 2 %/s: 72 % expected
        Assert.AreEqual(0.72, heat.hottest_predicted, 1e-9);
        heat.Update(Parts(0.52), 2, 10, 25);          // measured 0, smoothed: 2 - 2/3 = 1.33 %/s
        Assert.AreEqual(0.52 + 10 * (0.02 - 0.02 / 3), heat.hottest_predicted, 1e-9);
    }

    [Test]
    public void EntryHeatLimit_RaisesBackAfterCoolingAWhile()
    {
        var heat = new EntryHeatLimit();
        heat.Reset(25);
        heat.Update(Parts(0.5), 0, 10, 25);
        double limit = heat.Update(Parts(0.54), 1, 10, 25);
        Assert.AreEqual(7.5, limit, 1e-9);
        // Cool and steady, but not yet raise_wait_s since the step down.
        double t = 2;
        for (; t <= 1 + EntryHeatLimit.raise_wait_s; t += 1)
            limit = heat.Update(Parts(0.54), t, 7.5, 25);
        Assert.AreEqual(7.5, limit, 1e-9, "the smoothed rate still says hot at first, then waits");
        for (int i = 0; i < 100; i++, t += 1)
            limit = heat.Update(Parts(0.54), t, limit, 25);
        Assert.AreEqual(25, limit, 1e-9, "back up to the maximum, a step at a time");
    }

    // ---- Settings keys, verbatim from upstream (IanMealworm/K2D2Redux upstream/main LandingSettings.cs) ----

    static readonly (string key, object def, double min, double max)[] Upstream =
    {
        ("land.entry_angle_of_attack", 10f, 0, 30),
        ("land.entry_flight_path_angle", 10f, 0, 60),
        ("land.boost_back_flip", FlipStyle.Powered, double.NaN, double.NaN),
    };

    static object Field(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null)
                return f.GetValue(o);
        }
        return null;
    }

    [TestCase(true)]
    [TestCase(false)]
    public void BoostBackSettingKeys_MatchUpstream(bool atmospheric)
    {
        var settings = new LandingSettings(null, atmospheric);
        var found = new Dictionary<string, object>();
        foreach (var f in typeof(LandingSettings).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            object value = f.GetValue(settings);
            if (value == null)
                continue;
            if ((Field(value, "key") ?? Field(value, "path")) is string key)
                found[key] = value;
        }
        foreach (var u in Upstream)
        {
            Assert.IsTrue(found.TryGetValue(u.key, out object s), u.key);
            Assert.AreEqual(u.def, Field(s, "default_value"), u.key + " default");
            if (double.IsNaN(u.min))
                continue;
            Assert.AreEqual(u.min, Convert.ToDouble(Field(s, "_min")), 1e-6, u.key + " min");
            Assert.AreEqual(u.max, Convert.ToDouble(Field(s, "_max")), 1e-6, u.key + " max");
        }
        Assert.AreEqual(new[] { "Powered", "Unpowered" }, Enum.GetNames(typeof(FlipStyle)));
    }
}
