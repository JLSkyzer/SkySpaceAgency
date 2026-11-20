using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SkySpaceAgency.Bridge;

// Plan 2026-11-20-stage-recovery-bridge, task 3: the reflected names exist in the Ksp2Redux.dll the project is
// built with (the installed game's 26w47a, SHA-256 1f0e9d99...), so a rename in a later Redux shows up here.
public class StageRecoveryAccessTests
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    static Type Manager => Type.GetType(StageRecoveryAccess.ManagerTypeName);

    static string[] Names(Type enumType) => Enum.GetNames(enumType);

    [Test]
    public void ReflectionTargets_ResolveAgainstTheRealRedux()
    {
        var manager = Manager;
        Assert.IsNotNull(manager, "Redux.StageRecovery.StageRecoveryManager");

        foreach (var name in StageRecoveryAccess.ManagerProperties)
            Assert.IsNotNull(manager.GetProperty(name, Any), "property " + name);

        var instance = manager.GetProperty("Instance", Any);
        Assert.AreEqual(manager, instance.PropertyType);
        Assert.IsTrue(instance.GetGetMethod(true).IsStatic, "Instance is static");

        foreach (var name in new[] { "IsCapturePending", "IsTransitioning", "IsReplayActive", "CanRetryRecovery", "CreateRecoveryPoints", "IsActiveReplayDocked" })
            Assert.AreEqual(typeof(bool), manager.GetProperty(name, Any).PropertyType, name);

        var phase = manager.GetProperty("SessionPhase", Any).PropertyType;
        Assert.IsTrue(phase.IsEnum);
        foreach (var name in new[] { "Idle", "SavingMission", "LoadingStage", "FlyingStage", "LoadingMission" })
            CollectionAssert.Contains(Names(phase), name);

        var start = manager.GetMethod("StartReplay", Any, null, new[] { typeof(string) }, null);
        Assert.IsNotNull(start, "StartReplay(string)");
        Assert.AreEqual(typeof(bool), start.ReturnType);
        foreach (var name in new[] { "CompleteActiveLanding", "ReturnToMission" })
        {
            var m = manager.GetMethod(name, Any, null, Type.EmptyTypes, null);
            Assert.IsNotNull(m, name);
            Assert.AreEqual(typeof(bool), m.ReturnType, name);
        }
    }

    [Test]
    public void RecordFields_ResolveWithTheRightTypes()
    {
        var records = Manager.GetProperty("Records", Any).PropertyType;
        Assert.IsTrue(records.IsGenericType);
        Assert.AreEqual(typeof(IReadOnlyList<>), records.GetGenericTypeDefinition());
        var record = records.GetGenericArguments()[0];

        foreach (var name in StageRecoveryAccess.RecordFields)
            Assert.IsNotNull(record.GetField(name, Any), "field " + name);
        foreach (var name in new[] { "Id", "VesselGuid", "ParentVesselGuid", "VesselName", "LastError" })
            Assert.AreEqual(typeof(string), record.GetField(name, Any).FieldType, name);
        Assert.AreEqual(typeof(double), record.GetField("CapturedUniversalTime", Any).FieldType);
        Assert.AreEqual(typeof(double?), record.GetField("CapturedMissionElapsedTime", Any).FieldType);
        Assert.AreEqual(typeof(double?), record.GetField("CompletedUniversalTime", Any).FieldType);

        var status = record.GetField("Status", Any).FieldType;
        Assert.IsTrue(status.IsEnum);
        foreach (var name in new[] { "Ready", "Replaying", "Landed", "Splashed", "Recovered", "Lost", "Failed" })
            CollectionAssert.Contains(Names(status), name);
        Assert.IsTrue(record.GetField("Origin", Any).FieldType.IsEnum);
    }

    [Test]
    public void TheGameSaveDelaySetting_Exists()
    {
        var type = typeof(KSP.Game.GameManager).Assembly.GetType("KSP.Game.PersistentProfileManager");
        Assert.IsNotNull(type);
        var property = type.GetProperty("StageRecoverySaveDelaySeconds", Any);
        Assert.IsNotNull(property);
        Assert.AreEqual(typeof(float), property.PropertyType);
    }

    [Test]
    public void Resolve_SucceedsOnThisRedux_WithNoMissingName()
    {
        Assert.IsTrue(StageRecoveryAccess.Resolved, StageRecoveryAccess.LastProblem);
        Assert.AreEqual("", StageRecoveryAccess.MissingNames, "a reflected name does not exist in this Redux");
    }

    [Test]
    public void ReadRecord_ReadsEveryFieldOfARealRedux_Record()
    {
        Assert.IsTrue(StageRecoveryAccess.Resolved);
        var recordType = Manager.GetProperty("Records", Any).PropertyType.GetGenericArguments()[0];
        object record = Activator.CreateInstance(recordType, true);
        void Set(string name, object value) => recordType.GetField(name, Any).SetValue(record, value);
        Set("Id", "abc"); Set("VesselGuid", "g1"); Set("ParentVesselGuid", "p1"); Set("VesselName", "Booster");
        Set("CapturedUniversalTime", 1234.5); Set("LastError", "oops");
        Set("Status", Enum.Parse(recordType.GetField("Status", Any).FieldType, "Landed"));
        Set("Origin", Enum.Parse(recordType.GetField("Origin", Any).FieldType, "Undocked"));

        var r = StageRecoveryAccess.ReadRecord(record);
        Assert.AreEqual("abc", r.Id);
        Assert.AreEqual("g1", r.VesselGuid);
        Assert.AreEqual("p1", r.ParentVesselGuid);
        Assert.AreEqual("Booster", r.VesselName);
        Assert.AreEqual(1234.5, r.CapturedUT);
        Assert.IsNull(r.CapturedMET, "a nullable left null");
        Assert.IsNull(r.CompletedUT);
        Assert.AreEqual(BridgeStatus.Landed, r.Status);
        Assert.AreEqual("Undocked", r.Origin);
        Assert.AreEqual("oops", r.LastError);

        Set("CapturedMissionElapsedTime", 66.25);
        Set("CompletedUniversalTime", 2000.0);
        r = StageRecoveryAccess.ReadRecord(record);
        Assert.AreEqual(66.25, r.CapturedMET);
        Assert.AreEqual(2000.0, r.CompletedUT);
    }

    [Test]
    public void Read_WithoutAGame_IsUnavailableAndDoesNotThrow()
    {
        var state = StageRecoveryAccess.Read();
        Assert.IsFalse(state.Available);
        Assert.AreEqual(BridgePhase.Unknown, state.Phase);
        Assert.IsEmpty(state.Records);
    }

    [Test]
    public void TheCalls_WithoutAGame_ReturnFalseAndDoNotThrow()
    {
        Assert.IsFalse(StageRecoveryAccess.StartReplay("x"));
        Assert.IsFalse(StageRecoveryAccess.CompleteActiveLanding());
        Assert.IsFalse(StageRecoveryAccess.ReturnToMission());
    }

    [Test]
    public void ReduxStageRecovery_Get_IsUnavailableWithoutAGame()
    {
        var replay = SkySpaceAgency.Landing.ReduxStageRecovery.Get();
        Assert.IsFalse(replay.available);
        Assert.AreEqual("Unavailable", replay.phase);
    }
}
