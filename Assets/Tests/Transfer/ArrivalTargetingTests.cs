using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class ArrivalTargetingTests
{
    const double Deg = Math.PI / 180;
    const double KerbinDay = 21600; // s (6 h)

    BodySystem _s;
    int _kerbin, _duna, _ike;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbin = _s["Kerbin"].Index;
        _duna = _s["Duna"].Index;
        _ike = _s["Ike"].Index;
    }

    // From a 100 km circular equatorial orbit of Kerbin, as in the search tests.
    TransferPlan FromLko(string to)
    {
        double radius = 700_000, speed = Math.Sqrt(_s[_kerbin].Mu / radius);
        var q = new TransferRequest
        {
            FromBody = _kerbin,
            ToBody = _s[to].Index,
            R0 = new Vector3d(radius, 0, 0),
            V0 = new Vector3d(0, speed, 0),
            UT0 = 0,
        };
        var watch = Stopwatch.StartNew();
        var plan = TransferSearch.Find(_s, q, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        TestContext.Out.WriteLine($"plan Kerbin -> {to}: {plan.CaseName}, total {plan.TotalDeltaV:F1} m/s (reserve {plan.ReserveDeltaV:F1}), "
            + $"trip {plan.TripSeconds / KerbinDay:F1} d, search {watch.Elapsed.TotalMilliseconds:F0} ms; steps "
            + string.Join("; ", plan.Steps.Select(st => $"{st.Kind}@{_s[st.Body].Name} leg {st.LegIndex} {st.DeltaVMagnitude:F1} m/s T+{(st.UT - plan.DepartureUT) / KerbinDay:F2} d"
                + (st.TargetBody >= 0 ? $" -> {_s[st.TargetBody].Name} Pe {st.TargetPeriapsisRadius / 1000:F0} km" : ""))));
        return plan;
    }

    TransferPlan KerbinToDuna()
    {
        var plan = FromLko("Duna");
        Assert.AreEqual(3, plan.Steps.Count(st => st.Kind == StepKind.Correction));
        return plan;
    }

    // The vessel's state, carried from one UT to the next through the patched conics.
    class Flight
    {
        public int Body;
        public Vector3d R, V;
        public double UT;
    }

    Flight Start(TransferPlan p) =>
        new Flight { Body = p.Request.FromBody, R = p.Request.R0, V = p.Request.V0, UT = p.Request.UT0 };

    void CoastTo(Flight f, double ut)
    {
        var patches = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, ut, null, 32);
        var last = patches[patches.Count - 1];
        Assert.AreEqual(PatchEnd.HorizonReached, last.End, $"coast to {ut} ended early ({last.End} at {last.EndUT})");
        f.Body = last.Body;
        f.R = last.EndR;
        f.V = last.EndV;
        f.UT = ut;
    }

    // Flies the plan's burns with a known Δv vector (the injection) up to, not including, 'ut'.
    void FlyBurnsTo(TransferPlan p, Flight f, double ut, double injectionError = 0)
    {
        foreach (var st in p.Steps.Where(x => x.UT < ut && x.DeltaV.magnitude > 0))
        {
            CoastTo(f, st.UT);
            Assert.AreEqual(st.Body, f.Body);
            var dv = st.DeltaV;
            if (st.Kind == StepKind.Injection && injectionError != 0)
                dv = dv * (1 + injectionError / dv.magnitude);
            f.V += dv;
        }
        CoastTo(f, ut);
    }

    // The arrival at the target, flown from the current state: the first patch around the target.
    Patch Arrival(Flight f, Vector3d dv, double horizonUT, int target)
    {
        var patches = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V + dv, f.UT, horizonUT, null, 8);
        return patches.FirstOrDefault(pa => pa.Body == target);
    }

    double InclinationDeg(Patch p) => OrbitMath.Elements(p.StartR, p.StartV, _s[p.Body].Mu).InclinationRad / Deg;

    // plannedUT NaN: the overload without a planned arrival (closest approach on a miss).
    bool Solve(Flight f, int target, double rp, double incDeg, double horizonUT, double plannedUT,
        out Vector3d dv, out double predRp, out double predInc, string label)
    {
        var watch = Stopwatch.StartNew();
        bool ok = double.IsNaN(plannedUT)
            ? ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, target, rp, incDeg, horizonUT, out dv, out predRp, out predInc)
            : ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, target, rp, incDeg, horizonUT, plannedUT, out dv, out predRp, out predInc);
        TestContext.Out.WriteLine($"{label}: ok {ok}, dv {dv.magnitude:F3} m/s, predicted Pe {predRp / 1000:F3} km (wanted {rp / 1000:F3}), "
            + $"inclination {predInc:F3}° (wanted {incDeg:F1}), solve {watch.Elapsed.TotalMilliseconds:F0} ms");
        return ok;
    }

    double MidCourse(TransferPlan p) => p.Steps.Where(st => st.Kind == StepKind.Correction).ElementAt(1).UT;
    double Horizon(TransferPlan p) => p.ArrivalUT + 0.25 * p.TripSeconds;

    // The planned time of a step's arrival at its target: the start of the next leg, or the plan's arrival.
    static double PlannedArrival(TransferPlan p, PlanStep step)
    {
        var next = p.Steps.FirstOrDefault(st => st.LegIndex > step.LegIndex);
        return next != null ? next.UT : p.ArrivalUT;
    }

    [Test]
    public void PerturbedKerbinDuna_CorrectedToPeriapsisWithin1Km()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, MidCourse(plan), injectionError: 5);
        Assert.AreEqual(_s["Kerbol"].Index, f.Body);
        double wanted = _s[_duna].LowOrbitRadius;
        var before = Arrival(f, Vector3d.zero, Horizon(plan), _duna);
        TestContext.Out.WriteLine(before == null ? "uncorrected: no Duna encounter" : $"uncorrected: Pe {before.PeriapsisRadius / 1000:F0} km");

        Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan), plan.ArrivalUT, out var dv, out double predRp, out _, "mid-course, 5 m/s error"));
        Assert.Greater(dv.magnitude, ArrivalTargeting.SkipThreshold, "a 5 m/s error is worth correcting");
        Assert.Less(dv.magnitude, 150);

        var after = Arrival(f, dv, Horizon(plan), _duna);
        Assert.IsNotNull(after, "the corrected path meets Duna");
        Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
        Assert.AreEqual(after.PeriapsisRadius, predRp, 1e-3);
    }

    [Test]
    public void InclinationTarget_Met_Within0_5Deg()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, plan.Steps.First(st => st.Kind == StepKind.Correction).UT);
        double wanted = _s[_duna].LowOrbitRadius;
        foreach (double inc in new[] { 60.0, 135.0 })
        {
            Assert.IsTrue(Solve(f, _duna, wanted, inc, Horizon(plan), plan.ArrivalUT, out var dv, out double predRp, out double predInc, $"first correction, inclination {inc}°"));
            var after = Arrival(f, dv, Horizon(plan), _duna);
            Assert.IsNotNull(after);
            Assert.AreEqual(inc, InclinationDeg(after), 0.5);
            Assert.AreEqual(inc, predInc, 0.5);
            Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
            Assert.Less(dv.magnitude, 150);
        }
    }

    // Without a planned arrival, a miss is recovered through the closest approach.
    [Test]
    public void NoEncounter_Recovers_IfWithinBudget()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, MidCourse(plan));
        // Push the vessel off course: no Duna SOI on the way.
        f.V += 15 * Vector3d.Cross(f.R, f.V).normalized + 10 * f.V.normalized;
        Assert.IsNull(Arrival(f, Vector3d.zero, Horizon(plan), _duna), "the push must cause a miss");

        double wanted = _s[_duna].LowOrbitRadius;
        Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan), double.NaN, out var dv, out double predRp, out _, "mid-course, after a miss"));
        Assert.LessOrEqual(dv.magnitude, ArrivalTargeting.MaxDeltaV);
        var after = Arrival(f, dv, Horizon(plan), _duna);
        Assert.IsNotNull(after);
        Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
    }

    // With the planned arrival, a miss comes back to the planned pass, not to whichever pass is nearest.
    [Test]
    public void NoEncounter_PlannedArrival_KeepsThePlannedPass()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, plan.Steps.First(st => st.Kind == StepKind.Correction).UT);
        double wanted = _s[_duna].LowOrbitRadius;
        Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan), plan.ArrivalUT, out var dv, out _, out _, "first correction, planned pass"));
        var after = Arrival(f, dv, Horizon(plan), _duna);
        Assert.IsNotNull(after);
        Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
        double offDays = (after.PeriapsisUT - plan.ArrivalUT) / KerbinDay;
        TestContext.Out.WriteLine($"arrival periapsis {offDays:F2} d from the planned one");
        Assert.Less(Math.Abs(offDays), 5);
    }

    [Test]
    public void Unreachable_ReturnsFalse()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, MidCourse(plan));
        f.V -= 2500 * f.V.normalized; // falling sunward, far from Duna's orbit
        Assert.IsNull(Arrival(f, Vector3d.zero, Horizon(plan), _duna));

        foreach (double planned in new[] { double.NaN, plan.ArrivalUT })
        {
            Assert.IsFalse(Solve(f, _duna, _s[_duna].LowOrbitRadius, double.NaN, Horizon(plan), planned, out var dv, out double predRp, out double predInc, $"unreachable, planned {planned}"));
            Assert.AreEqual(0, dv.magnitude);
            Assert.IsTrue(double.IsNaN(predRp));
            Assert.IsTrue(double.IsNaN(predInc));
        }
    }

    // Flies a plan's steps up to (not including) its first step of leg 'untilLeg', solving each
    // correction toward its step's target with the planned arrival of its leg. Returns the Δv flown.
    double FlyLegs(TransferPlan plan, Flight f, int untilLeg, List<string> log)
    {
        double flown = 0;
        int n = 0;
        foreach (var st in plan.Steps.Where(x => x.LegIndex < untilLeg))
        {
            if (st.Kind == StepKind.Capture || (st.Kind != StepKind.Correction && !(st.DeltaV.magnitude > 0)))
                continue;
            CoastTo(f, st.UT);
            Assert.AreEqual(st.Body, f.Body, $"{st.Kind} at {st.UT}");
            if (st.Kind == StepKind.Correction)
            {
                n++;
                bool solved = Solve(f, st.TargetBody, st.TargetPeriapsisRadius, st.TargetInclinationDeg, Horizon(plan), PlannedArrival(plan, st),
                    out var dv, out double predRp, out _, $"correction {n}");
                if (!solved)
                    TestContext.Out.WriteLine(string.Join("; ", log));
                Assert.IsTrue(solved, $"correction {n}");
                bool skipped = dv.magnitude < ArrivalTargeting.SkipThreshold;
                if (!skipped)
                {
                    f.V += dv;
                    flown += dv.magnitude;
                }
                var entry = Arrival(f, Vector3d.zero, Horizon(plan), st.TargetBody);
                log.Add($"correction {n} at T+{(st.UT - plan.DepartureUT) / KerbinDay:F1} d: {dv.magnitude:F3} m/s{(skipped ? " (skipped)" : "")}, "
                    + $"predicted Pe {predRp / 1000:F3} km, " + (entry == null ? "no SOI" : $"{_s[st.TargetBody].Name} SOI at T+{(entry.StartUT - plan.DepartureUT) / KerbinDay:F1} d"));
            }
            else
            {
                f.V += st.DeltaV;
                flown += st.DeltaV.magnitude;
                log.Add($"{st.Kind} {st.DeltaV.magnitude:F1} m/s");
            }
        }
        return flown;
    }

    // The whole plan, flown with impulses: the injection from the plan, the three corrections solved
    // here, and the capture computed at the arrival periapsis toward the capture step's target orbit,
    // as the pilot will (Task 11).
    [Test]
    public void EndToEnd_KerbinToDuna_Capture()
    {
        var watch = Stopwatch.StartNew();
        var plan = KerbinToDuna();
        var f = Start(plan);
        var log = new List<string>();
        double flown = FlyLegs(plan, f, int.MaxValue, log);
        Assert.AreEqual(3, log.Count(x => x.StartsWith("correction")));

        var capture = plan.Steps.Single(st => st.Kind == StepKind.Capture);
        Assert.AreEqual(_duna, capture.TargetBody);
        double wantedPe = capture.TargetPeriapsisRadius, wantedAp = capture.TargetApoapsisRadius;
        Assert.AreEqual(_s[_duna].LowOrbitRadius, wantedPe, 1e-6);

        var arrival = Arrival(f, Vector3d.zero, Horizon(plan), _duna);
        Assert.IsNotNull(arrival, "the corrected path meets Duna");
        Assert.Greater(arrival.PeriapsisUT, arrival.StartUT);
        Assert.Less(arrival.PeriapsisUT, arrival.EndUT);
        double mu = _s[_duna].Mu;
        KeplerPropagator.Propagate(arrival.StartR, arrival.StartV, mu, arrival.PeriapsisUT - arrival.StartUT, out var rCap, out var vPe);
        double rp = rCap.magnitude;
        double vWanted = Math.Sqrt(mu * (2 / rp - 2 / (rp + wantedAp)));
        var dvCap = vWanted * vPe.normalized - vPe;
        var vCap = vPe + dvCap;
        flown += dvCap.magnitude;
        log.Add($"capture {dvCap.magnitude:F1} m/s at Pe {rp / 1000:F3} km, T+{(arrival.PeriapsisUT - plan.DepartureUT) / KerbinDay:F2} d (planned {capture.DeltaVMagnitude:F1} m/s at T+{plan.TripSeconds / KerbinDay:F2} d)");

        var el = OrbitMath.Elements(rCap, vCap, mu);
        TestContext.Out.WriteLine(string.Join("; ", log));
        TestContext.Out.WriteLine($"final Duna orbit {el.PeriapsisRadius / 1000:F3} x {el.ApoapsisRadius / 1000:F3} km (wanted {wantedPe / 1000:F0} x {wantedAp / 1000:F0}), "
            + $"inclination {el.InclinationRad / Deg:F2}°; flown {flown:F1} m/s vs plan total {plan.TotalDeltaV:F1} (steps {plan.TotalDeltaV - plan.ReserveDeltaV:F1}, "
            + $"reserve {plan.ReserveDeltaV:F1}), {100 * (flown / plan.TotalDeltaV - 1):F2} %; whole test {watch.Elapsed.TotalMilliseconds:F0} ms");

        Assert.IsTrue(el.IsClosed);
        Assert.AreEqual(wantedPe, el.PeriapsisRadius, 2000);
        Assert.AreEqual(wantedAp, el.ApoapsisRadius, 2000);
        Assert.AreEqual(plan.TotalDeltaV, flown, 0.05 * plan.TotalDeltaV);
    }

    // A direct descent into a moon: the plan aims the Duna periapsis at Ike's orbit without budgeting
    // Ike's phasing, so the correction solved on entering the Duna SOI does the phasing.
    [Test]
    public void DirectDescent_KerbinToIke_CorrectionAtDunaEntry()
    {
        var plan = FromLko("Ike");
        var entryStep = plan.Steps.FirstOrDefault(st => st.Kind == StepKind.Correction && st.Body == _duna);
        Assert.IsNotNull(entryStep, "a direct descent, with a correction on entering the Duna SOI");
        Assert.AreEqual(_ike, entryStep.TargetBody);

        var f = Start(plan);
        var log = new List<string>();
        double flown = FlyLegs(plan, f, entryStep.LegIndex, log);

        // Coast to the actual Duna SOI entry (the step's UT is the plan's estimate of it).
        var patches = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, Horizon(plan), null, 8);
        var inDuna = patches.FirstOrDefault(pa => pa.Body == _duna);
        Assert.IsNotNull(inDuna, "the leg-0 corrections reach Duna");
        CoastTo(f, inDuna.StartUT + 60);
        Assert.AreEqual(_duna, f.Body);

        Assert.IsTrue(Solve(f, _ike, entryStep.TargetPeriapsisRadius, entryStep.TargetInclinationDeg, Horizon(plan), plan.ArrivalUT,
            out var dv, out double predRp, out _, "Duna SOI entry -> Ike"));
        var atIke = Arrival(f, dv, Horizon(plan), _ike);
        Assert.IsNotNull(atIke);
        Assert.AreEqual(entryStep.TargetPeriapsisRadius, atIke.PeriapsisRadius, 1000);

        var ikeCapture = plan.Steps.Last(st => st.Kind == StepKind.Capture);
        TestContext.Out.WriteLine(string.Join("; ", log));
        TestContext.Out.WriteLine($"Duna entry at T+{(inDuna.StartUT - plan.DepartureUT) / KerbinDay:F2} d (planned {(entryStep.UT - plan.DepartureUT) / KerbinDay:F2}): "
            + $"Ike correction {dv.magnitude:F1} m/s against a plan reserve of {plan.ReserveDeltaV:F1} m/s (plan total {plan.TotalDeltaV:F1}, Ike capture {ikeCapture.DeltaVMagnitude:F1}); "
            + $"Ike entry T+{(atIke.StartUT - plan.DepartureUT) / KerbinDay:F2} d (planned arrival T+{plan.TripSeconds / KerbinDay:F2}); legs 0 flown {flown:F1} m/s");
        Assert.LessOrEqual(dv.magnitude, ArrivalTargeting.MaxDeltaV);
    }
}
