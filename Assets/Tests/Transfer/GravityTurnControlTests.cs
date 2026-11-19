using System;
using K2D2.Lift;
using NUnit.Framework;

// The Gravity Turn's throttle and steering rules (K2-D2 1.4.0, GravityTurnControl), flown by
// ReactiveAscent and tried out by AscentPredictor.
public class GravityTurnControlTests
{
    const float G = 9.81f;

    static GravityTurnControl Control()
    {
        var c = new GravityTurnControl
        {
            kick_speed_mps = 100,
            kick_deg = 7.5f,
            hold_time_s = 45,
            max_throttle = 1,
            min_throttle = 0.25f,
            max_twr = 2.5f,
            max_q_kpa = 40,
        };
        c.Reset();
        return c;
    }

    // Rising in thick air, below the kick speed; max_accel 20 m/s2 (TWR about 2).
    static GravityTurnControl.Inputs Climb() => new GravityTurnControl.Inputs
    {
        vertical_speed = 50,
        prograde_elevation_deg = 90,
        orbital_prograde_elevation_deg = 85,
        atmosphere_fraction = 0.05f,
        heat_fraction = 0.1f,
        time_to_apoapsis_s = 20,
        gravity = G,
        effective_gravity = G,
        max_accel = 20,
        dynamic_pressure_kpa = 5,
        remaining_ap_km = 80,
        end_error_km = 0.1f,
        taper_rate = 0,
    };

    // Through the kick into the turn.
    static GravityTurnControl Turning()
    {
        var c = Control();
        var i = Climb();
        i.vertical_speed = 120;
        c.Update(i, 0.1f);
        i.prograde_elevation_deg = 82;
        c.Update(i, 0.1f);
        Assert.AreEqual(GravityTurnControl.Phase.Turn, c.phase);
        return c;
    }

    [Test]
    public void BelowTheKickSpeed_StraightUp()
    {
        var c = Control();
        c.Update(Climb(), 0.1f);
        Assert.AreEqual(GravityTurnControl.Phase.VerticalRise, c.phase);
        Assert.AreEqual(90f, c.target_elevation_deg);
        Assert.AreEqual(1f, c.throttle, 1e-6, "TWR cap 1.23 above max throttle");
        Assert.AreEqual("Max throttle", c.throttle_limit);
    }

    [Test]
    public void TheKickTiltsToTheKickAngle_ThenTheTurnStartsOnceProgradeFollows()
    {
        var c = Control();
        var i = Climb();
        i.vertical_speed = 100;
        c.Update(i, 0.1f);
        Assert.AreEqual(GravityTurnControl.Phase.PitchKick, c.phase);

        // Prograde still straight up: the angle of attack allowed at 5 kPa is 12 deg, more than the kick.
        c.Update(i, 0.1f);
        Assert.AreEqual(82.5f, c.target_elevation_deg, 1e-4);
        Assert.AreEqual(GravityTurnControl.Phase.PitchKick, c.phase);

        // In thick air (30 kPa: 2 deg allowed) the kick is held back to prograde - 2 deg.
        i.dynamic_pressure_kpa = 30;
        c.Update(i, 0.1f);
        Assert.AreEqual(88f, c.target_elevation_deg, 1e-4);

        i.prograde_elevation_deg = 83.4f;
        c.Update(i, 0.1f);
        Assert.AreEqual(GravityTurnControl.Phase.Turn, c.phase, "within 1 deg of the kick angle");
    }

    [Test]
    public void TheKickTimesOut_WhenTheVelocityNeverFollows()
    {
        var c = Control();
        var i = Climb();
        i.vertical_speed = 100;
        c.Update(i, 0.1f);
        for (int k = 0; k < 29; k++)
            c.Update(i, 1f);
        Assert.AreEqual(GravityTurnControl.Phase.PitchKick, c.phase);
        c.Update(i, 1.5f);
        Assert.AreEqual(GravityTurnControl.Phase.Turn, c.phase);
    }

    [Test]
    public void TheTurnFollowsSurfacePrograde_ThenOrbitalProgradeInThinAir()
    {
        var c = Turning();
        var i = Climb();
        i.prograde_elevation_deg = 40;
        i.orbital_prograde_elevation_deg = 30;
        i.time_to_apoapsis_s = 45;
        c.Update(i, 0.1f);
        Assert.IsFalse(c.thin_air);
        Assert.AreEqual(40f, c.target_elevation_deg, 1e-4);

        i.dynamic_pressure_kpa = 0.5f;
        i.atmosphere_fraction = 0.5f;
        c.Update(i, 0.1f);
        Assert.IsTrue(c.thin_air);
        Assert.AreEqual(30f, c.target_elevation_deg, 1e-4);

        // Low pressure low down (a quarter of the atmosphere or less) is still the surface's.
        i.atmosphere_fraction = 0.2f;
        c.Update(i, 0.1f);
        Assert.IsFalse(c.thin_air);
    }

