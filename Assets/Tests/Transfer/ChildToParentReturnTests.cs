using System;
using System.Linq;
using System.Threading;
using SkySpaceAgency.OrbitPlanning;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The end of the 2026-11-18 in-game Minmus -> Kerbin return (Redux 26w46a, Player.log of that day):
// after the escape, the vessel was around Kerbin on an outbound hyperbola (r 49 047 067 m,
// 423.35 m/s, e 1.383225, a -100 283 398 m, periapsis 37 831 km behind it). These tests pin down
// what the pilot's pure rules do from there: the diagnostics at the SOI change, the correction, the
// resume, and the orbit at Kerbin planned instead of a refusal.
public class ChildToParentReturnTests
{
    BodySystem _s;
    int _kerbin, _minmus;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbin = _s["Kerbin"].Index;
        _minmus = _s["Minmus"].Index;
        TransferSearch.Trace = line => TestContext.Out.WriteLine(line);
    }

    [TearDown]
    public void TearDown() => TransferSearch.Trace = null;

    // A conic of eccentricity e and semi-major axis a (m), at radius r, outbound (or inbound) in the
    // xy plane: the in-game state after the Minmus escape.
    static void Conic(double mu, double a, double e, double r, bool outbound, out Vector3d rv, out Vector3d vv)
    {
        double p = a * (1 - e * e);
        double nu = Math.Acos(Math.Max(-1, Math.Min(1, (p / r - 1) / e))) * (outbound ? 1 : -1);
        double k = Math.Sqrt(mu / p);
        rv = new Vector3d(r * Math.Cos(nu), r * Math.Sin(nu), 0);
        var radial = new Vector3d(Math.Cos(nu), Math.Sin(nu), 0);
        var along = new Vector3d(-Math.Sin(nu), Math.Cos(nu), 0);
        vv = k * e * Math.Sin(nu) * radial + k * (1 + e * Math.Cos(nu)) * along;
    }

    void InGameEscape(out Vector3d r, out Vector3d v) =>
        Conic(_s[_kerbin].Mu, -100_283_398, 1.383225, 49_047_067, true, out r, out v);

    // ---------------------------------------------------------------- problem 2: the SOI change

    [Test]
    public void EnergySince_TheBurn_IsNotComparedAcrossAnSoiChange()
    {
        // In game: 24903.454 around Minmus at the burn's end, 17608.099 around Kerbin after the change.
        Assert.IsNaN(TransferFlight.EquivalentDeltaVSince(_minmus, 24903.454, _kerbin, 17608.099, 423.35));
        Assert.IsNaN(TransferFlight.EquivalentDeltaVSince(-1, 24903.454, _kerbin, 17608.099, 423.35));
        // The same body: as before (24903.454 -> 24910.731 at 267.9 m/s: 0.03 m/s).
        Assert.AreEqual(0.0272, TransferFlight.EquivalentDeltaVSince(_minmus, 24903.454, _minmus, 24910.731, 267.9), 1e-3);
    }

    // ---------------------------------------------------------------- problem 3: the late SOI rule

    [Test]
    public void InsideTheChildsSoiRadius_MovingAway_IsNoEntry()
    {
        // In game: 2247427 m from Minmus (SOI 2247428 m), rate +225.9 m/s, right after the game's exit.
        Assert.IsFalse(TransferFlight.InsideChildIsPendingEntry(225.9));
        Assert.IsFalse(TransferFlight.InsideChildIsPendingEntry(0));
        Assert.IsTrue(TransferFlight.InsideChildIsPendingEntry(-12));
        Assert.IsTrue(TransferFlight.InsideChildIsPendingEntry(double.NaN));
    }

    // ---------------------------------------------------------------- problem 4: the correction

    [Test]
    public void Correction_InTheTargetsSoiPastItsPeriapsis_IsNotSolved()
    {
        InGameEscape(out var r, out var v);
        double mu = _s[_kerbin].Mu;
        Assert.AreEqual(38_431_130, OrbitMath.Elements(r, v, mu).PeriapsisRadius, 2_000, "the in-game conic");
        Assert.IsFalse(ArrivalTargeting.PeriapsisAhead(r, v, mu, _s[_kerbin].Soi));
        bool ok = ArrivalTargeting.Solve(_s, _kerbin, r, v, 0, _kerbin, 700_000, double.NaN, 2 * 86400, 188_458, double.NaN,
            out var dv, out double pe, out _, out double arrival, out _, out bool converged);
        TestContext.Out.WriteLine($"solve: {ok}, {dv.magnitude:F1} m/s, Pe {pe:F0}, arrival {arrival:F0}, converged {converged}");
        Assert.IsFalse(ok, "an arrival before the solve's time was returned");
    }

    [Test]
    public void Correction_InTheTargetsSoiInbound_AimsThePeriapsis()
    {
        // The path a good Minmus escape gives: an ellipse falling from Minmus's distance, its Pe off.
        double mu = _s[_kerbin].Mu, rp = 2_600_000, ra = 47_000_000;
        Conic(mu, (rp + ra) / 2, (ra - rp) / (ra + rp), 40_000_000, false, out var r, out var v);
        Assert.IsTrue(ArrivalTargeting.PeriapsisAhead(r, v, mu, _s[_kerbin].Soi));
        bool ok = ArrivalTargeting.Solve(_s, _kerbin, r, v, 0, _kerbin, 700_000, double.NaN, 3 * 86400, double.NaN, double.NaN,
            out var dv, out double pe, out _, out _, out _, out bool converged);
        TestContext.Out.WriteLine($"solve: {ok}, {dv.magnitude:F1} m/s, Pe {pe:F0}, converged {converged}");
        Assert.IsTrue(ok && converged);
        Assert.AreEqual(700_000, pe, 2_000);
    }

    // ---------------------------------------------------------------- problem 5: the orbit at the target

    TransferPlan InTarget(Vector3d r, Vector3d v, double pe = 700_000, double ap = 700_000) =>
        TransferSearch.Find(_s, new TransferRequest
        {
            FromBody = _kerbin, ToBody = _kerbin, R0 = r, V0 = v, UT0 = 1000,
            ArrivalPeriapsisRadius = pe, ArrivalApoapsisRadius = ap,
        }, CancellationToken.None);

    [Test]
    public void SearchInTheTargetsSoi_FromTheInGameEscape_CapturesThenReachesTheOrbit()
    {
        InGameEscape(out var r, out var v);
        var plan = InTarget(r, v);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.InTarget, plan.CaseName);
        Assert.IsTrue(plan.Steps.All(st => st.Kind == StepKind.OrbitChange && st.Body == _kerbin && st.TargetBody == _kerbin));
        Assert.IsTrue(TransferFlight.EndsInOrbit(plan));
        TestContext.Out.WriteLine(string.Join("; ", plan.Steps.Select(st => $"{st.Explanation} {st.DeltaVMagnitude:F1} m/s at +{st.UT - 1000:F0} s")));
        // In game the Orbit tab captured with 384.1 m/s; the first burn leaves the planner's lead.
        Assert.That(plan.Steps[0].DeltaVMagnitude, Is.InRange(330, 440));
        Assert.GreaterOrEqual(plan.Steps[0].UT, 1000 + TransferSearch.InTargetLeadSeconds);

        // The burns flown as impulses: a 700 km circular orbit around Kerbin.
        var burns = plan.Steps.Select(st => new Impulse(st.UT, st.DeltaV)).ToList();
        double end = plan.ArrivalUT + 60;
        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 1000, end, burns);
        var last = patches.Last();
        Assert.AreEqual(_kerbin, last.Body);
        KeplerPropagator.Propagate(last.StartR, last.StartV, _s[_kerbin].Mu, end - last.StartUT, out var rf, out var vf);
        var el = OrbitMath.Elements(rf, vf, _s[_kerbin].Mu);
        TestContext.Out.WriteLine($"final Pe {(el.PeriapsisRadius - 600_000) / 1000:F1} km, Ap {(el.ApoapsisRadius - 600_000) / 1000:F1} km");
        Assert.AreEqual(700_000, el.PeriapsisRadius, 5_000);
        Assert.AreEqual(700_000, el.ApoapsisRadius, 5_000);
    }

    [Test]
    public void SearchInTheTargetsSoi_AlreadyThere_SaysSo()
    {
        double mu = _s[_kerbin].Mu;
        var plan = InTarget(new Vector3d(700_000, 0, 0), new Vector3d(0, Math.Sqrt(mu / 700_000), 0));
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("Already in the requested orbit around Kerbin.", plan.Refusal);
    }

    [Test]
    public void SearchInTheTargetsSoi_TurnedFrame_InclinationIsTheBodys()
    {
        // A target body whose frame is turned: the inclination asked for is the body's own.
        var nodes = Enumerable.Range(0, _s.Count).Select(i => _s[i]).ToList();
        var q = QuaternionD.AngleAxis(40, new Vector3d(1, 0, 0));
        nodes[_minmus].FrameToCommon = q;
        var s = new BodySystem(nodes);
        double mu = s[_minmus].Mu, r0 = 160_000, speed = Math.Sqrt(mu / r0);
        // Equatorial in Minmus's frame, given in the shared one.
        var plan = TransferSearch.Find(s, new TransferRequest
        {
            FromBody = _minmus, ToBody = _minmus, R0 = q * new Vector3d(r0, 0, 0), V0 = q * new Vector3d(0, speed, 0), UT0 = 0,
            ArrivalPeriapsisRadius = 160_000, ArrivalApoapsisRadius = 160_000, ArrivalInclinationDeg = 0,
        }, CancellationToken.None);
        Assert.IsFalse(plan.Ok, "an equatorial orbit in the body's frame was taken as 40° inclined");
        Assert.AreEqual("Already in the requested orbit around Minmus.", plan.Refusal);
    }

    [Test]
    public void Resume_AfterAFailedCorrection_InTheTargetsSoiWithNoPeriapsisAhead_PlansAgain()
    {
        var plan = new TransferPlan { Ok = true };
        plan.Steps.Add(new PlanStep { Kind = StepKind.Escape, Body = _minmus });
        plan.Steps.Add(new PlanStep { Kind = StepKind.Correction, Body = _kerbin, TargetBody = _kerbin, TargetPeriapsisRadius = 700_000 });
        var saved = new SavedPlan { Plan = plan, Phase = TransferPhase.Failed, StepIndex = 1 };
        Assert.AreEqual(ResumeAction.CorrectExisting, TransferFlight.DecideResume(saved, ResumeCheck.Resume()));

        Assert.AreEqual(ResumeAction.Replan, TransferFlight.InTargetSoi(ResumeAction.CorrectExisting, saved, _kerbin, false));
        Assert.AreEqual(ResumeAction.CorrectExisting, TransferFlight.InTargetSoi(ResumeAction.CorrectExisting, saved, _kerbin, true));
        Assert.AreEqual(ResumeAction.CorrectExisting, TransferFlight.InTargetSoi(ResumeAction.CorrectExisting, saved, _minmus, false));
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.InTargetSoi(ResumeAction.Continue, saved, _kerbin, false));
    }
}
