using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using K2D2.Rendezvous;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The assist search (4.6): the offer rule, the time budget, cancellation, the 4.5 hook, the resume
// re-plans and the two lines of the tab.
public class AssistOfferTests
{
    BodySystem _s;
    int _kerbol, _kerbin, _eve, _moho, _duna;
    readonly List<string> _log = new List<string>();

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbol = _s["Kerbol"].Index;
        _kerbin = _s["Kerbin"].Index;
        _eve = _s["Eve"].Index;
        _moho = _s["Moho"].Index;
        _duna = _s["Duna"].Index;
        _log.Clear();
        AssistSearch.Log = line => { lock (_log) _log.Add(line); };
    }

    [TearDown]
    public void TearDown() => AssistSearch.Log = null;

    void Out(FormattableString text) => TestContext.Out.WriteLine(FormattableString.Invariant(text));

    void DumpLog()
    {
        foreach (var line in _log)
            TestContext.Out.WriteLine(line);
    }

    TransferRequest ToMoho() => AssistSearchTests.FromLko(_s, _moho);

    static double Reserve(double sum) => Math.Max(10, 0.02 * sum);

    // ---------------------------------------------------------------- the offer

    [Test]
    public void Find_KerbinMoho_OffersEve_WhenCheaper()
    {
        var watch = Stopwatch.StartNew();
        var choice = AssistSearch.Find(_s, ToMoho(), CancellationToken.None);
        Out($"{watch.ElapsedMilliseconds} ms in the editor");
        DumpLog();
        Assert.IsTrue(choice.Direct.Ok, choice.Direct.Refusal);
        Assert.IsTrue(choice.Offered, choice.NotOffered);
        Assert.AreEqual(_eve, choice.Assist.FlybyBody);
        Assert.GreaterOrEqual(choice.Saving, AssistSearch.MinSaving);
        Assert.AreSame(choice.Assist, choice.Preselected);
        double reference = AssistCases.AssistReference + Reserve(AssistCases.AssistReference);
        Assert.AreEqual(reference, choice.Assist.TotalDeltaV, 0.10 * reference);
        Assert.IsTrue(_log.Any(l => l.StartsWith("[Transfer] assist: offer: via Eve")));
    }

    [Test]
    public void Find_KerbinDuna_NoAssist()
    {
        var choice = AssistSearch.Find(_s, AssistSearchTests.FromLko(_s, _duna, AssistCases.DunaUT0), CancellationToken.None);
        DumpLog();
        Assert.IsTrue(choice.Direct.Ok, choice.Direct.Refusal);
        Assert.IsFalse(choice.Offered);
        StringAssert.StartsWith("no gravity assist saves 5 %", choice.NotOffered);
        Assert.AreSame(choice.Direct, choice.Preselected);
    }

    [Test]
    public void Find_AssistTooLong_NotOffered()
    {
        var full = AssistSearch.Find(_s, ToMoho(), CancellationToken.None);
        Assert.IsTrue(full.Offered, full.NotOffered);
        var q = ToMoho();
        q.MaxTripSeconds = 0.5 * (full.Direct.TripSeconds + full.Assist.TripSeconds);
        Assert.Less(full.Direct.TripSeconds, q.MaxTripSeconds);
        var choice = AssistSearch.Find(_s, q, CancellationToken.None);
        DumpLog();
        Assert.IsTrue(choice.Direct.Ok, choice.Direct.Refusal);
        Assert.IsFalse(choice.Offered);
        StringAssert.Contains("longer than the maximum trip", choice.NotOffered);
        Assert.AreSame(choice.Direct, choice.Preselected);
    }

    [Test]
    public void Find_DirectRefused_AssistAlone()
    {
        TransferPlan Refused(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> p) =>
            new TransferPlan { Ok = false, Refusal = TransferSearch.NoWindow, Request = q };
        var choice = AssistSearch.Find(_s, ToMoho(), CancellationToken.None, null, AssistSearch.TimeBudgetSeconds, Refused);
        DumpLog();
        Assert.IsFalse(choice.Direct.Ok);
        Assert.IsTrue(choice.Offered, choice.NotOffered);
        Assert.IsTrue(double.IsNaN(choice.Saving));
        Assert.AreSame(choice.Assist, choice.Preselected);
    }

    [Test]
    public void Find_TinyBudget_DirectOnly()
    {
        var choice = AssistSearch.Find(_s, ToMoho(), CancellationToken.None, null, 1e-3);
        DumpLog();
        Assert.IsTrue(choice.BudgetReached);
        Assert.IsNull(choice.Assist);
        Assert.AreSame(choice.Direct, choice.Preselected);
        StringAssert.EndsWith("(assist search stopped: time budget)", choice.NotOffered);
        Assert.IsTrue(_log.Any(l => l.Contains("time budget reached")));
    }

    [Test]
    public void Find_Cancelled_ThrowsQuickly()
    {
        var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var task = Task.Run(() => AssistSearch.Find(_s, ToMoho(), cts.Token));
        Thread.Sleep(50);
        cts.Cancel();
        var e = Assert.Throws<AggregateException>(() => task.Wait());
        Assert.IsInstanceOf<OperationCanceledException>(e.InnerException);
        Out($"ended {watch.ElapsedMilliseconds} ms after the start");
        Assert.Less(watch.Elapsed.TotalSeconds, 2.05);
    }

    [Test]
    public void Finder_Off_IsThePlainSearch()
    {
        var q = AssistSearchTests.FromLko(_s, _duna, AssistCases.DunaUT0);
        var plain = TransferSearch.Find(_s, q, CancellationToken.None);
        var off = AssistSearch.Finder(false)(_s, q, CancellationToken.None, null);
        Assert.AreEqual(plain.TotalDeltaV, off.TotalDeltaV, 1e-9);
        Assert.AreEqual(AssistKind.None, off.Assist);
        Assert.IsEmpty(_log);
    }

    // ---------------------------------------------------------------- the 4.5 hook

    [Test]
    public void FarRendezvous_WithTheAssistFinder()
    {
        double alt = 200_000, r = _s[_moho].Radius + alt, v = Math.Sqrt(_s[_moho].Mu / r);
        var tilt = QuaternionD.AngleAxis(30, new Vector3d(1, 0, 0));
        double lko = AssistCases.ParkingRadius;
        var request = new FarRendezvousRequest
        {
            FromBody = _kerbin, TargetBody = _moho, R0 = new Vector3d(lko, 0, 0), V0 = new Vector3d(0, Math.Sqrt(_s[_kerbin].Mu / lko), 0),
            UT0 = AssistCases.UT0, TargetR = tilt * new Vector3d(r, 0, 0), TargetV = tilt * new Vector3d(0, v, 0), TargetName = "Station",
        };
        var p = FarRendezvous.Plan(_s, request, AssistSearch.Finder(true), CancellationToken.None);
        DumpLog();
        Out($"{p.Explanation} {p.Refusal}");
        Assert.IsTrue(p.Ok, p.Refusal);
        Assert.IsTrue(p.Transfer.Steps.Any(st => st.Kind == StepKind.Flyby));
        Assert.AreEqual("last leg", p.AsymptoteSource);
    }

    // ---------------------------------------------------------------- resume

    sealed class State
    {
        public int Body;
        public Vector3d R, V;
        public double UT;
    }

    // The pinned plan flown on its planned vectors (its corrections are all under 1 m/s) to 'ut'.
    State FlyTo(TransferPlan plan, double ut)
    {
        var q = plan.Request;
        var f = new State { Body = q.FromBody, R = q.R0, V = q.V0, UT = q.UT0 };
        void Coast(double until)
        {
            if (until <= f.UT)
                return;
            var last = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, until, null, 8).Last();
            f.Body = last.Body;
            f.R = last.EndR;
            f.V = last.EndV;
            f.UT = last.EndUT;
        }
        foreach (var st in plan.Steps.Where(x => x.UT < ut && x.DeltaV.sqrMagnitude > 0 && x.Kind != StepKind.Flyby))
        {
            Coast(st.UT);
            f.V += st.DeltaV;
        }
        Coast(ut);
        return f;
    }

    TransferRequest From(State f, TransferRequest original) => new TransferRequest
    {
        FromBody = f.Body, ToBody = original.ToBody, R0 = f.R, V0 = f.V, UT0 = f.UT, Arrival = original.Arrival,
    };

    TransferPlan Pinned()
    {
        var plan = AssistSearch.Find(_s, ToMoho(), CancellationToken.None).Assist;
        Assert.IsNotNull(plan);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        return plan;
    }

    [Test]
    public void ResumeAssist_BeforeFlyby_ReachesTheFlyby()
    {
        var plan = Pinned();
        var flyby = plan.Steps.First(st => st.Kind == StepKind.Flyby);
        var mid = plan.Steps.First(st => st.Kind == StepKind.Correction && st.Body == _kerbol);
        var f = FlyTo(plan, mid.UT + 1000);
        Assert.AreEqual(_kerbol, f.Body);
        var resumed = TransferSearch.ResumeAssist(_s, From(f, plan.Request), AssistResumeRoute.BeforeFlyby, _eve, plan.Steps.First(st => st.Kind == StepKind.Flyby).UT, plan.ArrivalUT, CancellationToken.None);
        DumpLog();
        foreach (var st in resumed.Steps)
            Out($"  leg {st.LegIndex} {st.Kind} {_s[st.Body].Name} {st.UT:F0} {st.DeltaVMagnitude:F1}: {st.Explanation}");
        Assert.IsTrue(resumed.Ok, resumed.Refusal);
        Assert.AreEqual(AssistKind.Sibling, resumed.Assist);
        Assert.AreEqual(StepKind.Correction, resumed.Steps[0].Kind);
        Assert.AreEqual(_kerbol, resumed.Steps[0].Body);
        Assert.Greater(resumed.Steps[0].DeltaV.sqrMagnitude, 0);
        Assert.AreEqual(_eve, resumed.Steps[0].TargetBody);
        var fly = resumed.Steps.Single(st => st.Kind == StepKind.Flyby);
        Assert.AreEqual(flyby.UT, fly.UT, 0.15 * (flyby.UT - f.UT));
        Assert.AreEqual(StepKind.Capture, resumed.Steps.Last().Kind);
    }

    [Test]
    public void ResumeAssist_InFlybySoi_NewExcess()
    {
        var plan = Pinned();
        var flyby = plan.Steps.First(st => st.Kind == StepKind.Flyby);
        var f = FlyTo(plan, flyby.UT - 3 * 3600);
        Assert.AreEqual(_eve, f.Body, "inside Eve's SOI, before the periapsis");
        var resumed = TransferSearch.ResumeAssist(_s, From(f, plan.Request), AssistResumeRoute.InFlybySoi, _eve, flyby.UT, plan.ArrivalUT, CancellationToken.None);
        foreach (var st in resumed.Steps)
            Out($"  leg {st.LegIndex} {st.Kind} {_s[st.Body].Name} {st.UT:F0} {st.DeltaVMagnitude:F1}: {st.Explanation}");
        Assert.IsTrue(resumed.Ok, resumed.Refusal);
        Assert.AreEqual(StepKind.Flyby, resumed.Steps[0].Kind);
        Assert.Greater(resumed.Steps[0].TargetExcess.magnitude, 0);
        Assert.AreEqual(StepKind.Escape, resumed.Steps[1].Kind);
        Assert.AreEqual(StepKind.Capture, resumed.Steps.Last().Kind);
        Assert.AreEqual(flyby.DeltaVMagnitude, resumed.Steps[0].DeltaVMagnitude, 50);
    }

    [Test]
    public void ResumeAssist_BeforeFlyby_NoCandidate_Refuses()
    {
        var plan = Pinned();
        var mid = plan.Steps.First(st => st.Kind == StepKind.Correction && st.Body == _kerbol);
        var f = FlyTo(plan, mid.UT + 1000);
        f.V = -f.V; // far off the cruise: the other way round the Sun
        var resumed = TransferSearch.ResumeAssist(_s, From(f, plan.Request), AssistResumeRoute.BeforeFlyby, _eve, plan.Steps.First(st => st.Kind == StepKind.Flyby).UT, plan.ArrivalUT, CancellationToken.None);
        Out($"{resumed.Refusal}");
        Assert.IsFalse(resumed.Ok);
        Assert.IsFalse(string.IsNullOrEmpty(resumed.Refusal));
    }

    // ---------------------------------------------------------------- final review fixes

    // C1: a departure flyby resumed in the moon's SOI is not a sibling flyby: refused with a reason
    // (the glue then plans from the SOI exit), never re-planned across frames.
    [Test]
    public void ResumeAssist_DepartureFlyby_InMoonSoi_Refused()
    {
        var q = AssistSearchTests.FromLko(_s, _duna, AssistCases.DunaUT0);
        int mun = _s["Mun"].Index;
        var plan = TransferSearch.FindMoonDeparture(_s, q, mun, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        var flyby = plan.Steps.First(st => st.Kind == StepKind.Flyby);
        var f = FlyTo(plan, flyby.UT - 600);
        Assert.AreEqual(mun, f.Body, "inside the Mun's SOI, before the periapsis");
        var resumed = TransferSearch.ResumeAssist(_s, From(f, plan.Request), AssistResumeRoute.InFlybySoi, mun, flyby.UT, plan.ArrivalUT, CancellationToken.None);
        Out($"{resumed.Refusal}");
        Assert.IsFalse(resumed.Ok);
        StringAssert.Contains("not a body on the way", resumed.Refusal);
        Assert.AreNotEqual(AssistKind.Sibling, resumed.Assist);
    }

    // I1: resumed after the injection, still in Kerbin's SOI on the escape: the assist is kept,
    // planned from the SOI exit.
    [Test]
    public void ResumeAssist_BeforeFlyby_FromTheDepartureEscape()
    {
        var plan = Pinned();
        var injection = plan.Steps.First(st => st.Kind == StepKind.Injection);
        var flyby = plan.Steps.First(st => st.Kind == StepKind.Flyby);
        var f = FlyTo(plan, injection.UT + 1000);
        Assert.AreEqual(_kerbin, f.Body);
        var resumed = TransferSearch.ResumeAssist(_s, From(f, plan.Request), AssistResumeRoute.BeforeFlyby, _eve, flyby.UT, plan.ArrivalUT, CancellationToken.None);
        foreach (var st in resumed.Steps)
            Out($"  leg {st.LegIndex} {st.Kind} {_s[st.Body].Name} {st.UT:F0} {st.DeltaVMagnitude:F1}");
        Assert.IsTrue(resumed.Ok, resumed.Refusal);
        Assert.AreEqual(AssistKind.Sibling, resumed.Assist);
        Assert.AreEqual(_kerbol, resumed.Steps[0].Body);
        Assert.GreaterOrEqual(resumed.Steps[0].UT, f.UT);
        Assert.IsTrue(resumed.Steps.Any(st => st.Kind == StepKind.Flyby));
    }

    // I2: past the flyby periapsis, still in Eve's SOI on the way out: planned from the SOI exit.
    [Test]
    public void FindFromExit_AfterTheFlybyPeriapsis_PlansFromTheExit()
    {
        var plan = Pinned();
        var flyby = plan.Steps.First(st => st.Kind == StepKind.Flyby);
        var f = FlyTo(plan, flyby.UT + 3600);
        Assert.AreEqual(_eve, f.Body);
        var q = From(f, plan.Request);
        Assert.IsFalse(TransferSearch.Find(_s, q, CancellationToken.None).Ok, "the plain search refuses an open path");
        var next = TransferSearch.FindFromExit(_s, q, CancellationToken.None);
        Out($"{(next.Ok ? FormattableString.Invariant($"{next.TotalDeltaV:F0} m/s") : next.Refusal)}; {string.Join(" | ", next.Warnings)}");
        Assert.IsTrue(next.Ok, next.Refusal);
        Assert.AreEqual(_kerbol, next.Request.FromBody);
        Assert.Greater(next.Request.UT0, f.UT);
        Assert.IsTrue(next.Warnings.Any(w => w.Contains("Eve SOI exit")));
    }

    // Answer 2 of the user (4.5 chain): the chosen plan and the alternative are written in the plan.
    [Test]
    public void Finder_On_NamesTheAlternative()
    {
        var plan = AssistSearch.Finder(true)(_s, ToMoho(), CancellationToken.None, null);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        Assert.AreEqual(AssistKind.Sibling, plan.Assist);
        var line = plan.Warnings.FirstOrDefault(w => w.StartsWith("Gravity assist:"));
        Out($"{line}");
        Assert.IsNotNull(line);
        StringAssert.Contains("Via Eve", line);
        StringAssert.Contains("Direct", line);
    }

    // ---------------------------------------------------------------- the lines of the tab

    [Test]
    public void AssistLine_Texts()
    {
        var plan = new TransferPlan { Ok = true, TotalDeltaV = 2140.4, DepartureUT = 0, ArrivalUT = KerbinCalendar.YearSeconds + 120 * KerbinCalendar.DaySeconds };
        Assert.AreEqual("● Direct: 2 140 m/s, 1y 120d", TransferReadout.AssistLine("Direct", plan, true));
        Assert.AreEqual("○ Via Eve: 2 140 m/s, 1y 120d", TransferReadout.AssistLine("Via Eve", plan, false));
        var refused = new TransferPlan { Ok = false, Refusal = "No window within the maximum trip duration." };
        Assert.AreEqual("Direct: refused (No window within the maximum trip duration.)", TransferReadout.AssistLine("Direct", refused, false));
        Assert.AreEqual("Direct", TransferReadout.AssistLabel(plan, _s));
        plan.FlybyBody = _eve;
        plan.Assist = AssistKind.Sibling;
        Assert.AreEqual("Via Eve", TransferReadout.AssistLabel(plan, _s));
    }
}
