using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class StopPointPredictorTests
{
    // Mun.
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double UT = 1000;
    static double SurfaceGravity => Mu / (R * R);

    static Vector3d Zero => new Vector3d(0, 0, 0);

    static Body Mun(bool rotating) => new Body
    {
        Mu = Mu,
        Radius = R,
        Omega = rotating ? BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1) : Zero,
        TerrainHeight = p => 0,
    };

    // TWR 3 against the Mun's surface gravity.
    static Vehicle Lander(double twr = 3) => new Vehicle
    {
        MassT = 10,
        ThrustKN = twr * 10 * SurfaceGravity,
        Isp = 320,
    };

    // Circular 30 km polar orbit: at the equator on +x, heading north. Polar so that the body's
    // rotation shifts the stop point across the track, where it does not change the braking.
    static readonly double R0 = R + 30000;
    static double CircularSpeed => Math.Sqrt(Mu / R0);
    static Vector3d R0Vec => new Vector3d(R0, 0, 0);
    static Vector3d V0Vec => new Vector3d(0, 0, CircularSpeed);

    // Retrograde burn at apoapsis R0 that lowers the periapsis to R - 20 km.
    static Impulse Deorbit(double ut)
    {
        double rp = R - 20000;
        double va = Math.Sqrt(2 * Mu * rp / (R0 * (R0 + rp)));
        // The vessel is back at +x after a whole number of orbits only at ut == UT; for a later
        // burn the velocity direction is taken from the propagated state.
        KeplerPropagator.Propagate(R0Vec, V0Vec, Mu, ut - UT, out _, out var v);
        return new Impulse { UT = ut, DeltaV = v / v.magnitude * (va - CircularSpeed) };
    }

    // Independent ballistic impact on the flat Mun: first point of the coast below R.
    static void BallisticImpact(Vector3d r, Vector3d v, out Vector3d impact, out double speed)
    {
        for (double dt = 0.1; dt < 20000; dt += 0.1)
        {
            KeplerPropagator.Propagate(r, v, Mu, dt, out var ri, out var vi);
            if (ri.magnitude <= R)
            {
                impact = ri;
                speed = vi.magnitude;
                return;
            }
        }
        throw new Exception("no impact");
    }

    [Test]
    public void NoRotation_StopsBeforeBallisticImpactByTheBrakingDistance()
    {
        Impulse burn = Deorbit(UT);

        StopPrediction p = StopPointPredictor.Predict(R0Vec, V0Vec, UT, Mun(false), Lander(), burn);

        Assert.IsTrue(p.Ok, p.Reason);
        BallisticImpact(R0Vec, V0Vec + burn.DeltaV, out var impact, out double impactSpeed);
        Vector3d impactDir = impact / impact.magnitude;

        // Ground track at the stop: northward-ish, in the orbit plane (x-z).
        Vector3d orbitNormal = Vector3d.Cross(R0Vec, V0Vec);
        Vector3d track = Vector3d.Cross(orbitNormal, p.StopDirNow);
        SurfaceGeometry.Split(p.StopDirNow, impactDir, track, R, out double along, out double cross);

        TestContext.Out.WriteLine($"along {along:F0} m, cross {cross:F2} m, brake dv {p.BrakeDeltaV:F1} vs impact speed {impactSpeed:F1}, stop alt {p.StopAltitude:F1}");
        Assert.That(along, Is.GreaterThan(1000), "impact must lie ahead of the stop point");
        Assert.That(Math.Abs(cross), Is.LessThan(1), "no rotation: stays in the orbit plane");
        Assert.That(p.BrakeDeltaV, Is.EqualTo(impactSpeed).Within(10).Percent,
            $"brake dv {p.BrakeDeltaV} vs impact speed {impactSpeed}");
        Assert.That(p.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude - 1));
        Assert.That(p.StopUT, Is.GreaterThan(p.BrakeStartUT));
        Assert.That(p.BrakeStartUT, Is.GreaterThan(UT));
        Assert.AreEqual(1.0, p.StopDirNow.magnitude, 1e-9);
    }

    [Test]
    public void Rotation_ShiftsTheStopPointWestByTheBodyTurn()
    {
        // Burn 1000 s after the prediction time, so the body turns well before the stop.
        Impulse burn = Deorbit(UT + 1000);

        StopPrediction still = StopPointPredictor.Predict(R0Vec, V0Vec, UT, Mun(false), Lander(), burn);
        StopPrediction turning = StopPointPredictor.Predict(R0Vec, V0Vec, UT, Mun(true), Lander(), burn);

        Assert.IsTrue(still.Ok, still.Reason);
        Assert.IsTrue(turning.Ok, turning.Reason);

        double omega = Mun(true).Omega.z;
        double expectedTurn = -omega * (turning.StopUT - UT); // rad of azimuth, westward
        double azStill = Math.Atan2(still.StopDirNow.y, still.StopDirNow.x);
        double azTurning = Math.Atan2(turning.StopDirNow.y, turning.StopDirNow.x);
        double dAz = azTurning - azStill;

        double lat = Math.Asin(turning.StopDirNow.z);
        double expectedM = Math.Abs(expectedTurn) * R * Math.Cos(lat);
        double shiftM = SurfaceGeometry.GreatCircle(still.StopDirNow, turning.StopDirNow, R);

        TestContext.Out.WriteLine($"shift {shiftM:F0} m vs expected {expectedM:F0} m, dAz {dAz:E4} vs {expectedTurn:E4}");
        Assert.That(dAz, Is.EqualTo(expectedTurn).Within(5).Percent, $"azimuth shift (expected {expectedM:F0} m)");
        Assert.That(shiftM, Is.EqualTo(expectedM).Within(5).Percent, "shift along the latitude circle");
    }

    [Test]
    public void NoThrust_IsRefusedWithAReason()
    {
        Vehicle dead = Lander();
        dead.ThrustKN = 0;

        StopPrediction p = StopPointPredictor.Predict(R0Vec, V0Vec, UT, Mun(false), dead, Deorbit(UT));

        Assert.IsFalse(p.Ok);
        Assert.That(p.Reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void EscapeTrajectory_IsRefused()
    {
        double escape = Math.Sqrt(2 * Mu / R0);
        var v = new Vector3d(0, 0, 1.5 * escape);

        StopPrediction p = StopPointPredictor.Predict(R0Vec, v, UT, Mun(false), Lander(), null);

        Assert.IsFalse(p.Ok);
        Assert.That(p.Reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void OrbitAboveTheGround_IsRefused()
    {
        StopPrediction p = StopPointPredictor.Predict(R0Vec, V0Vec, UT, Mun(false), Lander(), null);

        Assert.IsFalse(p.Ok);
        Assert.That(p.Reason, Is.Not.Null.And.Not.Empty);
    }
}
