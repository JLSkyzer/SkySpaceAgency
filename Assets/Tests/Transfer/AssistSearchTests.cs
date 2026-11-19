using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SkySpaceAgency.Node;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// Gravity-assist plans built from the actual state (4.6), flown through the patched conics.
public class AssistSearchTests
{
    BodySystem _s;
    int _kerbol, _kerbin, _eve, _moho, _mun, _minmus, _duna;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbol = _s["Kerbol"].Index;
        _kerbin = _s["Kerbin"].Index;
        _eve = _s["Eve"].Index;
        _moho = _s["Moho"].Index;
        _mun = _s["Mun"].Index;
        _minmus = _s["Minmus"].Index;
        _duna = _s["Duna"].Index;
    }

    static void Out(FormattableString text) => TestContext.Out.WriteLine(FormattableString.Invariant(text));

    // A 100 km circular equatorial Kerbin orbit at ut0, to a low orbit of 'to'.
    public static TransferRequest FromLko(BodySystem s, int to, double ut0 = AssistCases.UT0)
    {
        double mu = s["Kerbin"].Mu, r = AssistCases.ParkingRadius;
        return new TransferRequest
        {
            FromBody = s["Kerbin"].Index, ToBody = to, R0 = new Vector3d(r, 0, 0), V0 = new Vector3d(0, Math.Sqrt(mu / r), 0), UT0 = ut0,
        };
    }

    // The best grid candidate of the pinned case, built.
    static TransferPlan Pinned(BodySystem s, out AssistCandidate best)
    {
        var q = FromLko(s, s["Moho"].Index);
        int eve = s["Eve"].Index;
        var legs = TransferSearch.SiblingAssistLegs(s, q, eve, out var refusal);
        Assert.IsNull(refusal, refusal?.Refusal);
        var list = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1);
        Assert.IsNotEmpty(list);
        best = list[0];
        return TransferSearch.FindSiblingAssist(s, q, eve, best, CancellationToken.None);
    }

    static double Reserve(double sum) => Math.Max(10, 0.02 * sum);

    // ---------------------------------------------------------------- built plan

    [Test]
    public void SiblingAssist_KerbinMohoViaEve_Built()
    {
        var watch = Stopwatch.StartNew();
        var plan = Pinned(_s, out var best);
        Out($"grid {best.Cost:F1} m/s, built {plan.TotalDeltaV:F1} m/s ({plan.Refusal}), {watch.ElapsedMilliseconds} ms");
        foreach (var st in plan.Steps)
            Out($"  leg {st.LegIndex} {st.Kind} {_s[st.Body].Name} UT {st.UT:F0} {st.DeltaVMagnitude:F1} m/s: {st.Explanation}");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(AssistKind.Sibling, plan.Assist);
        Assert.AreEqual(_eve, plan.FlybyBody);
        StringAssert.EndsWith(" via Eve", plan.CaseName);

        // Decision 7: leg 0 injection, exit, 3 corrections at Eve; leg 1 flyby, exit; leg 2 corrections, capture.
        var leg0 = plan.Steps.Where(st => st.LegIndex == 0).ToList();
        var leg1 = plan.Steps.Where(st => st.LegIndex == 1).ToList();
        var leg2 = plan.Steps.Where(st => st.LegIndex == 2).ToList();
        Assert.AreEqual(3, leg0.Count(st => st.Kind == StepKind.Correction));
        Assert.IsTrue(leg0.Where(st => st.Kind == StepKind.Correction).All(st => st.TargetBody == _eve));
        Assert.IsTrue(leg0.Any(st => st.Kind == StepKind.Injection));
        CollectionAssert.AreEqual(new[] { StepKind.Flyby, StepKind.Escape }, leg1.Select(st => st.Kind).ToArray());
        Assert.AreEqual(_eve, leg1[0].Body);
        Assert.AreEqual(_moho, leg1[0].TargetBody);
        Assert.Greater(leg1[0].TargetExcess.magnitude, 0);
        Assert.AreEqual(3, leg2.Count(st => st.Kind == StepKind.Correction));
        Assert.AreEqual(StepKind.Capture, leg2.Last().Kind);

        // The flyby periapsis aimed above Eve's atmosphere.
        var aim = leg0.First(st => st.Kind == StepKind.Correction);
        Assert.GreaterOrEqual(aim.TargetPeriapsisRadius, _s[_eve].LowOrbitRadius);
        Assert.AreEqual(1, aim.TargetPlaneNormal.magnitude, 1e-9);

        double reference = AssistCases.AssistReference + Reserve(AssistCases.AssistReference);
        Assert.AreEqual(reference, plan.TotalDeltaV, 0.10 * reference);
    }

    // ---------------------------------------------------------------- flown

    sealed class Flight
    {
        public int Body;
        public Vector3d R, V;
        public double UT;
        public double Flown;
    }

    void CoastTo(Flight f, double ut)
    {
        if (ut <= f.UT)
            return;
        var patches = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, ut, null, 8);
        var last = patches.Last();
        Assert.AreNotEqual(PatchEnd.Impact, last.End, $"impact on {_s[last.Body].Name}");
        f.Body = last.Body;
        f.R = last.EndR;
        f.V = last.EndV;
        f.UT = last.EndUT;
    }

    // Flies the plan's burns from step 0: planned vectors as planned, corrections solved in turn with
    // ArrivalTargeting (the pilot's overload), the flyby at the propagated periapsis through its
    // guidance. Stops before the capture and returns the arrival patch around the target.
    Patch Fly(TransferPlan plan, Flight f, int finalBody)
    {
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var st = plan.Steps[i];
            if (st.Kind == StepKind.Capture)
                break;
            if (st.Kind == StepKind.Escape)
                continue;
            if (st.Kind == StepKind.Flyby)
            {
                var ahead = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, st.UT + 0.5 * KerbinDay * 20, null, 6);
                var inside = ahead.FirstOrDefault(p => p.Body == st.Body);
                Assert.IsNotNull(inside, "the flyby SOI is entered");
                Out($"  flyby periapsis {(inside.PeriapsisRadius - _s[st.Body].Radius) / 1000:F1} km at UT {inside.PeriapsisUT:F0} (planned {st.UT:F0})");
                Assert.GreaterOrEqual(inside.PeriapsisRadius, _s[st.Body].LowOrbitRadius - 10_000);
                CoastTo(f, inside.PeriapsisUT);
                Assert.AreEqual(st.Body, f.Body);
                if (TransferFlight.IsBurn(st))
                {
                    var g = TransferFlight.GuidanceFor(st, f.R, f.V, _s[st.Body].Mu, _s[st.Body].Soi);
                    var dv = g.DeltaVToGo(f.R, f.V, f.UT, _s[st.Body].Mu);
                    Out($"  flyby burn {dv.magnitude:F1} m/s (planned {st.DeltaVMagnitude:F1})");
                    f.V += dv;
                    f.Flown += dv.magnitude;
                }
                continue;
            }
            CoastTo(f, st.UT);
            Assert.AreEqual(st.Body, f.Body, $"step {i} {st.Kind} flown in {_s[f.Body].Name}");
            if (st.DeltaV.sqrMagnitude > 0)
            {
                f.V += st.DeltaV;
                f.Flown += st.DeltaV.magnitude;
                continue;
            }
            // A correction: the next arrival of its leg (the flyby, else the plan's arrival).
            var next = plan.Steps.Skip(i + 1).FirstOrDefault(x => x.Kind == StepKind.Flyby || x.Kind == StepKind.Capture);
            double planned = next?.UT ?? plan.ArrivalUT;
            double horizon = planned + 0.25 * (planned - f.UT);
            bool ok = ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, st.TargetBody, st.TargetPeriapsisRadius, st.TargetInclinationDeg,
                st.TargetPlaneNormal, horizon, planned, double.NaN, ArrivalTargeting.MaxDeltaV, out var c, out double pe, out _, out _, out _, out _);
            Out($"  correction {i} toward {_s[st.TargetBody].Name}: ok {ok}, {c.magnitude:F2} m/s, Pe {(pe - _s[st.TargetBody].Radius) / 1000:F1} km");
            // The pilot's rule: under 1 m/s, skipped only when the path already meets the wanted periapsis.
            double without = TransferFlight.PeriapsisWithout(_s, f.Body, f.R, f.V, f.UT, st.TargetBody, horizon);
            var verdict = TransferFlight.JudgeCorrection(c.magnitude, true, pe, st.TargetPeriapsisRadius, without,
                _s[st.TargetBody].LowOrbitRadius - 10_000, out _);
            if (ok && verdict == CorrectionVerdict.Fly)
            {
                f.V += c;
                f.Flown += c.magnitude;
            }
        }
        var rest = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, plan.ArrivalUT + 30 * KerbinDay, null, 6);
        return rest.FirstOrDefault(p => p.Body == finalBody);
    }

    const double KerbinDay = 21_600;

    [Test]
    public void SiblingAssist_FlownWithItsCorrections_ReachesMoho()
    {
        var plan = Pinned(_s, out _);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        var q = plan.Request;
        var f = new Flight { Body = q.FromBody, R = q.R0, V = q.V0, UT = q.UT0 };
        var arrival = Fly(plan, f, _moho);
        Assert.IsNotNull(arrival, "Moho is reached");
        double wanted = _s[_moho].LowOrbitRadius;
        double capture = plan.Steps.Last(st => st.Kind == StepKind.Capture).DeltaVMagnitude;
        Out($"Moho periapsis {(arrival.PeriapsisRadius - _s[_moho].Radius) / 1000:F2} km (wanted {(wanted - _s[_moho].Radius) / 1000:F0}), flown {f.Flown:F1} m/s + capture {capture:F1} against the plan's {plan.TotalDeltaV:F1}");
        Assert.AreEqual(wanted, arrival.PeriapsisRadius, 5000);
        Assert.LessOrEqual(f.Flown + capture, plan.TotalDeltaV);
    }

    // ---------------------------------------------------------------- frames, special cases

    static QuaternionD Turn() => QuaternionD.AngleAxis(50, new Vector3d(0, 0, 1)) * QuaternionD.AngleAxis(30, new Vector3d(1, 0, 0));

    [Test]
    public void Assist_TurnedFlybyFrame_SameTotal()
    {
        var plain = Pinned(_s, out var best);
        Assert.IsTrue(plain.Ok, plain.Refusal);
        var turned = KerbolFixture.Build();
        var q = Turn();
        turned[_eve].FrameToCommon = q;
        var plan = TransferSearch.FindSiblingAssist(turned, FromLko(turned, _moho), _eve, best, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(plain.TotalDeltaV, plan.TotalDeltaV, 1e-3);
        var n0 = plain.Steps.First(st => st.Kind == StepKind.Correction && st.TargetBody == _eve).TargetPlaneNormal;
        var n1 = plan.Steps.First(st => st.Kind == StepKind.Correction && st.TargetBody == _eve).TargetPlaneNormal;
        Assert.AreEqual(0, (CelestialFrames.ToLocal(q, n0) - n1).magnitude, 1e-9);
    }

    [Test]
    public void SiblingAssist_FlybyBodyIsTheDeparture_OneRevolution()
    {
        var q = FromLko(_s, _moho);
        var legs = TransferSearch.SiblingAssistLegs(_s, q, _kerbin, out var refusal);
        Assert.IsNull(refusal, refusal?.Refusal);
        CollectionAssert.Contains(legs.Revolutions1, 1);
        var list = AssistPorkchop.Search(legs, CancellationToken.None, null, 0, 1);
        Out($"{list.Count} candidate(s) via Kerbin, best {(list.Count > 0 ? list[0].Cost : double.NaN):F1} m/s, {(list.Count > 0 ? list[0].Revolutions1 : -1)} revolution(s)");
        if (list.Count == 0)
            return;
        var plan = TransferSearch.FindSiblingAssist(_s, q, _kerbin, list[0], CancellationToken.None);
        Out($"built: {(plan.Ok ? FormattableString.Invariant($"{plan.TotalDeltaV:F1} m/s") : plan.Refusal)}");
        if (plan.Ok)
        {
            Assert.AreEqual(AssistKind.Sibling, plan.Assist);
            Assert.AreEqual(_kerbin, plan.FlybyBody);
        }
        else
            Assert.IsFalse(string.IsNullOrEmpty(plan.Refusal));
    }

    [Test]
    public void SiblingAssist_UnplannedSoiOnTheWay_Warned()
    {
        var plain = Pinned(_s, out var best);
        Assert.IsTrue(plain.Ok, plain.Refusal);
        // A body on the cruise after the flyby, halfway to Moho, where the plan never goes.
        var last = plain.FinalLegRequest;
        Assert.IsNotNull(last, "the post-flyby request");
        double mid = 0.5 * (last.UT0 + plain.ArrivalUT);
        KeplerPropagator.Propagate(last.R0, last.V0, _s[_kerbol].Mu, mid - last.UT0, out var rMid, out _);
        var nodes = Enumerable.Range(0, _s.Count).Select(i => _s[i]).ToList();
        double mu = _s[_kerbol].Mu, rm = rMid.magnitude;
        var rock = new BodyNode
        {
            Name = "Rock", Index = nodes.Count, ParentIndex = _kerbol, Mu = 1e9, Radius = 10_000, Soi = 2e9,
            EpochUT = mid, EpochPosition = rMid,
            EpochVelocity = Math.Sqrt(mu / rm) * Vector3d.Cross(new Vector3d(0, 0, 1), rMid).normalized,
        };
        nodes.Add(rock);
        var s = new BodySystem(nodes);
        var plan = TransferSearch.FindSiblingAssist(s, FromLko(s, _moho), _eve, best, CancellationToken.None);
        Out($"{(plan.Ok ? "ok" : plan.Refusal)}; warnings: {string.Join(" | ", plan.Warnings)}");
        Assert.IsTrue(plan.Warnings.Any(w => w.Contains("Rock")) || !plan.Ok && plan.Refusal.Contains("Rock"),
            "the unplanned SOI is named");
    }
}
