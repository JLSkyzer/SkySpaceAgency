using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The departure × flyby × arrival grid (4.6), against the pinned brute-force reference.
public class AssistPorkchopTests
{
    const double KerbinDay = 21_600;

    static double Hohmann(double a1, double a2, double mu) => Math.PI * Math.Sqrt(Math.Pow((a1 + a2) / 2, 3) / mu);

    // The legs of the pinned case with BruteForceReference's ideal end costs.
    public static AssistLegs PinnedLegs(BodySystem s)
    {
        int kerbin = s[AssistCases.From].Index, eve = s[AssistCases.Flyby].Index, moho = s[AssistCases.To].Index;
        int sun = s["Kerbol"].Index;
        double mu = s[sun].Mu;
        double aK = BruteForceReference.OrbitRadius(s, kerbin), aE = BruteForceReference.OrbitRadius(s, eve),
            aM = BruteForceReference.OrbitRadius(s, moho);
        double h1 = Hohmann(aK, aE, mu), h2 = Hohmann(aE, aM, mu);
        double horizon = AssistCases.Horizon(s);
        var up = new List<int> { kerbin };
        var down = new List<int> { moho };
        (Vector3d, Vector3d) State(int b, double t)
        {
            s.StateRelativeToParent(b, t, out var r, out var v);
            return (r, v);
        }
        return new AssistLegs
        {
            DepartureState = t => State(kerbin, t),
            FlybyState = t => State(eve, t),
            ArrivalState = t => State(moho, t),
            LeaveCost = v => BruteForceReference.Leave(s, up, 0, v, AssistCases.ParkingRadius),
            ArriveCost = v => BruteForceReference.Reach(s, down, 0, v, s[moho].LowOrbitRadius, true),
            Mu = mu,
            FlybyMu = s[eve].Mu,
            FlybyFloor = s[eve].LowOrbitRadius,
            FlybyCeiling = FlybyModel.MaxPeriapsisShare * s[eve].Soi,
            Tof1Lo = 0.3 * h1, Tof1Hi = 1.5 * h1, Tof2Lo = 0.3 * h2, Tof2Hi = 1.5 * h2,
            DepartureLo = AssistCases.UT0, DepartureHi = AssistCases.UT0 + horizon,
            FlybyLo = AssistCases.UT0 + 0.3 * h1, FlybyHi = AssistCases.UT0 + horizon + 1.5 * h1,
        };
    }

    // Leave + flyby + arrive, solved again at the candidate's dates.
    static double Recost(AssistLegs legs, AssistCandidate c, out double flybyDv)
    {
        var (rB, vB) = legs.FlybyState(c.FlybyUT);
        var (r0, v0) = legs.DepartureState(c.DepartureUT);
        Assert.IsTrue(Lambert.Solve(r0, rB, c.FlybyUT - c.DepartureUT, legs.Mu, c.Prograde1, c.Revolutions1, c.LongPeriod1, out var v1, out var v2));
        var (rT, vT) = legs.ArrivalState(c.ArrivalUT);
        Assert.IsTrue(Lambert.Solve(rB, rT, c.ArrivalUT - c.FlybyUT, legs.Mu, c.Prograde2, 0, false, out var w1, out var w2));
        var f = FlybyModel.Solve(v2 - vB, w1 - vB, legs.FlybyMu, legs.FlybyFloor, legs.FlybyCeiling);
        Assert.IsTrue(f.Ok, f.Why);
        flybyDv = f.DeltaV;
        double leave = legs.FixedDeparture ? (v1 - v0).magnitude : legs.LeaveCost((v1 - v0).magnitude);
        return leave + f.DeltaV + legs.ArriveCost((w2 - vT).magnitude);
    }

