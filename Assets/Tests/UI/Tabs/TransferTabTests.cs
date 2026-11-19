using System;
using System.Collections.Generic;
using KSP.Sim;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.Transfer;
using UnityEngine;
using UnityEngine.UIElements;
using AppTabContext = SkySpaceAgency.AppUI.AppTabContext;

// The TRANSFER tab of the new window (plan 2026-11-19-new-ui-tab-migration): built and ticked without the game,
// its settings bound, the mockup's sample, and the pure parts (map geometry from a plan, texts, timeline).
public class TransferTabTests
{
    // ---- A small system: a star and three coplanar circular planets ----

    const double StarMu = 1.1723328e18;

    static BodyNode Planet(string name, int index, double orbit, double radius, double phaseRad)
    {
        double v = Math.Sqrt(StarMu / orbit);
        return new BodyNode
        {
            Name = name,
            Index = index,
            ParentIndex = 0,
            Mu = 3.5316e12,
            Radius = radius,
            Soi = orbit / 10,
            EpochPosition = new Vector3d(orbit * Math.Cos(phaseRad), 0, orbit * Math.Sin(phaseRad)),
            EpochVelocity = new Vector3d(-v * Math.Sin(phaseRad), 0, v * Math.Cos(phaseRad)),
            EpochUT = 0,
        };
    }

    static BodySystem System() => new BodySystem(new List<BodyNode>
    {
        new BodyNode { Name = "Star", Index = 0, ParentIndex = -1, Mu = StarMu, Radius = 261_600_000, Soi = double.PositiveInfinity },
        Planet("Home", 1, 13_599_840_256, 600_000, 0),
        Planet("Red", 2, 20_726_155_264, 320_000, 0.8),
        Planet("Violet", 3, 9_832_684_544, 700_000, 2.0),
    });

    static TransferPlan Plan(BodySystem s, int flyby = -1)
    {
        var q = new TransferRequest { FromBody = 1, ToBody = 2, UT0 = 0, R0 = new Vector3d(700_000, 0, 0), V0 = new Vector3d(0, 0, 2300) };
        var plan = new TransferPlan
        {
            Ok = true,
            Request = q,
            DepartureUT = 1000,
            ArrivalUT = 1000 + 5_000_000,
            CaseName = "Sibling",
            ReserveDeltaV = 20,
            TotalDeltaV = 1398,
            FlybyBody = flyby,
            Assist = flyby >= 0 ? AssistKind.Sibling : AssistKind.None,
        };
        plan.Steps.Add(new PlanStep { Kind = StepKind.Injection, Body = 1, UT = 1000, DeltaVMagnitude = 1061, DeltaV = new Vector3d(1061, 0, 0) });
        plan.Steps.Add(new PlanStep { Kind = StepKind.Escape, Body = 1, UT = 50_000 });
        if (flyby >= 0)
            plan.Steps.Add(new PlanStep { Kind = StepKind.Flyby, Body = flyby, UT = 2_500_000 });
        plan.Steps.Add(new PlanStep { Kind = StepKind.Capture, Body = 2, UT = 5_001_000, DeltaVMagnitude = 317, TargetPeriapsisRadius = 320_000 + 60_000 });
        return plan;
    }

    // ---- The tab ----

