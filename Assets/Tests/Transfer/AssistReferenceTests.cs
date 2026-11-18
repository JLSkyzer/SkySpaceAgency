using System;
using System.Diagnostics;
using System.Linq;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

// The pinned gravity-assist cases (4.6), shared by the assist tests. Every number here was pinned
// on 2026-11-18 from BruteForceAssistReference / BruteForceReference on KerbolFixture; none is an
// external figure.
public static class AssistCases
{
    public const double ParkingRadius = 700_000; // 100 km above Kerbin

    // Kerbin -> Moho via Eve. The plan named Kerbin -> Jool via Eve first: on the fixture no single
    // Eve flyby saves enough on the way to Jool (epochs 0 to 6 Kerbin-Eve synodic periods, 80 x 30 x 30
    // grid: -5.1 % to +2.1 %; the turn Eve can give does not pay for the faster Jool arrival). The
    // user's answer (2026-11-18): pin another pair with a real saving, never lower the threshold. Moho
    // via Eve saves 18 % at epoch 0 on the coarse grid (and 5 % to 38 % at most other epochs).
    public const string From = "Kerbin", Flyby = "Eve", To = "Moho";
    public const double UT0 = 0;
    public static double Horizon(BodySystem s) => 2 * s.SynodicPeriod(s[From].Index, s[To].Index);

    // Pinned values (reference grids of the tests below).
    public const double DirectReference = 4945.767;  // departure 757061, flight 2050173 (240 x 120)
    public const double AssistReference = 4046.316;  // 160 x 60 x 60: saves 18.19 %
    public const double AssistDepartureUT = 0, AssistFlybyUT = 4022682, AssistArrivalUT = 6837996;
    public const double AssistFlybyDeltaV = 318.734;

    // Kerbin -> Duna: no flyby pays (pinned below 3 %), and the Mun departure saving.
    public const double DunaUT0 = 0;
    // Negative: from 100 km, the Mun costs 156 m/s more than a direct departure for this window's
    // 960.5 m/s excess (48 flyby dates x 16 flights x 360 phases).
    public const double MunDepartureSaving = -156.043;
}

public class AssistReferenceTests
{
    static void Out(FormattableString text) => TestContext.Out.WriteLine(FormattableString.Invariant(text));

    [Test]
    public void Reference_AsymptoteFormula_SelfCheck()
    {
        var rng = new Random(46);
        const double mu = 3.5316e12;
        for (int i = 0; i < 50; i++)
        {
            var r = new Vector3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * 2e7;
            var w = new Vector3d(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * 3000;
            int n = 0;
            foreach (var v in BruteForceAssistReference.VelocitiesForAsymptote(r, w, mu))
            {
                Assert.AreEqual(0, (Hyperbola.Asymptote(r, v, mu) - w).magnitude, 1e-6 * w.magnitude, $"case {i}");
                n++;
            }
            Assert.AreEqual(2, n);
        }
    }

    [Test]
    public void Reference_KerbinMohoViaEve()
    {
        var s = KerbolFixture.Build();
        int kerbin = s[AssistCases.From].Index, eve = s[AssistCases.Flyby].Index, moho = s[AssistCases.To].Index;
        var watch = Stopwatch.StartNew();
        double direct = BruteForceReference.IdealTotal(s, kerbin, AssistCases.ParkingRadius, moho, s[moho].LowOrbitRadius, true,
            AssistCases.UT0, 240, 120, out double dT, out double dTof);
        double assist = BruteForceAssistReference.SiblingAssist(s, kerbin, AssistCases.ParkingRadius, eve, moho, s[moho].LowOrbitRadius,
            true, AssistCases.UT0, AssistCases.Horizon(s), 160, 60, 60, out double tD, out double tF, out double tA, out double fdv);
        Out($"direct {direct:F3} m/s (departure {dT:F0}, flight {dTof:F0}); via Eve {assist:F3} m/s (departure {tD:F0}, flyby {tF:F0}, arrival {tA:F0}, flyby burn {fdv:F3}), saving {(1 - assist / direct) * 100:F2} %, {watch.ElapsedMilliseconds} ms");
        Assert.AreEqual(AssistCases.DirectReference, direct, 1e-2);
        Assert.AreEqual(AssistCases.AssistReference, assist, 1e-2);
        Assert.AreEqual(AssistCases.AssistFlybyUT, tF, 1);
        Assert.LessOrEqual(assist, 0.93 * direct);
    }

    [Test]
    public void Reference_KerbinDunaNoAssist()
    {
        var s = KerbolFixture.Build();
        int kerbin = s["Kerbin"].Index, duna = s["Duna"].Index, sun = s["Kerbol"].Index;
        var watch = Stopwatch.StartNew();
        double direct = BruteForceReference.IdealTotal(s, kerbin, AssistCases.ParkingRadius, duna, s[duna].LowOrbitRadius, true,
            AssistCases.DunaUT0, 240, 120, out _, out _);
        double best = double.PositiveInfinity;
        string bestName = "";
        for (int b = 0; b < s.Count; b++)
        {
            if (s[b].ParentIndex != sun || b == kerbin || b == duna)
                continue;
            double c = BruteForceAssistReference.SiblingAssist(s, kerbin, AssistCases.ParkingRadius, b, duna, s[duna].LowOrbitRadius,
                true, AssistCases.DunaUT0, 2 * s.SynodicPeriod(kerbin, duna), 80, 30, 30, out _, out _, out _, out _);
            Out($"via {s[b].Name}: {c:F1} m/s");
            if (c < best)
            {
                best = c;
                bestName = s[b].Name;
            }
        }
        Out($"direct {direct:F1} m/s, best assist via {bestName} {best:F1} m/s, saving {(1 - best / direct) * 100:F2} %, {watch.ElapsedMilliseconds} ms");
        Assert.Less(1 - best / direct, 0.03);
    }

    [Test]
    public void Reference_MunDeparture_KerbinDuna()
    {
        var s = KerbolFixture.Build();
        int kerbin = s["Kerbin"].Index, duna = s["Duna"].Index, mun = s["Mun"].Index;
        var watch = Stopwatch.StartNew();
        BruteForceReference.IdealTotal(s, kerbin, AssistCases.ParkingRadius, duna, s[duna].LowOrbitRadius, true,
            AssistCases.DunaUT0, 240, 120, out double t, out double tof);
        var w = MunDepartureExcess(s, t, tof);
        double cost = BruteForceAssistReference.MoonDeparture(s, kerbin, AssistCases.ParkingRadius, mun, w, t, 48, 16, out double saving);
        Out($"excess {w.magnitude:F1} m/s at {t:F0}: Mun departure {cost:F1} m/s, saving {saving:F2} m/s, {watch.ElapsedMilliseconds} ms");
        Assert.IsFalse(double.IsNaN(saving) || double.IsInfinity(saving));
        Assert.AreEqual(AssistCases.MunDepartureSaving, saving, 1e-2);
    }

    // The excess the direct reference's best Kerbin -> Duna window leaves Kerbin with.
    public static Vector3d MunDepartureExcess(BodySystem s, double t, double tof)
    {
        int kerbin = s["Kerbin"].Index, duna = s["Duna"].Index;
        s.StateRelativeToParent(kerbin, t, out var rK, out var vK);
        s.StateRelativeToParent(duna, t + tof, out var rD, out _);
        Lambert.Solve(rK, rD, tof, s["Kerbol"].Mu, true, 0, false, out var v1, out _);
        return v1 - vK;
    }
}
