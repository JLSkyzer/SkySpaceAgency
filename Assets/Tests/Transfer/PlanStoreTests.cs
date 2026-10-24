using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class PlanStoreTests
{
    const double LowKerbin = 700_000;
    const double InjectionUT = 1000;

    BodySystem _s;
    int B(string name) => _s[name].Index;

    [SetUp]
    public void SetUp() => _s = KerbolFixture.Build();

    // A Kerbin -> Duna plan by hand: injection, SOI exit, a correction, the capture. The doubles are
    // chosen to trip a formatting that does not round-trip.
    SavedPlan Saved(int stepIndex = 0)
    {
        double mu = _s["Kerbin"].Mu;
        var q = new TransferRequest
        {
            FromBody = B("Kerbin"),
            ToBody = B("Duna"),
            R0 = new Vector3d(LowKerbin, 0, 0),
            V0 = new Vector3d(0, Math.Sqrt(mu / LowKerbin), 0),
            UT0 = 0.1 + 0.2,
            ArrivalApoapsisRadius = 1.0 / 3.0 * 1e6,
            MaxTripSeconds = double.PositiveInfinity,
        };
        int duna = B("Duna");
        var plan = new TransferPlan
        {
            Ok = true,
            Request = q,
            DepartureUT = InjectionUT,
            ArrivalUT = 5.0e6 + Math.PI,
            TotalDeltaV = 1753.4123456789012,
            ReserveDeltaV = 34.38,
            CaseName = "sibling",
            ExcessBody = B("Kerbin"),
            RequiredExcess = new Vector3d(-1.0e-300, 960.123456789, double.Epsilon),
            Warnings = { "The Mun may cross the escape.", "ünïcode Δv" },
            Steps =
            {
                new PlanStep { Kind = StepKind.Injection, Body = B("Kerbin"), UT = InjectionUT, DeltaV = new Vector3d(0, 1050.25, 3.0e-5),
                    DeltaVMagnitude = 1050.2500000004285, Explanation = "Injection toward Duna", TargetBody = duna, TargetPeriapsisRadius = 380_000 },
                new PlanStep { Kind = StepKind.Escape, Body = B("Kerbin"), UT = 9000, TargetBody = duna, TargetPeriapsisRadius = 380_000 },
                new PlanStep { Kind = StepKind.Correction, Body = B("Kerbol"), UT = 2.0e6, TargetBody = duna, TargetPeriapsisRadius = 380_000 },
                new PlanStep { Kind = StepKind.Capture, Body = duna, UT = 5.0e6, DeltaVMagnitude = 586.7, TargetBody = duna,
                    TargetPeriapsisRadius = 380_000, TargetApoapsisRadius = 380_000, TargetInclinationDeg = -0.0, LegIndex = 0 },
            },
        };
        return new SavedPlan
        {
            VesselId = "0b7c1f6e-2a7e-4c55-9a39-3d1f4a7d9e10",
            Kind = "transfer",
            Plan = plan,
            Phase = TransferPhase.WaitWindow,
            StepIndex = stepIndex,
            SavedUT = 123.456,
            PrepareOnly = true,
            FromBodyName = "Kerbin",
            ToBodyName = "Duna",
            BodyNames = Enumerable.Range(0, _s.Count).Select(i => _s[i].Name).ToList(),
        };
    }

    static void SameBits(double expected, double actual, string what) =>
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual), $"{what}: {expected:R} vs {actual:R}");

    static void SameBits(Vector3d expected, Vector3d actual, string what)
    {
        SameBits(expected.x, actual.x, what + ".x");
        SameBits(expected.y, actual.y, what + ".y");
        SameBits(expected.z, actual.z, what + ".z");
    }

    SavedPlan RoundTrip(SavedPlan p)
    {
        string json = PlanStore.ToJson(p);
        Assert.IsTrue(PlanStore.TryFromJson(json, out var back, out string error), error);
        Assert.IsNull(error);
        Assert.AreEqual(json, PlanStore.ToJson(back), "a second trip writes the same text");
        return back;
    }

    [Test]
    public void RoundTrip_KeepsEveryField()
    {
        var p = Saved(2);
        var b = RoundTrip(p);

        Assert.AreEqual(1, b.Version);
        Assert.AreEqual(p.VesselId, b.VesselId);
        Assert.AreEqual("transfer", b.Kind);
        Assert.AreEqual(TransferPhase.WaitWindow, b.Phase);
        Assert.AreEqual(2, b.StepIndex);
        SameBits(p.SavedUT, b.SavedUT, "SavedUT");
        Assert.IsTrue(b.PrepareOnly);
        Assert.AreEqual("Kerbin", b.FromBodyName);
        Assert.AreEqual("Duna", b.ToBodyName);
        CollectionAssert.AreEqual(p.BodyNames, b.BodyNames);

        TransferPlan e = p.Plan, a = b.Plan;
        Assert.AreEqual(e.Ok, a.Ok);
        Assert.IsNull(a.Refusal);
        SameBits(e.DepartureUT, a.DepartureUT, "DepartureUT");
        SameBits(e.ArrivalUT, a.ArrivalUT, "ArrivalUT");
        SameBits(e.TotalDeltaV, a.TotalDeltaV, "TotalDeltaV");
        SameBits(e.ReserveDeltaV, a.ReserveDeltaV, "ReserveDeltaV");
        Assert.AreEqual(e.CaseName, a.CaseName);
        CollectionAssert.AreEqual(e.Warnings, a.Warnings);
        Assert.AreEqual(e.ExcessBody, a.ExcessBody);
        SameBits(e.RequiredExcess, a.RequiredExcess, "RequiredExcess");

        var eq = e.Request; var aq = a.Request;
        Assert.AreEqual(eq.FromBody, aq.FromBody);
        Assert.AreEqual(eq.ToBody, aq.ToBody);
        SameBits(eq.R0, aq.R0, "R0");
        SameBits(eq.V0, aq.V0, "V0");
        SameBits(eq.UT0, aq.UT0, "UT0");
        Assert.AreEqual(eq.Arrival, aq.Arrival);
        SameBits(eq.ArrivalPeriapsisRadius, aq.ArrivalPeriapsisRadius, "ArrivalPeriapsisRadius (NaN)");
        SameBits(eq.ArrivalApoapsisRadius, aq.ArrivalApoapsisRadius, "ArrivalApoapsisRadius");
        SameBits(eq.ArrivalInclinationDeg, aq.ArrivalInclinationDeg, "ArrivalInclinationDeg (NaN)");
        SameBits(eq.MaxTripSeconds, aq.MaxTripSeconds, "MaxTripSeconds (+inf)");

        Assert.AreEqual(e.Steps.Count, a.Steps.Count);
        for (int i = 0; i < e.Steps.Count; i++)
        {
            PlanStep es = e.Steps[i], st = a.Steps[i];
            string w = $"step {i}";
            Assert.AreEqual(es.Kind, st.Kind, w);
            Assert.AreEqual(es.Body, st.Body, w);
            SameBits(es.UT, st.UT, w + " UT");
            SameBits(es.DeltaV, st.DeltaV, w + " DeltaV");
            SameBits(es.DeltaVMagnitude, st.DeltaVMagnitude, w + " DeltaVMagnitude");
            Assert.AreEqual(es.Explanation, st.Explanation, w);
            Assert.AreEqual(es.TargetBody, st.TargetBody, w);
            SameBits(es.TargetPeriapsisRadius, st.TargetPeriapsisRadius, w + " TargetPeriapsisRadius");
            SameBits(es.TargetApoapsisRadius, st.TargetApoapsisRadius, w + " TargetApoapsisRadius");
            SameBits(es.TargetInclinationDeg, st.TargetInclinationDeg, w + " TargetInclinationDeg");
            Assert.AreEqual(es.LegIndex, st.LegIndex, w);
            Assert.AreEqual(es.ReplanAtLegStart, st.ReplanAtLegStart, w);
        }
    }

    // A real multi-leg plan from the search (Kerbin -> Ike: a staged arrival), legs and targets kept.
    [Test]
    public void RoundTrip_SearchedPlanWithLegs()
    {
        double mu = _s["Kerbin"].Mu;
        var q = new TransferRequest
        {
            FromBody = B("Kerbin"), ToBody = B("Ike"),
            R0 = new Vector3d(LowKerbin, 0, 0), V0 = new Vector3d(0, Math.Sqrt(mu / LowKerbin), 0),
        };
        var plan = TransferSearch.Find(_s, q, System.Threading.CancellationToken.None);
        Assume.That(plan.Ok, plan.Refusal);
        var p = Saved();
        p.Plan = plan;
        p.ToBodyName = "Ike";
        var b = RoundTrip(p);
        Assert.AreEqual(plan.Steps.Count, b.Plan.Steps.Count);
        Assert.AreEqual(plan.Steps.Max(st => st.LegIndex), b.Plan.Steps.Max(st => st.LegIndex));
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            SameBits(plan.Steps[i].UT, b.Plan.Steps[i].UT, $"step {i} UT");
            SameBits(plan.Steps[i].DeltaV, b.Plan.Steps[i].DeltaV, $"step {i} DeltaV");
            SameBits(plan.Steps[i].TargetPeriapsisRadius, b.Plan.Steps[i].TargetPeriapsisRadius, $"step {i} target Pe");
            Assert.AreEqual(plan.Steps[i].ReplanAtLegStart, b.Plan.Steps[i].ReplanAtLegStart);
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("{not json")]
    [TestCase("[1, 2, 3]")]
    [TestCase("{\"version\": 1, \"vesselId\": \"x\"")]
    public void CorruptFile_RefusedWithoutThrowing(string json)
    {
        bool ok = true; SavedPlan p = null; string error = null;
        Assert.DoesNotThrow(() => ok = PlanStore.TryFromJson(json, out p, out error));
        Assert.IsFalse(ok);
        Assert.IsNull(p);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [Test]
    public void WrongTypes_RefusedWithoutThrowing()
    {
        var o = JObject.Parse(PlanStore.ToJson(Saved()));
        o["stepIndex"] = "two";
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out _, out string error));
        StringAssert.Contains("stepIndex", error);

        o = JObject.Parse(PlanStore.ToJson(Saved()));
        o["phase"] = "Sleeping";
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out _, out error));
        StringAssert.Contains("phase", error);
    }

    [Test]
    public void Version2_Refused()
    {
        var o = JObject.Parse(PlanStore.ToJson(Saved()));
        o["version"] = 2;
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out var p, out string error));
        Assert.IsNull(p);
        StringAssert.Contains("version 2", error);
    }

    [TestCase("plan")]
    [TestCase("stepIndex")]
    [TestCase("vesselId")]
    [TestCase("toBody")]
    [TestCase("bodyNames")]
    public void MissingField_Refused(string field)
    {
        var o = JObject.Parse(PlanStore.ToJson(Saved()));
        o.Remove(field);
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out _, out string error));
        StringAssert.Contains(field, error);
    }

    [Test]
    public void MissingStepField_Refused()
    {
        var o = JObject.Parse(PlanStore.ToJson(Saved()));
        ((JObject)o["plan"]["steps"][3]).Remove("targetPeriapsisRadius");
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out _, out string error));
        StringAssert.Contains("targetPeriapsisRadius", error);
    }

    [Test]
    public void FileName_PerVessel() =>
        Assert.AreEqual("plan_abc.json", PlanStore.FileName("abc"));

    // --- Check ---

    // The vessel on the request's orbit, coasted to ut.
    void Coast(SavedPlan p, double ut, out Vector3d r, out Vector3d v) =>
        KeplerPropagator.Propagate(p.Plan.Request.R0, p.Plan.Request.V0, _s["Kerbin"].Mu, ut - p.Plan.Request.UT0, out r, out v);

    [Test]
    public void Nominal_Resume()
    {
        var p = Saved();
        Coast(p, 500, out var r, out var v);
        var c = PlanStore.Check(p, _s, B("Kerbin"), r, v, 500);
        Assert.AreEqual(ResumeVerdict.Resume, c.Verdict, c.Reason);
    }

    [Test]
    public void AfterTheInjection_Resume()
    {
        var p = Saved(1);
        Coast(p, InjectionUT, out var r, out var v);
        v += p.Plan.Steps[0].DeltaV;
        KeplerPropagator.Propagate(r, v, _s["Kerbin"].Mu, 2000, out r, out v);
        var c = PlanStore.Check(p, _s, B("Kerbin"), r, v, InjectionUT + 2000);
        Assert.AreEqual(ResumeVerdict.Resume, c.Verdict, c.Reason);
    }

    [Test]
    public void BurnedByHand_Replan()
    {
        var p = Saved();
        Coast(p, 500, out var r, out var v);
        var c = PlanStore.Check(p, _s, B("Kerbin"), r, v * 1.05, 500);
        Assert.AreEqual(ResumeVerdict.Replan, c.Verdict);
        StringAssert.Contains("energy", c.Reason);
    }

    [Test]
    public void MissedBurn_Replan()
    {
        var p = Saved();
        Coast(p, InjectionUT + 600, out var r, out var v);
        var c = PlanStore.Check(p, _s, B("Kerbin"), r, v, InjectionUT + 600);
        Assert.AreEqual(ResumeVerdict.Replan, c.Verdict);
        StringAssert.Contains("Injection", c.Reason);
    }

    [Test]
    public void OlderSaveLoaded_Replan()
    {
        var p = Saved();
        p.SavedUT = 800;
        Coast(p, 500, out var r, out var v);
        var c = PlanStore.Check(p, _s, B("Kerbin"), r, v, 500);
        Assert.AreEqual(ResumeVerdict.Replan, c.Verdict);
        StringAssert.Contains("earlier", c.Reason);
    }

    static BodySystem Renamed(BodySystem s, string from, string to)
    {
        var nodes = new List<BodyNode>();
        for (int i = 0; i < s.Count; i++)
        {
            var n = s[i];
            nodes.Add(new BodyNode
            {
                Name = n.Name == from ? to : n.Name, Index = n.Index, ParentIndex = n.ParentIndex,
                Mu = n.Mu, Radius = n.Radius, Soi = n.Soi, AtmosphereDepth = n.AtmosphereDepth,
                MaxTerrainHeight = n.MaxTerrainHeight, EpochPosition = n.EpochPosition,
                EpochVelocity = n.EpochVelocity, EpochUT = n.EpochUT,
            });
        }
        return new BodySystem(nodes);
    }

    [TestCase("Duna")]    // the target
    [TestCase("Kerbin")]  // the departure
    [TestCase("Kerbol")]  // a body the plan passes through
    public void BodyRenamedOrMissing_Mismatch(string body)
    {
        var p = Saved();
        Coast(p, 500, out var r, out var v);
        var c = PlanStore.Check(p, Renamed(_s, body, body + "Prime"), B("Kerbin"), r, v, 500);
        Assert.AreEqual(ResumeVerdict.Mismatch, c.Verdict);
        Assert.AreEqual(PlanStore.MismatchMessage, c.Reason);
        StringAssert.Contains(body, c.Detail);
    }

    [Test]
    public void BodiesReordered_FollowedByName()
    {
        // The plan was saved when Kerbin had another index: its names table maps it back.
        var p = Saved();
        p.BodyNames = p.BodyNames.ToList();
        int kerbin = B("Kerbin"), eve = B("Eve");
        p.BodyNames[kerbin] = "Eve";
        p.BodyNames[eve] = "Kerbin";
        Coast(p, 500, out var r, out var v);
        var c = PlanStore.Check(p, _s, kerbin, r, v, 500);
        Assert.AreEqual(ResumeVerdict.Mismatch, c.Verdict, "the request's FromBody now names Eve");

        p.Plan.Request.FromBody = eve;
        foreach (var st in p.Plan.Steps)
            if (st.Body == kerbin) st.Body = eve;
        p.Plan.ExcessBody = eve;
        c = PlanStore.Check(p, _s, kerbin, r, v, 500);
        Assert.AreEqual(ResumeVerdict.Resume, c.Verdict, c.Reason);
    }

    [Test]
    public void OrbitingABodyOffThePlan_Mismatch()
    {
        var p = Saved();
        double mu = _s["Mun"].Mu;
        var r = new Vector3d(300_000, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(mu / 300_000), 0);
        var c = PlanStore.Check(p, _s, B("Mun"), r, v, 500);
        Assert.AreEqual(ResumeVerdict.Mismatch, c.Verdict);
        Assert.AreEqual(PlanStore.MismatchMessage, c.Reason);
        StringAssert.Contains("Mun", c.Detail);
    }

    void DunaOrbit(double pe, double ap, out Vector3d r, out Vector3d v)
    {
        double mu = _s["Duna"].Mu, a = (pe + ap) / 2;
        r = new Vector3d(pe, 0, 0);
        v = new Vector3d(0, Math.Sqrt(mu * (2 / pe - 1 / a)), 0);
    }

    [Test]
    public void CapturedAsPlanned_Resume()
    {
        var p = Saved(4);
        p.Phase = TransferPhase.Capture;
        DunaOrbit(380_000, 382_000, out var r, out var v);
        var c = PlanStore.Check(p, _s, B("Duna"), r, v, 5.0e6 + 100);
        Assert.AreEqual(ResumeVerdict.Resume, c.Verdict, c.Reason);
    }

    // After an injection onto a parabola the expected energy is ~0: a small difference must not
    // count as a huge relative one (the absolute floor of mu/r).
    [Test]
    public void NearParabolic_SmallDifference_Resume_LargeOne_Replan()
    {
        var p = Saved(1);
        double mu = _s["Kerbin"].Mu;
        Coast(p, InjectionUT, out var r, out var v);
        p.Plan.Steps[0].DeltaV = v.normalized * (Math.Sqrt(2 * mu / r.magnitude) - v.magnitude);
        var after = v + p.Plan.Steps[0].DeltaV;
        KeplerPropagator.Propagate(r, after, mu, 600, out var r1, out var v1);
        double ut = InjectionUT + 600;

        var c = PlanStore.Check(p, _s, B("Kerbin"), r1, v1 * 1.001, ut);
        Assert.AreEqual(ResumeVerdict.Resume, c.Verdict, c.Reason);
        c = PlanStore.Check(p, _s, B("Kerbin"), r1, v1 * 1.05, ut);
        Assert.AreEqual(ResumeVerdict.Replan, c.Verdict, "a real burn by hand still counts");
    }

    // A plan stopped by a failure (saved in the Failed phase) resumes when the state still matches.
    [Test]
    public void FailedPhase_Resume()
    {
        var p = Saved(2);
        p.Phase = TransferPhase.Failed;
        p = RoundTrip(p);
        Coast(p, InjectionUT, out var r, out var v);
        v += p.Plan.Steps[0].DeltaV;
        KeplerPropagator.Propagate(r, v, _s["Kerbin"].Mu, 2000, out r, out v);
        var c = PlanStore.Check(p, _s, B("Kerbin"), r, v, InjectionUT + 2000);
        Assert.AreEqual(ResumeVerdict.Resume, c.Verdict, c.Reason);
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(p, c));
    }

    [Test]
    public void OffOrbitWithNoStepPending_Mismatch()
    {
        var p = Saved(4);
        p.Phase = TransferPhase.Capture;
        DunaOrbit(380_000, 900_000, out var r, out var v);
        var c = PlanStore.Check(p, _s, B("Duna"), r, v, 5.0e6 + 100);
        Assert.AreEqual(ResumeVerdict.Mismatch, c.Verdict);
        Assert.AreEqual(PlanStore.MismatchMessage, c.Reason);
        StringAssert.Contains("energy", c.Detail);
    }
}
