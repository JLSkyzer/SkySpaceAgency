using System.Collections.Generic;
using K2D2.Landing;
using NUnit.Framework;

public class WaypointFollowTests
{
    static ReduxWaypoint W(string id, double lat, double lon) =>
        new ReduxWaypoint { id = id, name = "Site", body_name = "Mun", latitude = lat, longitude = lon };

    static readonly List<ReduxWaypoint> Moved = new List<ReduxWaypoint> { W("a", 1, 2), W("b", 10.5, 20.25) };

    [Test]
    public void MovedWaypoint_TargetUntouched_Follows()
    {
        var r = WaypointChoices.Follow("b", 10, 20, (float)10.0, (float)20.0, Moved);
        Assert.AreEqual(WaypointChoices.FollowAction.Follow, r.Action);
        Assert.AreEqual(10.5, r.Waypoint.latitude, 1e-12);
        Assert.AreEqual(20.25, r.Waypoint.longitude, 1e-12);
    }

    [Test]
    public void TargetTypedOver_ForgetsTheWaypoint()
    {
        var r = WaypointChoices.Follow("b", 10, 20, 11f, 20f, Moved);
        Assert.AreEqual(WaypointChoices.FollowAction.Forget, r.Action);
    }

    [Test]
    public void NotMoved_DoesNothing()
    {
        var r = WaypointChoices.Follow("b", 10.5, 20.25, (float)10.5, (float)20.25, Moved);
        Assert.AreEqual(WaypointChoices.FollowAction.None, r.Action);
    }

    [Test]
    public void NoSelection_OrNoId_DoesNothing()
    {
        Assert.AreEqual(WaypointChoices.FollowAction.None, WaypointChoices.Follow(null, 10, 20, 10f, 20f, Moved).Action);
        Assert.AreEqual(WaypointChoices.FollowAction.None, WaypointChoices.Follow("", 10, 20, 10f, 20f, Moved).Action);
    }

    [Test]
    public void WaypointGone_DoesNothing()
    {
        var r = WaypointChoices.Follow("c", 10, 20, 10f, 20f, Moved);
        Assert.AreEqual(WaypointChoices.FollowAction.None, r.Action);
    }

    [Test]
    public void WaypointsWithoutId_NeverMatch()
    {
        var list = new List<ReduxWaypoint> { W(null, 10.5, 20.25) };
        Assert.AreEqual(WaypointChoices.FollowAction.None, WaypointChoices.Follow("b", 10, 20, 10f, 20f, list).Action);
    }

    [Test]
    public void TargetStoredAsFloat_StillCountsAsUntouched()
    {
        // The settings keep floats: a target written from 10.123456789 reads back rounded.
        var list = new List<ReduxWaypoint> { W("b", 12, 22) };
        var r = WaypointChoices.Follow("b", 10.123456789, -20.987654321, (float)10.123456789, (float)-20.987654321, list);
        Assert.AreEqual(WaypointChoices.FollowAction.Follow, r.Action);
    }
}
