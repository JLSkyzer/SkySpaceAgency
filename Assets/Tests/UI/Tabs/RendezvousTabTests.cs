using System;
using AppTabContext = SkySpaceAgency.AppUI.AppTabContext;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.Rendezvous;
using UnityEngine;

// The RENDEZVOUS tab of the new window (plan 2026-11-19-new-ui-tab-migration), without the game.
public class RendezvousTabTests
{
    static RendezvousTab Built()
    {
        var tab = new RendezvousTab(AppTabContext.Editor());
        tab.Build();
        return tab;
    }

    // ---------------------------------------------------------------- the tab

    [Test]
    public void Tab_BuildsAndTicks_WithoutTheGame()
    {
        var tab = Built();
        Assert.AreEqual("rendezvous", tab.Code);
        Assert.AreEqual("Rendezvous", tab.Label);
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); tab.Tick(); });
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.AreEqual(RendezvousTab.NoPilotStatus, tab.Status.Pill.Text);
        Assert.IsFalse(tab.MainButton.enabledSelf, "no pilot: the actions are disabled");
        Assert.AreEqual("Start", tab.MainButton.Text);
        Assert.AreEqual(RvMode.RendezvousAndDock, tab.SelectedMode, "the mockup's default: + Dock");
        Assert.AreEqual("Choose a target", tab.View.MessageText);
    }

    [Test]
    public void Tab_BindsItsSettings()
    {
        Built();
        Assert.That(AppBind.Bound, Does.Contain(RendezvousSettings.prepare_only.key));
        Assert.That(AppBind.Bound, Does.Contain(RendezvousSettings.target_id.key));
    }

    [Test]
    public void ShowSample_FillsThePage()
    {
        var tab = Built();
        tab.ShowSample();
        Assert.AreEqual(3, tab.Targets.RowCount);
        Assert.AreEqual("sample-1", tab.Targets.Selected);
        Assert.AreEqual("48.6", tab.DistanceTile.Value);
        Assert.AreEqual("86", tab.RcsTile.Value);
        Assert.AreEqual(3, tab.Checks.Count);
        Assert.AreEqual(RendezvousChecks.NeverCommanded, tab.Checks.TextAt(0));
        Assert.AreEqual("Holding at 50 m, then final approach", tab.Status.Pill.Text);
        Assert.AreEqual(StatusKind.Ok, tab.Status.Pill.Kind);
        Assert.IsTrue(tab.View.State.HasTarget && tab.View.State.PortFrame);
        Assert.AreEqual("hold at 50 m", tab.View.NoteText);
        var model = tab.Page.Timeline.Model;
        Assert.AreEqual("Hold 50 m", model.Steps[model.Current]);
        Assert.AreEqual("Docking…", tab.MainButton.Text);
        Assert.AreEqual(RendezvousTab.IdleNote, tab.Page.Actions.Note);
        // A tick after the sample keeps it (the harness may tick).
        tab.Tick();
        Assert.AreEqual("48.6", tab.DistanceTile.Value);
    }

    [Test]
    public void View_RepaintsOnlyOnAChange()
    {
        var view = new ApproachView();
        var s = new ApproachState { HasTarget = true, PortFrame = true, Distance = 48.6, Angle = 0.01, Note = "hold at 50 m" };
        Assert.IsTrue(view.Set(s));
        Assert.IsFalse(view.Set(s), "same state");
        s.Distance = 48.61;
        Assert.IsFalse(view.Set(s), "under the tolerance");
        s.Distance = 40;
        Assert.IsTrue(view.Set(s));
        s.Note = "hold at 10 m";
        Assert.IsTrue(view.Set(s));
    }

    // ---------------------------------------------------------------- phases

    [Test]
    public void Phases_FollowThePilot()
    {
        var m = RvMode.RendezvousAndDock;
        Assert.AreEqual(0, RendezvousTabPhases.Index(m, RvPhase.Plan, DockPhase.Align));
        Assert.AreEqual(1, RendezvousTabPhases.Index(m, RvPhase.PlaneMatch, DockPhase.Align));
        Assert.AreEqual(2, RendezvousTabPhases.Index(m, RvPhase.Intercept, DockPhase.Align));
        Assert.AreEqual(3, RendezvousTabPhases.Index(m, RvPhase.PhasingWait, DockPhase.Align));
        Assert.AreEqual(3, RendezvousTabPhases.Index(m, RvPhase.Correction, DockPhase.Align));
        Assert.AreEqual(4, RendezvousTabPhases.Index(m, RvPhase.Brake, DockPhase.Align));
        Assert.AreEqual(5, RendezvousTabPhases.Index(m, RvPhase.Dock, DockPhase.Align));
        Assert.AreEqual(5, RendezvousTabPhases.Index(m, RvPhase.Dock, DockPhase.Hold50));
        Assert.AreEqual(5, RendezvousTabPhases.Index(m, RvPhase.Dock, DockPhase.Backoff));
        Assert.AreEqual(6, RendezvousTabPhases.Index(m, RvPhase.Dock, DockPhase.Hold10));
        Assert.AreEqual(6, RendezvousTabPhases.Index(m, RvPhase.Dock, DockPhase.Final));
        Assert.AreEqual(7, RendezvousTabPhases.Index(m, RvPhase.Docked, DockPhase.Docked));
        Assert.AreEqual(-1, RendezvousTabPhases.Index(m, RvPhase.Failed, DockPhase.Aborted));
        Assert.AreEqual("Hold 50 m", RendezvousTabPhases.Full[5]);
        Assert.AreEqual("Docked", RendezvousTabPhases.Full[7]);

        Assert.AreEqual(RendezvousTabPhases.RendezvousOnly.Length - 1, RendezvousTabPhases.Index(RvMode.RendezvousOnly, RvPhase.Done, DockPhase.Align));
        Assert.AreEqual(0, RendezvousTabPhases.Index(RvMode.DockOnly, RvPhase.Dock, DockPhase.Align));
        Assert.AreEqual(2, RendezvousTabPhases.Index(RvMode.DockOnly, RvPhase.Dock, DockPhase.Hold10));
        Assert.AreEqual(3, RendezvousTabPhases.Index(RvMode.DockOnly, RvPhase.Docked, DockPhase.Docked));
    }

    [Test]
    public void Phases_ChainStartsWithTheTransfer()
    {
        var steps = RendezvousTabPhases.Steps(RvMode.RendezvousAndDock, chain: true);
        Assert.AreEqual(RendezvousTabPhases.Full.Length + 1, steps.Count);
        Assert.AreEqual(RendezvousTabPhases.Transfer, steps[0]);
        Assert.AreEqual(RendezvousTabPhases.DockOnly, RendezvousTabPhases.Steps(RvMode.DockOnly, chain: false));
        Assert.AreEqual("Docking…", RendezvousTabPhases.RunningLabel(false, true, false));
        Assert.AreEqual("Planning…", RendezvousTabPhases.RunningLabel(true, false, false));
        Assert.AreEqual("hold at 50 m", RendezvousTab.HoldNote(DockPhase.Hold50));
    }

    // ---------------------------------------------------------------- geometry

    [Test]
    public void Geometry_RingsAreTheMockups()
    {
        Assert.AreEqual(0, ApproachGeometry.Radius(0), 1e-4);
        Assert.AreEqual(70, ApproachGeometry.Radius(10), 1e-4);
        Assert.AreEqual(150, ApproachGeometry.Radius(50), 1e-4);
        Assert.AreEqual(230, ApproachGeometry.Radius(200), 1e-4);
        Assert.AreEqual(ApproachGeometry.RMax, ApproachGeometry.Radius(200000), 1e-3);
        Assert.AreEqual(ApproachGeometry.RMax, ApproachGeometry.Radius(1e9), 1e-3);
        Assert.AreEqual(ApproachGeometry.RMax, ApproachGeometry.Radius(double.NaN), 1e-3);
        float last = -1;
        foreach (double d in new[] { 0.5, 5, 9.9, 10.1, 30, 49, 51, 120, 199, 201, 2000, 20000 })
        {
            float r = ApproachGeometry.Radius(d);
            Assert.Greater(r, last, $"monotonic at {d} m");
            last = r;
        }
        Assert.IsFalse(ApproachGeometry.OffScale(200));
        Assert.IsTrue(ApproachGeometry.OffScale(201));
    }

    [Test]
    public void Geometry_PlacesTheVessel()
    {
        Vector2 a = ApproachGeometry.Place(50, 0);
        Assert.AreEqual(150, a.x, 1e-3);
        Assert.AreEqual(0, a.y, 1e-3);
        Vector2 up = ApproachGeometry.Place(50, Math.PI / 2);
        Assert.AreEqual(0, up.x, 1e-3);
        Assert.AreEqual(-150, up.y, 1e-3, "positive angles go up (screen y down)");
        Assert.AreEqual(0, ApproachGeometry.PortAngle(48.6, 0), 1e-9);
        Assert.AreEqual(Math.PI / 2, ApproachGeometry.PortAngle(10, 10), 1e-9);
        Assert.AreEqual(Math.PI / 2, ApproachGeometry.PortAngle(10, 30), 1e-9, "lateral clamped to the distance");
        Assert.AreEqual(0, ApproachGeometry.PortAngle(double.NaN, 1), 1e-9);
        Assert.IsTrue(ApproachGeometry.InCorridor(48.6, 0.1));
        Assert.IsFalse(ApproachGeometry.InCorridor(48.6, 0.5));
    }

    // ---------------------------------------------------------------- texts

    [Test]
    public void Format_Distances()
    {
        Assert.AreEqual("49 m", RendezvousTabFormat.Distance(48.6));
        Assert.AreEqual("9 800 m", RendezvousTabFormat.Distance(9800));
        Assert.AreEqual("18.2 km", RendezvousTabFormat.Distance(18200));
        Assert.AreEqual("182 km", RendezvousTabFormat.Distance(182000));
        Assert.AreEqual("2 410 km", RendezvousTabFormat.Distance(2410000));
        Assert.AreEqual("-", RendezvousTabFormat.Distance(double.NaN));
        Assert.AreEqual(("48.6", "m"), RendezvousTabFormat.DistanceTile(48.6));
        Assert.AreEqual(("1 250", "m"), RendezvousTabFormat.DistanceTile(1250));
        Assert.AreEqual(("182", "km"), RendezvousTabFormat.DistanceTile(182000));
        Assert.AreEqual(("-", "m"), RendezvousTabFormat.DistanceTile(double.NaN));
    }

    [Test]
    public void Checks_SayWhatIsKnown()
    {
        var none = RendezvousChecks.Build(false, false, null, false, -1, 0, null, true, null, null);
        Assert.AreEqual(1, none.Count);
        Assert.AreEqual(RendezvousChecks.NeverCommanded, none[0].Text);

        var bad = RendezvousChecks.Build(true, true, "The target has no docking port.", false, 0, 0, "Δv: short by 5 m/s", false,
            "Translation: refused", "Aborts: 1");
        Assert.AreEqual(7, bad.Count);
        Assert.AreEqual(StatusKind.Info, bad[1].Kind, "far target");
        Assert.AreEqual(StatusKind.Alert, bad[2].Kind, "no port");
        Assert.AreEqual(StatusKind.Info, bad[3].Kind, "no RCS: the main engine docks");
        Assert.AreEqual(StatusKind.Alert, bad[4].Kind, "short of Δv");
        Assert.AreEqual(StatusKind.Alert, bad[5].Kind, "translation refused");
        Assert.AreEqual(StatusKind.Alert, bad[6].Kind, "aborts");

        var good = RendezvousChecks.Build(true, false, "Auto picks the free compatible pair that needs the least rotation.", true,
            12, 12, "Δv: 300 m/s left after the plan", true, null, null);
        Assert.IsTrue(good.TrueForAll(c => c.Kind == StatusKind.Ok));
        Assert.AreEqual("RCS: 12 of 12 thruster(s) usable", good[2].Text);

        Assert.AreEqual(0.5f, RendezvousChecks.Fraction(5, 10), 1e-6);
        Assert.IsNaN(RendezvousChecks.Fraction(1, 0));
    }
}
