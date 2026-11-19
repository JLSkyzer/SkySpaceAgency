using System;
using System.Linq;
using System.Threading;
using SkySpaceAgency.Node;
using SkySpaceAgency.OrbitPlanning;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The 2026-11-18 in-game Minmus -> Kerbin return (Redux 26w46a): the plan aimed at a 100 km
// Kerbin periapsis, the injection was flown to its asymptote within 0.2 m/s, and the vessel left
// Minmus for a 37 831 km periapsis on an escape from Kerbin. The game gives the vectors of an orbit
// around Minmus in Minmus's celestial frame, turned by its CelestialTilt (its orbit frame: about
// 116° for stock Minmus); the transfer code took them as Kerbin's. These tests rebuild that with
// the fixture: Minmus's frame is the fixture's own Minmus orbit rotation.
public class CelestialFramesTests
{
    const double Deg = Math.PI / 180;

    // The fixture's Minmus orbit frame (KerbolFixture: Rz(LAN 78°) · Rx(inc 6°) · Rz(argPe 38°)).
    static QuaternionD MinmusTilt() =>
        QuaternionD.AngleAxis(78, new Vector3d(0, 0, 1)) * QuaternionD.AngleAxis(6, new Vector3d(1, 0, 0))
        * QuaternionD.AngleAxis(38, new Vector3d(0, 0, 1));

    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance, string what)
    {
        Assert.AreEqual(0, (expected - actual).magnitude, tolerance, $"{what}: expected {expected}, got {actual}");
    }

    [Test]
    public void ToCommon_ComposesTheTiltsFromTheRootDown_RootLeftOut()
    {
        // 0 root, 1 planet, 2 moon.
        var parents = new[] { -1, 0, 1 };
        var root = QuaternionD.AngleAxis(30, new Vector3d(1, 0, 0));
        var planet = QuaternionD.AngleAxis(20, new Vector3d(0, 1, 0));
        var moon = MinmusTilt();
        var tilts = new[] { root, planet, moon };
        var x = new Vector3d(1, 2, 3);

        AssertVector(x, CelestialFrames.ToCommon(0, parents, tilts) * x, 1e-12, "root");
        AssertVector(planet * x, CelestialFrames.ToCommon(1, parents, tilts) * x, 1e-12, "planet");
        // As PatchedConicsOrbit.GetPositionAtUTInStarSpace: L = tilt(planet) · tilt(moon).
        AssertVector(planet * (moon * x), CelestialFrames.ToCommon(2, parents, tilts) * x, 1e-12, "moon");
        AssertVector(x, CelestialFrames.ToLocal(CelestialFrames.ToCommon(2, parents, tilts), CelestialFrames.ToCommon(2, parents, tilts) * x), 1e-12, "back");
    }

    [Test]
    public void InLocalFrame_IdentityKeepsTheGuidance_OthersAnswerInTheBodysFrame()
    {
        var asymptote = new AsymptoteGuidance(new Vector3d(-200, -100, 10));
        Assert.AreSame(asymptote, CelestialFrames.InLocalFrame(asymptote, QuaternionD.identity));

        var q = MinmusTilt();
        double mu = 1.7658e9;
        var rC = new Vector3d(160_000, 0, 0);
        var vC = new Vector3d(0, 80, 60);
        var expected = asymptote.DeltaVToGo(rC, vC, 0, mu);
        var local = CelestialFrames.InLocalFrame(asymptote, q);
        var got = local.DeltaVToGo(CelestialFrames.ToLocal(q, rC), CelestialFrames.ToLocal(q, vC), 0, mu);
        AssertVector(CelestialFrames.ToLocal(q, expected), got, 1e-9, "asymptote");

        // A target orbit stays a target orbit (TargetOrbitBurn validates it), its plane turned.
        var plane = new Vector3d(0, -0.6, 0.8);
        var orbit = new OrbitTargetGuidance(new TargetOrbit { Ra = 200_000, Rp = 160_000, PlaneNormal = plane });
        var turned = CelestialFrames.InLocalFrame(orbit, q) as OrbitTargetGuidance;
        Assert.IsNotNull(turned);
        AssertVector(CelestialFrames.ToLocal(q, plane), turned.Target.PlaneNormal, 1e-12, "plane");
        Assert.AreEqual(200_000, turned.Target.Ra);
    }

    // The pilot's way: the vessel read in Minmus's frame, turned into the shared frame (snapshot),
    // the plan searched there, its injection guidance turned back for TargetOrbitBurn, which flies it
    // from the state in Minmus's frame. The result, in the shared frame, reaches Kerbin's 100 km.
    [Test]
    public void MinmusToKerbin_TiltedMinmusFrame_InjectionReachesThePlannedPeriapsis()
    {
        var s = KerbolFixture.Build();
        int minmus = s["Minmus"].Index, kerbin = s["Kerbin"].Index;
        var q = MinmusTilt();
        double mu = s[minmus].Mu, r0 = s[minmus].Radius + 100_000, speed = Math.Sqrt(mu / r0);
        // As in game: a near-polar circular orbit, 80° in Minmus's frame.
        var rLocal = new Vector3d(r0, 0, 0);
        var vLocal = new Vector3d(0, speed * Math.Cos(80 * Deg), speed * Math.Sin(80 * Deg));

        double pe = FlyInjection(s, minmus, kerbin, q * rLocal, q * vLocal, rLocal, vLocal, q, out var plan);
        TestContext.Out.WriteLine($"shared frame: Kerbin periapsis {(pe - 600_000) / 1000:F1} km, plan {plan.TotalDeltaV:F1} m/s");
        Assert.AreEqual(700_000, pe, 5_000);

        // The failure, for the record: the same vessel with its Minmus-frame vectors taken as the
        // shared frame's (the code before 2026-11-18).
        double wrong = FlyInjection(s, minmus, kerbin, rLocal, vLocal, rLocal, vLocal, QuaternionD.identity, out _, q);
        TestContext.Out.WriteLine($"Minmus frame taken as Kerbin's: Kerbin periapsis {(wrong - 600_000) / 1000:F1} km");
        Assert.Greater(Math.Abs(wrong - 700_000), 1_000_000);
    }

    // Plans from (rC, vC) in the shared frame, flies the injection with the guidance turned by
    // frameUsed from the state (rL, vL) in Minmus's frame, and returns the Kerbin periapsis of the
    // real trajectory (the real frame is trueFrame, default frameUsed).
    static double FlyInjection(BodySystem s, int minmus, int kerbin, Vector3d rC, Vector3d vC, Vector3d rL, Vector3d vL,
        QuaternionD frameUsed, out TransferPlan plan, QuaternionD? trueFrame = null)
    {
        var real = trueFrame ?? frameUsed;
        double mu = s[minmus].Mu;
        plan = TransferSearch.Find(s, new TransferRequest
        {
            FromBody = minmus, ToBody = kerbin, R0 = rC, V0 = vC, UT0 = 0,
            ArrivalPeriapsisRadius = 700_000, ArrivalApoapsisRadius = 700_000,
        }, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        int i = plan.Steps.FindIndex(st => st.Kind == StepKind.Injection);
        var step = plan.Steps[i];
        // The pilot propagates the snapshot to the burn time (shared frame), the burn reads the game's.
        KeplerPropagator.Propagate(rC, vC, mu, step.UT, out var rb, out var vb);
        KeplerPropagator.Propagate(rL, vL, mu, step.UT, out var rbL, out var vbL);
        var guidance = CelestialFrames.InLocalFrame(TransferFlight.GuidanceFor(plan, i, rb, vb, mu, s[minmus].Soi), frameUsed);
        var dvLocal = guidance.DeltaVToGo(rbL, vbL, step.UT, mu);
        var rTrue = real * rbL;
        var vTrue = real * (vbL + dvLocal);
        var patches = PatchedPropagator.Propagate(s, minmus, rTrue, vTrue, step.UT, step.UT + 30 * 86400.0, null, 3);
        var k = patches.FirstOrDefault(p => p.Body == kerbin);
        Assert.IsNotNull(k, "never reached Kerbin's SOI");
        return k.PeriapsisRadius;
    }
}
