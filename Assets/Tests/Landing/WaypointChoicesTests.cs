using System.Collections.Generic;
using K2D2.Landing;
using NUnit.Framework;

public class WaypointChoicesTests
{
    static ReduxWaypoint Wp(string name, string body, double lat, double lon) =>
        new ReduxWaypoint { name = name, body_name = body, latitude = lat, longitude = lon };

    [Test]
    public void Build_PlayerWaypointsFirst_ThenMissionWaypoints()
    {
        var player = new List<ReduxWaypoint> { Wp("Base", "Mun", 1, 2) };
        var game = new List<ReduxWaypoint> { Wp("Mysterious Signal", "Mun", -12.5, 33.25) };

        var choices = WaypointChoices.Build(player, game, "Mun");

        Assert.AreEqual(2, choices.Count);
        Assert.AreEqual("Base (1.00, 2.00)", choices[0].Label);
        Assert.IsFalse(choices[0].Waypoint.from_mission);
        Assert.AreEqual("[Mission] Mysterious Signal (-12.50, 33.25)", choices[1].Label);
        Assert.IsTrue(choices[1].Waypoint.from_mission);
        Assert.AreEqual(-12.5, choices[1].Waypoint.latitude);
        Assert.AreEqual(33.25, choices[1].Waypoint.longitude);
    }

    [Test]
    public void Build_SkipsGameWaypointsThatArePlayerWaypoints()
    {
        // A player waypoint also exists as a waypoint of the simulation: listed once.
        var player = new List<ReduxWaypoint> { Wp("Base", "Mun", 1, 2) };
        var game = new List<ReduxWaypoint> { Wp("Base", "Mun", 1.000001, 2.000001), Wp("Other", "Mun", 1, 2) };

        var choices = WaypointChoices.Build(player, game, "Mun");

        // "Other" sits at the same spot under another name: a different waypoint, kept.
        Assert.AreEqual(2, choices.Count);
        Assert.AreEqual("Base (1.00, 2.00)", choices[0].Label);
        Assert.AreEqual("[Mission] Other (1.00, 2.00)", choices[1].Label);
    }

    [Test]
    public void Build_KeepsOnlyTheCurrentBody()
    {
        var player = new List<ReduxWaypoint> { Wp("Pad", "Kerbin", 0, 0) };
        var game = new List<ReduxWaypoint> { Wp("Signal", "Mun", 3, 4), Wp("Crash site", "Minmus", 5, 6) };

        var choices = WaypointChoices.Build(player, game, "Mun");

        Assert.AreEqual(1, choices.Count);
        Assert.AreEqual("[Mission] Signal (3.00, 4.00)", choices[0].Label);
    }

    [Test]
    public void Build_MakesLabelsUnique()
    {
        // The dropdown is keyed by label: two waypoints with the same name and rounded position
        // must not collapse into one entry.
        var game = new List<ReduxWaypoint> { Wp("Signal", "Mun", 3.001, 4), Wp("Signal", "Mun", 3.004, 4) };

        var choices = WaypointChoices.Build(new List<ReduxWaypoint>(), game, "Mun");

        Assert.AreEqual(2, choices.Count);
        Assert.AreNotEqual(choices[0].Label, choices[1].Label);
    }

    [Test]
    public void Build_UnnamedGameWaypointGetsAName()
    {
        var game = new List<ReduxWaypoint> { Wp(null, "Mun", 3, 4) };
        var choices = WaypointChoices.Build(new List<ReduxWaypoint>(), game, "Mun");
        Assert.AreEqual("[Mission] Waypoint (3.00, 4.00)", choices[0].Label);
    }

