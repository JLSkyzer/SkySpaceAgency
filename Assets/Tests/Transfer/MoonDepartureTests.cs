using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

// The departure flyby of a moon (4.6): Kerbin -> Duna via the Mun, against the pinned reference.
public class MoonDepartureTests
{
    const double KerbinDay = 21_600;

    BodySystem _s;
    int _kerbol, _kerbin, _mun, _minmus, _duna;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbol = _s["Kerbol"].Index;
        _kerbin = _s["Kerbin"].Index;
        _mun = _s["Mun"].Index;
        _minmus = _s["Minmus"].Index;
        _duna = _s["Duna"].Index;
    }

    static void Out(FormattableString text) => TestContext.Out.WriteLine(FormattableString.Invariant(text));

    static QuaternionD Turn() => QuaternionD.AngleAxis(50, new Vector3d(0, 0, 1)) * QuaternionD.AngleAxis(30, new Vector3d(1, 0, 0));

    // The departure burns of a plan: every burn before the first step around the Sun.
    double DepartureCost(TransferPlan plan) =>
        plan.Steps.TakeWhile(st => st.Body != _kerbol).Where(st => st.DeltaV.sqrMagnitude > 0 || st.Kind == StepKind.Flyby)
            .Sum(st => st.DeltaVMagnitude);

    [Test]
    public void MoonDeparture_KerbinDunaViaMun_NearTheReference()
    {
        var q = AssistSearchTests.FromLko(_s, _duna, AssistCases.DunaUT0);
        var watch = Stopwatch.StartNew();
        var direct = TransferSearch.Find(_s, q, CancellationToken.None);
        var plan = TransferSearch.FindMoonDeparture(_s, q, _mun, CancellationToken.None);
        Out($"direct {direct.TotalDeltaV:F1} m/s (departure {DepartureCost(direct):F1}); via the Mun {(plan.Ok ? FormattableString.Invariant($"{plan.TotalDeltaV:F1}") : plan.Refusal)} (departure {DepartureCost(plan):F1}), {watch.ElapsedMilliseconds} ms");
        foreach (var st in plan.Steps)
            Out($"  leg {st.LegIndex} {st.Kind} {_s[st.Body].Name} UT {st.UT:F0} {st.DeltaVMagnitude:F1} m/s: {st.Explanation}");
        Assert.IsTrue(direct.Ok, direct.Refusal);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(AssistKind.Departure, plan.Assist);
        Assert.AreEqual(_mun, plan.FlybyBody);
        // Decision 7: leg 0 injection and corrections at the Mun; leg 1 flyby, Mun exit; leg 2 the
        // re-aim burn, Kerbin exit, then the cruise.
        var leg0 = plan.Steps.Where(st => st.LegIndex == 0).ToList();
        var leg1 = plan.Steps.Where(st => st.LegIndex == 1).ToList();
        var leg2 = plan.Steps.Where(st => st.LegIndex == 2).ToList();
        Assert.AreEqual(StepKind.Injection, leg0[0].Kind);
        Assert.IsTrue(leg0.Skip(1).All(st => st.Kind == StepKind.Correction && st.TargetBody == _mun));
        Assert.GreaterOrEqual(leg0[1].TargetPeriapsisRadius, _s[_mun].LowOrbitRadius);
        CollectionAssert.AreEqual(new[] { StepKind.Flyby, StepKind.Escape }, leg1.Select(st => st.Kind).ToArray());
        Assert.AreEqual(StepKind.Correction, leg2[0].Kind);
        Assert.AreEqual(_kerbin, leg2[0].Body);
        Assert.Greater(leg2[0].DeltaV.sqrMagnitude, 0);
        Assert.AreEqual(StepKind.Escape, leg2[1].Kind);
        Assert.AreEqual(LegReplan.AimExcess, TransferFlight.LegKind(plan, plan.Steps.IndexOf(leg2[0])));
        // The reference: a Mun departure costs (direct - saving), here more than the direct one.
        double saving = AssistCases.MunDepartureSaving;
        Assert.LessOrEqual(DepartureCost(plan), DepartureCost(direct) - saving + 0.1 * Math.Abs(saving) + 10);
    }

    sealed class Flight
    {
        public int Body;
        public Vector3d R, V;
        public double UT;
    }

    // The Kerbin SOI exit excess of a departure flyby plan, flown with its corrections, the flyby
    // burn and the re-aim burn through their guidance.
    Vector3d FlyDeparture(BodySystem s, TransferPlan plan)
    {
        var q = plan.Request;
        var f = new Flight { Body = q.FromBody, R = q.R0, V = q.V0, UT = q.UT0 };
        void Coast(double ut)
        {
            if (ut <= f.UT)
                return;
            var last = PatchedPropagator.Propagate(s, f.Body, f.R, f.V, f.UT, ut, null, 8).Last();
            Assert.AreNotEqual(PatchEnd.Impact, last.End);
            f.Body = last.Body;
            f.R = last.EndR;
            f.V = last.EndV;
            f.UT = last.EndUT;
        }
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var st = plan.Steps[i];
            if (st.Body == _kerbol)
                break;
            if (st.Kind == StepKind.Escape)
                continue;
            if (st.Kind == StepKind.Flyby)
            {
                var ahead = PatchedPropagator.Propagate(s, f.Body, f.R, f.V, f.UT, st.UT + 86_400, null, 6);
                var inside = ahead.FirstOrDefault(p => p.Body == st.Body);
                Assert.IsNotNull(inside, "the moon is met");
                Out($"  moon periapsis {(inside.PeriapsisRadius - s[st.Body].Radius) / 1000:F1} km at {inside.PeriapsisUT:F0} (planned {st.UT:F0})");
                Assert.GreaterOrEqual(inside.PeriapsisRadius, s[st.Body].LowOrbitRadius - 10_000);
                Coast(inside.PeriapsisUT);
                if (TransferFlight.IsBurn(st))
                    f.V += TransferFlight.GuidanceFor(st, f.R, f.V, s[st.Body].Mu, s[st.Body].Soi).DeltaVToGo(f.R, f.V, f.UT, s[st.Body].Mu);
                continue;
            }
            Coast(st.UT);
            Assert.AreEqual(st.Body, f.Body, $"step {i} {st.Kind}");
            if (st.Kind == StepKind.Correction && st.DeltaV.sqrMagnitude > 0)
            {
                var g = TransferFlight.GuidanceFor(plan, i, f.R, f.V, s[f.Body].Mu, s[f.Body].Soi);
                f.V += g.DeltaVToGo(f.R, f.V, f.UT, s[f.Body].Mu);
                continue;
            }
            if (st.DeltaV.sqrMagnitude > 0)
            {
                f.V += st.DeltaV;
                continue;
            }
            var next = plan.Steps.Skip(i + 1).First(x => x.Kind == StepKind.Flyby);
            double horizon = next.UT + 0.25 * (next.UT - f.UT);
            if (ArrivalTargeting.Solve(s, f.Body, f.R, f.V, f.UT, st.TargetBody, st.TargetPeriapsisRadius, st.TargetInclinationDeg,
                    st.TargetPlaneNormal, horizon, next.UT, double.NaN, ArrivalTargeting.MaxDeltaV, out var c, out _, out _, out _, out _, out _)
                && c.magnitude >= 0.01)
                f.V += c;
        }
        var exit = PatchedPropagator.Propagate(s, f.Body, f.R, f.V, f.UT, f.UT + 30 * KerbinDay, null, 4)
            .First(p => p.Body == _kerbin && p.End == PatchEnd.Escape);
        return Hyperbola.Asymptote(exit.EndR, exit.EndV, s[_kerbin].Mu);
    }

    [Test]
    public void MoonDeparture_FlownWithItsCorrections_LeavesWithTheExcess()
    {
        var plan = TransferSearch.FindMoonDeparture(_s, AssistSearchTests.FromLko(_s, _duna, AssistCases.DunaUT0), _mun, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        var excess = FlyDeparture(_s, plan);
        Out($"excess out {excess.magnitude:F2} m/s, {(excess - plan.RequiredExcess).magnitude:F3} m/s from the required one");
        Assert.AreEqual(0, (excess - plan.RequiredExcess).magnitude, 1);
    }

    [Test]
    public void MoonDeparture_NotEligible_FromTheMun()
    {
        double mu = _s[_mun].Mu, r = 300_000;
        var q = new TransferRequest { FromBody = _mun, ToBody = _duna, R0 = new Vector3d(r, 0, 0), V0 = new Vector3d(0, Math.Sqrt(mu / r), 0) };
        var plan = TransferSearch.FindMoonDeparture(_s, q, _minmus, CancellationToken.None);
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("No moon to fly by around Mun.", plan.Refusal);
    }

    // Review Focus 1 with the Mun turned (30° about x, then 50° about z). The plan named Minmus: its
    // departure flyby (about 1 000 m/s dearer than a direct departure, a 900+ m/s powered burn) gave
    // two different schedules, 28 m/s apart, with the frame turned, while one ArrivalTargeting solve
    // toward Minmus agrees to 1e-6 m/s in both frames (probed 2026-11-18): the flat, ill-conditioned
    // Minmus grid flips on rounding, not the frame handling. With the Mun, the two plans agree to
    // 0.94 m/s of 1 700 (0.06 %): the injection's aim at the Mun (three ArrivalTargeting solves from a
    // centre-aimed Lambert, stopped within 100 m on the B-plane) ends a little apart in each frame. A
    // frame mistake would be kilometres off (the Minmus return of 2026-11-18: 37 831 km), so the
    // bound is 0.1 % on the total and 0.5° on the aimed plane.
    [Test]
    public void MoonDeparture_TurnedMoonFrame_SameTotal()
    {
        var plain = TransferSearch.FindMoonDeparture(_s, AssistSearchTests.FromLko(_s, _duna, AssistCases.DunaUT0), _mun, CancellationToken.None);
        var turned = KerbolFixture.Build();
        turned[_mun].FrameToCommon = Turn();
        var plan = TransferSearch.FindMoonDeparture(turned, AssistSearchTests.FromLko(turned, _duna, AssistCases.DunaUT0), _mun, CancellationToken.None);
        Out($"plain {(plain.Ok ? FormattableString.Invariant($"{plain.TotalDeltaV:F3}") : plain.Refusal)}, turned {(plan.Ok ? FormattableString.Invariant($"{plan.TotalDeltaV:F3}") : plan.Refusal)}");
        for (int i = 0; i < Math.Min(plain.Steps.Count, plan.Steps.Count); i++)
            Out($"  {plain.Steps[i].Kind} {plain.Steps[i].UT:F3} / {plan.Steps[i].UT:F3}: {plain.Steps[i].DeltaVMagnitude:F6} / {plan.Steps[i].DeltaVMagnitude:F6}");
        Assert.IsTrue(plain.Ok, plain.Refusal);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(plain.TotalDeltaV, plan.TotalDeltaV, 1e-3 * plain.TotalDeltaV);
        var n0 = plain.Steps.First(st => st.Kind == StepKind.Correction && st.TargetBody == _mun).TargetPlaneNormal;
        var n1 = plan.Steps.First(st => st.Kind == StepKind.Correction && st.TargetBody == _mun).TargetPlaneNormal;
        var back = CelestialFrames.ToLocal(Turn(), n0);
        double deg = Math.Atan2(Vector3d.Cross(back, n1).magnitude, Vector3d.Dot(back, n1)) * 180 / Math.PI;
        Out($"aimed planes {deg:F4}° apart");
        Assert.Less(deg, 0.5);
    }
}
