using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using K2D2.Lift;
using K2D2.OrbitPlanning;
using K2D2.Rendezvous;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class RendezvousLaunchTests
{
    const double Deg = Math.PI / 180;
    const double R = 600000;          // Kerbin (KerbolFixture, external data)
    const double Mu = 3.5316e12;
    const double Low = 680000;        // 80 km
    const double Clear = 670000;      // 70 km atmosphere
    const double Lead = 150;
    const double Downrange = 5 * Deg;

    static Vector3d Omega() => KerbolFixture.KerbinAngularVelocity();
    static Vector3d Ksc() => KerbolFixture.SurfacePoint(R, KerbolFixture.KscLatitudeDeg, KerbolFixture.KscLongitudeDeg);

    // A circular orbit of radius r, 'angle' along it from its ascending node at longitude 'lan', inclined 'incl' (degrees).
    static void Circular(double r, double angle, double incl, double lan, out Vector3d pos, out Vector3d vel)
    {
        var node = new Vector3d(Math.Cos(lan * Deg), Math.Sin(lan * Deg), 0);
        var normal = OrbitMath.Rotate(OrbitMath.North, node, -incl * Deg);
        Vector3d inPlane = Vector3d.Cross(normal, node);
        Vector3d u = Math.Cos(angle * Deg) * node + Math.Sin(angle * Deg) * inPlane;
        Vector3d w = Vector3d.Cross(normal, u);
        pos = r * u;
        vel = Math.Sqrt(Mu / r) * w;
    }

    static RendezvousLaunchRequest Request(double targetRadius, double angle, double incl, double lan)
    {
        Circular(targetRadius, angle, incl, lan, out var rT, out var vT);
        return new RendezvousLaunchRequest
        {
            BodyName = "Kerbin", BodyRadius = R, Mu = Mu, LowOrbitRadius = Low, ClearRadius = Clear,
            Omega = Omega(), SiteAtUT0 = Ksc(), UT0 = 0, TargetR = rT, TargetV = vT,
            LeadSeconds = Lead, DownrangeRad = Downrange,
        };
    }

    static RendezvousLaunchPlan Run(RendezvousLaunchRequest q)
    {
        var watch = Stopwatch.StartNew();
        var plan = RendezvousLaunch.Plan(q, CancellationToken.None);
        watch.Stop();
        TestContext.Out.WriteLine($"ok {plan.Ok} ({plan.Refusal}) parking {(plan.ParkingRadius - R) / 1e3:F0} km ({plan.ParkingReason}), " +
                                  $"{plan.Passes?.Count ?? 0} pass(es) in {watch.ElapsedMilliseconds} ms " +
                                  $"({(plan.Passes != null && plan.Passes.Count > 0 ? watch.ElapsedMilliseconds / plan.Passes.Count : 0)} ms per pass), chosen {plan.Chosen}");
        if (plan.Passes != null)
            foreach (var p in plan.Passes)
                TestContext.Out.WriteLine($"  pass UT {p.Window.LaunchUT:F0} {p.Window.Pass} error {p.Window.PlaneErrorDeg:F3}° " +
                                          $"phase {p.PhaseDeg:F1}° Δv {p.DeltaV:F1} {(p.Rendezvous.Ok ? "" : p.Rendezvous.Refusal)}");
        TestContext.Out.WriteLine(plan.Explanation);
        return plan;
    }

    static OrbitElements Elements(double pe, double ap)
    {
        double a = (pe + ap) / 2, e = (ap - pe) / (ap + pe);
        var r = new Vector3d(pe, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(Mu * (2 / pe - 1 / a)), 0);
        var el = OrbitMath.Elements(r, v, Mu);
        Assert.AreEqual(e, el.Eccentricity, 1e-9);
        return el;
    }

    [Test]
    public void ParkingRadius_10kmUnderPe()
    {
        Assert.AreEqual(710000, RendezvousLaunch.ParkingRadius(720000, Low, R, out var why), 1e-6);
        Assert.AreEqual("10 km under the target's periapsis", why);
    }

    [Test]
    public void ParkingRadius_NeverUnderLowOrbit()
    {
        Assert.AreEqual(Low, RendezvousLaunch.ParkingRadius(685000, Low, R, out var why), 1e-6);
        Assert.AreEqual("the low orbit (80 km): the target's periapsis is too low for 10 km under it", why);
    }

    [Test]
    public void ParkingRadius_HighTarget_CappedAtLowPlus150()
    {
        Assert.AreEqual(Low + 150000, RendezvousLaunch.ParkingRadius(1100000, Low, R, out var why), 1e-6);
        Assert.AreEqual("capped at the low orbit + 150 km: the 4.3 phasing raises the rest", why);
        // Just at the cap: still 10 km under.
        Assert.AreEqual(Low + 150000, RendezvousLaunch.ParkingRadius(Low + 160000, Low, R, out why), 1e-6);
        Assert.AreEqual("10 km under the target's periapsis", why);
    }

    [Test]
    public void TargetRefusals()
    {
        var ok = Elements(720000, 720000);
        Assert.IsNull(RendezvousLaunch.TargetRefusal(true, false, "Kerbin", "Kerbin", ok, Clear, R));
        Assert.AreEqual("Choose the rendezvous target.", RendezvousLaunch.TargetRefusal(false, false, "Kerbin", "Kerbin", ok, Clear, R));
        Assert.AreEqual("The target is landed: a rendezvous needs a vessel in orbit.",
            RendezvousLaunch.TargetRefusal(true, true, "Kerbin", "Kerbin", ok, Clear, R));
        Assert.AreEqual("The target orbits Mun, not Kerbin: a rendezvous around another body is not built yet (sub-project 4.5).",
            RendezvousLaunch.TargetRefusal(true, false, "Mun", "Kerbin", ok, Clear, R));
        var open = OrbitMath.Elements(new Vector3d(720000, 0, 0), new Vector3d(0, 4000, 0), Mu);
        Assert.AreEqual("The target is not on a closed orbit.", RendezvousLaunch.TargetRefusal(true, false, "Kerbin", "Kerbin", open, Clear, R));
        Assert.AreEqual("The target's periapsis (50 km) is inside the atmosphere or under the terrain of Kerbin.",
            RendezvousLaunch.TargetRefusal(true, false, "Kerbin", "Kerbin", Elements(650000, 900000), Clear, R));
    }

    [Test]
    public void Insertion_IsCircularInTheReachedPlane()
    {
        var w = LaunchWindow.Find(R, Mu, Omega(), Ksc(), 0, new Vector3d(0, -Math.Sin(28.5 * Deg), Math.Cos(28.5 * Deg)),
            710000, Lead, Downrange, 2 * KerbolFixture.KerbinSiderealRotationSeconds);
        Assert.IsTrue(w.Ok, w.Refusal);
        RendezvousLaunch.Insertion(w, 710000, Mu, out var r, out var v);
        Assert.AreEqual(710000, r.magnitude, 1e-6);
        Assert.AreEqual(Math.Sqrt(Mu / 710000), v.magnitude, 1e-9);
        Assert.AreEqual(0, Vector3d.Dot(r, v), 1e-3);
        var h = Vector3d.Cross(r, v).normalized;
        Assert.Less(Vector3d.Cross(h, w.ReachedPlaneNormal).magnitude, 1e-9);
        Assert.Greater(Vector3d.Dot(h, w.ReachedPlaneNormal), 0);
    }

    [Test]
    public void PhaseDeg_TargetAhead_Positive_Behind_Negative()
    {
        var n = new Vector3d(0, 0, 1);
        var r = new Vector3d(700000, 0, 0);
        Assert.AreEqual(30, RendezvousLaunch.PhaseDeg(r, new Vector3d(Math.Cos(30 * Deg), Math.Sin(30 * Deg), 0.1), n), 1e-9);
        Assert.AreEqual(-45, RendezvousLaunch.PhaseDeg(r, new Vector3d(Math.Cos(45 * Deg), -Math.Sin(45 * Deg), 0), n), 1e-9);
        Assert.AreEqual(180, RendezvousLaunch.PhaseDeg(r, new Vector3d(-1, 0, 0), n), 1e-9);
    }

    static RendezvousPass Pass(double ut, double dv) => new RendezvousPass
    {
        Window = new LaunchWindowResult { Ok = true, LaunchUT = ut },
        Rendezvous = new RvPlan { Ok = !double.IsNaN(dv), TotalDeltaV = dv },
        DeltaV = dv,
    };

    [Test]
    public void Choose_LowestDeltaV()
    {
        Assert.AreEqual(2, RendezvousLaunch.Choose(new[] { Pass(0, 100), Pass(10, 80), Pass(20, 40), Pass(30, 60) }));
    }

    [Test]
    public void Choose_TieWithin5ms_EarliestWins()
    {
        Assert.AreEqual(1, RendezvousLaunch.Choose(new[] { Pass(0, 100), Pass(10, 44), Pass(20, 40), Pass(30, 60) }));
        Assert.AreEqual(2, RendezvousLaunch.Choose(new[] { Pass(0, 100), Pass(10, 45.5), Pass(20, 40) }));
        Assert.AreEqual(1, RendezvousLaunch.Choose(new[] { Pass(0, double.NaN), Pass(10, 40) }));
    }

    [Test]
    public void Choose_AllRefused_MinusOne()
    {
        Assert.AreEqual(-1, RendezvousLaunch.Choose(new[] { Pass(0, double.NaN), Pass(10, double.NaN) }));
        Assert.AreEqual(-1, RendezvousLaunch.Choose(new RendezvousPass[0]));
    }

    [Test]
    public void StationAt120km_28Deg_PicksCheapestOfThePasses_AndBeatsTheFirst()
    {
        var plan = Run(Request(720000, 40, 28.5, 200));
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(710000, plan.ParkingRadius, 1e-6);
        Assert.Greater(plan.Passes.Count, 1);
        Assert.LessOrEqual(plan.Passes.Count, RendezvousLaunch.MaxPasses);
        double min = plan.Passes.Where(p => !double.IsNaN(p.DeltaV)).Min(p => p.DeltaV);
        Assert.AreEqual(min, plan.Rendezvous.TotalDeltaV, RendezvousLaunch.TieDeltaV);
        Assert.AreSame(plan.Passes[plan.Chosen].Window, plan.Window);
        Assert.AreSame(plan.Passes[plan.Chosen].Rendezvous, plan.Rendezvous);
        if (!double.IsNaN(plan.Passes[0].DeltaV))
            Assert.LessOrEqual(plan.Rendezvous.TotalDeltaV, plan.Passes[0].DeltaV);
        StringAssert.StartsWith($"Launch on pass {plan.Chosen + 1} of {plan.Passes.Count} (in ", plan.Explanation);
        // The launch window is in the target's plane: no plane match.
        Assert.Less(plan.Window.PlaneErrorDeg, 0.05);
        Assert.IsFalse(plan.Rendezvous.Steps.Any(s => s.Kind == RvStepKind.PlaneMatch));
    }

    [Test]
    public void ChosenPass_SimulatedInsertion_RendezvousWithin200m()
    {
        var q = Request(720000, 40, 28.5, 200);
        var plan = Run(q);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        RendezvousLaunch.Insertion(plan.Window, plan.ParkingRadius, Mu, out var r, out var v);
        double ut = plan.Window.InsertionUT;
        foreach (var step in plan.Rendezvous.Steps)
        {
            KeplerPropagator.Propagate(r, v, Mu, step.UT - ut, out r, out v);
            v += step.DeltaV;
            ut = step.UT;
        }
        KeplerPropagator.Propagate(r, v, Mu, plan.Rendezvous.ArrivalUT - ut, out var rEnd, out var vEnd);
        KeplerPropagator.Propagate(q.TargetR, q.TargetV, Mu, plan.Rendezvous.ArrivalUT - q.UT0, out var rT, out var vT);
        double miss = (rEnd - rT).magnitude, speed = (vEnd - vT).magnitude;
        TestContext.Out.WriteLine($"miss {miss:F1} m, relative {speed:F3} m/s");
        Assert.That(miss, Is.LessThanOrEqualTo(201));
        Assert.That(speed, Is.LessThan(1));
    }

    [Test]
    public void EquatorialTargetFromKsc_OutOfReach_ShowsResidual()
    {
        var plan = Run(Request(720000, 40, 0, 0));
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(0.097, plan.Window.PlaneErrorDeg, 0.01);
        StringAssert.Contains("plane match", plan.Explanation);
        Assert.IsTrue(plan.Rendezvous.Steps.Any(s => s.Kind == RvStepKind.PlaneMatch));
        var times = plan.Passes.Select(p => p.Window.LaunchUT).ToList();
        Assert.AreEqual(times.Count, times.Distinct().Count());
        Assert.Greater(times.Count, 1);
    }

    // Review finding: a station at 0.03° is out of reach from the KSC, but launching at any time costs
    // at most ~2.4 m/s more than under the plane's vertex: the passes sample one target orbit, not
    // one pass per Kerbin day.
    [Test]
    public void NearlyEquatorialTarget_OutOfReach_LaunchesAnyTime()
    {
        var q = Request(720000, 40, 0.03, 40);
        var plan = Run(q);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(RendezvousLaunch.MaxPasses, plan.Passes.Count);
        double period = 2 * Math.PI * Math.Sqrt(Math.Pow(720000, 3) / Mu);
        for (int k = 0; k < plan.Passes.Count; k++)
        {
            Assert.AreEqual(PlanePass.Any, plan.Passes[k].Window.Pass);
            Assert.AreEqual(q.UT0 + k * period / 12, plan.Passes[k].Window.LaunchUT, 1e-6);
        }
        Assert.That(plan.Window.PlaneErrorDeg, Is.InRange(0.06, 0.14));
        StringAssert.Contains("plane match", plan.Explanation);
    }

    [Test]
    public void SlightlyInclinedTarget_Reachable_KeepsThePlanePasses()
    {
        // 0.2° is reachable from the KSC; launching any time would cost up to ~16 m/s of plane change.
        var plan = Run(Request(720000, 40, 0.2, 40));
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.IsTrue(plan.Passes.All(p => p.Window.Pass != PlanePass.Any));
        Assert.Less(plan.Window.PlaneErrorDeg, 0.01);
    }

    static Vector3d TurnZ(Vector3d p, double deg)
    {
        double c = Math.Cos(deg * Deg), s = Math.Sin(deg * Deg);
        return new Vector3d(c * p.x - s * p.y, s * p.x + c * p.y, p.z);
    }

    [Test]
    public void TurnedAboutTheAxis_SameLaunchAndDeltaV()
    {
        var q = Request(720000, 40, 6, 123);
        var a = Run(q);
        q.SiteAtUT0 = TurnZ(q.SiteAtUT0, 73);
        q.TargetR = TurnZ(q.TargetR, 73);
        q.TargetV = TurnZ(q.TargetV, 73);
        q.Omega = TurnZ(q.Omega, 73);
        var b = Run(q);
        Assert.IsTrue(a.Ok && b.Ok);
        Assert.AreEqual(a.Window.LaunchUT, b.Window.LaunchUT, 1e-3);
        Assert.AreEqual(a.Rendezvous.TotalDeltaV, b.Rendezvous.TotalDeltaV, 1e-6);
    }

    [Test]
    public void TargetUnderLowOrbit_EveryPassRefused_ClearRefusal()
    {
        var plan = Run(Request(675000, 40, 6, 123));
        Assert.IsFalse(plan.Ok);
        StringAssert.StartsWith("No rendezvous from any of the", plan.Refusal);
        Assert.AreEqual(Low, plan.ParkingRadius, 1e-6);
    }

    [Test]
    public void LandedTargetOrbit_RefusedBeforeAnyPass()
    {
        var plan = Run(Request(650000, 40, 6, 123));
        Assert.IsFalse(plan.Ok);
        StringAssert.Contains("inside the atmosphere", plan.Refusal);
    }

    [Test]
    public void StartDelay_NoPassBeforeIt()
    {
        // The plan takes seconds of game time on the pad: no pass may fall inside that delay.
        var q = Request(720000, 40, 0, 0);
        q.UT0 = 1000;
        q.StartDelaySeconds = 60;
        var plan = Run(q);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(1060, plan.Passes[0].Window.LaunchUT, 1e-6);
        foreach (var p in plan.Passes)
            Assert.GreaterOrEqual(p.Window.LaunchUT, 1060 - 1e-9);
    }

    [Test]
    public void Cancelled_Throws()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.That(() => RendezvousLaunch.Plan(Request(720000, 40, 28.5, 200), cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }
}
