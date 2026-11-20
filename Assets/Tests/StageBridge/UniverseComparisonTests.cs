using System;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.Bridge;

// Plan 2026-11-20-stage-recovery-bridge, task 2: the before / after identity comparison.
public class UniverseComparisonTests
{
    static UniverseIdentity Identity() => new UniverseIdentity
    {
        Plugin = 11, PilotsManager = 12, Game = 13, Universe = 14, Simulation = 15, Messages = 16,
        PilotCount = 6, ActivePartCount = 22, VesselCount = 3, PluginCount = 1,
        ActiveVesselGuid = "aaaa", ActiveVesselName = "Rocket", GameState = "FlightView", Phase = "Idle",
        UniverseTime = 5000, HasWindow = true, RunningPilots = new[] { "Lift" },
    };

    static string Joined(UniverseIdentity before, UniverseIdentity after) => string.Join("\n", UniverseComparison.Compare(before, after));

    [Test]
    public void AllSame_SaysSameEverywhere()
    {
        var lines = UniverseComparison.Compare(Identity(), Identity());
        Assert.IsFalse(lines.Any(l => l.Contains("NEW") || l.Contains("changed")), string.Join("\n", lines));
        foreach (var what in new[] { "plugin", "pilots manager", "game instance", "universe model", "space simulation", "message center", "window" })
            CollectionAssert.Contains(lines, what + " same");
        StringAssert.Contains("active vessel guid same", string.Join("\n", lines));
    }

    [Test]
    public void NewUniverseModel_IsFlaggedNew()
    {
        var after = Identity();
        after.Universe = 99;
        string all = Joined(Identity(), after);
        StringAssert.Contains("universe model NEW (14 -> 99)", all);
        StringAssert.Contains("plugin same", all);
    }

    [Test]
    public void MissingObjects_AreReported()
    {
        var after = Identity();
        after.Plugin = 0;
        StringAssert.Contains("plugin NEW (11 -> 0)", Joined(Identity(), after));
        var before = Identity();
        before.Simulation = 0;
        after = Identity();
        after.Simulation = 0;
        StringAssert.Contains("space simulation missing before and after", Joined(before, after));
    }

    [Test]
    public void ActiveVesselGuid_SameAndChanged()
    {
        var after = Identity();
        after.ActiveVesselGuid = "bbbb";
        StringAssert.Contains("active vessel guid changed aaaa -> bbbb", Joined(Identity(), after));
        after.ActiveVesselGuid = null;
        StringAssert.Contains("changed aaaa -> -", Joined(Identity(), after));
    }

    [Test]
    public void CountsAndRunningPilots_ArePrinted()
    {
        var after = Identity();
        after.VesselCount = 2;
        after.RunningPilots = new string[0];
        string all = Joined(Identity(), after);
        StringAssert.Contains("vessels 3 -> 2", all);
        StringAssert.Contains("running [Lift] -> []", all);
        StringAssert.Contains("plugin objects in the scene 1 -> 1", all);
    }

    [Test]
    public void SameHashButAnotherObject_IsNotReportedSame()
    {
        object a = new object(), b = new object();
        var before = Identity();
        var after = Identity();
        before.UniverseRef = new WeakReference(a);
        after.UniverseRef = new WeakReference(b);
        StringAssert.Contains("universe model NEW (same hash 14, but not the same object: the old one is still alive)", Joined(before, after));

        after.UniverseRef = new WeakReference(a);
        StringAssert.Contains("universe model same", Joined(before, after), "the very same object");
        GC.KeepAlive(a);
        GC.KeepAlive(b);
    }

    [Test]
    public void SameHash_WhenTheOldObjectIsGone_IsNotReportedSame()
    {
        var before = Identity();
        before.UniverseRef = new WeakReference(null);   // a reference whose target is gone
        var after = Identity();
        after.UniverseRef = new WeakReference(new object());
        StringAssert.Contains("the old one is gone", Joined(before, after));
    }

    [Test]
    public void MissingBefore_SaysSo()
    {
        StringAssert.Contains("no before snapshot", UniverseComparison.Compare(null, Identity()).Single());
    }

    [Test]
    public void Format_ListsEveryField()
    {
        string s = UniverseComparison.Format(Identity());
        foreach (var part in new[] { "plugin#11", "game#13", "universe#14", "Rocket", "aaaa", "FlightView", "Lift", "UT 5000", "parts 22", "vessels 3" })
            StringAssert.Contains(part, s);
        StringAssert.Contains("no identity", UniverseComparison.Format(null));
    }

    [Test]
    public void RunningPilots_IsNeverNull()
    {
        var id = new UniverseIdentity { RunningPilots = null };
        Assert.IsNotNull(id.RunningPilots);
    }
}