    [Test]
    public void Tab_BuildsAndTicksWithoutTheGame()
    {
        var tab = new TransferTab(AppTabContext.Editor());
        var page = tab.Build();
        Assert.IsNotNull(page);
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); });
        Assert.AreEqual("transfer", tab.Code);
        Assert.AreEqual("Transfer", tab.Label);
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.AreEqual("Start", tab.MainButton.Text);
        Assert.IsFalse(tab.MainButton.enabledSelf, "no game: no action");
        Assert.IsFalse(tab.PlanButton.enabledSelf);
        Assert.IsNull(tab.Map.Model, "no plan: an empty map");
        Assert.AreEqual(0, tab.BodyTiles, "no body list without the game");
        Assert.AreEqual(TransferTab.IdlePhases.Length, page.Timeline.Dots.Count);
        Assert.AreEqual(4, page.Actions.Buttons.Count, "Cancel saved plan, Resume, Plan, Start");
        Assert.AreEqual("Choose a target, then Plan.", tab.Status.Pill.Text);
    }

    [Test]
    public void Tab_BindsEveryTransferSetting()
    {
        var tab = new TransferTab(AppTabContext.Editor());
        tab.Build();
        foreach (string key in new[]
                 {
                     TransferSettings.target_body.key, TransferSettings.arrival.path, TransferSettings.low_circular.key,
                     TransferSettings.pe_km.key, TransferSettings.ap_km.key, TransferSettings.inc_enabled.key,
                     TransferSettings.inc_deg.key, TransferSettings.prepare_only.key, TransferSettings.max_trip_days.key,
                     TransferSettings.gravity_assist.key,
                 })
            Assert.IsTrue(AppBind.Bound.Contains(key), key);
        Assert.AreEqual("transfer.arrival", TransferSettings.arrival.path, "the classic keys, unchanged");
        int expected = TransferSettings.arrival.V == ArrivalKind.Orbit ? 0 : 1;
        Assert.AreEqual(expected, tab.ArrivalControl.Selected, "the arrival control shows the setting");
    }

    [Test]
    public void Tab_SampleFillsThePage()
    {
        var tab = new TransferTab(AppTabContext.Editor());
        var page = tab.Build();
        tab.ShowSample();
        Assert.AreEqual(6, tab.BodyTiles);
        Assert.IsTrue(page.Q("body-Duna").ClassListContains("ssa-transfer-body--selected"));
        Assert.IsNotNull(tab.Map.Model);
        Assert.AreEqual("Pe 61 km", tab.Map.Model.EncounterText);
        Assert.AreEqual("1 398", tab.Ring.ValueText);
        Assert.AreEqual("of 2 070 m/s", tab.Ring.OfText);
        Assert.AreEqual(3, tab.BudgetRows.Count);
        Assert.AreEqual("1 061 m/s", tab.BudgetRows.Get("Ejection"));
        Assert.AreEqual(TransferTab.SamplePhases.Length, page.Timeline.Dots.Count);
        Assert.AreEqual("Re-plan", tab.PlanButton.Text);
        Assert.AreEqual(TransferTab.Tagline, page.Actions.Note);
        tab.Tick();
        Assert.AreEqual("1 398", tab.Ring.ValueText, "a tick without the game keeps the sample");
        Assert.AreEqual(6, tab.BodyTiles);
    }

    // ---- The map from a plan ----

    [Test]
    public void Map_FromAPlan_PlacesTheOrbitsAndTheArc()
    {
        var s = System();
        var m = TransferViewModel.Build(s, Plan(s), now: 0);
        Assert.IsNotNull(m);
        Assert.AreEqual("Star", m.CentreName);
        Assert.AreEqual("Home", m.DepartName);
        Assert.AreEqual("Red", m.ArriveName);
        Assert.AreEqual(1f, m.ArriveRadius, 1e-5f, "the outer orbit at radius 1");
        Assert.Less(m.DepartRadius, m.ArriveRadius);
        Assert.Greater(m.DepartRadius, 0.3f - 1e-5f);
        Assert.That(m.Sweep, Is.GreaterThan(0).And.LessThanOrEqualTo(2 * Mathf.PI + 1e-4f), "counter-clockwise, at most one turn");
        Assert.Less((m.ArcPoint(0) - m.DepartPoint).magnitude, 1e-4f);
        Assert.Less((m.ArcPoint(1) - m.ArrivePoint).magnitude, 1e-4f);
        Assert.IsTrue(m.HasToday);
        Assert.IsFalse(m.HasFlyby);
        Assert.AreEqual(-1f, m.FlownFraction);
        Assert.AreEqual("Pe 60 km", m.EncounterText);
        StringAssert.EndsWith("° go", m.PhaseText);

        // The departure body at the departure UT lies on +x of the drawing (the plane's basis).
        Assert.AreEqual(0f, m.DepartAngle, 1e-4f);
        // Red started 0.8 rad ahead: now, the phase angle is about 45.8°.
        StringAssert.StartsWith("45.8° now", m.PhaseText);
    }

    [Test]
    public void Map_WithAFlyby_GoesThroughTheFlybyBody()
    {
        var s = System();
        var m = TransferViewModel.Build(s, Plan(s, flyby: 3), now: 0, flownFraction: 0.5);
        Assert.IsNotNull(m);
        Assert.IsTrue(m.HasFlyby);
        Assert.AreEqual("Violet", m.FlybyName);
        Assert.AreEqual(3, m.ArcAngles.Count, "departure, flyby, arrival");
        Assert.Less(m.ArcAngles[0], m.ArcAngles[1]);
        Assert.Less(m.ArcAngles[1], m.ArcAngles[2]);
        Assert.AreEqual(0.5f, m.FlownFraction, 1e-6f);
    }

    [Test]
    public void Map_WithoutAUsablePlan_IsNull()
    {
        var s = System();
        Assert.IsNull(TransferViewModel.Build(null, Plan(s), 0));
        Assert.IsNull(TransferViewModel.Build(s, null, 0));
        var refused = Plan(s);
        refused.Ok = false;
        Assert.IsNull(TransferViewModel.Build(s, refused, 0));
        var bad = Plan(s);
        bad.Request.ToBody = 42;
        Assert.IsNull(TransferViewModel.Build(s, bad, 0));
    }

    [Test]
    public void Map_Sample_IsTheMockup()
    {
        var m = TransferViewModel.Sample();
        Assert.Less((m.ArcPoint(0) - new Vector2(-0.6f, 0)).magnitude, 1e-4f, "Kerbin at the left, radius 120 of 200");
        Assert.Less((m.ArcPoint(1) - new Vector2(1, 0)).magnitude, 1e-4f, "Duna at the right");
        Assert.Greater(m.ArcPoint(0.5f).y, 0, "over the top");
        Assert.AreEqual("44.3° now · 44.0° go", m.PhaseText);
    }

    [Test]
    public void Map_Helpers()
    {
        Assert.AreEqual(1f, TransferViewModel.Compress(10, 10), 1e-6f);
        Assert.AreEqual(0.3f, TransferViewModel.Compress(0.001, 10), 1e-6f, "a small orbit stays visible");
        Assert.AreEqual(Mathf.PI / 2, TransferViewModel.Forward(-3 * Mathf.PI / 2), 1e-4f);
        Assert.AreEqual(2 * Mathf.PI, TransferViewModel.Forward(0), 1e-4f, "the same angle is a full turn");
        Assert.AreEqual(-170, TransferViewModel.Wrap180(190), 1e-9);
        Assert.AreEqual(180, TransferViewModel.Wrap180(-180), 1e-9);
        Assert.AreNotEqual(TransferBodyColors.For("Duna"), TransferBodyColors.For("Kerbin"));
        Assert.AreEqual(TransferBodyColors.For("Planet X"), TransferBodyColors.For("Planet X"), "a stable guess");
    }

    // ---- Texts and timeline ----

    [Test]
    public void Logic_TimelineFromThePlan()
    {
        var s = System();
        var plan = Plan(s);
        var steps = TransferTabLogic.Timeline(plan, s);
        CollectionAssert.AreEqual(new[] { "Wait for window", "Injection · 1 061 m/s", "Leave Home SOI", "Capture · 317 m/s" }, steps);

        Assert.AreEqual(0, TransferTabLogic.TimelineIndex(false, TransferPhase.Planned, 0, 3), "planned: waiting for the window");
        Assert.AreEqual(0, TransferTabLogic.TimelineIndex(true, TransferPhase.WaitWindow, 0, 3));
        Assert.AreEqual(1, TransferTabLogic.TimelineIndex(true, TransferPhase.Injection, 0, 3));
        Assert.AreEqual(3, TransferTabLogic.TimelineIndex(true, TransferPhase.Coast, 2, 3), "coasting to the capture");
        Assert.AreEqual(3, TransferTabLogic.TimelineIndex(false, TransferPhase.Done, 3, 3), "done: the last one");
        Assert.AreEqual(3, TransferTabLogic.TimelineIndex(true, TransferPhase.Capture, 9, 3), "clamped");
    }

    [Test]
    public void Logic_Texts()
    {
        Assert.AreEqual("1 398", TransferTabLogic.Ms(1397.6));
        Assert.AreEqual("12 345 678", TransferTabLogic.Ms(12_345_678));
        Assert.AreEqual("-", TransferTabLogic.Ms(double.NaN));
        Assert.AreEqual("Y1, D3", TransferTabLogic.AfterColon("Departure: Y1, D3"));
        Assert.AreEqual("plain", TransferTabLogic.AfterColon("plain"));
        Assert.AreEqual("Choose a target body.", TransferTabLogic.LowOrbitHint(null, true, true));
        var red = System()[2];
        StringAssert.StartsWith("Capture into 10 km above Red", TransferTabLogic.LowOrbitHint(red, true, true));
        StringAssert.StartsWith("Fly past at", TransferTabLogic.LowOrbitHint(red, true, false));
        StringAssert.StartsWith("Lowest safe altitude at Red", TransferTabLogic.LowOrbitHint(red, false, true));

        var steps = new List<TransferReadout.StepRow>
        {
            new TransferReadout.StepRow { Kind = "Injection burn (Home)", DeltaV = "1061.0 m/s", When = "Y1, D1", Done = true },
            new TransferReadout.StepRow { Kind = "Re-aim burn (Star)", DeltaV = "5.0 m/s", When = "Y1, D9" },
            new TransferReadout.StepRow { Kind = "Re-aim burn (Star)", DeltaV = "3.0 m/s", When = "Y1, D20" },
        };
        var rows = TransferTabLogic.BudgetRows(steps, 20);
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual("1061.0 m/s · done", rows[0].value);
        Assert.IsTrue(rows[0].done);
        Assert.AreEqual("Re-aim burn (Star) 2", rows[2].label, "labels kept unique");
        Assert.AreEqual(("Reserve", "20 m/s", false), rows[3]);
        Assert.AreEqual("Next: Re-aim burn (Star) · Y1, D9", TransferTabLogic.NextLine(steps));

        var plan = Plan(System());
        Assert.AreEqual(-1, TransferTabLogic.FlownFraction(plan, 0), "before the departure");
        Assert.AreEqual(0.5, TransferTabLogic.FlownFraction(plan, 1000 + 2_500_000), 1e-9);
        Assert.AreEqual(1, TransferTabLogic.FlownFraction(plan, 1e12), 1e-9);
    }
}
