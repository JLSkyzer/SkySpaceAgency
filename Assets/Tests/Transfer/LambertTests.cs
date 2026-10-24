using System;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class LambertTests
{
    const double MuEarth = 398600e9; // m³/s²

    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance, string what)
    {
        Assert.That((actual - expected).magnitude, Is.LessThan(tolerance),
            $"{what}: expected ({expected.x}, {expected.y}, {expected.z}), got ({actual.x}, {actual.y}, {actual.z})");
    }

    static bool IsFinite(Vector3d v) =>
        !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z)
        && !double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);

    [Test]
    public void Curtis_Example5_2()
    {
        // Published test case: Curtis, "Orbital Mechanics for Engineering Students", example 5.2.
        var r1 = new Vector3d(5000e3, 10000e3, 2100e3);
        var r2 = new Vector3d(-14600e3, 2500e3, 7000e3);

        Assert.That(Lambert.Solve(r1, r2, 3600, MuEarth, true, 0, false, out var v1, out var v2), Is.True);

        AssertVector(new Vector3d(-5992.5, 1925.4, 3245.6), v1, 1, "v1");
        AssertVector(new Vector3d(-3312.5, -4196.6, -385.29), v2, 1, "v2");
    }

    // The branch only matters with at least one revolution.
    [TestCase(0, false)]
    [TestCase(1, false)]
    [TestCase(1, true)]
    public void RoundTrip_WithKeplerPropagator(int revolutions, bool longPeriodBranch)
    {

        var rng = new Random(4242 + revolutions);
        for (int i = 0; i < 50; i++)
        {
            Vector3d r1 = RandomDirection(rng) * (7e6 + rng.NextDouble() * 33e6);
            Vector3d r2 = RandomDirection(rng) * (7e6 + rng.NextDouble() * 33e6);
            double angle = Math.Acos(Vector3d.Dot(r1.normalized, r2.normalized));
            if (angle < 0.05 || angle > Math.PI - 0.05)
                continue; // near-collinear: covered by Collinear_ReturnsFalse
            bool prograde = rng.NextDouble() < 0.7;

            double rMax = Math.Max(r1.magnitude, r2.magnitude);
            double circularPeriod = 2 * Math.PI * Math.Sqrt(rMax * rMax * rMax / MuEarth);
            double tof = revolutions == 0
                ? (0.1 + 0.9 * rng.NextDouble()) * circularPeriod
                : (2.2 + 1.3 * rng.NextDouble()) * circularPeriod; // above the one-revolution minimum

            Assert.That(Lambert.Solve(r1, r2, tof, MuEarth, prograde, revolutions, longPeriodBranch, out var v1, out var v2),
                Is.True, $"case {i}: no solution for tof {tof:F0} s");

            KeplerPropagator.Propagate(r1, v1, MuEarth, tof, out var rEnd, out var vEnd);
            AssertVector(r2, rEnd, 1, $"case {i} (prograde {prograde}) arrival position");
            AssertVector(v2, vEnd, 1e-3, $"case {i} arrival velocity");

            double hz = Vector3d.Cross(r1, v1).z;
            if (Math.Abs(Vector3d.Cross(r1, r2).normalized.z) > 0.2)
                Assert.That(prograde ? hz > 0 : hz < 0, Is.True, $"case {i}: wrong direction of motion");
        }
    }

    [Test]
    public void RoundTrip_OneRevolution_BranchesDiffer()
    {
        var r1 = new Vector3d(8000e3, 0, 0);
        var r2 = new Vector3d(0, 12000e3, 1000e3);
        double tof = 3 * 2 * Math.PI * Math.Sqrt(Math.Pow(12000e3, 3) / MuEarth);

        Assert.That(Lambert.Solve(r1, r2, tof, MuEarth, true, 1, false, out var vShort, out _), Is.True);
        Assert.That(Lambert.Solve(r1, r2, tof, MuEarth, true, 1, true, out var vLong, out _), Is.True);

        double aShort = 1 / (2 / r1.magnitude - vShort.sqrMagnitude / MuEarth);
        double aLong = 1 / (2 / r1.magnitude - vLong.sqrMagnitude / MuEarth);
        Assert.That(aLong, Is.GreaterThan(aShort * 1.001), "the long-period branch has the larger orbit");
    }

    [Test]
    public void Collinear_ReturnsFalse()
    {
        var r1 = new Vector3d(7000e3, 1000e3, 500e3);
        var r2 = -1.5 * r1;
        Assert.That(Lambert.Solve(r1, r2, 5000, MuEarth, true, 0, false, out var v1, out var v2), Is.False);
        Assert.That(IsFinite(v1) && IsFinite(v2), Is.True, "outputs stay finite");

        Assert.That(Lambert.Solve(r1, 2 * r1, 5000, MuEarth, true, 0, false, out v1, out v2), Is.False, "angle 0");
        Assert.That(IsFinite(v1) && IsFinite(v2), Is.True, "outputs stay finite");
    }

    [Test]
    public void MultiRev_NoSolutionBelowMinimumTime_ReturnsFalse()
    {
        var r1 = new Vector3d(8000e3, 0, 0);
        var r2 = new Vector3d(0, 9000e3, 0);
        double period = 2 * Math.PI * Math.Sqrt(Math.Pow(9000e3, 3) / MuEarth);

        foreach (bool longBranch in new[] { false, true })
        {
            Assert.That(Lambert.Solve(r1, r2, 0.5 * period, MuEarth, true, 1, longBranch, out var v1, out var v2), Is.False);
            Assert.That(IsFinite(v1) && IsFinite(v2), Is.True, "outputs stay finite");
        }
        // The same geometry has a zero-revolution solution in that time.
        Assert.That(Lambert.Solve(r1, r2, 0.5 * period, MuEarth, true, 0, false, out _, out _), Is.True);
    }

    [Test]
    public void BadInput_ReturnsFalse()
    {
        var r1 = new Vector3d(8000e3, 0, 0);
        var r2 = new Vector3d(0, 9000e3, 0);
        Assert.That(Lambert.Solve(r1, r2, 0, MuEarth, true, 0, false, out _, out _), Is.False, "zero time");
        Assert.That(Lambert.Solve(r1, r2, -10, MuEarth, true, 0, false, out _, out _), Is.False, "negative time");
        Assert.That(Lambert.Solve(r1, r2, 1000, MuEarth, true, -1, false, out _, out _), Is.False, "negative revolutions");
        Assert.That(Lambert.Solve(Vector3d.zero, r2, 1000, MuEarth, true, 0, false, out _, out _), Is.False, "zero radius");
    }

    // Regression (4.3 Task 1, the rendezvous planner): with several revolutions between two points
    // of the same circular orbit, Solve returned true with velocities that missed r2 by up to
    // hundreds of kilometres. Rounding in y(z) near the asymptotes made the time look like a jump
    // to +infinity, and the root finder reported the jump as a root.
    const double MuKerbin = 3.5316e12; // m³/s²
    const double KerbinOrbit = 700e3;  // m: a 100 km circular orbit

    static double Miss(Vector3d r1, Vector3d r2, double tof, double mu, Vector3d v1)
    {
        KeplerPropagator.Propagate(r1, v1, mu, tof, out var rEnd, out _);
        return (rEnd - r2).magnitude;
    }

    [Test]
    public void SameOrbit_MultiRev_NeverReportsAMiss()
    {
        double period = 2 * Math.PI * Math.Sqrt(Math.Pow(KerbinOrbit, 3) / MuKerbin);
        var r1 = new Vector3d(KerbinOrbit, 0, 0);
        // Phase angles from a few metres apart to 30°, both ways round.
        double[] degrees = { 0.0014, -0.0014, 0.0537, -0.0537, 0.3, -0.3, 1, -1, 30, -30 };
        int solved = 0, missed = 0;
        double worst = 0;
        string worstCase = "";
        foreach (double deg in degrees)
        {
            double ang = deg * Math.PI / 180;
            var r2 = new Vector3d(KerbinOrbit * Math.Cos(ang), KerbinOrbit * Math.Sin(ang), 0);
            for (int rev = 1; rev <= 10; rev++)
                foreach (bool longBranch in new[] { false, true })
                    for (int k = 0; k <= 40; k++)
                    {
                        // The planner's window per revolution count: [max(0.3, k − 0.5), k + 1.5] periods.
                        double lo = Math.Max(0.3, rev - 0.5), hi = rev + 1.5;
                        double tof = (lo + (hi - lo) * k / 40) * period;
                        if (!Lambert.Solve(r1, r2, tof, MuKerbin, true, rev, longBranch, out var v1, out _))
                            continue;
                        solved++;
                        double miss = Miss(r1, r2, tof, MuKerbin, v1);
                        if (!(miss <= 1))
                            missed++;
                        if (!(miss <= worst))
                        {
                            worst = miss;
                            worstCase = $"{deg}°, {rev} rev, long {longBranch}, tof {tof / period:F3} periods";
                        }
                    }
        }
        Assert.That(missed, Is.EqualTo(0),
            $"{missed} of {solved} solutions miss r2 by more than 1 m; worst {worst:F1} m at {worstCase}");
        Assert.That(solved, Is.GreaterThan(4000), "most of these flights have a solution");
    }

    [Test]
    public void SameOrbit_TenRevs_FakeRootAtTheAsymptote_IsNotASuccess()
    {
        // The worst case found while reproducing: r2 17 m from r1, ten revolutions, short branch. The
        // old solver returned a z whose time of flight was 0.01 % of 'tof'.
        var r1 = new Vector3d(KerbinOrbit, 0, 0);
        double ang = -2.5001875908312325e-05, r2m = 700110.0011465943, tof = 22454.01463802305;
        var r2 = new Vector3d(r2m * Math.Cos(ang), r2m * Math.Sin(ang), 0);
        foreach (bool longBranch in new[] { false, true })
            if (Lambert.Solve(r1, r2, tof, MuKerbin, true, 10, longBranch, out var v1, out _))
                Assert.That(Miss(r1, r2, tof, MuKerbin, v1), Is.LessThanOrEqualTo(1), $"long {longBranch}");
        // The long branch is a genuine solution and must stay one.
        Assert.That(Lambert.Solve(r1, r2, tof, MuKerbin, true, 10, true, out _, out _), Is.True);
    }

    // Property: random multi-revolution problems, near-same-orbit ones included. Whatever Solve
    // says true to must reach r2 within 1 m.
    [Test]
    public void Property_RandomMultiRev_EveryTrueResultArrives()
    {
        var rng = new Random(20261024);
        int solved = 0, missed = 0;
        string first = "";
        for (int i = 0; i < 3000; i++)
        {
            Vector3d r1, r2;
            double mu = rng.NextDouble() < 0.5 ? MuKerbin : MuEarth;
            double rBase = mu == MuKerbin ? 650e3 + rng.NextDouble() * 2e6 : 7e6 + rng.NextDouble() * 30e6;
            r1 = RandomDirection(rng) * rBase;
            if (rng.NextDouble() < 0.5)
            {
                // Near the same orbit: a small phase in the orbit's plane, a small change of radius.
                Vector3d axis = Vector3d.Cross(r1, RandomDirection(rng)).normalized;
                double phase = (rng.NextDouble() < 0.5 ? -1 : 1) * Math.Pow(10, -5 + 4.5 * rng.NextDouble());
                Vector3d along = Vector3d.Cross(axis, r1).normalized;
                r2 = (r1.normalized * Math.Cos(phase) + along * Math.Sin(phase))
                    * rBase * (1 + 0.06 * (rng.NextDouble() - 0.5));
            }
            else
                r2 = RandomDirection(rng) * (rBase * (0.5 + rng.NextDouble()));
            int rev = 1 + rng.Next(10);
            bool longBranch = rng.NextDouble() < 0.5, prograde = rng.NextDouble() < 0.7;
            double rMax = Math.Max(r1.magnitude, r2.magnitude);
            double period = 2 * Math.PI * Math.Sqrt(rMax * rMax * rMax / mu);
            double tof = (Math.Max(0.3, rev - 0.5) + 2.5 * rng.NextDouble()) * period;

            if (!Lambert.Solve(r1, r2, tof, mu, prograde, rev, longBranch, out var v1, out _))
                continue;
            solved++;
            double miss = Miss(r1, r2, tof, mu, v1);
            if (!(miss <= 1) && missed++ == 0)
                first = $"case {i}: {rev} rev, long {longBranch}, prograde {prograde}, miss {miss:F1} m";
        }
        Assert.That(missed, Is.EqualTo(0), $"{missed} of {solved} solutions miss r2 by more than 1 m; first {first}");
        Assert.That(solved, Is.GreaterThan(1000), "enough cases are solved to mean something");
    }

    static Vector3d RandomDirection(Random rng)
    {
        double z = 2 * rng.NextDouble() - 1;
        double phi = 2 * Math.PI * rng.NextDouble();
        double s = Math.Sqrt(1 - z * z);
        return new Vector3d(s * Math.Cos(phi), s * Math.Sin(phi), z);
    }
}