    [Test]
    public void NelderMead3_Quadratic()
    {
        double x = 0, y = 0, z = 0;
        double f = GridRefiner.NelderMead3((a, b, c) => (a - 1) * (a - 1) + 2 * (b + 2) * (b + 2) + 3 * (c - 0.5) * (c - 0.5),
            ref x, ref y, ref z, 0.5, 0.5, 0.5, CancellationToken.None);
        // NelderMead's stopping rules: a value spread under 1e-4 (m/s on a cost) ends the search.
        Assert.AreEqual(0, f, 1e-4);
        Assert.AreEqual(1, x, 1e-2);
        Assert.AreEqual(-2, y, 1e-2);
        Assert.AreEqual(0.5, z, 1e-2);
    }

    [Test]
    public void Search_KerbinMohoViaEve_NearTheReference()
    {
        var s = KerbolFixture.Build();
        var legs = PinnedLegs(s);
        var watch = Stopwatch.StartNew();
        var list = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1);
        TestContext.Out.WriteLine(FormattableString.Invariant(
            $"{watch.ElapsedMilliseconds} ms, {AssistPorkchop.LastLambertSolves} Lambert solves, {AssistPorkchop.LastFeasible} of {AssistPorkchop.LastCells} feasible"));
        Assert.IsNotEmpty(list);
        var best = list[0];
        TestContext.Out.WriteLine(FormattableString.Invariant(
            $"best {best.Cost:F1} m/s (reference {AssistCases.AssistReference:F1}), departure {best.DepartureUT:F0}, flyby {best.FlybyUT:F0} (reference {AssistCases.AssistFlybyUT:F0}), arrival {best.ArrivalUT:F0}, flyby burn {best.FlybyDeltaV:F1} at {best.FlybyPeriapsis:F0} m"));
        Assert.AreEqual(AssistCases.AssistReference, best.Cost, 0.02 * AssistCases.AssistReference);
        Assert.AreEqual(AssistCases.AssistFlybyUT, best.FlybyUT, 10 * KerbinDay);
        for (int i = 1; i < list.Count; i++)
            Assert.LessOrEqual(list[i - 1].Cost, list[i].Cost);
    }

    [Test]
    public void Search_CostAgreesWithItsParts()
    {
        var s = KerbolFixture.Build();
        var legs = PinnedLegs(s);
        var best = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1)[0];
        double cost = Recost(legs, best, out double flybyDv);
        Assert.AreEqual(best.Cost, cost, 1e-6);
        Assert.AreEqual(best.FlybyDeltaV, flybyDv, 1e-6);
        Assert.AreEqual(1, best.Normal.magnitude, 1e-9);
        Assert.GreaterOrEqual(best.FlybyPeriapsis, legs.FlybyFloor);
    }

    [Test]
    public void Search_RespectsMaxTrip()
    {
        var s = KerbolFixture.Build();
        var legs = PinnedLegs(s);
        var best = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1)[0];
        legs.MaxTrip = 0.8 * best.TripSeconds;
        var list = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1);
        foreach (var c in list)
            Assert.LessOrEqual(c.TripSeconds, legs.MaxTrip * (1 + 1e-12));
    }

    [Test]
    public void Search_Cancelled_Throws()
    {
        var s = KerbolFixture.Build();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AssistPorkchop.Search(PinnedLegs(s), cts.Token, null, 0, 1));
    }

    [Test]
    public void Search_FixedDeparture_ResumeShape()
    {
        var s = KerbolFixture.Build();
        var legs = PinnedLegs(s);
        int kerbin = s[AssistCases.From].Index;
        double mu = legs.Mu, ut0 = AssistCases.UT0;
        s.StateRelativeToParent(kerbin, ut0, out var rK, out var vK);
        var r0 = rK;
        var v0 = vK + 2000 * rK.normalized;
        legs.FixedDeparture = true;
        legs.DepartureState = t =>
        {
            if (t == ut0)
                return (r0, v0);
            KeplerPropagator.Propagate(r0, v0, mu, t - ut0, out var r, out var v);
            return (r, v);
        };
        var list = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1);
        Assert.IsNotEmpty(list);
        var best = list[0];
        Assert.IsFalse(double.IsInfinity(best.Cost));
        Assert.AreEqual(best.Cost, Recost(legs, best, out _), 1e-6);
    }
}
