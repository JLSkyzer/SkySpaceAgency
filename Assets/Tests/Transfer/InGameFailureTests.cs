using System;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The 2026-10-25 in-game Kerbin -> Duna flight (log Player-2026-10-25.log, lines 16128-17410):
// the Kerbin SOI was left ~29 900 s before both predictions (at ~54 Mm, not 84 Mm), then
// correction 1 found no burn under 300 m/s to Duna. These tests rebuild the plausible states with
// the stock-like fixture (external data) and pin down what the pure code does from them.
public class InGameFailureTests
{
    const double KerbinDay = 21600;

    BodySystem _s;
    int _kerbin, _kerbol, _duna;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbin = _s["Kerbin"].Index;
        _kerbol = _s["Kerbol"].Index;
        _duna = _s["Duna"].Index;
    }

    // As in game: Duna periapsis at 200 km (radius 520 km), circular capture.
    TransferPlan Plan()
    {
        double radius = 700_000, speed = Math.Sqrt(_s[_kerbin].Mu / radius);
        var q = new TransferRequest
        {
            FromBody = _kerbin, ToBody = _duna, R0 = new Vector3d(radius, 0, 0), V0 = new Vector3d(0, speed, 0),
            ArrivalPeriapsisRadius = 520_000, ArrivalApoapsisRadius = 520_000,
        };
        var plan = TransferSearch.Find(_s, q, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        return plan;
    }

    // The vessel relative to Kerbin right after the injection, flown 'shortBy' m/s short.
    void AfterInjection(TransferPlan plan, double shortBy, out Vector3d r, out Vector3d v, out double ut)
    {
        var inj = plan.Steps.First(st => st.Kind == StepKind.Injection);
        KeplerPropagator.Propagate(plan.Request.R0, plan.Request.V0, _s[_kerbin].Mu, inj.UT - plan.Request.UT0, out r, out v);
        v += inj.DeltaV * (1 - shortBy / inj.DeltaV.magnitude);
        ut = inj.UT;
    }

    // At switchUT the vessel goes to the Kerbol frame with its Kerbin-relative state taken at
    // switchUT + shift (0: the true state, the SOI cut short; > 0: an orbit epoch ahead of time).
    void SwitchToKerbol(Vector3d r0, Vector3d v0, double ut0, double switchUT, double shift, out Vector3d r, out Vector3d v)
    {
        KeplerPropagator.Propagate(r0, v0, _s[_kerbin].Mu, switchUT + shift - ut0, out var rr, out var vr);
        _s.StateRelativeToParent(_kerbin, switchUT, out var kr, out var kv);
        r = kr + rr;
        v = kv + vr;
    }

    double TimeAtDistance(Vector3d r0, Vector3d v0, double ut0, double distance)
    {
        double lo = 0, hi = 1e6;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            KeplerPropagator.Propagate(r0, v0, _s[_kerbin].Mu, mid, out var r, out _);
            if (r.magnitude < distance) lo = mid; else hi = mid;
        }
        return ut0 + 0.5 * (lo + hi);
    }

    bool Solve(TransferPlan plan, Vector3d r, Vector3d v, double ut, double budget, string label, out double dv)
    {
        double horizon = plan.ArrivalUT + 0.25 * plan.TripSeconds;
        bool ok = ArrivalTargeting.Solve(_s, _kerbol, r, v, ut, _duna, 520_000, double.NaN, horizon, plan.ArrivalUT, 520_000, budget,
            out var d, out double pe, out _, out double arrival, out double capture, out bool converged);
        _s.StateRelativeToParent(_kerbin, ut, out var kr, out _);
        TestContext.Out.WriteLine($"{label}: ok {ok}, dv {d.magnitude:F2} m/s (budget {budget:F0}), Pe {(pe - 320_000) / 1000:F1} km, " +
            $"arrival {(arrival - plan.ArrivalUT) / KerbinDay:F1} d from planned, capture {capture:F1}, converged {converged}; " +
            $"|r| - |r Kerbin| {(r.magnitude - kr.magnitude) / 1e6:F1} Mm");
        dv = d.magnitude;
        return ok;
    }

    // Point 2 of the brief: the correction solver is not what failed. From every state an 8 m/s short
    // injection and an early SOI exit can produce (nominal exit, the SOI cut at 54 Mm with the true
    // state, the orbit's epoch ~29 900 s ahead), correction 1 costs 10-30 m/s. In game the vessel was
    // then 124 Mm farther from Kerbol than Kerbin's orbit (frame check at UT 359000964: |r| 13 723 816 729
    // m against Kerbin's 13 599 840 256 m); these states give about 5 Mm. The real heliocentric state
    // after the exit was far from what the managed orbit predicted: the state jump, not the solver.
    [Test]
    public void CorrectionOne_FromThePlausibleStates_IsSmall()
    {
        var plan = Plan();
        AfterInjection(plan, 8, out var r0, out var v0, out double ut0);
        double exit84 = TimeAtDistance(r0, v0, ut0, _s[_kerbin].Soi);
        double exit54 = TimeAtDistance(r0, v0, ut0, 54e6);
        double corUT = ut0 + 150_555; // as in game: the correction 150 555 s after the injection
        TestContext.Out.WriteLine($"SOI exit at 84 Mm after {exit84 - ut0:F0} s, at 54 Mm after {exit54 - ut0:F0} s (game: 77 318 predicted, 47 423 real)");

        foreach (var (label, switchUT, shift) in new[]
        {
            ("nominal exit", exit84, 0.0),
            ("SOI cut at 54 Mm, true state", exit54, 0.0),
            ("orbit epoch ahead by the gap", exit54, exit84 - exit54),
        })
        {
            SwitchToKerbol(r0, v0, ut0, switchUT, shift, out var r, out var v);
            KeplerPropagator.Propagate(r, v, _s[_kerbol].Mu, corUT - switchUT, out var rc, out var vc);
            Assert.IsTrue(Solve(plan, rc, vc, corUT, ArrivalTargeting.MaxDeltaV, label, out double dv), label);
            Assert.Less(dv, 50, label);
            _s.StateRelativeToParent(_kerbin, corUT, out var kr, out _);
            Assert.Less(Math.Abs(rc.magnitude - kr.magnitude), 20e6, $"{label}: near Kerbin's orbit radius, unlike the 124 Mm seen in game");
        }
    }

    // The budget of a correction of the existing path (resume after a failed correction).
    [Test]
    public void Solve_WithinABudget()
    {
        var plan = Plan();
        AfterInjection(plan, 8, out var r0, out var v0, out double ut0);
        double exit = TimeAtDistance(r0, v0, ut0, _s[_kerbin].Soi);
        SwitchToKerbol(r0, v0, ut0, exit, 0, out var r, out var v);
        double corUT = ut0 + 150_555;
        KeplerPropagator.Propagate(r, v, _s[_kerbol].Mu, corUT - exit, out var rc, out var vc);
        Assert.IsTrue(Solve(plan, rc, vc, corUT, ArrivalTargeting.MaxDeltaV, "300 m/s", out double needed));
        Assert.IsFalse(Solve(plan, rc, vc, corUT, 0.5 * needed, "half of it", out _), "a budget below the need refuses");
        Assert.IsTrue(Solve(plan, rc, vc, corUT, 2 * needed, "twice", out _));
    }

    // Point D: planning from an orbit of Kerbol that already meets Duna (or nearly) costs the
    // correction of that encounter, and picks it when it is cheaper than a new transfer.
    [Test]
    public void PlanFromTheParent_WithAnEncounter_CorrectsIt()
    {
        var plan = Plan();
        AfterInjection(plan, 3, out var r0, out var v0, out double ut0);
        double exit = TimeAtDistance(r0, v0, ut0, _s[_kerbin].Soi);
        SwitchToKerbol(r0, v0, ut0, exit, 0, out var r, out var v);
        double ut = exit + 5 * KerbinDay;
        KeplerPropagator.Propagate(r, v, _s[_kerbol].Mu, ut - exit, out var rk, out var vk);

        var q = new TransferRequest { FromBody = _kerbol, ToBody = _duna, R0 = rk, V0 = vk, UT0 = ut,
            ArrivalPeriapsisRadius = 520_000, ArrivalApoapsisRadius = 520_000 };
        string trace = "";
        TransferSearch.Trace = t => trace += t + "\n";
        TransferPlan replan;
        try
        {
            replan = TransferSearch.Find(_s, q, CancellationToken.None);
        }
        finally
        {
            TransferSearch.Trace = null;
        }
        TestContext.Out.WriteLine(TransferFlight.Describe(replan, _s, 0));
        TestContext.Out.WriteLine(string.Join("\n", trace.Split('\n').Where(l => l.StartsWith("existing"))));
        Assert.IsTrue(replan.Ok, replan.Refusal);
        Assert.AreEqual(TransferSearch.ExistingEncounterCase, replan.CaseName);
        var first = replan.Steps[0];
        Assert.AreEqual(StepKind.Correction, first.Kind);
        Assert.AreEqual(Vector3d.zero, first.DeltaV, "solved again in flight from the actual orbit, no saved vector");
        Assert.Less(first.DeltaVMagnitude, 50, "a small correction, not a new Lambert transfer");
        Assert.AreEqual(StepKind.Capture, replan.Steps.Last().Kind);
        Assert.IsTrue(replan.Warnings.Any(w => w.Contains("new transfer would cost")), "the other option is shown");
        Assert.IsFalse(TransferFlight.SavedVectorsAhead(replan, 0), "a resume can continue it");
    }

    // Point B: within our SOI radius of a child while still in the parent's frame, the propagator says
    // so instead of dropping the case.
    [Test]
    public void Propagate_StartInsideAChild_ReportsIt()
    {
        double ut = 1_000_000;
        _s.StateRelativeToParent(_duna, ut, out var dr, out var dv);
        var r = dr + new Vector3d(1e7, 0, 0);
        var v = dv + new Vector3d(-800, 0, 0);
        var patches = PatchedPropagator.Propagate(_s, _kerbol, r, v, ut, ut + 3600, null, 4);
        Assert.AreEqual(_duna, patches[0].StartInsideBody);
        Assert.IsFalse(patches.Any(p => p.End == PatchEnd.Encounter), "entered only after leaving it first");

        var outside = PatchedPropagator.Propagate(_s, _kerbol, dr + new Vector3d(1e8, 0, 0), dv, ut, ut + 3600, null, 4);
        Assert.AreEqual(-1, outside[0].StartInsideBody);
    }
}