    [Test]
    public void TheThrottleIsTheLowestCap()
    {
        var c = Control();
        var i = Climb();
        i.max_accel = 40;           // TWR 4: capped at 2.5
        c.Update(i, 0.1f);
        Assert.AreEqual(2.5f * G / 40, c.throttle, 1e-5);
        Assert.AreEqual("TWR", c.throttle_limit);

        i.max_accel = 20;           // TWR cap 1.23: not limiting
        i.dynamic_pressure_kpa = 36;  // 90 % of max Q: (44 - 36) / 12
        c.Update(i, 0.1f);
        Assert.AreEqual(8f / 12f, c.throttle, 1e-5);
        Assert.AreEqual("Max Q", c.throttle_limit);

        i.dynamic_pressure_kpa = 50;  // past it: down to the floor, never below
        c.Update(i, 0.1f);
        Assert.AreEqual(0.25f, c.throttle, 1e-6);
    }

    [Test]
    public void TheMinimumThrottleIsAFloor_WhenTheHoldAsksForLess()
    {
        var c = Turning();
        var i = Climb();
        i.prograde_elevation_deg = 30;
        i.time_to_apoapsis_s = 80;  // far past the hold: no push wanted
        c.Update(i, 0.1f);
        Assert.AreEqual(0.25f, c.throttle, 1e-6);
        Assert.AreEqual("Min throttle", c.throttle_limit);
    }

    [Test]
    public void ShortOfClimbAtFullThrottle_TheNoseComesAbovePrograde()
    {
        var c = Turning();
        var i = Climb();
        i.prograde_elevation_deg = 30;
        i.time_to_apoapsis_s = 15;  // 30 s short of the hold
        c.Update(i, 0.1f);
        Assert.AreEqual(1f, c.throttle, 1e-6);
        // As much as the air allows at 5 kPa (12 deg): the climb wanted needs more than that.
        Assert.AreEqual(42f, c.target_elevation_deg, 1e-4);
    }

    [Test]
    public void AHotPartInTheAir_TheNoseComesUp()
    {
        var c = Turning();
        var i = Climb();
        i.prograde_elevation_deg = 30;
        i.time_to_apoapsis_s = 45;
        i.heat_fraction = 0.95f;
        c.Update(i, 0.1f);
        Assert.IsTrue(c.heat_guard);
        Assert.AreEqual(42f, c.target_elevation_deg, 1e-4, "full share: min(12 deg allowed, 15 deg)");

        i.heat_fraction = 0.875f;   // half way between 0.8 and 0.95
        c.Update(i, 0.1f);
        Assert.AreEqual(36f, c.target_elevation_deg, 1e-3);

        i.atmosphere_fraction = 1.2f;   // out of the air: climbing no longer cools it
        c.Update(i, 0.1f);
        Assert.IsFalse(c.heat_guard);
        Assert.AreEqual(30f, c.target_elevation_deg, 1e-4);
    }

    [Test]
    public void FallingBack_TheLimitsLetGo()
    {
        var c = Turning();
        var i = Climb();
        i.prograde_elevation_deg = 10;
        i.max_accel = 40;
        i.dynamic_pressure_kpa = 50;
        i.time_to_apoapsis_s = 5;   // under 10 s
        c.Update(i, 0.1f);
        Assert.AreEqual(1f, c.throttle, 1e-6, "neither TWR nor max Q");
    }

    [Test]
    public void NearTheTargetApoapsis_TheThrottleTapers()
    {
        var c = Turning();
        var i = Climb();
        i.prograde_elevation_deg = 30;
        i.time_to_apoapsis_s = 15;
        i.remaining_ap_km = 0.2f;
        i.taper_rate = 1;           // km/s of apoapsis per unit of throttle
        c.Update(i, 0.1f);
        Assert.AreEqual((0.2f + 0.1f) / 5f, c.throttle, 1e-6);
        Assert.AreEqual("Apoapsis", c.throttle_limit);
    }

    [Test]
    public void SignedTimeToApoapsis_HalfAnOrbitFromPeriapsis_NegativeOnceFalling()
    {
        const double mu = 3.5316e12;
        double rp = 700000, ra = 900000, a = (rp + ra) / 2;
        double period = 2 * Math.PI * Math.Sqrt(a * a * a / mu);
        double vp = Math.Sqrt(mu * (2 / rp - 1 / a));
        Assert.AreEqual(period / 2, GravityTurnControl.SignedTimeToApoapsis(new Vector3d(rp, 0, 0), new Vector3d(0, vp, 0), mu), 1e-3);

        double va = Math.Sqrt(mu * (2 / ra - 1 / a));
        // Just past apoapsis: a small radial speed inwards.
        var r = new Vector3d(-ra, 0, 0);
        var v = new Vector3d(1, -va, 0);
        double t = GravityTurnControl.SignedTimeToApoapsis(r, v, mu);
        Assert.Less(t, 0);
        Assert.Greater(t, -60);

        Assert.AreEqual(double.PositiveInfinity, GravityTurnControl.SignedTimeToApoapsis(new Vector3d(rp, 0, 0), new Vector3d(0, 2 * vp, 0), mu));
    }
}
