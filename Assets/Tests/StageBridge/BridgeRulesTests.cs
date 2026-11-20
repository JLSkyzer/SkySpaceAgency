using System;
using System.Collections.Generic;
using NUnit.Framework;
using SkySpaceAgency.Bridge;

// Plan 2026-11-20-stage-recovery-bridge, task 1: the pure states, rules and guarded calls.
public class BridgeRulesTests
{
    const string Guid1 = "11111111-1111-1111-1111-111111111111";
    const string Guid2 = "22222222-2222-2222-2222-222222222222";

    static BridgeRecord Record(string id, string guid = Guid1, BridgeStatus status = BridgeStatus.Ready)
        => new BridgeRecord { Id = id, VesselGuid = guid, VesselName = "Booster " + id, Status = status };

    static BridgeState Good(params BridgeRecord[] records) => new BridgeState
    {
        Available = true,
        Phase = BridgePhase.Idle,
        Records = new List<BridgeRecord>(records),
    };

    static BridgeState Flying(string activeId = "a", bool docked = false) => new BridgeState
    {
        Available = true,
        Phase = BridgePhase.FlyingStage,
        ActiveRecordId = activeId,
        ActiveReplayDocked = docked,
        CanRetry = true,
        Records = new List<BridgeRecord> { Record("a", status: BridgeStatus.Replaying) },
    };

    [Test]
    public void ParsePhase_KnownNames_AndUnknown()
    {
        Assert.AreEqual(BridgePhase.FlyingStage, BridgeNames.ParsePhase("FlyingStage"));
        Assert.AreEqual(BridgePhase.Idle, BridgeNames.ParsePhase("Idle"));
        Assert.AreEqual(BridgePhase.FlyingStage, BridgeNames.ParsePhase("flyingstage"));
        Assert.AreEqual(BridgePhase.LoadingMission, BridgeNames.ParsePhase("LoadingMission"));
        Assert.AreEqual(BridgePhase.Unknown, BridgeNames.ParsePhase(null));
        Assert.AreEqual(BridgePhase.Unknown, BridgeNames.ParsePhase("x"));
        Assert.AreEqual(BridgePhase.Unknown, BridgeNames.ParsePhase("7"), "a number is not a phase name");
    }

    [Test]
    public void ParseStatus_KnownNames_AndUnknown()
    {
        foreach (var name in new[] { "Ready", "Replaying", "Landed", "Splashed", "Recovered", "Lost", "Failed" })
            Assert.AreEqual(Enum.Parse(typeof(BridgeStatus), name), BridgeNames.ParseStatus(name), name);
        Assert.AreEqual(BridgeStatus.Unknown, BridgeNames.ParseStatus("Frozen"));
        Assert.AreEqual(BridgeStatus.Unknown, BridgeNames.ParseStatus(null));
    }

    [Test]
    public void HasSaved_RequiresReadyRecordAndNoPendingCapture()
    {
        var state = Good(Record("a"));
        Assert.IsTrue(BridgeRules.HasSaved(state, Guid1));
        state.IsCapturePending = true;
        Assert.IsFalse(BridgeRules.HasSaved(state, Guid1), "capture pending");
        Assert.IsFalse(BridgeRules.HasSaved(Good(Record("a", status: BridgeStatus.Landed)), Guid1), "not Ready");
        Assert.IsFalse(BridgeRules.HasSaved(Good(Record("a")), Guid2), "other vessel");
        Assert.IsFalse(BridgeRules.HasSaved(BridgeState.Unavailable, Guid1));
        Assert.IsFalse(BridgeRules.HasSaved(null, Guid1));
    }

    [Test]
    public void FindReady_ReturnsTheNewestReadyRecordOfTheVessel()
    {
        var state = Good(Record("old"), Record("done", status: BridgeStatus.Lost), Record("new"));
        Assert.AreEqual("new", BridgeRules.FindReady(state, Guid1).Id);
        Assert.IsNull(BridgeRules.FindReady(state, null));
    }

    [Test]
    public void CanStartReplay_VerdictOrder()
    {
        Assert.AreEqual(BridgeVerdict.ReduxUnavailable, BridgeRules.CanStartReplay(BridgeState.Unavailable, "a", false), "unavailable wins over disabled");
        Assert.AreEqual(BridgeVerdict.ReduxUnavailable, BridgeRules.CanStartReplay(null, "a", true));
        Assert.AreEqual(BridgeVerdict.Disabled, BridgeRules.CanStartReplay(Good(Record("a")), "a", false));

        var busy = Good(Record("a"));
        busy.Phase = BridgePhase.FlyingStage;
        busy.IsCapturePending = true;
        busy.IsTransitioning = true;
        Assert.AreEqual(BridgeVerdict.SessionBusy, BridgeRules.CanStartReplay(busy, "a", true), "phase before pending before transitioning");
        busy.Phase = BridgePhase.Idle;
        Assert.AreEqual(BridgeVerdict.CapturePending, BridgeRules.CanStartReplay(busy, "a", true));
        busy.IsCapturePending = false;
        Assert.AreEqual(BridgeVerdict.Transitioning, BridgeRules.CanStartReplay(busy, "a", true));
        busy.IsTransitioning = false;

        Assert.AreEqual(BridgeVerdict.NoSuchRecord, BridgeRules.CanStartReplay(busy, "zzz", true));
        Assert.AreEqual(BridgeVerdict.NoSuchRecord, BridgeRules.CanStartReplay(busy, null, true));
        Assert.AreEqual(BridgeVerdict.RecordNotReady, BridgeRules.CanStartReplay(Good(Record("a", status: BridgeStatus.Landed)), "a", true));
        Assert.AreEqual(BridgeVerdict.Ok, BridgeRules.CanStartReplay(Good(Record("a")), "a", true));
    }

