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

    // The last solve's predicted arrival periapsis UT and convergence (planned overload only).
    double _lastArrivalUT;
    bool _lastConverged;

    // plannedUT NaN: no planned arrival (closest approach on a miss).
    bool Solve(Flight f, int target, double rp, double incDeg, double horizonUT, double plannedUT,
        out Vector3d dv, out double predRp, out double predInc, string label)
    {
        var watch = Stopwatch.StartNew();
        _lastArrivalUT = double.NaN;
        _lastConverged = false;
        bool ok = ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, target, rp, incDeg, horizonUT, plannedUT, out dv, out predRp, out predInc,
            out _lastArrivalUT, out _lastConverged);
        TestContext.Out.WriteLine($"{label}: ok {ok}, dv {dv.magnitude:F3} m/s, predicted Pe {predRp / 1000:F3} km (wanted {rp / 1000:F3}), "
            + $"inclination {predInc:F3}° (wanted {incDeg:F1}), arrival {Days(_lastArrivalUT - plannedUT):F2} d from the planned one, "
            + $"converged {_lastConverged}, solve {watch.Elapsed.TotalMilliseconds:F0} ms");
        return ok;
    }

    static double Days(double seconds) => seconds / KerbinDay;

    // The pilot's solve: the cheapest pass in all, correction + capture into an orbit of apoapsis 'captureAp'.
    bool SolveCheapest(Flight f, int target, double rp, double horizonUT, double plannedUT, double captureAp,
        out Vector3d dv, out double predCapture, string label)
    {
        var watch = Stopwatch.StartNew();
        bool ok = ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, target, rp, double.NaN, horizonUT, plannedUT, captureAp,
            out dv, out double predRp, out _, out _lastArrivalUT, out predCapture, out _lastConverged);
        TestContext.Out.WriteLine($"{label}: ok {ok}, dv {dv.magnitude:F3} m/s + capture {predCapture:F1} = {dv.magnitude + predCapture:F1} m/s, "
            + $"Pe {predRp / 1000:F3} km, arrival {Days(_lastArrivalUT - plannedUT):F2} d from the planned one, converged {_lastConverged}, "
            + $"solve {watch.Elapsed.TotalMilliseconds:F0} ms");
        return ok;
    }

    // The planned pass (planned-first overload) and the nearest pass (no planned UT), each flown
    // through the propagator: correction + capture, and the arrival periapsis UT.
    void BothPasses(Flight f, double horizonUT, double plannedUT, double captureAp,
        out double plannedTotal, out double plannedArrival, out double nearestTotal, out double nearestArrival)
    {
        double wanted = _s[_duna].LowOrbitRadius;
        plannedTotal = nearestTotal = double.PositiveInfinity;
        plannedArrival = nearestArrival = double.NaN;
        if (ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, _duna, wanted, double.NaN, horizonUT, plannedUT, out var dvP, out _, out _, out _, out bool convP)
            && convP)
        {
            var a = Arrival(f, dvP, horizonUT, _duna);
            plannedTotal = dvP.magnitude + CaptureCost(a, captureAp);
            plannedArrival = a.PeriapsisUT;
        }
        if (ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, _duna, wanted, double.NaN, horizonUT, out var dvN, out _, out _))
        {
            var a = Arrival(f, dvN, horizonUT, _duna);
            nearestTotal = dvN.magnitude + CaptureCost(a, captureAp);
            nearestArrival = a.PeriapsisUT;
        }
        TestContext.Out.WriteLine($"  planned pass {plannedTotal:F1} m/s in all, arriving {Days(plannedArrival - plannedUT):F2} d from the planned one; "
            + $"nearest pass {nearestTotal:F1} m/s, arriving {Days(nearestArrival - plannedUT):F2} d");
    }

    // The cheapest solve keeps the pass of lower total, and its total is the one flown.
    void AssertCheapest(Flight f, Vector3d dv, double predCapture, double horizonUT, double captureAp,
        double plannedTotal, double plannedArrival, double nearestTotal, double nearestArrival)
    {
        var a = Arrival(f, dv, horizonUT, _duna);
        Assert.IsNotNull(a);
        Assert.AreEqual(_s[_duna].LowOrbitRadius, a.PeriapsisRadius, 1000);
        double flown = dv.magnitude + CaptureCost(a, captureAp);
        Assert.AreEqual(flown, dv.magnitude + predCapture, 0.5, "predicted total against the one flown");
        Assert.AreEqual(Math.Min(plannedTotal, nearestTotal), flown, 0.5, "the cheaper pass");
        Assert.AreEqual(plannedTotal <= nearestTotal ? plannedArrival : nearestArrival, a.PeriapsisUT, 0.5 * KerbinDay, "the cheaper pass's arrival");
        Assert.AreEqual(a.PeriapsisUT, _lastArrivalUT, 1e-3);
    }

    // The capture at the arrival periapsis into an orbit of apoapsis 'wantedAp', as the pilot flies it.
    double CaptureCost(Patch arrival, double wantedAp)
    {
        double mu = _s[arrival.Body].Mu;
        KeplerPropagator.Propagate(arrival.StartR, arrival.StartV, mu, arrival.PeriapsisUT - arrival.StartUT, out var rPe, out var vPe);
        double rp = rPe.magnitude;
        return vPe.magnitude - Math.Sqrt(mu * (2 / rp - 2 / (rp + wantedAp)));
    }

    // The plan's tolerance for the planned pass, as the solver applies it.
    double PassTolerance(Flight f, int target, double plannedUT, Patch arrival) =>
        ArrivalTargeting.PassTolerance(_s, target, f.UT, plannedUT, arrival.PeriapsisUT - arrival.StartUT);

    // The arrival is on the planned pass and costs what the plan says to capture.
    void AssertPlannedPass(TransferPlan plan, Flight f, Patch arrival, double plannedUT)
    {
        var capture = plan.Steps.Single(st => st.Kind == StepKind.Capture);
        double cost = CaptureCost(arrival, capture.TargetApoapsisRadius);
        double tolerance = PassTolerance(f, _duna, plannedUT, arrival);
        TestContext.Out.WriteLine($"  arrival periapsis {Days(arrival.PeriapsisUT - plannedUT):F2} d from the planned one (tolerance {Days(tolerance):F2} d), "
            + $"capture {cost:F1} m/s (planned {capture.DeltaVMagnitude:F1})");
        Assert.LessOrEqual(Math.Abs(arrival.PeriapsisUT - plannedUT), tolerance, "on the planned pass");
        Assert.AreEqual(capture.DeltaVMagnitude, cost, 0.05 * capture.DeltaVMagnitude, "capture cost");
    }

    double MidCourse(TransferPlan p) => p.Steps.Where(st => st.Kind == StepKind.Correction).ElementAt(1).UT;
    double Horizon(TransferPlan p) => p.ArrivalUT + 0.25 * p.TripSeconds;

    // The planned time of a step's arrival at its target: the start of the next leg, or the plan's arrival.
    static double PlannedArrival(TransferPlan p, PlanStep step)
    {
        var next = p.Steps.FirstOrDefault(st => st.LegIndex > step.LegIndex);
        return next != null ? next.UT : p.ArrivalUT;
    }

    // A 5 m/s injection error (prograde), corrected at mid-course, on the planned pass.
    [Test]
    public void PerturbedKerbinDuna_CorrectedToPeriapsisWithin1Km()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, MidCourse(plan), injectionError: 5);
        double wanted = _s[_duna].LowOrbitRadius;
        Assert.AreEqual(_s["Kerbol"].Index, f.Body);
        var before = Arrival(f, Vector3d.zero, Horizon(plan), _duna);
        TestContext.Out.WriteLine(before == null ? "uncorrected: no Duna encounter" : $"uncorrected: Pe {before.PeriapsisRadius / 1000:F0} km");

        Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan), plan.ArrivalUT, out var dv, out double predRp, out _, "mid-course, 5 m/s error"));
        Assert.IsTrue(_lastConverged);
        Assert.Greater(dv.magnitude, ArrivalTargeting.SkipThreshold, "a 5 m/s error is worth correcting");
        // A prograde injection error grows about 3 times through the Oberth effect, and moves the
        // arrival in time: keeping the planned pass costs more at mid-course than re-aiming at the
        // nearest one, which the capture pays back (logged, for the record).
        Assert.Less(dv.magnitude, 150);
        if (ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, _duna, wanted, double.NaN, Horizon(plan), out var nearDv, out _, out _))
        {
            var near = Arrival(f, nearDv, Horizon(plan), _duna);
            TestContext.Out.WriteLine($"  nearest pass instead: {nearDv.magnitude:F1} m/s, arrival {Days(near.PeriapsisUT - plan.ArrivalUT):F2} d from the planned one, "
                + $"capture {CaptureCost(near, plan.Steps.Single(st => st.Kind == StepKind.Capture).TargetApoapsisRadius):F1} m/s");
        }

        var after = Arrival(f, dv, Horizon(plan), _duna);
        Assert.IsNotNull(after, "the corrected path meets Duna");
        Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
        Assert.AreEqual(after.PeriapsisRadius, predRp, 1e-3);
        Assert.AreEqual(after.PeriapsisUT, _lastArrivalUT, 1e-3);
        AssertPlannedPass(plan, f, after, plan.ArrivalUT);

        // Δv first: the pilot's solve keeps the pass that is cheaper in all, here the nearest one.
        double captureAp = plan.Steps.Single(st => st.Kind == StepKind.Capture).TargetApoapsisRadius;
        BothPasses(f, Horizon(plan), plan.ArrivalUT, captureAp, out double pT, out double pA, out double nT, out double nA);
        Assert.Less(nT, pT, "in this case the nearest pass is cheaper");
        Assert.IsTrue(SolveCheapest(f, _duna, wanted, Horizon(plan), plan.ArrivalUT, captureAp, out var cheapest, out double predCapture, "mid-course, cheapest pass"));
        AssertCheapest(f, cheapest, predCapture, Horizon(plan), captureAp, pT, pA, nT, nA);
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
        // Push the vessel off course, so the solve starts from a miss whatever the injection's accuracy.
        f.V += 15 * Vector3d.Cross(f.R, f.V).normalized + 10 * f.V.normalized;
        Assert.IsNull(Arrival(f, Vector3d.zero, Horizon(plan), _duna), "the push must cause a miss");

        double wanted = _s[_duna].LowOrbitRadius;
        Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan), plan.ArrivalUT, out var dv, out _, out _, "first correction, after a miss, planned pass"));
        Assert.IsTrue(_lastConverged);
        var after = Arrival(f, dv, Horizon(plan), _duna);
        Assert.IsNotNull(after);
        Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
        AssertPlannedPass(plan, f, after, plan.ArrivalUT);

    }

    // Δv first, the other way round: the vessel leaves Kerbin's SOI 43 m/s too fast (prograde), as
    // the plans did before the SOI-exit fix. The nearest pass is then an early one with a high v∞,
    // dear to capture, and the planned pass, met at the first correction, is the cheaper one in all:
    // the pilot's solve keeps it.
    [Test]
    public void FastDeparture_KeepsThePlannedPassWhenCheaper()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, plan.Steps.First(st => st.Kind == StepKind.Correction).UT);
        f.V += 43 * f.V.normalized;
        double wanted = _s[_duna].LowOrbitRadius;
        double captureAp = plan.Steps.Single(st => st.Kind == StepKind.Capture).TargetApoapsisRadius;
        BothPasses(f, Horizon(plan), plan.ArrivalUT, captureAp, out double pT, out double pA, out double nT, out double nA);
        Assert.Less(pT, nT, "in this case the planned pass is cheaper");
        Assert.IsTrue(SolveCheapest(f, _duna, wanted, Horizon(plan), plan.ArrivalUT, captureAp, out var cheapest, out double predCapture, "first correction, cheapest pass"));
        AssertCheapest(f, cheapest, predCapture, Horizon(plan), captureAp, pT, pA, nT, nA);
        var after = Arrival(f, cheapest, Horizon(plan), _duna);
        Assert.LessOrEqual(Math.Abs(_lastArrivalUT - plan.ArrivalUT), PassTolerance(f, _duna, plan.ArrivalUT, after), "the planned pass kept");
    }

    // An encounter on another pass, earlier or later than the planned one, is not kept: the solve
    // moves the arrival to the planned pass. The vessel is first put on the plan's pass, then the
    // planned UT is moved 25 d either way (the Kerbin -> Duna drift seen before the SOI-exit fix was
    // 57 d early; 40 d earlier is out of the 300 m/s reach). Duna's period (801 Kerbin days) is longer
    // than the trip, so passes one target period apart do not exist on this leg; 25 d is 1.8 times
    // the tolerance here (13.8 d).
    [Test]
    public void EncounterOnAnotherPass_BroughtBackToThePlannedPass()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, plan.Steps.First(st => st.Kind == StepKind.Correction).UT);
        double wanted = _s[_duna].LowOrbitRadius;
        Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan), plan.ArrivalUT, out var first, out _, out _, "first correction"));
        f.V += first;
        var onPass = Arrival(f, Vector3d.zero, Horizon(plan), _duna);
        Assert.IsNotNull(onPass);

        foreach (double shiftDays in new[] { -25.0, 25.0 })
        {
            double plannedUT = onPass.PeriapsisUT + shiftDays * KerbinDay;
            Assert.Greater(Math.Abs(onPass.PeriapsisUT - plannedUT), PassTolerance(f, _duna, plannedUT, onPass), "the current encounter is another pass");
            Assert.IsTrue(Solve(f, _duna, wanted, double.NaN, Horizon(plan) + 60 * KerbinDay, plannedUT, out var dv, out double predRp, out _,
                $"encounter {-shiftDays:F0} d off the planned pass"));
            Assert.IsTrue(_lastConverged, "converged on the planned pass");
            Assert.LessOrEqual(dv.magnitude, ArrivalTargeting.MaxDeltaV);
            var after = Arrival(f, dv, Horizon(plan) + 60 * KerbinDay, _duna);
            Assert.IsNotNull(after);
            Assert.AreEqual(wanted, after.PeriapsisRadius, 1000);
            Assert.LessOrEqual(Math.Abs(after.PeriapsisUT - plannedUT), PassTolerance(f, _duna, plannedUT, after), "moved to the planned pass");
        }
    }

    [Test]
    public void Unreachable_ReturnsFalse()
    {
        var plan = KerbinToDuna();
        var f = Start(plan);
        FlyBurnsTo(plan, f, MidCourse(plan));
        f.V -= 2500 * f.V.normalized; // falling sunward, far from Duna's orbit
        Assert.IsNull(Arrival(f, Vector3d.zero, Horizon(plan), _duna));

        Assert.IsFalse(ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, _duna, _s[_duna].LowOrbitRadius, double.NaN, Horizon(plan),
            out var plainDv, out double plainRp, out _), "the overload without a planned arrival");
        Assert.AreEqual(0, plainDv.magnitude);
        Assert.IsTrue(double.IsNaN(plainRp));
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
                // As the pilot: the cheapest pass in all, with the capture of this leg's target (none
                // for a body only passed through).
                var capture = plan.Steps.FirstOrDefault(x => x.Kind == StepKind.Capture && x.LegIndex == st.LegIndex && x.TargetBody == st.TargetBody);
                double captureAp = capture != null ? capture.TargetApoapsisRadius : double.NaN;
                double predRp = double.NaN;
                bool solved = double.IsNaN(st.TargetInclinationDeg)
                    ? SolveCheapest(f, st.TargetBody, st.TargetPeriapsisRadius, Horizon(plan), PlannedArrival(plan, st), captureAp, out var dv, out _, $"correction {n}")
                    : Solve(f, st.TargetBody, st.TargetPeriapsisRadius, st.TargetInclinationDeg, Horizon(plan), PlannedArrival(plan, st), out dv, out predRp, out _, $"correction {n}");
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

        // The leg into Ike is re-planned at its start (ReplanAtLegStart): its planned times are
        // estimates, so the solve there is made without a planned arrival. With the stale estimate, for
        // the record:
        Assert.IsTrue(entryStep.ReplanAtLegStart);
        Solve(f, _ike, entryStep.TargetPeriapsisRadius, entryStep.TargetInclinationDeg, Horizon(plan), plan.ArrivalUT,
            out _, out _, out _, "Duna SOI entry -> Ike, with the plan's estimated arrival");
        Assert.IsTrue(Solve(f, _ike, entryStep.TargetPeriapsisRadius, entryStep.TargetInclinationDeg, Horizon(plan), double.NaN,
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
