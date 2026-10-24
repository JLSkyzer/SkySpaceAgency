using System.Collections.Generic;
using System.Linq;
using K2D2.Rendezvous;
using K2D2.Transfer;
using NUnit.Framework;

public class RendezvousReadoutTests
{
    static VesselEntry V(string id, string name, string body) => new VesselEntry { Id = id, Name = name, Body = body };

    static List<(string body, List<VesselEntry> vessels)> Groups() =>
        DockingReads.GroupByBody(new[]
        {
            V("a", "Station", "Kerbin"),
            V("b", "Relay", "Mun"),
            V("c", "Station", "Kerbin"),
            V("d", "Probe", "Kerbin"),
            new VesselEntry { Id = "e", Name = "Ours", Body = "Kerbin", IsOurs = true },
            new VesselEntry { Id = "f", Name = "Debris", Body = "Kerbin", IsDebris = true },
        }, "Kerbin");

    [Test]
    public void Targets_AreGroupedByBody_OursFirst_WithUniqueLabels()
    {
        var t = RendezvousReadout.Targets(Groups());
        var labels = RendezvousReadout.Labels(t);
        string I = RendezvousReadout.Indent;
        CollectionAssert.AreEqual(new[] { "Kerbin", I + "Probe", I + "Station", I + "Station (2)", "Mun", I + "Relay" }, labels);
        CollectionAssert.AllItemsAreUnique(labels);
        Assert.IsTrue(t[0].Header);
        Assert.IsNull(t[0].Id);
        Assert.IsTrue(t[4].Header);
        CollectionAssert.AreEqual(new[] { "d", "a", "c", "b" }, t.Where(c => !c.Header).Select(c => c.Id));
    }

    [Test]
    public void Targets_NoVessel_GivesAnEmptyList()
    {
        Assert.IsEmpty(RendezvousReadout.Targets(null));
        Assert.IsEmpty(RendezvousReadout.Targets(DockingReads.GroupByBody(new VesselEntry[0], "Kerbin")));
    }

    [Test]
    public void PreSelect_PrefersTheGameTarget_ThenTheRememberedOne()
    {
        var t = RendezvousReadout.Targets(Groups());
        Assert.AreEqual("b", t[RendezvousReadout.PreSelect(t, "b", "a")].Id);
        Assert.AreEqual("a", t[RendezvousReadout.PreSelect(t, "gone", "a")].Id);
        Assert.AreEqual("a", t[RendezvousReadout.PreSelect(t, null, "a")].Id);
        Assert.AreEqual(-1, RendezvousReadout.PreSelect(t, null, ""));
        Assert.AreEqual(-1, RendezvousReadout.PreSelect(t, "e", null), "our own vessel is not in the list");
    }

    [Test]
    public void FindId_NeverReturnsAHeaderOrAuto()
    {
        var t = RendezvousReadout.Targets(Groups());
        Assert.AreEqual(-1, RendezvousReadout.FindId(t, null));
        var p = RendezvousReadout.Ports(new RendezvousReadout.PortEntry[0]);
        Assert.AreEqual(-1, RendezvousReadout.FindId(p, null));
        Assert.AreEqual(-1, RendezvousReadout.FindLabel(t, "nothing"));
        Assert.AreEqual(4, RendezvousReadout.FindLabel(t, "Mun"));
    }

    [Test]
    public void Ports_StartWithAuto_AreNumbered_AndSayWhenInUse()
    {
        var p = RendezvousReadout.Ports(new[]
        {
            new RendezvousReadout.PortEntry { Id = "p1", Name = "dock_0v_small", Free = true },
            new RendezvousReadout.PortEntry { Id = "p2", Name = "dock_0v_small", Free = false },
            new RendezvousReadout.PortEntry { Id = "p3", Name = null, Free = true },
        });
        CollectionAssert.AreEqual(new[] { "Auto", "1. dock_0v_small", "2. dock_0v_small (in use)", "3. port" }, RendezvousReadout.Labels(p));
        Assert.IsNull(p[0].Id);
        Assert.AreEqual("p2", p[2].Id);
        Assert.AreEqual(1, RendezvousReadout.Ports(null).Count);
    }

    [Test]
    public void DockOnly_IsAllowedWithin2Km()
    {
        Assert.IsTrue(RendezvousReadout.DockOnlyAllowed(150));
        Assert.IsTrue(RendezvousReadout.DockOnlyAllowed(2000));
        Assert.IsFalse(RendezvousReadout.DockOnlyAllowed(2000.5));
        Assert.IsFalse(RendezvousReadout.DockOnlyAllowed(double.NaN));
        Assert.IsFalse(RendezvousReadout.DockOnlyAllowed(double.PositiveInfinity));
    }

    [Test]
    public void Relative_FormatsDistanceSpeedAndInclination()
    {
        Assert.AreEqual("Distance 850 m, relative speed 1.2 m/s, relative inclination 0.05°",
            RendezvousReadout.Relative(850.4, 1.23, 0.049));
        Assert.AreEqual("Distance 123.5 km, relative speed 45.0 m/s, relative inclination 12.30°",
            RendezvousReadout.Relative(123456, 45, 12.3));
        Assert.AreEqual("Distance -", RendezvousReadout.Relative(double.NaN, double.NaN, double.NaN));
    }

