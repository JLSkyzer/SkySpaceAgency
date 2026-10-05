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
