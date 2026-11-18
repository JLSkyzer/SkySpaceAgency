using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using K2D2.Rendezvous;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// Sub-project 4.5, Task 3: the chain's plan, a transfer into the target's plane, the rendezvous
// budgeted at the arrival (worst of 12 phases) and the docking reserve. Pure code.
public class FarRendezvousTests
{
    const double Deg = Math.PI / 180;

    BodySystem _s;
    int _kerbin, _duna, _ike, _kerbol;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbin = _s["Kerbin"].Index;
        _duna = _s["Duna"].Index;
        _ike = _s["Ike"].Index;
        _kerbol = _s["Kerbol"].Index;
    }

    // Circular, at altitude 'alt' above Duna, inclination and node (°), at the ascending node; Duna's frame.
    void Circular(BodyNode body, double alt, double incDeg, double nodeDeg, out Vector3d r, out Vector3d v)
    {
        double radius = body.Radius + alt, speed = Math.Sqrt(body.Mu / radius);
        double i = incDeg * Deg, o = nodeDeg * Deg;
        r = radius * new Vector3d(Math.Cos(o), Math.Sin(o), 0);
        v = speed * new Vector3d(-Math.Sin(o) * Math.Cos(i), Math.Cos(o) * Math.Cos(i), Math.Sin(i));
    }

    OrbitElements Elements(BodyNode body, double alt, double incDeg = 30, double nodeDeg = 70)
    {
        Circular(body, alt, incDeg, nodeDeg, out var r, out var v);
        return OrbitMath.Elements(r, v, body.Mu);
    }

    // Our vessel in a 100 km circular equatorial Kerbin orbit, a station around 'target'.
    FarRendezvousRequest Request(int target, double alt = 200_000, double incDeg = 30, double nodeDeg = 70, BodySystem s = null)
    {
        s = s ?? _s;
        double radius = 700_000, speed = Math.Sqrt(s[_kerbin].Mu / radius);
        Circular(s[target], alt, incDeg, nodeDeg, out var rT, out var vT);
        return new FarRendezvousRequest
        {
            FromBody = _kerbin, TargetBody = target,
            R0 = new Vector3d(radius, 0, 0), V0 = new Vector3d(0, speed, 0), UT0 = 0,
            TargetR = rT, TargetV = vT, TargetName = "Station",
        };
    }

    static TransferPlan Find(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> p) => TransferSearch.Find(s, q, ct, p);

    // ------------------------------------------------------------ target

    [Test]
    public void TargetRefusals()
    {
        var duna = _s[_duna];
        var ok = Elements(duna, 200_000);
        Assert.IsNull(FarRendezvous.TargetRefusal(_s, _kerbin, _duna, true, false, ok));
        Assert.AreEqual("Choose a target vessel.", FarRendezvous.TargetRefusal(_s, _kerbin, _duna, false, false, ok));
        Assert.AreEqual("The target is landed: a rendezvous needs a vessel in orbit.", FarRendezvous.TargetRefusal(_s, _kerbin, _duna, true, true, ok));
        Assert.AreEqual("The target orbits Duna, like us: use Rendezvous + dock.", FarRendezvous.TargetRefusal(_s, _duna, _duna, true, false, ok));
        Assert.AreEqual("The target orbits Kerbol: a rendezvous around the star is not planned.",
            FarRendezvous.TargetRefusal(_s, _kerbin, _kerbol, true, false, Elements(_s[_kerbol], 1e10)));
        var open = OrbitMath.Elements(new Vector3d(520_000, 0, 0), new Vector3d(0, 2000, 0), duna.Mu);
        Assert.AreEqual("The target is not on a closed orbit of Duna.", FarRendezvous.TargetRefusal(_s, _kerbin, _duna, true, false, open));
        var beyondSoi = OrbitMath.Elements(new Vector3d(1e6, 0, 0), new Vector3d(0, Math.Sqrt(duna.Mu * 2 * 6e7 / (1e6 * (1e6 + 6e7))), 0), duna.Mu);
        Assert.AreEqual("The target is not on a closed orbit of Duna.", FarRendezvous.TargetRefusal(_s, _kerbin, _duna, true, false, beyondSoi));
        Assert.AreEqual("The target's periapsis (40 km) is inside the atmosphere or under the terrain of Duna.",
            FarRendezvous.TargetRefusal(_s, _kerbin, _duna, true, false, Elements(duna, 40_000)));
    }

    [Test]
    public void TransferRequestFor_CircularParkingInTheTargetsPlane()
    {
        var duna = _s[_duna];
        var q = Request(_duna);
        var el = OrbitMath.Elements(q.TargetR, q.TargetV, duna.Mu);
        double parking = K2D2.Lift.RendezvousLaunch.ParkingRadius(el.PeriapsisRadius, duna.LowOrbitRadius, duna.Radius, out _);
        Assert.AreEqual(duna.Radius + 190_000, parking, 1e-6);
        var t = FarRendezvous.TransferRequestFor(q, parking);
        Assert.AreEqual(ArrivalKind.Orbit, t.Arrival);
        Assert.AreEqual(parking, t.ArrivalPeriapsisRadius, 1e-9);
        Assert.AreEqual(parking, t.ArrivalApoapsisRadius, 1e-9);
        Assert.IsTrue(double.IsNaN(t.ArrivalInclinationDeg));
        Assert.AreEqual(0, (t.ArrivalPlaneNormal - Vector3d.Cross(q.TargetR, q.TargetV).normalized).magnitude, 1e-12);
        Assert.AreEqual(_kerbin, t.FromBody);
        Assert.AreEqual(_duna, t.ToBody);

        // A low target: 10 km under it is below the low orbit (60 km here), so the low orbit.
        var low = Request(_duna, 65_000);
        var elLow = OrbitMath.Elements(low.TargetR, low.TargetV, duna.Mu);
        Assert.AreEqual(duna.LowOrbitRadius,
            K2D2.Lift.RendezvousLaunch.ParkingRadius(elLow.PeriapsisRadius, duna.LowOrbitRadius, duna.Radius, out _), 1e-6);
    }

    // ------------------------------------------------------------ arrival geometry

    // The corrected arrival, flown: to the first correction, solved for the periapsis, then flown on.
    Vector3d CorrectedAsymptote(TransferPlan plan)
    {
        var q = plan.Request;
        double c1 = plan.Steps.First(st => st.Kind == StepKind.Correction).UT;
        var impulses = plan.Steps.Where(st => st.UT < c1 && st.DeltaV.magnitude > 0).Select(st => new Impulse(st.UT, st.DeltaV)).ToList();
        var coast = PatchedPropagator.Propagate(_s, q.FromBody, q.R0, q.V0, q.UT0, c1, impulses, 32);
        var at = coast[coast.Count - 1];
        double horizon = plan.ArrivalUT + 0.25 * plan.TripSeconds;
        Assert.IsTrue(ArrivalTargeting.Solve(_s, at.Body, at.EndR, at.EndV, c1, _duna, q.ArrivalPeriapsisRadius, double.NaN, horizon, plan.ArrivalUT,
            double.NaN, ArrivalTargeting.MaxDeltaV, out var dv, out _, out _, out _, out _, out _));
        var arrival = PatchedPropagator.Propagate(_s, at.Body, at.EndR, at.EndV + dv, c1, horizon, null, 8).First(p => p.Body == _duna);
        return ArrivalTargeting.IncomingAsymptote(arrival.StartR, arrival.StartV, _s[_duna].Mu);
    }

    static double Angle(Vector3d a, Vector3d b) => Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;

    TransferPlan TransferFor(FarRendezvousRequest q)
    {
        var el = OrbitMath.Elements(q.TargetR, q.TargetV, _s[q.TargetBody].Mu);
        double parking = K2D2.Lift.RendezvousLaunch.ParkingRadius(el.PeriapsisRadius, _s[q.TargetBody].LowOrbitRadius, _s[q.TargetBody].Radius, out _);
        var plan = TransferSearch.Find(_s, FarRendezvous.TransferRequestFor(q, parking), CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        return plan;
    }

    [Test]
    public void ArrivalAsymptote_KerbinDuna_Propagated()
    {
        var plan = TransferFor(Request(_duna));
        Assert.IsTrue(FarRendezvous.ArrivalAsymptote(_s, plan, out var sHat, out string source));
        Assert.AreEqual("propagated", source);
        Assert.AreEqual(1, sHat.magnitude, 1e-9);
        double angle = Angle(sHat, CorrectedAsymptote(plan));
        TestContext.Out.WriteLine($"predicted asymptote {angle:F3}° from the corrected arrival's");
        Assert.Less(angle, 2);
    }

    [Test]
    public void ArrivalAsymptote_KerbinIke_Staged_UsesTheLastLeg()
    {
        TransferPlan staged = null;
        for (double turn = 0; turn < 360 && staged == null; turn += 90)
        {
            _s = KerbolFixture.Build();
            var ike = _s["Ike"];
            ike.Mu = 1e8;
            var r = RotateZ(ike.EpochPosition, turn * Deg);
            var v = RotateZ(ike.EpochVelocity, turn * Deg);
            ike.EpochPosition = r;
            ike.EpochVelocity = OrbitMath.Rotate(v, r.normalized, 90 * Deg);
            var q = Request(_ike, 60_000, 10, 0);
            var el = OrbitMath.Elements(q.TargetR, q.TargetV, ike.Mu);
            double parking = K2D2.Lift.RendezvousLaunch.ParkingRadius(el.PeriapsisRadius, ike.LowOrbitRadius, ike.Radius, out _);
            var plan = TransferSearch.Find(_s, FarRendezvous.TransferRequestFor(q, parking), CancellationToken.None);
            Assert.IsTrue(plan.Ok, plan.Refusal);
            if (plan.FinalLegRequest != null)
                staged = plan;
        }
        Assert.IsNotNull(staged, "no staged arrival");
        Assert.IsTrue(FarRendezvous.ArrivalAsymptote(_s, staged, out var sHat, out string source));
        TestContext.Out.WriteLine($"staged asymptote ({sHat.x:F4}, {sHat.y:F4}, {sHat.z:F4}) from '{source}'");
        StringAssert.StartsWith("last leg", source);
        Assert.AreEqual(1, sHat.magnitude, 1e-9);
    }

    static Vector3d RotateZ(Vector3d p, double angle) =>
        new Vector3d(p.x * Math.Cos(angle) - p.y * Math.Sin(angle), p.x * Math.Sin(angle) + p.y * Math.Cos(angle), p.z);

    // ------------------------------------------------------------ estimate

    [Test]
    public void Estimate_WorstOfTwelve_AndPlaneMatchInside()
    {
        var duna = _s[_duna];
        var n = new Vector3d(0, 0, 1);
        var sHat = new Vector3d(Math.Cos(20 * Deg), 0, Math.Sin(20 * Deg));
        double residual = ArrivalTargeting.PlaneResidualDeg(sHat, n);
        Assert.AreEqual(20, residual, 1e-9);
        double parking = duna.Radius + 190_000;
        Circular(duna, 200_000, 0, 0, out var rT, out var vT);
        var watch = Stopwatch.StartNew();
        var worst = FarRendezvous.EstimateRendezvous(sHat, n, parking, duna.Mu, 1000, rT, vT, 0, duna.LowOrbitRadius,
            CancellationToken.None, out double best, out int planned);
        TestContext.Out.WriteLine($"estimate: {planned} phases, {best:F1} to {worst?.TotalDeltaV:F1} m/s, {watch.ElapsedMilliseconds} ms");
        Assert.IsNotNull(worst);
        Assert.AreEqual(12, planned);
        Assert.GreaterOrEqual(worst.TotalDeltaV, best);
        var pm = worst.Steps.Single(st => st.Kind == RvStepKind.PlaneMatch);
        double vc = Math.Sqrt(duna.Mu / parking);
        Assert.AreEqual(2 * vc * Math.Sin(residual * Deg / 2), pm.DeltaVMagnitude, 0.02 * pm.DeltaVMagnitude);
    }

    // ------------------------------------------------------------ plan

    [Test]
    public void Plan_KerbinToDunaStation_Ok()
    {
        var watch = Stopwatch.StartNew();
        var p = FarRendezvous.Plan(_s, Request(_duna), Find, CancellationToken.None);
        TestContext.Out.WriteLine($"plan in {watch.ElapsedMilliseconds} ms (estimate {p.PlanSeconds * 1000:F0} ms): {p.Explanation} {p.Refusal}");
        Assert.IsTrue(p.Ok, p.Refusal);
        Assert.AreEqual(p.Transfer.TotalDeltaV + p.RendezvousWorst + 10, p.TotalDeltaV, 1e-6);
        Assert.AreEqual(10, p.DockReserve);
        Assert.AreEqual(FarRendezvous.PhaseSamples, p.PhasesPlanned);
        Assert.AreEqual("propagated", p.AsymptoteSource);
        StringAssert.Contains("into a 190 km orbit", p.Explanation);
        StringAssert.Contains(p.PlaneResidualDeg.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "° left", p.Explanation);
        StringAssert.Contains("Docking reserve 10 m/s.", p.Explanation);
        Assert.AreEqual(0, (p.Transfer.Request.ArrivalPlaneNormal - p.TargetPlaneNormal).magnitude, 1e-12);
    }

    // Review Focus 1: the target body's frame turned, the target given in it: same total and residual.
    [Test]
    public void Plan_TurnedTargetFrame_SameTotal()
    {
        var identity = FarRendezvous.Plan(_s, Request(_duna), Find, CancellationToken.None);
        Assert.IsTrue(identity.Ok, identity.Refusal);

        var nodes = Enumerable.Range(0, _s.Count).Select(i => _s[i]).ToList();
        var q = QuaternionD.AngleAxis(50, new Vector3d(0, 0, 1)) * QuaternionD.AngleAxis(30, new Vector3d(1, 0, 0));
        var request = Request(_duna);
        nodes[_duna].FrameToCommon = q;
        var turned = new BodySystem(nodes);
        request.TargetR = CelestialFrames.ToLocal(q, request.TargetR);
        request.TargetV = CelestialFrames.ToLocal(q, request.TargetV);
        var t = FarRendezvous.Plan(turned, request, Find, CancellationToken.None);
        Assert.IsTrue(t.Ok, t.Refusal);
        TestContext.Out.WriteLine($"identity {identity.TotalDeltaV:F4} m/s, residual {identity.PlaneResidualDeg:F6}°; turned {t.TotalDeltaV:F4} m/s, residual {t.PlaneResidualDeg:F6}°");
        Assert.AreEqual(identity.TotalDeltaV, t.TotalDeltaV, 1e-3);
        Assert.AreEqual(identity.PlaneResidualDeg, t.PlaneResidualDeg, 1e-6);
    }

    // Review Focus 2: the arrival asymptote along the target's normal: about 90° left, all finite.
    [Test]
    public void Plan_AsymptoteAlongTheNormal_NoNaN()
    {
        var duna = _s[_duna];
        var request = Request(_duna, 200_000, 0, 0); // an equatorial station: normal +z
        // A hyperbola inside Duna's SOI coming in along +z.
        double vInf = 1000, rp = duna.Radius + 190_000, mu = duna.Mu;
        double e = 1 + rp * vInf * vInf / mu, a = -mu / (vInf * vInf), p = a * (1 - e * e), r = 0.9 * duna.Soi;
        double nu = -Math.Acos((p / r - 1) / e);
        // Periapsis direction and the in-plane normal chosen so that the asymptote is +z.
        double cosA = 1 / e, sinA = Math.Sqrt(1 - 1 / (e * e));
        var sHat = new Vector3d(0, 0, 1);
        var side = new Vector3d(1, 0, 0);
        var eHat = cosA * sHat - sinA * side;
        var qHat = sinA * sHat + cosA * side;
        var r0 = r * (Math.Cos(nu) * eHat + Math.Sin(nu) * qHat);
        var v0 = Math.Sqrt(mu / p) * (-Math.Sin(nu) * eHat + (e + Math.Cos(nu)) * qHat);
        Assert.Less(Angle(ArrivalTargeting.IncomingAsymptote(r0, v0, mu), sHat), 1e-6);
        TransferPlan Canned(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> pr) => new TransferPlan
        {
            Ok = true, Request = new TransferRequest { FromBody = _duna, ToBody = _duna, R0 = r0, V0 = v0, UT0 = 0 },
            ArrivalUT = 20 * 3600, TotalDeltaV = 1500, ReserveDeltaV = 30,
        };
        var plan = FarRendezvous.Plan(_s, request, Canned, CancellationToken.None);
        TestContext.Out.WriteLine($"{plan.Refusal} {plan.Explanation}");
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(90, plan.PlaneResidualDeg, 1e-3);
        Assert.IsFalse(double.IsNaN(plan.TotalDeltaV) || double.IsInfinity(plan.TotalDeltaV));
    }

    // The 4.6 hook: the search is the one passed in.
    [Test]
    public void Plan_UsesTheInjectedSearch()
    {
        TransferRequest seen = null;
        TransferPlan Refusing(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> pr)
        {
            seen = q;
            return new TransferPlan { Ok = false, Refusal = "No window within the maximum trip duration." };
        }
        var request = Request(_duna);
        var plan = FarRendezvous.Plan(_s, request, Refusing, CancellationToken.None);
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("No transfer to Duna: No window within the maximum trip duration.", plan.Refusal);
        Assert.IsNotNull(seen);
        Assert.AreEqual(0, (seen.ArrivalPlaneNormal - Vector3d.Cross(request.TargetR, request.TargetV).normalized).magnitude, 1e-12);
        Assert.AreEqual(_s[_duna].Radius + 190_000, seen.ArrivalPeriapsisRadius, 1e-6);
    }

    [Test]
    public void Plan_RefusedTarget_NoSearch()
    {
        bool called = false;
        TransferPlan Spy(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> pr) { called = true; return null; }
        var request = Request(_duna, 40_000);
        var plan = FarRendezvous.Plan(_s, request, Spy, CancellationToken.None);
        Assert.IsFalse(plan.Ok);
        StringAssert.StartsWith("The target's periapsis (40 km)", plan.Refusal);
        Assert.IsFalse(called);
    }

    // ------------------------------------------------------------ budget, rows, totals

    TransferPlan HandPlan() => new TransferPlan
    {
        Ok = true, ReserveDeltaV = 32, TotalDeltaV = 1632,
        Request = new TransferRequest { FromBody = _kerbin, ToBody = _duna },
        Steps =
        {
            new PlanStep { Kind = StepKind.Injection, Body = _kerbin, UT = 100, DeltaVMagnitude = 1000, TargetBody = _duna },
            new PlanStep { Kind = StepKind.Correction, Body = _kerbol, UT = 200, TargetBody = _duna },
            new PlanStep { Kind = StepKind.Capture, Body = _duna, UT = 300, DeltaVMagnitude = 600, TargetBody = _duna },
        },
    };

    static List<StageDv> Stages(double dv) => new List<StageDv> { new StageDv { Stage = 0, DeltaVVac = dv } };

    // Review Focus 5: a vessel that pays the transfer but not the rendezvous is refused; one short at
    // the capture gets the capture message.
    [Test]
    public void Budget_CaptureCheckedBeforeTheRendezvous()
    {
        var plan = HandPlan();
        Assert.IsTrue(FarRendezvous.Budget(plan, 0, 300, 10, Stages(2000)).Ok);
        var shortRv = FarRendezvous.Budget(plan, 0, 300, 10, Stages(1800));
        Assert.IsFalse(shortRv.Ok);
        StringAssert.StartsWith("Not enough Δv: needs 1942 m/s, the vessel has 1800 m/s.", shortRv.Refusal);
        StringAssert.Contains("Per stage: stage 0: 1800 m/s.", shortRv.Refusal);
        var shortCapture = FarRendezvous.Budget(plan, 0, 300, 10, Stages(1500));
        Assert.IsFalse(shortCapture.Ok);
        StringAssert.StartsWith("The stages left at the arrival have 500 m/s, the capture needs 642 m/s.", shortCapture.Refusal);
        // From the capture on (the transfer flown up to it).
        Assert.IsTrue(FarRendezvous.Budget(plan, 2, 300, 10, Stages(1000)).Ok);
    }

    FarRendezvousPlan HandChain(double residual)
    {
        var rv = new RvPlan { Ok = true, TotalDeltaV = 140 };
        rv.Steps.Add(new RvStep { Kind = RvStepKind.PlaneMatch, DeltaVMagnitude = 35.4 });
        rv.Steps.Add(new RvStep { Kind = RvStepKind.Intercept, DeltaVMagnitude = 60 });
        return new FarRendezvousPlan
        {
            Ok = true, TargetName = "Station", Transfer = HandPlan(), PlaneResidualDeg = residual, Rendezvous = rv,
            RendezvousBest = 80, RendezvousWorst = 140, PhasesPlanned = 12, DockReserve = 10,
        };
    }

    [Test]
    public void Rows_TransferThenPlaneThenRendezvousThenDocking()
    {
        var rows = FarRendezvous.Rows(HandChain(2.345), _s, 0, 1);
        Assert.AreEqual(3 + 3, rows.Count);
        Assert.IsTrue(rows[0].Done);
        Assert.IsFalse(rows[1].Done);
        Assert.AreEqual("Plane match left (Duna)", rows[3].Kind);
        Assert.AreEqual("35.4 m/s", rows[3].DeltaV);
        StringAssert.StartsWith("2.35°", rows[3].Explanation);
        Assert.AreEqual("Rendezvous with Station (planned at the arrival)", rows[4].Kind);
        Assert.AreEqual("up to 140 m/s", rows[4].DeltaV);
        Assert.AreEqual("The target's phase at the arrival is not known yet: 80 to 140 m/s over 12 phases.", rows[4].Explanation);
        Assert.AreEqual("Docking", rows[5].Kind);
        Assert.AreEqual("10 m/s reserve", rows[5].DeltaV);

        // No plane match row at or under 0.05°, no docking row without docking.
        var flat = HandChain(0.04);
        flat.DockReserve = 0;
        var rows2 = FarRendezvous.Rows(flat, _s, 0, 0);
        Assert.AreEqual(3 + 1, rows2.Count);
        StringAssert.StartsWith("Rendezvous with Station", rows2[3].Kind);
    }

    [Test]
    public void Totals_Text()
    {
        var p = HandChain(1);
        Assert.AreEqual("Total 1782 m/s (transfer 1632, rendezvous up to 140, docking reserve 10)", FarRendezvous.Totals(p, null));
        var budget = FarRendezvous.Budget(p.Transfer, 0, p.RendezvousWorst, p.DockReserve, Stages(2000));
        Assert.AreEqual("Total 1782 m/s (transfer 1632, rendezvous up to 140, docking reserve 10); the vessel has 2000 m/s, 218 m/s left",
            FarRendezvous.Totals(p, budget));
        var shortBudget = FarRendezvous.Budget(p.Transfer, 0, p.RendezvousWorst, p.DockReserve, Stages(1700));
        StringAssert.EndsWith("the vessel has 1700 m/s, short by 82 m/s", FarRendezvous.Totals(p, shortBudget));
        p.DockReserve = 0;
        Assert.AreEqual("Total 1772 m/s (transfer 1632, rendezvous up to 140)", FarRendezvous.Totals(p, null));
    }

    [Test]
    public void Cancelled_Throws()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FarRendezvous.Plan(_s, Request(_duna), Find, cts.Token));
    }
}
