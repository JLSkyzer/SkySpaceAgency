using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.Bridge;

// Plan 2026-11-20-stage-recovery-bridge, task 4: the glue, with no game (Redux's manager does not exist in the
// editor, so the bridge reads "unavailable"). The lines go to StageBridge.Sink.
public class StageBridgeGlueTests
{
    List<string> _lines;
    Action<string> _sink;
    Func<BridgeState> _reader;
    Func<UniverseIdentity> _capturer;
    bool _enabled;
    BridgeState _state;

    static BridgeState S(BridgePhase phase, bool available = true, bool pending = false, params BridgeRecord[] records)
        => new BridgeState { Available = available, Phase = phase, IsCapturePending = pending, CanRetry = true, Records = records };

    [SetUp]
    public void SetUp()
    {
        _lines = new List<string>();
        _sink = StageBridge.Sink;
        _reader = StageBridge.Reader;
        _capturer = StageBridge.Capturer;
        StageBridge.Sink = _lines.Add;
        StageBridge.ResetState();
        _enabled = StageBridgeSettings.Enabled.V;
    }

    [TearDown]
    public void TearDown()
    {
        StageBridge.Sink = _sink;
        StageBridge.Reader = _reader;
        StageBridge.Capturer = _capturer;
        StageBridgeSettings.Enabled.V = _enabled;
        StageBridge.ResetState();
    }

