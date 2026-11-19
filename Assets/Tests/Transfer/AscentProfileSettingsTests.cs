using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SkySpaceAgency.Lift;
using NUnit.Framework;

// The Gravity Turn ascent profile's settings keep K2-D2 1.4.0's keys, defaults and ranges
// verbatim (IanMealworm/K2D2Redux upstream/main LiftSettings.cs), so a K2-D2 1.4.x settings file
// reads the same in both flavours.
public class AscentProfileSettingsTests
{
    // Literal copy of upstream's declarations: key, default, min, max (NaN: not a ClampSetting).
    static readonly (string key, object def, double min, double max)[] Upstream =
    {
        ("lift.ascent_profile", AscentProfile.Guided, double.NaN, double.NaN),
        ("lift.gravity_turn_kick_speed_mps", 100f, 10, 300),
        ("lift.gravity_turn_pitch_kick_deg", 7.5f, 1, 25),
        ("lift.gravity_turn_hold_time_s", 45f, 25, 120),
        ("lift.gravity_turn_auto", true, double.NaN, double.NaN),
        ("lift.gravity_turn_min_throttle", 0.25f, 0, 0.5),
        ("lift.gravity_turn_max_twr", 2.5f, 1.2, 5),
        ("lift.gravity_turn_max_q_kpa", 40f, 10, 100),
        // Changed by 1.4.0 as well.
        ("lift.max_throttle", 1f, 0.05, 1),
        ("lift.pause_on_final", false, double.NaN, double.NaN),
    };

    static string KeyOf(object setting)
    {
        var t = setting.GetType();
        var f = t.GetField("key") ?? t.GetField("path");
        return f?.GetValue(setting) as string;
    }

    static object Field(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null)
                return f.GetValue(o);
        }
        return null;
    }

    static Dictionary<string, object> Settings()
    {
        var settings = new LiftSettings();
        var found = new Dictionary<string, object>();
        foreach (var f in typeof(LiftSettings).GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            object value = f.GetValue(settings);
            if (value == null)
                continue;
            string key = KeyOf(value);
            if (key != null)
                found[key] = value;
        }
        return found;
    }

    [Test]
    public void SettingKeysMatchUpstream()
    {
        var found = Settings();

        var ours = found.Keys.Where(k => k == "lift.ascent_profile" || k.StartsWith("lift.gravity_turn_")).OrderBy(k => k).ToArray();
        var theirs = Upstream.Select(u => u.key).Where(k => k == "lift.ascent_profile" || k.StartsWith("lift.gravity_turn_")).OrderBy(k => k).ToArray();
        CollectionAssert.AreEqual(theirs, ours, "the same keys, no more, no less");

        foreach (var u in Upstream)
        {
            Assert.IsTrue(found.TryGetValue(u.key, out object s), u.key);
            Assert.AreEqual(u.def, Field(s, "default_value"), u.key + " default");
            if (double.IsNaN(u.min))
                continue;
            Assert.AreEqual(u.min, Convert.ToDouble(Field(s, "_min")), 1e-6, u.key + " min");
            Assert.AreEqual(u.max, Convert.ToDouble(Field(s, "_max")), 1e-6, u.key + " max");
        }
    }

    // User decision 2026-11-19: Gravity Turn for Orbit launches only; the other goals fly Guided.
    [Test]
    public void GravityTurn_FliesOrbitLaunchesOnly()
    {
        Assert.AreEqual(AscentProfile.GravityTurn, LiftGoalRules.ProfileFor(LiftGoal.Orbit, AscentProfile.GravityTurn));
        Assert.AreEqual(AscentProfile.Guided, LiftGoalRules.ProfileFor(LiftGoal.Orbit, AscentProfile.Guided));
        Assert.AreEqual(AscentProfile.Guided, LiftGoalRules.ProfileFor(LiftGoal.PrepareTransfer, AscentProfile.GravityTurn));
        Assert.AreEqual(AscentProfile.Guided, LiftGoalRules.ProfileFor(LiftGoal.Rendezvous, AscentProfile.GravityTurn));
    }

    // Guided keeps the keys it always had; Gravity Turn stores its own, so the leads never mix.
    [Test]
    public void AscentLeads_ArePerProfile()
    {
        Assert.AreEqual("lift.ascent_lead.Kerbin.", LiftSettings.AscentLeadPrefix("Kerbin", AscentProfile.Guided));
        Assert.AreEqual("lift.ascent_lead.Kerbin.gt.", LiftSettings.AscentLeadPrefix("Kerbin", AscentProfile.GravityTurn));

        var settings = new LiftSettings();
        var guided = settings.MeasuredLead("Kerbin");
        var gt = settings.MeasuredLead("Kerbin", AscentProfile.GravityTurn);
        Assert.AreEqual("lift.ascent_lead.Kerbin.s", guided.s.key);
        Assert.AreEqual("lift.ascent_lead.Kerbin.deg", guided.deg.key);
        Assert.AreEqual("lift.ascent_lead.Kerbin.gt.s", gt.s.key);
        Assert.AreEqual("lift.ascent_lead.Kerbin.gt.deg", gt.deg.key);
        Assert.AreSame(guided.s, settings.MeasuredLead("Kerbin", AscentProfile.Guided).s);
    }
}
