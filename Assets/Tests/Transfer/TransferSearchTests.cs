using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class TransferSearchTests
{
    const double Deg = Math.PI / 180;
    const double KerbinDay = 21600; // s (6 h)
    const double BudgetSeconds = 3;

    BodySystem _s;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        TransferSearch.Trace = line => TestContext.Out.WriteLine(line);
    }

    [TearDown]
    public void TearDown() => TransferSearch.Trace = null;

    int B(string name) => _s[name].Index;

    // Circular orbit of 'radius' around 'body', at the +x point, inclined about +x.
    TransferRequest Request(string from, string to, double radius, double incDeg = 0, double ut0 = 0)
    {
        double mu = _s[from].Mu;
        double speed = Math.Sqrt(mu / radius);
        return new TransferRequest
        {
            FromBody = B(from),
            ToBody = B(to),
            R0 = new Vector3d(radius, 0, 0),
            V0 = new Vector3d(0, speed * Math.Cos(incDeg * Deg), speed * Math.Sin(incDeg * Deg)),
            UT0 = ut0,
        };
    }

    TransferPlan Find(TransferRequest q, string label, bool checkBudget = true)
    {
        var watch = Stopwatch.StartNew();
        var plan = TransferSearch.Find(_s, q, CancellationToken.None);
        watch.Stop();
        TestContext.Out.WriteLine($"{label}: {Describe(plan)}, search {watch.Elapsed.TotalMilliseconds:F0} ms");
        if (checkBudget)
            Assert.Less(watch.Elapsed.TotalSeconds, BudgetSeconds, "search time");
        return plan;
    }

    string Describe(TransferPlan p)
    {
        if (!p.Ok)
            return $"refused ({p.Refusal})";
        var steps = string.Join("; ", p.Steps.Select(st =>
            $"{st.Kind}@{_s[st.Body].Name} {st.DeltaVMagnitude:F1} m/s T+{(st.UT - p.Request.UT0) / KerbinDay:F2} d"));
        return $"{p.CaseName}, total {p.TotalDeltaV:F1} m/s (reserve {p.ReserveDeltaV:F1}), trip {p.TripSeconds / KerbinDay:F2} d, "
            + $"steps [{steps}], warnings [{string.Join(" | ", p.Warnings)}]";
    }

    static double StepSum(TransferPlan p) => p.TotalDeltaV - p.ReserveDeltaV;

    static PlanStep Injection(TransferPlan p, int body) => p.Steps.First(st => st.Kind == StepKind.Injection && st.Body == body);

    // The vessel flown through the plan's impulsive burns, from its first burn to 'untilUT'.
    List<Patch> FlyDeparture(TransferPlan p, double untilUT)
    {
        var q = p.Request;
        var burns = p.Steps.Where(st => st.DeltaV.magnitude > 0).Select(st => new Impulse(st.UT, st.DeltaV)).ToList();
        double start = burns.Min(b => b.UT);
        KeplerPropagator.Propagate(q.R0, q.V0, _s[q.FromBody].Mu, start - q.UT0, out var r, out var v);
        return PatchedPropagator.Propagate(_s, q.FromBody, r, v, start, untilUT, burns);
    }

    [Test]
    public void KerbinToDuna_FromLko_WithinDvMap()
    {
        var plan = Find(Request("Kerbin", "Duna", 700_000), "Kerbin -> Duna");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.Siblings, plan.CaseName);
        // EXTERNAL ESTIMATE: 1060 m/s from LKO to a Duna transfer, community Δv map (not measured).
        Assert.That(Injection(plan, B("Kerbin")).DeltaVMagnitude, Is.InRange(1060 * 0.9, 1060 * 1.1));
        Assert.That(plan.TripSeconds / KerbinDay, Is.InRange(200, 400));
        // The search horizon is two synodic periods (the SOI exit is the Lambert departure).
        double exit = plan.Steps.First(st => st.Kind == StepKind.Escape).UT;
        Assert.LessOrEqual(exit, 2 * _s.SynodicPeriod(B("Kerbin"), B("Duna")) + 1);
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Capture && st.Body == B("Duna")));
        Assert.AreEqual(3, plan.Steps.Count(st => st.Kind == StepKind.Correction));
        Assert.AreEqual(Math.Max(10, 0.02 * StepSum(plan)), plan.ReserveDeltaV, 1e-6);
        Assert.That(plan.Steps.Select(st => st.UT), Is.Ordered);
        Assert.IsTrue(plan.Steps.All(st => !string.IsNullOrEmpty(st.Explanation)));
    }

    [Test]
    public void KerbinToMun_FromLko_WithinDvMap()
    {
        var plan = Find(Request("Kerbin", "Mun", 700_000), "Kerbin -> Mun");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.ParentToChild, plan.CaseName);
        // EXTERNAL ESTIMATE: 860 m/s from LKO to a Mun transfer, community Δv map (not measured).
        Assert.That(Injection(plan, B("Kerbin")).DeltaVMagnitude, Is.InRange(860 * 0.9, 860 * 1.1));

        // The injection really reaches the Mun.
        var patches = FlyDeparture(plan, plan.ArrivalUT + 3600);
        Assert.IsTrue(patches.Any(pa => pa.Body == B("Mun")), "no Mun encounter");
    }

    [Test]
    public void KerbinToMun_FromPolarOrbit_Planned()
    {
        // Review carry: Lambert's direction from a polar parking orbit.
        var plan = Find(Request("Kerbin", "Mun", 700_000, incDeg: 90), "Kerbin -> Mun, polar");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        // A polar orbit can depart when the Mun crosses its plane: no 90° plane change (≈ 3 km/s).
        Assert.Less(Injection(plan, B("Kerbin")).DeltaVMagnitude, 860 * 1.2);
        var patches = FlyDeparture(plan, plan.ArrivalUT + 3600);
        Assert.IsTrue(patches.Any(pa => pa.Body == B("Mun")), "no Mun encounter");
    }

    [Test]
    public void MunToDuna_WithinTenPercentOfBruteForce()
    {
        double low = _s["Mun"].LowOrbitRadius;
        var plan = Find(Request("Mun", "Duna", low), "Mun -> Duna");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.DifferentBranches, plan.CaseName);

        var watch = Stopwatch.StartNew();
        double ideal = BruteForceReference.IdealTotal(_s, B("Mun"), low, B("Duna"), _s["Duna"].LowOrbitRadius, true, 0,
            600, 120, out double dep, out double tof);
        TestContext.Out.WriteLine($"brute force: {ideal:F1} m/s at T+{dep / KerbinDay:F1} d, tof {tof / KerbinDay:F1} d, {watch.ElapsedMilliseconds} ms; plan {StepSum(plan):F1} m/s");
        Assert.That(StepSum(plan), Is.InRange(ideal * 0.9, ideal * 1.1));
    }

    [Test]
    public void MunToKerbin_ChildToParent_PeriapsisAtLowOrbit()
    {
        var plan = Find(Request("Mun", "Kerbin", _s["Mun"].LowOrbitRadius), "Mun -> Kerbin");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.ChildToParent, plan.CaseName);
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Escape && st.Body == B("Mun")));
        var capture = plan.Steps.Single(st => st.Kind == StepKind.Capture);
        Assert.AreEqual(B("Kerbin"), capture.Body);

        var kerbin = FlyDeparture(plan, plan.ArrivalUT + 60).First(pa => pa.Body == B("Kerbin"));
        TestContext.Out.WriteLine($"flown Kerbin periapsis {(kerbin.PeriapsisRadius - 600_000) / 1000:F2} km at T+{kerbin.PeriapsisUT - plan.ArrivalUT:F0} s from the plan");
        Assert.AreEqual(_s["Kerbin"].LowOrbitRadius, kerbin.PeriapsisRadius, 2000);
        Assert.AreEqual(plan.ArrivalUT, kerbin.PeriapsisUT, 600);
    }

    [Test]
    public void IkeToLaythe_DifferentBranches_HasStagedSteps()
    {
        var plan = Find(Request("Ike", "Laythe", _s["Ike"].LowOrbitRadius), "Ike -> Laythe");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.DifferentBranches, plan.CaseName);
        // Every stage is shown: the burn at Ike, the way out of Duna, the arrival at Laythe.
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Injection && st.Body == B("Ike")));
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Escape && st.Body == B("Ike")));
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Escape && st.Body == B("Duna")));
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Capture && st.Body == B("Laythe")));

        double ideal = BruteForceReference.IdealTotal(_s, B("Ike"), _s["Ike"].LowOrbitRadius, B("Laythe"),
            _s["Laythe"].LowOrbitRadius, true, 0, 600, 120, out _, out _);
        TestContext.Out.WriteLine($"brute force: {ideal:F1} m/s; plan {StepSum(plan):F1} m/s");
        Assert.That(StepSum(plan), Is.InRange(ideal * 0.9, ideal * 1.1));
    }

    [Test]
    public void MunToKerbol_GrandparentTarget_Classified()
    {
        var q = Request("Mun", "Kerbol", _s["Mun"].LowOrbitRadius);
        q.Arrival = ArrivalKind.Encounter;
        q.ArrivalPeriapsisRadius = 9e9; // between Eve and Kerbin
        var plan = Find(q, "Mun -> Kerbol");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.ChildToAncestor, plan.CaseName);
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Injection && st.Body == B("Mun")));
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Escape && st.Body == B("Kerbin")));
        Assert.IsFalse(plan.Steps.Any(st => st.Kind == StepKind.Capture));
    }

    [Test]
    public void TargetIsCurrentBody_Refused()
    {
        var plan = Find(Request("Kerbin", "Kerbin", 700_000), "Kerbin -> Kerbin");
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("The target is the body you are orbiting.", plan.Refusal);
    }

    [Test]
    public void MaxTrip_TooShort_Refused()
    {
        var q = Request("Kerbin", "Duna", 700_000);
        q.MaxTripSeconds = 10 * KerbinDay;
        var plan = Find(q, "Kerbin -> Duna in 10 days");
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("No window within the maximum trip duration.", plan.Refusal);
    }

    [Test]
    public void MaxTrip_Respected()
    {
        var q = Request("Kerbin", "Duna", 700_000);
        q.MaxTripSeconds = 200 * KerbinDay;
        var plan = Find(q, "Kerbin -> Duna in 200 days");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.LessOrEqual(plan.TripSeconds, q.MaxTripSeconds);
    }

    [Test]
    public void CaptureBelowAtmosphere_Refused()
    {
        var q = Request("Kerbin", "Duna", 700_000);
        q.ArrivalPeriapsisRadius = _s["Duna"].Radius + 20_000; // the atmosphere is 50 km deep
        var plan = Find(q, "Kerbin -> Duna at 20 km");
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("Capture orbit below the atmosphere or terrain of Duna.", plan.Refusal);
    }

    [Test]
    public void NotClosed_Refused_ButEscapeTowardParentAllowed()
    {
        var q = Request("Kerbin", "Duna", 700_000);
        q.V0 = 1.5 * q.V0; // hyperbolic
        Assert.AreEqual("Not in a closed orbit.", Find(q, "Kerbin -> Duna, hyperbolic").Refusal);

        double low = _s["Mun"].LowOrbitRadius;
        var escaping = Request("Mun", "Kerbin", low);
        escaping.V0 = Math.Sqrt(2 * _s["Mun"].Mu / low + 300 * 300) / escaping.V0.magnitude * escaping.V0;
        var plan = Find(escaping, "Mun -> Kerbin, already escaping");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.ChildToParent, plan.CaseName);
        Assert.IsFalse(plan.Steps.Any(st => st.Kind == StepKind.Injection));
    }

    [Test]
    public void Encounter_HasNoCaptureStep()
    {
        var q = Request("Kerbin", "Duna", 700_000);
        q.Arrival = ArrivalKind.Encounter;
        var plan = Find(q, "Kerbin -> Duna, flyby");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.IsFalse(plan.Steps.Any(st => st.Kind == StepKind.Capture));
        Assert.IsTrue(plan.Steps.Any(st => st.Kind == StepKind.Injection));
    }

    sealed class SyncProgress : IProgress<float>
    {
        readonly Action<float> _report;
        public SyncProgress(Action<float> report) => _report = report;
        public void Report(float value) => _report(value);
    }

    [Test]
    public void Cancel_StopsWithinOneSecond()
    {
        var cts = new CancellationTokenSource();
        var sinceCancel = new Stopwatch();
        var values = new List<float>();
        var progress = new SyncProgress(v =>
        {
            values.Add(v);
            if (v > 0.05f && !sinceCancel.IsRunning)
            {
                sinceCancel.Start();
                cts.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() =>
            TransferSearch.Find(_s, Request("Mun", "Duna", _s["Mun"].LowOrbitRadius), cts.Token, progress));
        TestContext.Out.WriteLine($"stopped {sinceCancel.Elapsed.TotalMilliseconds:F1} ms after the cancel, progress {string.Join(", ", values.Take(5))}…");
        Assert.IsTrue(sinceCancel.IsRunning, "no progress reported");
        Assert.Less(sinceCancel.Elapsed.TotalSeconds, 1);
        Assert.That(values, Is.Ordered);
    }

    [Test]
    public void KerbinToDuna_UnplannedMunCrossing_Flagged()
    {
        var q = Request("Kerbin", "Duna", 700_000);
        var clean = TransferSearch.Find(_s, q, CancellationToken.None);
        Assert.IsTrue(clean.Ok, clean.Refusal);
        Assert.IsFalse(FlyDeparture(clean, Escape(clean)).Any(pa => pa.End == PatchEnd.Encounter), "the reference departure already meets a moon");

        // Move the Mun onto that departure: where the escape crosses the Mun's orbit radius.
        var burn = Injection(clean, B("Kerbin"));
        KeplerPropagator.Propagate(q.R0, q.V0, _s["Kerbin"].Mu, burn.UT - q.UT0, out var r, out var v);
        v += burn.DeltaV;
        double munRadius = _s["Mun"].EpochPosition.magnitude;
        double lo = 0, hi = 1e5;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            KeplerPropagator.Propagate(r, v, _s["Kerbin"].Mu, mid, out var rm, out _);
            if (rm.magnitude < munRadius) lo = mid; else hi = mid;
        }
        KeplerPropagator.Propagate(r, v, _s["Kerbin"].Mu, hi, out var cross, out _);
        _s.StateRelativeToParent(B("Mun"), burn.UT + hi, out var munThen, out _);
        double turn = Math.Atan2(cross.y, cross.x) - Math.Atan2(munThen.y, munThen.x);
        var mun = _s["Mun"];
        mun.EpochPosition = RotateZ(mun.EpochPosition, turn);
        mun.EpochVelocity = RotateZ(mun.EpochVelocity, turn);
        Assert.IsTrue(FlyDeparture(clean, Escape(clean)).Any(pa => pa.End == PatchEnd.Encounter && pa.NextBody == B("Mun")),
            "the test failed to put the Mun on the departure");

        var plan = Find(q, "Kerbin -> Duna, Mun on the way");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        bool meetsMun = FlyDeparture(plan, Escape(plan)).Any(pa => pa.End == PatchEnd.Encounter && pa.NextBody == B("Mun"));
        TestContext.Out.WriteLine($"shifted by {(Injection(plan, B("Kerbin")).UT - burn.UT):F0} s, still meets the Mun: {meetsMun}");
        if (meetsMun)
            Assert.IsTrue(plan.Warnings.Any(w => w.Contains("Mun")), "a Mun crossing must be warned about");
        else
            Assert.Greater(Math.Abs(Injection(plan, B("Kerbin")).UT - burn.UT), 1, "the departure should have moved");
    }

    [Test]
    public void KerbinToDuna_SingleLeg_CorrectionsAimAtDuna()
    {
        var plan = Find(Request("Kerbin", "Duna", 700_000), "Kerbin -> Duna, targets");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.IsTrue(plan.Steps.All(st => st.LegIndex == 0 && !st.ReplanAtLegStart));
        foreach (var st in plan.Steps.Where(st => st.Kind == StepKind.Correction || st.Kind == StepKind.Capture))
        {
            Assert.AreEqual(B("Duna"), st.TargetBody);
            Assert.AreEqual(_s["Duna"].LowOrbitRadius, st.TargetPeriapsisRadius, 1e-6);
            Assert.AreEqual(_s["Duna"].LowOrbitRadius, st.TargetApoapsisRadius, 1e-6);
            Assert.IsNaN(st.TargetInclinationDeg);
        }
        Assert.AreEqual(B("Kerbin"), plan.ExcessBody);
    }

    [Test]
    public void IkeToLaythe_FirstLeg_LeavesDunaWithTheRequiredExcess()
    {
        var plan = Find(Request("Ike", "Laythe", _s["Ike"].LowOrbitRadius), "Ike -> Laythe, climb flown");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(B("Duna"), plan.ExcessBody);
        // Every leg after the first is re-planned in flight; corrections aim at their own leg's arrival.
        Assert.IsTrue(plan.Steps.All(st => st.ReplanAtLegStart == (st.LegIndex > 0)));
        Assert.IsTrue(plan.Steps.Where(st => st.Kind == StepKind.Correction && st.Body == B("Kerbol"))
            .All(st => st.TargetBody == B("Jool")), "cruise corrections must aim at Jool, the leg's first arrival");
        Assert.IsTrue(plan.Steps.All(st => st.TargetBody >= 0), "every step has a target");

        // Fly the climb (Ike and Duna burns) through the propagator.
        double dunaExit = plan.Steps.First(st => st.Kind == StepKind.Escape && st.Body == B("Duna")).UT;
        var q = plan.Request;
        var burns = plan.Steps.Where(st => st.DeltaV.magnitude > 0 && st.UT <= dunaExit).Select(st => new Impulse(st.UT, st.DeltaV)).ToList();
        double start = burns.Min(b => b.UT);
        KeplerPropagator.Propagate(q.R0, q.V0, _s["Ike"].Mu, start - q.UT0, out var r, out var v);
        var patches = PatchedPropagator.Propagate(_s, B("Ike"), r, v, start, dunaExit + 3600, burns);
        Assert.IsFalse(patches.Any(pa => pa.End == PatchEnd.Impact), "the climb hits a body");
        var leaving = patches.First(pa => pa.Body == B("Duna") && pa.End == PatchEnd.Escape);
        var excess = Hyperbola.Asymptote(leaving.EndR, leaving.EndV, _s["Duna"].Mu);
        double error = (excess - plan.RequiredExcess).magnitude / plan.RequiredExcess.magnitude;
        TestContext.Out.WriteLine($"leaves Duna at {excess.magnitude:F1} m/s for {plan.RequiredExcess.magnitude:F1} m/s required, error {100 * error:F2} %");
        Assert.Less(error, 0.05);
    }

    [Test]
    public void KerbinToIke_StagedArrival_DunaCaptureThenIkeLeg()
    {
        // A very light Ike (test data, not stock): the capture at Ike gets no Oberth help, so a capture
        // into a low Duna orbit first, then a leg to Ike, is cheaper.
        _s["Ike"].Mu = 1e8;
        var plan = Find(Request("Kerbin", "Ike", 700_000), "Kerbin -> light Ike");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.DifferentBranches, plan.CaseName);
        var duna = plan.Steps.Single(st => st.Kind == StepKind.Capture && st.Body == B("Duna"));
        Assert.AreEqual(B("Duna"), duna.TargetBody);
        Assert.AreEqual(_s["Duna"].LowOrbitRadius, duna.TargetPeriapsisRadius, 1e-6);
        Assert.AreEqual(_s["Duna"].LowOrbitRadius, duna.TargetApoapsisRadius, 1e-6);
        var ikeLeg = plan.Steps.Where(st => st.LegIndex > duna.LegIndex).ToList();
        Assert.IsTrue(ikeLeg.Any(st => st.Kind == StepKind.Injection && st.Body == B("Duna")), "no transfer burn from the low Duna orbit");
        Assert.IsTrue(ikeLeg.All(st => st.ReplanAtLegStart));
        var ike = plan.Steps.Last();
        Assert.AreEqual(StepKind.Capture, ike.Kind);
        Assert.AreEqual(B("Ike"), ike.Body);
        Assert.Greater(ike.UT, duna.UT);
    }

    [Test]
    public void KerbinToMinmus_FromLko()
    {
        var plan = Find(Request("Kerbin", "Minmus", 700_000), "Kerbin -> Minmus");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(TransferSearch.ParentToChild, plan.CaseName);
        // EXTERNAL ESTIMATE: 930 m/s from LKO to a Minmus transfer, community Δv map (not measured).
        Assert.That(Injection(plan, B("Kerbin")).DeltaVMagnitude, Is.InRange(930 * 0.9, 930 * 1.1));
        var patches = FlyDeparture(plan, plan.ArrivalUT + 3600);
        bool reached = patches.Any(pa => pa.Body == B("Minmus"));
        Assert.IsTrue(reached || plan.Warnings.Count > 0, "neither reaches Minmus nor warns");
    }

    [Test]
    public void OrbitDippingIntoTheAtmosphere_Refused()
    {
        // 40 km × 1400 km around Kerbin: the periapsis is inside the 70 km atmosphere.
        double mu = _s["Kerbin"].Mu, rp = 640_000, ra = 2_000_000;
        var q = Request("Kerbin", "Duna", rp);
        q.V0 = Math.Sqrt(mu * (2 / rp - 2 / (rp + ra))) / q.V0.magnitude * q.V0;
        var plan = Find(q, "Kerbin -> Duna from a dipping orbit");
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual(TransferSearch.LowOrbit("Kerbin"), plan.Refusal);
    }

    [Test]
    public void HighOrbit_LowersThePeriapsisFirst()
    {
        var plan = Find(Request("Kerbin", "Jool", 10_000_000), "Kerbin -> Jool from 10 Mm");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        var change = plan.Steps.First(st => st.DeltaVMagnitude > 0);
        Assert.AreEqual(StepKind.OrbitChange, change.Kind);
        StringAssert.Contains("saves", change.Explanation);
        StringAssert.Contains("lower the periapsis", change.Explanation);
        TestContext.Out.WriteLine(change.Explanation);
        // The lowered orbit really has its periapsis at the low orbit.
        var q = plan.Request;
        KeplerPropagator.Propagate(q.R0, q.V0, _s["Kerbin"].Mu, change.UT - q.UT0, out var r, out var v);
        var el = K2D2.OrbitPlanning.OrbitMath.Elements(r, v + change.DeltaV, _s["Kerbin"].Mu);
        Assert.AreEqual(_s["Kerbin"].LowOrbitRadius, el.PeriapsisRadius, 1000);
    }

    // The plan's burns flown to the exit of the climb: the velocity relative to Kerbol there must be
    // the Lambert velocity from that position and UT to the target's ephemeris at the arrival.
    void ExitMatchesLambert(string from, double radius, string label)
    {
        var plan = Find(Request(from, "Duna", radius), label);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        double exitUT = plan.Steps.First(st => st.Kind == StepKind.Escape && st.Body == plan.ExcessBody).UT;
        var patches = FlyDeparture(plan, exitUT + 600);
        var kerbol = patches.First(pa => pa.Body == B("Kerbol"));
        double arrival = plan.Steps.Single(st => st.Kind == StepKind.Capture).UT;
        _s.StateRelativeToParent(B("Duna"), arrival, out var rDuna, out _);
        Assert.IsTrue(Lambert.Solve(kerbol.StartR, rDuna, arrival - kerbol.StartUT, _s["Kerbol"].Mu, true, 0, false, out var v1, out _));
        double miss = (v1 - kerbol.StartV).magnitude;
        TestContext.Out.WriteLine($"{label}: exit velocity {kerbol.StartV.magnitude:F1} m/s, Lambert {v1.magnitude:F1} m/s, mismatch {miss:F3} m/s");
        Assert.Less(miss, 1);
    }

    [Test]
    public void KerbinToDuna_SoiExitVelocityMatchesLambert() => ExitMatchesLambert("Kerbin", 700_000, "Kerbin -> Duna exit");

    [Test]
    public void MunToDuna_SoiExitVelocityMatchesLambert() => ExitMatchesLambert("Mun", _s["Mun"].LowOrbitRadius, "Mun -> Duna exit");

    double Escape(TransferPlan p) => p.Steps.First(st => st.Kind == StepKind.Escape && st.Body == p.Request.FromBody).UT + 1;

    static Vector3d RotateZ(Vector3d p, double angle) =>
        new Vector3d(p.x * Math.Cos(angle) - p.y * Math.Sin(angle), p.x * Math.Sin(angle) + p.y * Math.Cos(angle), p.z);
}
