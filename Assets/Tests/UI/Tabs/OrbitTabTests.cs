using System;
using System.Linq;
using KSP.Sim;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.OrbitPlanning;
using UnityEngine;
using AppTabContext = SkySpaceAgency.AppUI.AppTabContext;

// The ORBIT tab of the new window (plan 2026-11-19-new-ui-tab-migration): built and ticked without the game,
// its settings bound, the mockup's sample, and the pure parts (view geometry, texts, pill, timeline).
public class OrbitTabTests
{
    static readonly BodyInfo Kerbin = OrbitViewModel.SampleBody;

    // Drawn radius (km) of a radius in km (the view is drawn by altitude, OrbitViewModel.DisplayRadius).
    static float D(double km) => (float)(OrbitViewModel.DisplayRadius(km * 1000, Kerbin.Radius) / 1000);

    static void Circular(double altitude, out Vector3d r, out Vector3d v)
    {
        double rad = Kerbin.Radius + altitude;
        r = new Vector3d(rad, 0, 0);
        v = new Vector3d(0, Math.Sqrt(Kerbin.Mu / rad), 0);
    }

    static OrbitPlan TwoBurns() => new OrbitPlan
    {
        Burns =
        {
            new PlannedBurn { UT = 100, DeltaV = new Vector3d(412, 0, 0), Label = "Set Ap 250.0 km in 3 min" },
            new PlannedBurn { UT = 900, DeltaV = new Vector3d(0, 690, 0), Label = "Set Pe 250.0 km at apoapsis" },
        }
    };

    // ---- The tab ----