    [Test]
    public void CanCompleteLanding_And_CanReturn_Verdicts()
    {
        Assert.AreEqual(BridgeVerdict.ReduxUnavailable, BridgeRules.CanCompleteLanding(BridgeState.Unavailable, true));
        Assert.AreEqual(BridgeVerdict.Disabled, BridgeRules.CanCompleteLanding(Flying(), false));
        Assert.AreEqual(BridgeVerdict.Disabled, BridgeRules.CanReturnToMission(Flying(), false));
        Assert.AreEqual(BridgeVerdict.NotFlyingStage, BridgeRules.CanCompleteLanding(Good(Record("a")), true));
        Assert.AreEqual(BridgeVerdict.NotFlyingStage, BridgeRules.CanReturnToMission(Good(Record("a")), true));

        var noActive = Flying(null);
        Assert.AreEqual(BridgeVerdict.NoActiveRecord, BridgeRules.CanCompleteLanding(noActive, true));
        Assert.AreEqual(BridgeVerdict.NoActiveRecord, BridgeRules.CanReturnToMission(noActive, true));
        Assert.AreEqual(BridgeVerdict.NoActiveRecord, BridgeRules.CanReturnToMission(Flying("ghost"), true), "an active id with no record");

        Assert.AreEqual(BridgeVerdict.Docked, BridgeRules.CanCompleteLanding(Flying(docked: true), true));
        Assert.AreEqual(BridgeVerdict.Ok, BridgeRules.CanReturnToMission(Flying(docked: true), true), "returning is allowed when docked");

        var loading = Flying();
        loading.IsTransitioning = true;
        Assert.AreEqual(BridgeVerdict.Transitioning, BridgeRules.CanCompleteLanding(loading, true));
        Assert.AreEqual(BridgeVerdict.Transitioning, BridgeRules.CanReturnToMission(loading, true));

        Assert.AreEqual(BridgeVerdict.Ok, BridgeRules.CanCompleteLanding(Flying(), true));
        Assert.AreEqual(BridgeVerdict.Ok, BridgeRules.CanReturnToMission(Flying(), true));

        var noRetry = Flying();
        noRetry.CanRetry = false;
        Assert.AreEqual(BridgeVerdict.WouldLose, BridgeRules.CanReturnToMission(noRetry, true), "quickload off: returning loses the stage");
        Assert.AreEqual(BridgeVerdict.Ok, BridgeRules.CanCompleteLanding(noRetry, true), "finishing a landing does not");
        Assert.AreEqual(BridgeVerdict.Disabled, BridgeRules.CanReturnToMission(noRetry, false), "the guard still comes first");
    }

    [Test]
    public void BridgeCalls_NeverInvokeWhenVerdictIsNotOk()
    {
        int calls = 0;
        Func<string, bool> start = id => { calls++; return true; };
        Func<bool> other = () => { calls++; return true; };

        Assert.IsFalse(BridgeCalls.TryStart(Good(Record("a")), "a", false, start).Invoked);
        Assert.IsFalse(BridgeCalls.TryStart(Flying(), "a", true, start).Invoked);
        Assert.IsFalse(BridgeCalls.TryComplete(Flying(null), true, other).Invoked);
        Assert.IsFalse(BridgeCalls.TryReturn(Good(Record("a")), true, other).Invoked);
        Assert.IsFalse(BridgeCalls.TryStart(Good(Record("a")), "a", true, null).Invoked, "no delegate, nothing to call");
        Assert.AreEqual(0, calls);

        var r = BridgeCalls.TryStart(Good(Record("a")), "a", true, start);
        Assert.IsTrue(r.Invoked);
        Assert.IsTrue(r.Result);
        Assert.AreEqual(BridgeVerdict.Ok, r.Verdict);

        var f = BridgeCalls.TryReturn(Flying(), true, () => { calls++; return false; });
        Assert.IsTrue(f.Invoked);
        Assert.IsFalse(f.Result, "Redux's own answer is passed through");
        Assert.AreEqual(2, calls);

        string seen = null;
        BridgeCalls.TryStart(Good(Record("rec1")), "rec1", true, id => { seen = id; return true; });
        Assert.AreEqual("rec1", seen, "the record id reaches Redux's method");
    }

    [Test]
    public void Explain_HasAnEnglishSentenceForEveryVerdict()
    {
        foreach (BridgeVerdict v in Enum.GetValues(typeof(BridgeVerdict)))
        {
            string s = BridgeRules.Explain(v);
            Assert.IsFalse(string.IsNullOrWhiteSpace(s), v.ToString());
            if (v != BridgeVerdict.Ok) StringAssert.DoesNotContain(v.ToString(), s, "a sentence, not the enum name");
        }
    }

    [Test]
    public void RecordsAreNeverNull()
    {
        var state = new BridgeState { Records = null };
        Assert.IsNotNull(state.Records);
        Assert.IsNull(BridgeRules.Find(state, "a"));
    }

    [Test]
    public void Setting_DefaultIsFalse_AndKeyIsStageBridgeEnabled()
    {
        Assert.AreEqual("stage_bridge_enabled", StageBridgeSettings.Enabled.key);
        // The default itself, not the value a settings file may hold: set it, reset it, read it, put it back.
        bool saved = StageBridgeSettings.Enabled.V;
        try
        {
            StageBridgeSettings.Enabled.V = true;
            StageBridgeSettings.Enabled.Reset();
            Assert.IsFalse(StageBridgeSettings.Enabled.V);
        }
        finally
        {
            StageBridgeSettings.Enabled.V = saved;
        }
    }
}
