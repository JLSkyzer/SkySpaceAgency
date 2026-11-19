using System.Collections.Generic;
using NUnit.Framework;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.Landing;
using UnityEngine;
using UnityEngine.UIElements;

// The new window's LAND tab (AppUI/Tabs/LandTab.cs), without the game: build, tick, bindings, the
// sample, and the pure parts (phases, descent profile geometry, formats).
public class LandTabTests
{
    static LandTab Built()
    {
        var tab = new LandTab(AppTabContext.Editor());
        tab.Build();
        return tab;
    }

    // Every persisted key of the classic Land tab (LandingSettings, TouchDown's settings with a control).
    static readonly string[] Keys =
    {
        "land.auto_warp", "land.burnBefore", "land.rotation_warp_duration", "land.max_rotation",
        "land.touch_down_altitude", "land.touch_down_ratio", "land.touch_down_speed",
        "land.precision_landing", "land.target_latitude", "land.target_longitude", "land.mode",
        "land.deploy_legs_early", "land.reentry_drag_scale", "land.drop_stage_at_km",
        "land.entry_angle_of_attack", "land.entry_flight_path_angle", "land.boost_back_flip",
        "land.touch_down_max_angle", "land.steering_max_angle",
    };

    [Test]
    public void BuildAndTick_WithoutTheGame()
    {
        var tab = Built();
        Assert.AreEqual("land", tab.Code);
        Assert.AreEqual("Land", tab.Label);
        Assert.IsNull(tab.Pilot);
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); tab.Tick(); });
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.AreEqual(1, tab.Page.Plan.childCount);
        Assert.IsTrue(tab.Page.View.Contains(tab.Profile));
        Assert.Greater(tab.Steps.Count, 2, "the idle timeline shows the planned landing");
        Assert.AreEqual(DisplayStyle.Flex, tab.Page.Timeline.style.display.value);
    }

    [Test]
    public void OneProfileShown_FollowingTheBody()
    {
        var tab = Built();
        tab.Tick();
        bool atmo = LandingProfile.IsAtmospheric;
        Assert.AreEqual(atmo ? DisplayStyle.Flex : DisplayStyle.None, tab.AtmoPlan.style.display.value);
        Assert.AreEqual(atmo ? DisplayStyle.None : DisplayStyle.Flex, tab.VacPlan.style.display.value);
    }

    [Test]
    public void EverySetting_IsBound()
    {
        Built();
        foreach (string key in Keys)
            Assert.IsTrue(AppBind.Bound.Contains(key), key + " has a control");
    }

    [Test]
    public void Mode_FollowsTheSettingBothWays()
    {
        var tab = Built();
        tab.AtmoSettings.mode.V = LandingMethod.Parachute;
        Assert.AreEqual(1, tab.ModeChoice.Selected);
        tab.ModeChoice.Select(0);
        Assert.AreEqual(LandingMethod.Propulsive, tab.AtmoSettings.mode.V);
    }

    [Test]
    public void AtmosphericPrecision_StaysOffInPropulsiveMode()
    {
        Assume.That(!ModIdentity.IsK2D2, "the K2-D2 flavour allows the boost-back");
        var tab = Built();
        tab.AtmoSettings.mode.V = LandingMethod.Propulsive;
        tab.AtmoSettings.precision_landing.V = true;
        Assert.IsFalse(tab.AtmoSettings.precision_landing.V);
        tab.AtmoSettings.mode.V = LandingMethod.Parachute;
        tab.AtmoSettings.precision_landing.V = true;
        Assert.IsTrue(tab.AtmoSettings.precision_landing.V);
        tab.AtmoSettings.mode.V = LandingMethod.Propulsive;
        Assert.IsFalse(tab.AtmoSettings.precision_landing.V, "switching back to Propulsive turns it off");
    }

    [Test]
    public void VacuumTarget_ShownWithPrecisionOnly()
    {
        var tab = Built();
        tab.VacSettings.precision_landing.V = false;
        Assert.AreEqual(DisplayStyle.None, tab.Targets.Vac.style.display.value);
        tab.VacSettings.precision_landing.V = true;
        Assert.AreEqual(DisplayStyle.Flex, tab.Targets.Vac.style.display.value);
    }

    [Test]
    public void Target_ShowsTheSavedCoordinates()
    {
        var tab = Built();
        tab.AtmoSettings.target_latitude.V = -12.5f;
        tab.AtmoSettings.target_longitude.V = 40.25f;
        var line = tab.Targets.Atmo.Q<Label>(className: "ssa-land-target__line");
        StringAssert.StartsWith("12.50° S · 40.25° E", line.text);
    }

    [Test]
    public void DropHeight_NotANumber_IsMarked()
    {
        var tab = Built();
        var field = tab.AtmoAdvanced.Q<AppTextField>();
        tab.AtmoSettings.drop_stage_at_km.V = "abc";
        Assert.IsTrue(field.ClassListContains("ssa-land-field--invalid"));
        tab.AtmoSettings.drop_stage_at_km.V = "";
        Assert.IsFalse(field.ClassListContains("ssa-land-field--invalid"), "empty is automatic");
        tab.AtmoSettings.drop_stage_at_km.V = "2.5";
        Assert.IsFalse(field.ClassListContains("ssa-land-field--invalid"));
    }

    [Test]
    public void Reset_ResetsBothProfiles()
    {
        var tab = Built();
        tab.AtmoSettings.burn_before.V = 7;
        tab.VacSettings.burn_before.V = 5;
        tab.AtmoAdvanced.Q<AppButton>("land-reset-atmo").Click();
        Assert.AreEqual(0, tab.AtmoSettings.burn_before.V, 1e-4f);
        Assert.AreEqual(0, tab.VacSettings.burn_before.V, 1e-4f);
    }

    [Test]
    public void ShowSample_FillsThePage()
    {
        var tab = Built();
        tab.ShowSample();
        tab.Tick();
        Assert.AreEqual(DisplayStyle.Flex, tab.AtmoPlan.style.display.value, "the mockup is on Duna: the atmospheric profile");
        Assert.IsTrue(tab.Profile.Model.HasData);
        Assert.GreaterOrEqual(tab.Profile.LabelCount, 6, "atmosphere, heating, four markers, target");
        CollectionAssert.AreEqual(new[] { "Deorbit", "Pre-brake", "Atmospheric coast", "Drop stage 1", "Brake", "Touch down" }, tab.Steps);
        Assert.AreEqual(1, tab.CurrentStep);
        Assert.AreEqual("1 122", tab.DvNeeded.Value);
        Assert.AreEqual("6:41", tab.Impact.Value);
        StringAssert.Contains("feasible", tab.Feasible.Text);
        Assert.AreEqual("Duna base site", tab.Targets.Atmo.Q<Label>(className: "ssa-land-target__name").text);
        Assert.AreEqual(205f / 336f, tab.Gauge.Fraction, 1e-4f);
        Assert.AreEqual("Landing…", tab.MainButton.Text);
    }

    // ---- LandPhases ----

    [Test]
    public void Phases_OfEachLanding()
    {
        CollectionAssert.AreEqual(new[] { "Deorbit", "Warp to burn", "Brake", "Touch down" }, LandPhases.Steps(LandRunKind.Descent, false, 0));
        CollectionAssert.AreEqual(new[] { "Warp to burn", "Drop stages", "Brake", "Touch down" }, LandPhases.Steps(LandRunKind.Collision, false, 2));
        CollectionAssert.AreEqual(new[] { "Parachutes", "Touch down" }, LandPhases.Steps(LandRunKind.Parachute, true, 0));
        Assert.AreEqual("Parachutes", LandPhases.Steps(LandRunKind.Reentry, true, 0)[5]);
        Assert.AreEqual(8, LandPhases.Steps(LandRunKind.Precision, false, 1).Count);
    }

    [Test]
    public void Phases_CurrentStepOfAMode()
    {
        var atmo = LandPhases.Steps(LandRunKind.Descent, true, 1);
        Assert.AreEqual(1, LandPhases.IndexOf(atmo, LandingPilot.Mode.PreBrake, false));
        Assert.AreEqual(3, LandPhases.IndexOf(atmo, LandingPilot.Mode.Brake, true), "a stage still to drop lights the drop step");
        Assert.AreEqual(4, LandPhases.IndexOf(atmo, LandingPilot.Mode.Brake, false));
        Assert.AreEqual(2, LandPhases.IndexOf(atmo, LandingPilot.Mode.QuickWarp, false), "no warp step: the coast");
        var vac = LandPhases.Steps(LandRunKind.Descent, false, 0);
        Assert.AreEqual(1, LandPhases.IndexOf(vac, LandingPilot.Mode.Waiting, false));
        Assert.AreEqual(3, LandPhases.IndexOf(vac, LandingPilot.Mode.TouchDown, false));
        Assert.AreEqual(0, LandPhases.IndexOf(vac, LandingPilot.Mode.Off, false));
        var precision = LandPhases.Steps(LandRunKind.Precision, false, 0);
        Assert.AreEqual(0, LandPhases.IndexOf(precision, LandingPilot.Mode.Calibrate, false));
    }

    [Test]
    public void Phases_KindOfTheLanding()
    {
        Assert.AreEqual(LandRunKind.Reentry, LandPhases.KindOf(false, false, false, false, false, true, false, true, true));
        Assert.AreEqual(LandRunKind.Parachute, LandPhases.KindOf(false, false, false, false, false, true, false, false, true));
        Assert.AreEqual(LandRunKind.Precision, LandPhases.KindOf(false, false, false, false, false, false, false, true, false));
        Assert.AreEqual(LandRunKind.Descent, LandPhases.KindOf(false, false, false, false, false, false, false, false, true));
        Assert.AreEqual(LandRunKind.Collision, LandPhases.KindOf(true, false, false, false, false, false, false, false, false));
        Assert.AreEqual(LandRunKind.Descent, LandPhases.KindOf(true, false, false, false, true, false, false, false, true));
        Assert.AreEqual(LandRunKind.Reentry, LandPhases.KindOf(true, false, true, true, false, true, false, true, true));
    }

    // ---- LandProfileModel ----

    [Test]
    public void Profile_GreatCircle()
    {
        double r = 600000;
        Assert.AreEqual(System.Math.PI * r / 2 / 1000, LandProfileModel.GreatCircleKm(0, 0, 0, 90, r), 1e-6);
        Assert.AreEqual(0, LandProfileModel.GreatCircleKm(10, 20, 10, 20, r), 1e-9);
        Assert.AreEqual(System.Math.PI * r / 1000, LandProfileModel.GreatCircleKm(90, 0, -90, 0, r), 1e-3);
    }

    [Test]
    public void Profile_TouchdownOnTheRight_GroundAtTheBottom()
    {
        var box = new Rect(0, 0, 1000, 500);
        Vector2 touchdown = LandProfileModel.Map(box, 100, 50, Vector2.zero);
        Vector2 far = LandProfileModel.Map(box, 100, 50, new Vector2(100, 50));
        Assert.AreEqual(LandProfileModel.RightAt * 1000, touchdown.x, 1e-3f);
        Assert.AreEqual(LandProfileModel.GroundAt * 500, touchdown.y, 1e-3f);
        Assert.AreEqual(LandProfileModel.LeftAt * 1000, far.x, 1e-3f);
        Assert.AreEqual(LandProfileModel.TopAt * 500, far.y, 1e-3f);
        Assert.Less(far.x, touchdown.x);
    }

    [Test]
    public void Profile_Samples_ArcToTheLastOne()
    {
        double r = 320000;   // Duna
        var samples = new List<LandSample>();
        for (int i = 0; i <= 10; i++)
        {
            double a = (10 - i) * 0.01;   // radians to the last sample
            samples.Add(new LandSample { X = r * System.Math.Cos(a), Y = r * System.Math.Sin(a), Z = 0, UT = 100 + i * 10, AltKm = 50 - i * 5 });
        }
        var m = new LandProfileModel();
        var uts = m.SetPredictedSamples(samples, r, 125);
        Assert.AreEqual(8, m.Predicted.Count, "the samples before now are left out");
        Assert.AreEqual(0.07 * r / 1000, m.Predicted[0].x, 1e-2);
        Assert.AreEqual(0, m.Predicted[m.Predicted.Count - 1].x, 1e-3);
        Assert.AreEqual(0, m.Predicted[m.Predicted.Count - 1].y, 1e-3);
        Vector2? mid = m.PointAtUT(uts, 155);
        Assert.IsTrue(mid.HasValue);
        Assert.AreEqual(22.5f, mid.Value.y, 1e-3f);
        Assert.IsNull(m.PointAtUT(uts, 500));
        Vector2? drop = LandProfileModel.PointAtAltitude(m.Predicted, 12.5f);
        Assert.IsTrue(drop.HasValue);
        Assert.AreEqual(12.5f, drop.Value.y, 1e-3f);
    }

    [Test]
    public void Profile_CurveEndsAtTheTouchdown()
    {
        var m = new LandProfileModel();
        m.SetPredictedCurve(new Vector2(120, 30));
        Assert.AreEqual(new Vector2(120, 30), m.Predicted[0]);
        Assert.AreEqual(Vector2.zero, m.Predicted[m.Predicted.Count - 1]);
        Assert.AreEqual(m.Predicted[20], LandProfileModel.PointAtFraction(m.Predicted, 0.5f));
        Assert.GreaterOrEqual(m.YMax, 30);
        Assert.GreaterOrEqual(m.XMax, 120);
    }

    [Test]
    public void Profile_Sample_IsTheMockup()
    {
        var m = LandProfileModel.Sample();
        Assert.IsTrue(m.HasData);
        Assert.AreEqual(50f, m.AtmosphereKm);
        Assert.AreEqual(4, m.Markers.Count);
        Assert.Greater(m.HeatTo, m.HeatFrom);
        Assert.GreaterOrEqual(m.HeatFrom, 0);
        Assert.AreEqual(Vector2.zero, m.Predicted[m.Predicted.Count - 1]);
    }

    // ---- formats and target text ----

    [Test]
    public void Formats()
    {
        Assert.AreEqual("1 640", LandFormat.Thousands(1640.2));
        Assert.AreEqual("562", LandFormat.Thousands(562));
        Assert.AreEqual("—", LandFormat.Thousands(double.NaN));
        Assert.AreEqual("6:41", LandFormat.Clock(401));
        Assert.AreEqual("1:02:05", LandFormat.Clock(3725));
        Assert.AreEqual("—", LandFormat.Clock(-3));
        StringAssert.Contains("46 % Δv margin", LandFormat.Feasibility(1122, 1640, 1.10, out bool ok));
        Assert.IsTrue(ok);
        StringAssert.StartsWith("Δv short", LandFormat.Feasibility(1000, 1050, 1.10, out ok));
        Assert.IsFalse(ok);
        Assert.IsNull(LandFormat.Feasibility(double.NaN, 1000, 1.10, out _));
    }

    [Test]
    public void TargetText()
    {
        Assert.AreEqual("17.42° N · 88.10° W", LandTargetBlock.CoordinatesText(17.42, -88.10));
        Assert.AreEqual("Duna base", LandTargetBlock.DisplayName(WaypointChoices.MissionPrefix + "Duna base"));
        Assert.AreEqual("mission waypoint", LandTargetBlock.KindOf(WaypointChoices.MissionPrefix + "Duna base"));
        Assert.AreEqual("launch site", LandTargetBlock.KindOf(WaypointChoices.KscName));
        Assert.AreEqual("Custom coordinates", LandTargetBlock.DisplayName(WaypointChoices.CustomName + " (1, 2)"));
        Assert.AreEqual("No target", LandTargetBlock.DisplayName(""));
    }
}
