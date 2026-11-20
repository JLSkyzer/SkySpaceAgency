using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.Bridge;

// Plan 2026-11-20-stage-recovery-bridge, task 2: change detection of Redux's stage recovery.
public class BridgeWatchTests
{
    static BridgeRecord Record(string id, BridgeStatus status = BridgeStatus.Ready) => new BridgeRecord
    {
        Id = id, VesselGuid = "guid-" + id, ParentVesselGuid = "parent-1", VesselName = "Booster " + id,
        CapturedUT = 1234.5, CapturedMET = 66.25, Status = status,
    };

    static BridgeState State(BridgePhase phase = BridgePhase.Idle, bool pending = false, params BridgeRecord[] records) => new BridgeState
    {
        Available = true, Phase = phase, IsCapturePending = pending, Records = new List<BridgeRecord>(records),
    };

    static BridgeWatch Started(BridgeState first)
    {
        var w = new BridgeWatch();
        w.Observe(first);
        return w;
    }

    [Test]
    public void FirstObservation_ReportsOnce()
    {
        var w = new BridgeWatch();
        var first = w.Observe(State());
        Assert.AreEqual(1, first.Count);
        StringAssert.StartsWith("first read", first[0]);
        Assert.IsEmpty(w.Observe(State()));
    }

    [Test]
    public void FirstObservation_WhenUnavailable_SaysSo()
    {
        var first = new BridgeWatch().Observe(BridgeState.Unavailable);
        StringAssert.Contains("unavailable", first.Single());
    }

    [Test]
    public void NoChange_IsSilent()
    {
        var w = Started(State(BridgePhase.Idle, false, Record("a")));
        Assert.IsEmpty(w.Observe(State(BridgePhase.Idle, false, Record("a"))));
        Assert.IsEmpty(w.Observe(State(BridgePhase.Idle, false, Record("a"))), "a second silent read");
    }

    [Test]
    public void PhaseChange_IsReported()
    {
        var w = Started(State());
        var lines = w.Observe(State(BridgePhase.SavingMission));
        StringAssert.Contains("phase Idle -> SavingMission", lines.Single());
    }

    [Test]
    public void CapturePendingToggle_IsReported()
    {
        var w = Started(State());
        StringAssert.Contains("capture pending on", w.Observe(State(BridgePhase.Idle, true)).Single());
        StringAssert.Contains("capture pending off", w.Observe(State(BridgePhase.Idle, false)).Single());
    }

    [Test]
    public void NewRecord_ReportsRecoveryPointSaved()
    {
        var w = Started(State(BridgePhase.Idle, true));
        var lines = w.Observe(State(BridgePhase.Idle, false, Record("r1")));
        var saved = lines.Single(l => l.StartsWith("recovery point saved"));
        foreach (var part in new[] { "r1", "Booster r1", "guid-r1", "parent-1", "1234.5", "66.25" })
            StringAssert.Contains(part, saved);
        Assert.IsTrue(lines.Any(l => l.Contains("capture pending off")));
    }

    [Test]
    public void NewRecord_WithoutACaptureSeen_IsReportedAsAppeared_NotSaved()
    {
        var w = Started(State());
        var line = w.Observe(State(BridgePhase.Idle, false, Record("r1"))).Single();
        StringAssert.StartsWith("recovery point appeared", line);
        StringAssert.DoesNotContain("saved", line);
    }

    [Test]
    public void RecordsOfAReturningRedux_AreABaseline_NotNewPoints()
    {
        var w = Started(BridgeState.Unavailable);
        var lines = w.Observe(State(BridgePhase.Idle, false, Record("r1"), Record("r2")));
        Assert.IsFalse(lines.Any(l => l.StartsWith("recovery point")), string.Join(" | ", lines));
        Assert.IsEmpty(w.Observe(State(BridgePhase.Idle, false, Record("r1"), Record("r2"))));
        StringAssert.Contains("removed: r2", w.Observe(State(BridgePhase.Idle, false, Record("r1"))).Single());
    }

    [Test]
    public void TransitioningToggle_IsReported()
    {
        var w = Started(State());
        var next = State();
        next.IsTransitioning = true;
        StringAssert.Contains("transitioning on", w.Observe(next).Single());
        StringAssert.Contains("transitioning off", w.Observe(State()).Single());
    }

    [Test]
    public void RecordStatusChange_IsReported()
    {
        var w = Started(State(BridgePhase.Idle, false, Record("r1")));
        StringAssert.Contains("Ready -> Landed", w.Observe(State(BridgePhase.Idle, false, Record("r1", BridgeStatus.Landed))).Single());
    }

    [Test]
    public void RecordRemoved_IsReported()
    {
        var w = Started(State(BridgePhase.Idle, false, Record("r1")));
        StringAssert.Contains("recovery point removed: r1", w.Observe(State()).Single());
    }

    [Test]
    public void ReduxUnavailableThenAvailable_IsReported()
    {
        var w = Started(BridgeState.Unavailable);
        StringAssert.Contains("became available", w.Observe(State()).First());
        StringAssert.Contains("became unavailable", w.Observe(BridgeState.Unavailable).First());
    }

    [Test]
    public void ActiveRecordChange_IsReported()
    {
        var w = Started(State(BridgePhase.LoadingStage, false, Record("r1")));
        var next = State(BridgePhase.FlyingStage, false, Record("r1"));
        next.ActiveRecordId = "r1";
        var lines = w.Observe(next);
        Assert.IsTrue(lines.Any(l => l.Contains("active record - -> r1")));
        Assert.IsTrue(lines.Any(l => l.Contains("phase LoadingStage -> FlyingStage")));
    }

    [Test]
    public void AReadAfterAReset_DoesNotReportTheSameRecordTwice()
    {
        var w = Started(State(BridgePhase.Idle, false, Record("r1")));
        w.Observe(State(BridgePhase.LoadingStage, false, Record("r1")));
        Assert.IsEmpty(w.Observe(State(BridgePhase.LoadingStage, false, Record("r1"))));
    }

    [Test]
    public void ANullState_IsReadAsUnavailable()
    {
        var w = Started(State());
        StringAssert.Contains("became unavailable", w.Observe(null).First());
    }
}
