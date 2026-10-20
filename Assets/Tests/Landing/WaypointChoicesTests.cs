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
}