    static RvPlan Plan() => new RvPlan
    {
        Ok = true,
        TotalDeltaV = 61.24,
        Revolutions = 2,
        Steps = new List<RvStep>
        {
            new RvStep { Kind = RvStepKind.PlaneMatch, UT = 1100, DeltaVMagnitude = 12.04, Explanation = "match the plane" },
            new RvStep { Kind = RvStepKind.Intercept, UT = 1700, DeltaVMagnitude = 30.0, Explanation = "intercept" },
            new RvStep { Kind = RvStepKind.Brake, UT = 5000, DeltaVMagnitude = 19.2 },
        },
    };

    [Test]
    public void Steps_ShowDoneNowAndTimeToGo()
    {
        var rows = RendezvousReadout.Steps(Plan(), 1100, 1);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("Plane match", rows[0].Kind);
        Assert.AreEqual("done", rows[0].When);
        Assert.IsTrue(rows[0].Done);
        Assert.AreEqual("12.0 m/s", rows[0].DeltaV);
        Assert.AreEqual("Intercept burn", rows[1].Kind);
        Assert.AreEqual("in 10m 00s", rows[1].When);
        Assert.AreEqual("intercept", rows[1].Explanation);
        Assert.IsFalse(rows[1].Done);
        Assert.AreEqual("Brake", rows[2].Kind);
        Assert.AreEqual("", rows[2].Explanation);
        Assert.AreEqual("now", RendezvousReadout.Steps(Plan(), 1700, 0)[1].When);
        Assert.IsEmpty(RendezvousReadout.Steps(null, 0, 0));
    }

    static List<StageDv> TwoStages() => new List<StageDv>
    {
        new StageDv { Stage = 1, DeltaVVac = 40 },
        new StageDv { Stage = 0, DeltaVVac = 100 },
    };

    [Test]
    public void Totals_AndStages_FollowTheBudget()
    {
        var plan = Plan();
        var budget = RendezvousFlight.Budget(plan, 0, TwoStages(), true);
        string totals = RendezvousReadout.Totals(plan, budget);
        StringAssert.StartsWith("Total 61.2 m/s, 2 full revolution(s) before the arrival; the vessel has 140 m/s, ", totals);
        StringAssert.EndsWith(" m/s left", totals);

        var rows = RendezvousReadout.Stages(plan, 0, TwoStages(), budget);
        Assert.AreEqual(2, rows.Count);
        StringAssert.StartsWith("Stage 1: 40 m/s, pays the plane match, intercept burn", rows[0]);
        StringAssert.StartsWith("Stage 0: 100 m/s, pays the intercept burn", rows[1]);
        StringAssert.Contains("brake", rows[1]);
    }

    [Test]
    public void Stages_FromAStep_OnlyNameTheStepsLeft_AndSayWhenShort()
    {
        var plan = Plan();
        var stages = new List<StageDv> { new StageDv { Stage = 0, DeltaVVac = 25 } };
        var budget = RendezvousFlight.Budget(plan, 1, stages, false);
        Assert.IsFalse(budget.Ok);
        StringAssert.Contains("short by", RendezvousReadout.Totals(plan, budget));
        var rows = RendezvousReadout.Stages(plan, 1, stages, budget);
        Assert.AreEqual("Stage 0: 25 m/s, pays the intercept burn", rows[0]);
        Assert.AreEqual("No stage is left for the last step(s).", rows[1]);
        // Before a plan: the stages alone.
        CollectionAssert.AreEqual(new[] { "Stage 0: 25 m/s" }, RendezvousReadout.Stages(null, 0, stages, null));
    }

    [Test]
    public void TranslateLine_SaysTheModeAndItsNote()
    {
        Assert.AreEqual("Translation: RCS", RendezvousReadout.TranslateLine(TranslateMode.Rcs, null));
        Assert.AreEqual("Translation: main engine. " + TranslateModes.NoRcsNote,
            RendezvousReadout.TranslateLine(TranslateMode.MainEngine, TranslateModes.NoRcsNote));
        StringAssert.StartsWith("Translation: refused. No RCS", RendezvousReadout.TranslateLine(TranslateMode.Refused, "No RCS and no active engine: cannot dock"));
    }

    [Test]
    public void DockLine_AndAborts()
    {
        Assert.AreEqual("Docking: holding at 50 m, 51.2 m from the port, lateral 0.30 m, closing 0.00 m/s",
            RendezvousReadout.DockLine(DockPhase.Hold50, 51.23, 0.3, 0.001));
        Assert.AreEqual("Docking: aligning on the port axis", RendezvousReadout.DockLine(DockPhase.Align, double.NaN, double.NaN, double.NaN));
        Assert.AreEqual("", RendezvousReadout.AbortLine(0, null));
        Assert.AreEqual("Aborts: 1 (last: lateral error 2.1 m at 30 m)", RendezvousReadout.AbortLine(1, "lateral error 2.1 m at 30 m"));
        Assert.AreEqual("Ports: a on our vessel, ? on the target", RendezvousReadout.PortsLine("a", null));
    }
}