    [Test]
    public void LogState_WithoutRedux_WritesTheStateLineSayingUnavailable()
    {
        StageBridge.LogState("test");
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] state (test): Redux's stage recovery is unavailable")), string.Join(" | ", _lines));
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] identity (test)")));
    }

    [Test]
    public void Tick_WithoutRedux_IsSilentAfterTheFirstLine()
    {
        StageBridge.Tick(1f);
        int first = _lines.Count;
        StringAssert.Contains("unavailable", string.Join(" ", _lines));
        StageBridge.Tick(1.1f);
        StageBridge.Tick(2f);
        StageBridge.Tick(3f);
        Assert.AreEqual(first, _lines.Count, "no change, no line: " + string.Join(" | ", _lines));
    }

    [Test]
    public void OnCampaignLoaded_WithoutBefore_LogsNoBeforeSnapshot()
    {
        StageBridge.OnCampaignLoaded();
        Assert.IsTrue(_lines.Any(l => l.Contains("survival (campaign loaded): no before snapshot")), string.Join(" | ", _lines));
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] after (campaign loaded)")));
    }

    [Test]
    public void OnCampaignUnloading_ThenLoaded_LogsBeforeThenTheComparison()
    {
        StageBridge.OnCampaignUnloading();
        StageBridge.OnCampaignLoaded();
        int before = _lines.FindIndex(l => l.StartsWith("[StageBridge] before reset #1 (campaign unloading)"));
        int after = _lines.FindIndex(l => l.StartsWith("[StageBridge] after (campaign loaded)"));
        Assert.GreaterOrEqual(before, 0);
        Assert.Greater(after, before);
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] survival (campaign loaded vs before #1 (campaign unloading)): plugin missing before and after")), string.Join(" | ", _lines));
        Assert.IsFalse(_lines.Any(l => l.Contains("no before snapshot")));
    }

    [Test]
    public void TheCalls_WhenTheSettingIsOff_AreRefusedAndReturnFalse()
    {
        StageBridgeSettings.Enabled.V = false;
        Assert.IsFalse(StageBridge.StartReplay("x"));
        Assert.IsFalse(StageBridge.CompleteActiveLanding());
        Assert.IsFalse(StageBridge.ReturnToMission());
        Assert.AreEqual(3, _lines.Count(l => l.Contains("refused")));
        // Redux is unavailable here, and that wins over the setting (the verdict order).
        StringAssert.Contains("refused (ReduxUnavailable)", _lines[0]);
        Assert.IsFalse(_lines.Any(l => l.Contains("called ")));
    }

    [Test]
    public void TheCalls_WhenEnabledButReduxIsUnavailable_AreRefused()
    {
        StageBridgeSettings.Enabled.V = true;
        Assert.IsFalse(StageBridge.StartReplay("x"));
        StringAssert.Contains("refused (ReduxUnavailable)", _lines.Single());
    }

    [Test]
    public void EveryLineStartsWithThePrefix()
    {
        StageBridge.Tick(1f);
        StageBridge.LogState("t");
        StageBridge.OnCampaignUnloading();
        StageBridge.OnCampaignLoaded();
        StageBridge.StartReplay("x");
        Assert.Greater(_lines.Count, 5);
        foreach (var l in _lines) StringAssert.StartsWith("[StageBridge] ", l);
    }

    [Test]
    public void Capture_WithoutAGame_DoesNotThrow_AndCountsNothing()
    {
        UniverseIdentity id = null;
        Assert.DoesNotThrow(() => id = UniverseIdentityReader.Capture());
        Assert.AreEqual(0, id.Game);
        Assert.AreEqual(0, id.Universe);
        Assert.IsNull(id.ActiveVesselGuid);
        Assert.AreEqual(0, id.VesselCount);
    }

    [Test]
    public void Id_IsZeroForNull_AndStableForAnObject()
    {
        Assert.AreEqual(0, UniverseIdentityReader.Id(null));
        var o = new object();
        Assert.AreEqual(UniverseIdentityReader.Id(o), UniverseIdentityReader.Id(o));
        Assert.AreNotEqual(0, UniverseIdentityReader.Id(o));
        var go = new UnityEngine.GameObject("x");
        UnityEngine.Object.DestroyImmediate(go);
        Assert.AreEqual(0, UniverseIdentityReader.Id(go), "a destroyed Unity object counts as missing");
    }

    [Test]
    public void BridgeFormat_StateAndRecords()
    {
        var state = new BridgeState
        {
            Available = true, Phase = BridgePhase.Idle, CreatePoints = true, SaveDelaySeconds = 2f, ActiveRecordId = "r1",
            Records = new List<BridgeRecord>
            {
                new BridgeRecord { Id = "r1", VesselName = "B", VesselGuid = "g", Status = BridgeStatus.Ready },
                new BridgeRecord { Id = "r2", VesselName = "C", VesselGuid = "h", Status = BridgeStatus.Landed },
                new BridgeRecord { Id = "r3", VesselName = "D", VesselGuid = "i", Status = BridgeStatus.Ready },
            },
        };
        string s = BridgeFormat.State(state);
        StringAssert.Contains("phase Idle", s);
        StringAssert.Contains("save delay 2 s", s);
        StringAssert.Contains("3 record(s) [Landed=1, Ready=2]", s);
        StringAssert.Contains("active record r1", s);
        Assert.AreEqual(3, BridgeFormat.Records(state).Count());
        StringAssert.Contains("unavailable", BridgeFormat.State(BridgeState.Unavailable));
        StringAssert.Contains("save delay unreadable", BridgeFormat.State(new BridgeState { Available = true }));
        Assert.IsEmpty(BridgeFormat.Records(null));
    }

    // ---- with Redux's state and the identity injected ----

    void UseSequence(Func<BridgeState> reader)
    {
        StageBridge.Reader = reader;
        StageBridge.Capturer = () => new UniverseIdentity();
    }

    [Test]
    public void Tick_BackToIdle_FiresOnce_AfterAWholeSession()
    {
        UseSequence(() => _state);
        float t = 1;
        foreach (var phase in new[] { BridgePhase.Idle, BridgePhase.SavingMission, BridgePhase.LoadingStage, BridgePhase.FlyingStage, BridgePhase.LoadingMission })
        {
            _state = S(phase);
            StageBridge.Tick(t++);
        }
        Assert.AreEqual(0, _lines.Count(l => l.Contains("after (phase back to Idle)")), "not before the phase is Idle again");
        _state = S(BridgePhase.Idle);
        StageBridge.Tick(t++);
        Assert.AreEqual(1, _lines.Count(l => l.StartsWith("[StageBridge] after (phase back to Idle)")));
        StageBridge.Tick(t++);
        StageBridge.Tick(t++);
        Assert.AreEqual(1, _lines.Count(l => l.StartsWith("[StageBridge] after (phase back to Idle)")), "once");
        Assert.IsTrue(_lines.Any(l => l.Contains("event: phase LoadingMission -> Idle")));
    }

    [Test]
    public void Tick_AnUnavailableOrUnknownReadInTheMiddle_DoesNotHideTheReturnToIdle()
    {
        UseSequence(() => _state);
        float t = 1;
        _state = S(BridgePhase.Idle); StageBridge.Tick(t++);
        _state = S(BridgePhase.FlyingStage); StageBridge.Tick(t++);
        _state = S(BridgePhase.Unknown, available: false); StageBridge.Tick(t++);
        _state = S(BridgePhase.Unknown); StageBridge.Tick(t++);
        _state = S(BridgePhase.Idle); StageBridge.Tick(t++);
        Assert.AreEqual(1, _lines.Count(l => l.StartsWith("[StageBridge] after (phase back to Idle)")), string.Join(" | ", _lines));
    }

    [Test]
    public void Tick_ACancelledStart_SavingMissionToIdle_SaysThereIsNothingToCompareWith()
    {
        UseSequence(() => _state);
        _state = S(BridgePhase.Idle); StageBridge.Tick(1);
        _state = S(BridgePhase.SavingMission); StageBridge.Tick(2);
        _state = S(BridgePhase.Idle); StageBridge.Tick(3);
        Assert.AreEqual(1, _lines.Count(l => l.StartsWith("[StageBridge] after (phase back to Idle)")));
        StringAssert.Contains("no before snapshot", string.Join(" | ", _lines), "no reset happened");
    }

    [Test]
    public void Survival_NamesItsBefore_AndTheBeforeIsClearedAfterTheSession()
    {
        UseSequence(() => _state);
        _state = S(BridgePhase.Idle); StageBridge.Tick(1);
        _state = S(BridgePhase.LoadingStage); StageBridge.Tick(2);
        StageBridge.OnCampaignUnloading();
        StageBridge.OnCampaignLoaded();
        _state = S(BridgePhase.Idle); StageBridge.Tick(3);
        Assert.IsTrue(_lines.Any(l => l.Contains("survival (campaign loaded vs before #1 (campaign unloading))")));
        Assert.IsTrue(_lines.Any(l => l.Contains("survival (phase back to Idle vs before #1 (campaign unloading))")));
        _lines.Clear();
        StageBridge.OnCampaignLoaded();
        Assert.IsTrue(_lines.Any(l => l.Contains("no before snapshot")), "the finished cycle's before is gone");
    }

    [Test]
    public void Tick_RecoveryPointSaved_LogsTheIdentityLine()
    {
        UseSequence(() => _state);
        _state = S(BridgePhase.Idle); StageBridge.Tick(1);
        _state = S(BridgePhase.Idle, true, true); StageBridge.Tick(2);
        _lines.Clear();
        _state = S(BridgePhase.Idle, true, false, new BridgeRecord { Id = "r1", VesselName = "B", VesselGuid = "g", Status = BridgeStatus.Ready });
        StageBridge.Tick(3);
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] event: recovery point saved")), string.Join(" | ", _lines));
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] identity:")));
    }

    [Test]
    public void Tick_AFailingReader_LogsTheErrorOnce()
    {
        StageBridge.Reader = () => throw new InvalidOperationException("boom");
        StageBridge.Tick(1); StageBridge.Tick(2); StageBridge.Tick(3);
        Assert.AreEqual(1, _lines.Count(l => l.Contains("poll failed (InvalidOperationException: boom)")), string.Join(" | ", _lines));
    }

    [Test]
    public void TheCalls_PassTheSettingToTheGuard()
    {
        StageBridge.Reader = () => new BridgeState
        {
            Available = true, Phase = BridgePhase.Idle, CanRetry = true,
            Records = new[] { new BridgeRecord { Id = "r1", VesselGuid = "g", Status = BridgeStatus.Ready } },
        };
        StageBridgeSettings.Enabled.V = false;
        Assert.IsFalse(StageBridge.StartReplay("r1"));
        StringAssert.Contains("refused (Disabled)", _lines.Single());
        _lines.Clear();

        StageBridgeSettings.Enabled.V = true;
        Assert.IsFalse(StageBridge.StartReplay("r1"), "Redux is not there in the editor: its answer is false");
        StringAssert.Contains("called StartReplay -> False", _lines.Single());
        _lines.Clear();
        Assert.IsFalse(StageBridge.StartReplay("zzz"));
        StringAssert.Contains("refused (NoSuchRecord)", _lines.Single());
    }

    [Test]
    public void OnGameLoadFinished_LogsTheSuccessFlag_AndTheSurvivalBlock()
    {
        StageBridge.Capturer = () => new UniverseIdentity();
        StageBridge.OnGameLoadFinished(new KSP.Messages.GameLoadFinishedMessage { IsSuccess = true });
        Assert.IsTrue(_lines.Any(l => l.Contains("event: game load finished, success True")));
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] after (game load finished)")));
        _lines.Clear();
        StageBridge.OnGameLoadFinished(new KSP.Messages.GameLoadFinishedMessage { IsSuccess = false });
        Assert.IsTrue(_lines.Any(l => l.Contains("success False")));
        _lines.Clear();
        StageBridge.OnQuitToMainMenu(null);
        Assert.IsTrue(_lines.Any(l => l.Contains("quit to main menu started")));
        Assert.IsTrue(_lines.Any(l => l.StartsWith("[StageBridge] before reset #1 (quit to main menu)")));
    }

    [Test]
    public void AFailingCapturer_NeverEscapes()
    {
        StageBridge.Capturer = () => throw new InvalidOperationException("no game");
        Assert.DoesNotThrow(() => StageBridge.OnCampaignUnloading());
        Assert.DoesNotThrow(() => StageBridge.OnCampaignLoaded());
        Assert.DoesNotThrow(() => StageBridge.LogState("x"));
        Assert.IsTrue(_lines.Any(l => l.Contains("failed (InvalidOperationException")));
    }
}
