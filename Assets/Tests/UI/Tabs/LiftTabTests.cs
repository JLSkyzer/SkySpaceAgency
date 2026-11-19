using System.Collections.Generic;
using System.Reflection;
using KTools;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.Controller;
using SkySpaceAgency.Lift;
using UnityEngine;
using UnityEngine.UIElements;

// The LIFT tab of the new window (plan 2026-11-19-new-ui-tab-migration): built and ticked without the
// game, every Lift setting bound, the mockup's sample, and the pure parts (planned curve, phases).
public class LiftTabTests
{
    static LiftTab Built()
    {
        var tab = new LiftTab(AppTabContext.Editor());
        tab.Build();
        return tab;
    }

    static bool Shown(VisualElement e) => e.style.display != DisplayStyle.None;

    // Every Setting / ClampSetting / EnumSetting field of LiftSettings, by its key.
    static List<string> SettingKeys(LiftSettings s)
    {
        var keys = new List<string>();
        foreach (var f in typeof(LiftSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object v = f.GetValue(s);
            if (v == null) continue;
            var t = v.GetType();
            var key = t.GetField("key") ?? t.GetField("path");
            if (key != null && key.GetValue(v) is string k) keys.Add(k);
        }
        return keys;
    }

    [Test]
    public void BuildsAndTicks_WithoutTheGame()
    {
        var tab = Built();
        Assert.AreEqual("lift", tab.Code);
        Assert.IsNotNull(tab.Page);
        Assert.Greater(tab.Page.Plan.childCount, 0);
        Assert.Greater(tab.Page.View.childCount, 0);
        Assert.Greater(tab.Page.Side.childCount, 0);
        Assert.IsNotNull(tab.Settings, "the tab builds its own settings without the pilot");
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); });
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.AreEqual("Start", tab.MainButton.Text);
        Assert.IsFalse(tab.MainButton.enabledSelf, "no pilot: Start is disabled");
        Assert.IsTrue(Shown(tab.Page.Timeline), "the phase timeline shows before a run");
    }

    [Test]
    public void EverySetting_IsBound()
    {
        var tab = Built();
        var keys = SettingKeys(tab.Settings);
        Assert.AreEqual(29, keys.Count, "LiftSettings holds 29 keys");
        foreach (string k in keys)
            Assert.IsTrue(AppBind.Bound.Contains(k), "no control for " + k);
        Assert.IsTrue(AppBind.Bound.Contains(StagingSettings.auto_staging.key), "auto staging switch");
    }

    [Test]
    public void ShowSample_FillsThePage()
    {
        var tab = Built();
        tab.ShowSample();
        Assert.AreEqual("18.4", tab.AltitudeTile.Value);
        Assert.AreEqual("41.2", tab.ApoapsisTile.Value);
        Assert.Greater(tab.ProfileView.Planned.Count, 10, "planned Guided curve");
        Assert.Greater(tab.ProfileView.Flown.Count, 1, "flown path");
        Assert.IsTrue(tab.ProfileView.Vessel.HasValue);
        Assert.That(tab.ProfileView.AtmosphereText, Does.Contain("70"));
        Assert.AreEqual(6, tab.Page.Timeline.Model.Steps.Count);
        Assert.AreEqual(1, tab.Page.Timeline.Model.Current);
        Assert.AreEqual("Running…", tab.MainButton.Text);
        Assert.That(tab.Page.Actions.Note, Does.StartWith("Next:"));
        Assert.DoesNotThrow(tab.Tick);
    }

    [Test]
    public void GoalAndProfile_ShowTheirSettings()
    {
        var tab = Built();
        var s = tab.Settings;
        s.ascent_profile.V = AscentProfile.Guided;
        s.goal.V = LiftGoal.PrepareTransfer;
        Assert.IsTrue(Shown(tab.TargetGroup), "a transfer has a target");
        Assert.IsTrue(Shown(tab.GuidedSettings));
        s.ascent_profile.V = AscentProfile.GravityTurn;
        Assert.IsTrue(Shown(tab.GuidedSettings), "a transfer always flies Guided");
        Assert.IsFalse(Shown(tab.GravityTurnSettings));
        s.goal.V = LiftGoal.Orbit;
        Assert.IsFalse(Shown(tab.TargetGroup));
        Assert.IsTrue(Shown(tab.GravityTurnSettings));
        Assert.IsFalse(Shown(tab.GuidedSettings));
        Assert.DoesNotThrow(tab.Tick);
        s.ascent_profile.V = AscentProfile.Guided;
    }

    [Test]
    public void MidPitch_NeverAboveEndPitch()
    {
        var tab = Built();
        var s = tab.Settings;
        s.end_rotate_ratio.V = 0.5f;
        s.mid_rotate_ratio.V = 0.2f;
        s.mid_rotate_ratio.V = 0.8f;
        Assert.AreEqual(0.8f, s.end_rotate_ratio.V, 1e-5f);
        s.end_rotate_ratio.V = 0.3f;
        Assert.AreEqual(0.3f, s.mid_rotate_ratio.V, 1e-5f);
    }

    // ---- pure parts ----

    [Test]
    public void Elevation_FollowsTheClassicCurve()
    {
        Assert.AreEqual(90, LiftProfileModel.Elevation(1, 2, 20, 50));
        Assert.AreEqual(67.5f, LiftProfileModel.Elevation(11, 2, 20, 50), 1e-3f);
        Assert.AreEqual(45, LiftProfileModel.Elevation(20, 2, 20, 50), 1e-3f);
        Assert.AreEqual(1, LiftProfileModel.Elevation(60, 2, 20, 50), 1e-3f);
    }

    [Test]
    public void PlannedPath_ClimbsToTheTargetOrTheEdge()
    {
        var path = LiftProfileModel.PlannedPath(2, 20, 50, 100, 1000);
        Assert.AreEqual(Vector2.zero, path[0]);
        for (int i = 1; i < path.Count; i++)
        {
            Assert.GreaterOrEqual(path[i].x, path[i - 1].x);
            Assert.Greater(path[i].y, path[i - 1].y);
        }
        // Past 50 km the pitch is 1°: the curve reaches the 1000 km edge first (at ~67 km).
        Vector2 last = path[path.Count - 1];
        Assert.IsTrue(last.y >= 100 - 1.01f || last.x >= 1000 - 1.01f, "ends at the target or the edge: " + last);
        Assert.GreaterOrEqual(last.x, 1000 - 1.01f);
        var wide = LiftProfileModel.PlannedPath(2, 20, 50, 100, 100000);
        Assert.GreaterOrEqual(wide[wide.Count - 1].y, 100 - 1.01f, "with room, it reaches the target");
        // Climbs straight up to 2 km.
        Assert.AreEqual(0, path[1].x, 1e-4f);
        var cut = LiftProfileModel.PlannedPath(2, 20, 50, 100, 30);
        Assert.Less(cut[cut.Count - 1].x, 31.1f, "stops at the right edge");
        Assert.AreEqual(1, LiftProfileModel.PlannedPath(2, 20, 50, 0, 100).Count);
    }

    [Test]
    public void Range_HoldsTheTargetAndTheFlownPath()
    {
        var r = LiftProfileModel.Range(100, null, null, 1.5f);
        Assert.AreEqual(112, r.y, 1e-3f);
        Assert.AreEqual(168, r.x, 1e-3f);
        r = LiftProfileModel.Range(100, new List<Vector2> { new Vector2(400, 130) }, null, 1.5f);
        Assert.AreEqual(130 * 1.12f, r.y, 1e-3f);
        Assert.GreaterOrEqual(r.x, 420 - 1e-3f);
    }

    [Test]
    public void Downrange_IsTheGreatCircle()
    {
        // One degree of longitude on the equator of a 600 km body.
        Assert.AreEqual(600 * Mathf.PI / 180, LiftProfileModel.DownrangeKm(0, 0, 0, 1, 600000), 1e-3f);
        Assert.AreEqual(0, LiftProfileModel.DownrangeKm(10, 20, 10, 20, 600000), 1e-5f);
    }

    [Test]
    public void Phases_FollowTheGoal()
    {
        CollectionAssert.AreEqual(
            new[] { "Vertical climb", "Gravity turn", "Coast to Ap", "Fine tuning", "Circularize", "In orbit" },
            LiftPhases.Steps(LiftGoal.Orbit, true, true));
        CollectionAssert.AreEqual(new[] { "Vertical climb", "Gravity turn", "Coast to Ap", "Ap set" },
            LiftPhases.Steps(LiftGoal.Orbit, false, false));
        var transfer = LiftPhases.Steps(LiftGoal.PrepareTransfer, true, false);
        Assert.AreEqual("Launch window", transfer[0]);
        Assert.Contains("Circularize", transfer, "a transfer always circularizes");
        Assert.AreEqual("Plane trim", transfer[transfer.Count - 1]);
        var rv = LiftPhases.Steps(LiftGoal.Rendezvous, true, true);
        Assert.AreEqual("Hand-over", rv[rv.Count - 1]);
    }

    [Test]
    public void PhaseIndex_LightsTheRunningStep()
    {
        var steps = LiftPhases.Steps(LiftGoal.Orbit, true, true);
        Assert.AreEqual(0, LiftPhases.Index(steps, LiftPilot.LiftStatus.Ascent, true, false));
        Assert.AreEqual(1, LiftPhases.Index(steps, LiftPilot.LiftStatus.Ascent, false, false));
        Assert.AreEqual(4, LiftPhases.Index(steps, LiftPilot.LiftStatus.Circularize, false, false));
        Assert.AreEqual(0, LiftPhases.Index(steps, LiftPilot.LiftStatus.Off, false, false));
        Assert.AreEqual(5, LiftPhases.Index(steps, LiftPilot.LiftStatus.Off, false, true));
        var noAdjust = LiftPhases.Steps(LiftGoal.Orbit, false, true);
        Assert.AreEqual(2, LiftPhases.Index(noAdjust, LiftPilot.LiftStatus.Adjust, false, false), "Adjust off: the coast stays lit");
        var transfer = LiftPhases.Steps(LiftGoal.PrepareTransfer, true, true);
        Assert.AreEqual(0, LiftPhases.Index(transfer, LiftPilot.LiftStatus.WaitLaunch, true, false));
        Assert.IsTrue(LiftPhases.IsVerticalClimb(AscentProfile.Guided, 1.5f, 2, null));
        Assert.IsFalse(LiftPhases.IsVerticalClimb(AscentProfile.Guided, 2.5f, 2, null));
        Assert.IsTrue(LiftPhases.IsVerticalClimb(AscentProfile.GravityTurn, 30, 2, "VerticalRise"));
        Assert.IsFalse(LiftPhases.IsVerticalClimb(AscentProfile.GravityTurn, 1, 2, "Turn"));
    }
}