    [Test]
    public void Tab_BuildsAndTicksWithoutTheGame()
    {
        var tab = new OrbitTab(AppTabContext.Editor());
        var page = tab.Build();
        Assert.IsNotNull(page);
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); });
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.AreEqual("orbit", tab.Code);
        Assert.AreEqual("Create nodes", tab.MainButton.Text);
        Assert.IsFalse(tab.MainButton.enabledSelf, "no game: the action is off");
        Assert.IsFalse(tab.PlanButton.enabledSelf);
        Assert.AreEqual("—", tab.ApTile.Value);
        Assert.IsTrue(tab.View.Model.IsEmpty);
        Assert.AreEqual(OrbitTab.Hint, tab.Console.text);
        Assert.AreEqual(2, page.Timeline.Dots.Count, "Plan and Done before any plan");
        Assert.AreEqual(2, page.Actions.Buttons.Count, "Plan, then the main button");
    }

    [Test]
    public void Tab_BindsEveryOrbitSetting()
    {
        new OrbitTab(AppTabContext.Editor()).Build();
        foreach (string key in new[]
                 {
                     OrbitSettings.ap_enabled.key, OrbitSettings.ap_km.key, OrbitSettings.pe_enabled.key,
                     OrbitSettings.pe_km.key, OrbitSettings.inc_enabled.key, OrbitSettings.inc_deg.key
                 })
            Assert.IsTrue(AppBind.Bound.Contains(key), key);
        Assert.AreEqual("orbit.ap_km", OrbitSettings.ap_km.key, "the classic keys, unchanged");
    }

    [Test]
    public void Tab_SampleFillsThePage()
    {
        var tab = new OrbitTab(AppTabContext.Editor());
        var page = tab.Build();
        tab.ShowSample();
        tab.Tick();
        Assert.AreEqual("100.2", tab.ApTile.Value);
        Assert.AreEqual("99.8", tab.PeTile.Value);
        Assert.AreEqual("0.1", tab.IncTile.Value);
        Assert.AreEqual("32:10", tab.PeriodTile.Value);
        Assert.AreEqual(2, tab.PlanRows.Count);
        Assert.AreEqual("412 m/s", tab.PlanRows.Get("1  Raise Ap to 250 km"));
        Assert.AreEqual("1 102 m/s", tab.TotalRows.Get("Total Δv"));
        Assert.AreEqual(StatusKind.Ok, tab.Pill.Kind);
        Assert.AreEqual(6, page.Timeline.Dots.Count);
        Assert.AreEqual("Start", tab.MainButton.Text, "a tick keeps the sample");
        var model = tab.View.Model;
        Assert.AreEqual(2, model.Markers.Count);
        Assert.AreEqual("1 · raise Ap · 412 m/s", tab.View.MarkerLabels[0].text);
        Assert.Greater(model.Current.Count, 10);
        Assert.Greater(model.Target.Count, 10);
        Assert.IsTrue(model.Vessel.HasValue);
        Assert.AreEqual("top view, Kerbin", tab.ViewCard.Note);
    }

    // ---- View geometry ----

    [Test]
    public void Basis_EquatorialOrbit_IsTheXYPlane()
    {
        OrbitViewModel.Basis(new Vector3d(0, 0, 5), out var u, out var w);
        Assert.AreEqual(1, u.x, 1e-9);
        Assert.AreEqual(1, w.y, 1e-9);
        OrbitViewModel.Basis(new Vector3d(0, -1, 1), out u, out w);
        Assert.AreEqual(0, Vector3d.Dot(u, new Vector3d(0, -1, 1)), 1e-9, "u in the orbit plane");
        Assert.AreEqual(0, Vector3d.Dot(u, w), 1e-9);
    }

    [Test]
    public void CircularOrbit_IsACircleAroundTheBody_WithTheVesselOnIt()
    {
        Circular(100000, out var r, out var v);
        var model = OrbitViewModel.Build(Kerbin, r, v, 0, null);
        Assert.IsTrue(model.CurrentClosed);
        Assert.AreEqual(OrbitViewModel.Samples + 1, model.Current.Count);
        foreach (var p in model.Current)
            Assert.AreEqual(D(700), p.magnitude, 0.5, "every point 700 km from the centre");
        Assert.AreEqual(0, model.Target.Count, "no plan: no target");
        Assert.AreEqual(0, model.Markers.Count);
        Assert.AreEqual(D(600), model.BodyRadiusKm, 1e-3);
        Assert.AreEqual(150, D(600), 1e-3, "drawn by altitude: the body shrinks by 0.75 of its radius");
        Assert.AreEqual(D(700), model.Vessel.Value.x, 0.5);
        Assert.AreEqual(0, model.Vessel.Value.y, 0.5);
        Assert.GreaterOrEqual(model.ExtentKm, D(700));
        Assert.AreEqual("current 100 x 100 km, 0°", model.CurrentText);
    }

    [Test]
    public void RefusedPlan_DrawsOnlyTheCurrentOrbit()
    {
        Circular(100000, out var r, out var v);
        var plan = new OrbitPlan { Error = OrbitPlanError.PeBelowSurface };
        var model = OrbitViewModel.Build(Kerbin, r, v, 0, plan);
        Assert.AreEqual(0, model.Target.Count);
        Assert.AreEqual(0, model.Markers.Count);
        Assert.AreEqual("", model.TargetText);
    }

    [Test]
    public void PlannerPlan_TargetAndMarkers_FollowTheBurns()
    {
        Circular(100000, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 0, Kerbin, new OrbitTargets { ApAltitude = 250000 });
        Assert.IsTrue(plan.Ok);
        Assert.Greater(plan.Burns.Count, 0);
        var model = OrbitViewModel.Build(Kerbin, r, v, 0, plan);
        Assert.AreEqual(plan.Burns.Count, model.Markers.Count);
        Assert.AreEqual(D(700), model.Markers[0].At.magnitude, 1, "the first burn is on the current orbit");
        float far = model.Target.Max(p => p.magnitude);
        Assert.AreEqual(D(850), far, 1.5, "the target reaches Ap 250 km");
        StringAssert.StartsWith("1 · set Ap 250", model.Markers[0].Label, "the planner's label (its number follows the culture)");
        StringAssert.EndsWith(" km · " + OrbitText.Dv(plan.Burns[0].DeltaV.magnitude), model.Markers[0].Label);
        StringAssert.StartsWith("target 250 x 100 km", model.TargetText);
    }

    [Test]
    public void Sample_BurnsSitWhereTheMockupPutsThem()
    {
        OrbitViewModel.Sample(out var r, out var v, out var ut, out var plan);
        var model = OrbitViewModel.Build(Kerbin, r, v, ut, plan);
        Assert.AreEqual(0, model.Vessel.Value.x, 0.5);
        Assert.AreEqual(D(700), model.Vessel.Value.y, 0.5, "vessel at the top");
        Assert.AreEqual(D(700), model.Markers[0].At.x, 2, "burn 1 on the right");
        Assert.AreEqual(0, model.Markers[0].At.y, 2);
        Assert.AreEqual(-D(850), model.Markers[1].At.x, 2, "burn 2 on the left, at the new apoapsis");
        Assert.AreEqual(D(850), model.Target.Max(p => Mathf.Abs(p.x)), 2);
        Assert.AreEqual(D(850) * Math.Cos(Math.PI / 4), model.Target.Max(p => Mathf.Abs(p.y)), 3, "a 45° tilt seen from above");
        Assert.Greater(model.Target.Max(p => Mathf.Abs(p.y)), D(700), "the gold orbit stays outside the dashed one, as in the mockup");
        StringAssert.EndsWith("45°", model.TargetText);
    }

    [Test]
    public void OpenTrajectory_IsCutAndNotClosed()
    {
        double rad = Kerbin.Radius + 100000;
        var r = new Vector3d(rad, 0, 0);
        var v = new Vector3d(0, 1.2 * Math.Sqrt(2 * Kerbin.Mu / rad), 0);
        var model = OrbitViewModel.Build(Kerbin, r, v, 0, null);
        Assert.IsFalse(model.CurrentClosed);
        Assert.Greater(model.Current.Count, 10);
        foreach (var p in model.Current)
        {
            Assert.GreaterOrEqual(p.magnitude, D(700) - 0.5f);
            Assert.IsFalse(float.IsNaN(p.x) || float.IsInfinity(p.x));
        }
        Assert.Less(model.ExtentKm, Kerbin.SoiRadius / 1000 * 1.1);
        StringAssert.StartsWith("current open, Pe 100 km", model.CurrentText);
    }

    [Test]
    public void BurnStates_AddEachBurnsDeltaV()
    {
        Circular(100000, out var r, out var v);
        var burns = new[]
        {
            new PlannedBurn { UT = 100, DeltaV = new Vector3d(412, 0, 0), Label = "a" },
            new PlannedBurn { UT = 100, DeltaV = new Vector3d(0, 690, 0), Label = "b" },
        };
        var states = OrbitViewModel.BurnStates(r, v, 100, Kerbin.Mu, burns, out var rEnd, out var vEnd);
        Assert.AreEqual(2, states.Count);
        Assert.AreEqual(r.x, states[0].r.x, 1e-6, "burn 1 at the start time: no propagation");
        Assert.AreEqual(v.x + 412, states[1].v.x, 1e-6, "burn 2 starts from burn 1's velocity");
        Assert.AreEqual(v.x + 412, vEnd.x, 1e-6);
        Assert.AreEqual(v.y + 690, vEnd.y, 1e-6, "the last burn's Δv is in the final velocity");
        Assert.AreEqual(r.x, rEnd.x, 1e-6);

        // Burn 1 at UT 100 from UT 0: its state is 100 s along the circular orbit (rEnd, at burn 2, is
        // after burn 1's radial Δv, so off the circle).
        states = OrbitViewModel.BurnStates(r, v, 0, Kerbin.Mu, TwoBurns().Burns, out _, out _);
        Assert.AreEqual(r.magnitude, states[0].r.magnitude, 1, "propagated along the circular orbit to burn 1");
        Assert.Greater(Vector3d.Dot(states[0].r, v), 0, "moved forward along the orbit");
    }

    // ---- Texts, pill, timeline ----

    [Test]
    public void Texts()
    {
        Assert.AreEqual("1 102 m/s", OrbitText.Dv(1101.6));
        Assert.AreEqual("412 m/s", OrbitText.Dv(412.2));
        Assert.AreEqual("32:10", OrbitText.Duration(1930));
        Assert.AreEqual("1:05:12", OrbitText.Duration(3912));
        Assert.AreEqual("—", OrbitText.Duration(double.PositiveInfinity));
        Assert.AreEqual("00:03:00", OrbitText.TPlus(180));
        Assert.AreEqual("Set Ap 250.0 km", OrbitText.Short("Set Ap 250.0 km at periapsis"));
        Assert.AreEqual("Set Pe 90.0 km", OrbitText.Short("Set Pe 90.0 km in 3 min"));
        Assert.AreEqual("Capture", OrbitText.Short("Capture at periapsis (120.0 km)"));
        Assert.AreEqual("Inclination 45.00°", OrbitText.Short("Inclination 45.00° at ascending node"));
        Assert.AreEqual("Step 1/2: node created for 'x'.", OrbitText.FirstSentence("Step 1/2: node created for 'x'. Start flies it."));
        Assert.AreEqual("No dot", OrbitText.FirstSentence("No dot"));
        Assert.AreEqual("2 · set Pe 250.0 km · 690 m/s", OrbitText.MarkerLabel(1, TwoBurns().Burns[1]));
    }

    [Test]
    public void Pill_PriorityAndKinds()
    {
        var plan = TwoBurns();
        Assert.AreEqual((StatusKind.Ok, "Plan ready: 2 burns, 1 102 m/s"), OrbitTab.PillFor(null, "", true, true, plan, null));
        Assert.AreEqual(StatusKind.Alert, OrbitTab.PillFor("Target Pe is below the surface.", "", true, true, plan, null).kind);
        Assert.AreEqual(StatusKind.Ok, OrbitTab.PillFor(null, "Plan complete: Ap 250 km.", true, true, plan, null).kind);
        Assert.AreEqual(StatusKind.Alert, OrbitTab.PillFor(null, "Plan stopped: no orbit to plan the next step from.", true, true, plan, null).kind);
        Assert.AreEqual(StatusKind.Alert, OrbitTab.PillFor(null, "Warning: the game holds 2 node(s), 1 expected. Plan cancelled.", true, true, plan, null).kind);
        var step = OrbitTab.PillFor(null, "Step 1/2: node created for 'Set Ap'. Start flies the whole plan.", true, true, plan, null);
        Assert.AreEqual((StatusKind.Info, "Step 1/2: node created for 'Set Ap'."), step);
        Assert.AreEqual(StatusKind.Neutral, OrbitTab.PillFor(null, "", false, false, null, null).kind);
        Assert.AreEqual(StatusKind.Neutral, OrbitTab.PillFor(null, "", true, false, null, null).kind);
        Assert.AreEqual(StatusKind.Neutral, OrbitTab.PillFor(null, "", true, true, null, "Tick at least one target (Ap, Pe or Inclination) first.").kind);
        Assert.AreEqual(StatusKind.Alert, OrbitTab.PillFor(null, "", true, true, plan, "Target Pe is inside the atmosphere.").kind);
        Assert.AreEqual(StatusKind.Ok, OrbitTab.PillFor(null, "", true, true, new OrbitPlan(), null).kind, "nothing to do");
    }

    [Test]
    public void Console_AsTheClassicSummary()
    {
        Assert.AreEqual("refused", OrbitTab.ConsoleText("refused", "plan\n", "Step 1", "preview"));
        Assert.AreEqual("plan\nStep 1", OrbitTab.ConsoleText(null, "plan\n", "Step 1", "preview"));
        Assert.AreEqual("Step 1", OrbitTab.ConsoleText(null, null, "Step 1", "preview"));
        Assert.AreEqual("preview", OrbitTab.ConsoleText(null, null, "", "preview"));
        Assert.AreEqual(OrbitTab.Hint, OrbitTab.ConsoleText(null, null, "", null));
    }

    [Test]
    public void Timeline_PlanStepsDone()
    {
        var phases = OrbitTab.PhasesFor(new[] { "Set Ap 250.0 km at periapsis", "Inclination 45.00° at ascending node" });
        CollectionAssert.AreEqual(new[] { "Plan", "Set Ap 250.0 km", "Inclination 45.00°", "Done" }, phases);
        CollectionAssert.AreEqual(new[] { "Plan", "Done" }, OrbitTab.PhasesFor(null));
        Assert.AreEqual(0, OrbitTab.PhaseIndex(4, false, 0, ""));
        Assert.AreEqual(1, OrbitTab.PhaseIndex(4, true, 1, "Step 1/2"));
        Assert.AreEqual(2, OrbitTab.PhaseIndex(4, true, 4, "Step 4/2"), "extra correction steps stay on the last burn");
        Assert.AreEqual(3, OrbitTab.PhaseIndex(4, false, 2, "Plan complete: Ap 250 km."));
        Assert.AreEqual(0, OrbitTab.PhaseIndex(4, false, 1, "Plan stopped: ..."));
    }
}