    [Test]
    public void Build_KscAndPickedPointComeFirst()
    {
        var ksc = Wp(WaypointChoices.KscName, "Kerbin", -0.1, -74.6);
        var picked = Wp(WaypointChoices.PickedName, "Kerbin", 1, 2);
        var player = new List<ReduxWaypoint> { Wp("Base", "Kerbin", 3, 4) };
        var game = new List<ReduxWaypoint> { Wp("Signal", "Kerbin", 5, 6) };

        var choices = WaypointChoices.Build(player, game, "Kerbin", ksc, picked);

        Assert.AreEqual(4, choices.Count);
        Assert.AreEqual("KSC (-0.10, -74.60)", choices[0].Label);
        Assert.AreEqual("Picked point (1.00, 2.00)", choices[1].Label);
        Assert.AreEqual("Base (3.00, 4.00)", choices[2].Label);
        Assert.AreEqual("[Mission] Signal (5.00, 6.00)", choices[3].Label);
        Assert.IsFalse(choices[0].Waypoint.from_mission);
        Assert.IsFalse(choices[1].Waypoint.from_mission);
    }

    [Test]
    public void Build_OmitsKscAndPickedPointOnAnotherBody()
    {
        var ksc = Wp(WaypointChoices.KscName, "Kerbin", -0.1, -74.6);
        var picked = Wp(WaypointChoices.PickedName, "Mun", 1, 2);

        var choices = WaypointChoices.Build(new List<ReduxWaypoint>(), new List<ReduxWaypoint>(), "Kerbin", ksc, picked);

        Assert.AreEqual(1, choices.Count);
        Assert.AreEqual("KSC (-0.10, -74.60)", choices[0].Label);
    }

    [Test]
    public void Build_NullKscAndPicked_EqualsThreeArgumentOverload()
    {
        var player = new List<ReduxWaypoint> { Wp("Base", "Mun", 1, 2) };
        var game = new List<ReduxWaypoint> { Wp("Mysterious Signal", "Mun", -12.5, 33.25) };

        var a = WaypointChoices.Build(player, game, "Mun");
        var b = WaypointChoices.Build(player, game, "Mun", null, null);

        Assert.AreEqual(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.AreEqual(a[i].Label, b[i].Label);
            Assert.AreEqual(a[i].Waypoint.from_mission, b[i].Waypoint.from_mission);
        }
    }
    static List<WaypointChoice> Choices(params ReduxWaypoint[] waypoints)
    {
        var list = new List<WaypointChoice>();
        foreach (var w in waypoints)
            list.Add(new WaypointChoice { Label = w.name, Waypoint = w });
        return list;
    }

    [Test]
    public void IndexOfSaved_MatchesWithinTolerance()
    {
        var choices = Choices(Wp("KSC", "Kerbin", -0.0972, -74.5577), Wp("Base", "Kerbin", 10, 20));

        Assert.AreEqual(1, WaypointChoices.IndexOfSaved(choices, 10.004, 19.996));
        Assert.AreEqual(0, WaypointChoices.IndexOfSaved(choices, -0.0972, -74.5577));
    }

    [Test]
    public void IndexOfSaved_NoMatch_ReturnsMinusOne()
    {
        var choices = Choices(Wp("Base", "Kerbin", 10, 20));

        Assert.AreEqual(-1, WaypointChoices.IndexOfSaved(choices, 10.006, 20));
        Assert.AreEqual(-1, WaypointChoices.IndexOfSaved(choices, 10, 20.006));
        Assert.AreEqual(-1, WaypointChoices.IndexOfSaved(new List<WaypointChoice>(), 10, 20));
    }

    [Test]
    public void IndexOfSaved_LongitudeComparedModulo360()
    {
        var choices = Choices(Wp("Edge", "Mun", 5, 179.999));

        Assert.AreEqual(0, WaypointChoices.IndexOfSaved(choices, 5, -180));
        Assert.AreEqual(0, WaypointChoices.IndexOfSaved(choices, 5, 539.999));
    }

    [Test]
    public void Custom_LabelUsesSavedValuesWithInvariantFormat()
    {
        var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var custom = WaypointChoices.Custom("Mun", -12.346, 67.891);

            Assert.AreEqual("Custom (-12.35, 67.89)", custom.Label);
            Assert.AreEqual(WaypointChoices.CustomName, custom.Waypoint.name);
            Assert.AreEqual("Mun", custom.Waypoint.body_name);
            Assert.AreEqual(-12.346, custom.Waypoint.latitude);
            Assert.AreEqual(67.891, custom.Waypoint.longitude);
            Assert.IsFalse(custom.Waypoint.from_mission);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
