using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

// The test assembly (named after the mod id, which ModIdentity alone spells out).
[assembly: InternalsVisibleTo(SkySpaceAgency.ModIdentity.ModId + ".Tests.StageBridge")]

namespace SkySpaceAgency.Bridge
{
    // The only place that touches Redux's stage recovery (Redux.StageRecovery in Ksp2Redux, all of it
    // `internal`, so through reflection, like ReduxWaypoints). Every name below was read in the IL of
    // Redux 26w47a (Ksp2Redux.dll SHA-256 1f0e9d99...), see docs/superpowers/notes/2026-11-20-roadmap-phase0-scan.md,
    // and StageRecoveryAccessTests resolves each of them against the Ksp2Redux.dll the project is built with.
    // Read() never throws and never writes to the game. The three calls are internal: only the guarded
    // StageBridge methods use them (BridgeCalls decides first).
    //
    // Required names (the type, Instance, Records, SessionPhase, ActiveRecord, the record's Id, VesselName and
    // CapturedUniversalTime) must resolve or the bridge reads "unavailable". The others are soft: a name a later
    // Redux renames is reported once in LastProblem and its value reads as the default.
    public static class StageRecoveryAccess
    {
        public const string ManagerTypeName = "Redux.StageRecovery.StageRecoveryManager, Ksp2Redux";
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // The names, shared with the test that resolves them.
        public static readonly string[] ManagerProperties =
        {
            "Instance", "Records", "IsCapturePending", "IsTransitioning", "IsReplayActive", "CanRetryRecovery",
            "SessionPhase", "ActiveRecord", "CreateRecoveryPoints", "IsActiveReplayDocked",
        };
        public static readonly string[] ManagerMethods = { "StartReplay", "CompleteActiveLanding", "ReturnToMission" };
        public static readonly string[] RecordFields =
        {
            "Id", "VesselGuid", "ParentVesselGuid", "VesselName", "CapturedUniversalTime", "CapturedMissionElapsedTime",
            "CompletedUniversalTime", "Origin", "Status", "LastError",
        };
        static readonly string[] RequiredProperties = { "Instance", "Records", "SessionPhase", "ActiveRecord" };
        static readonly string[] RequiredFields = { "Id", "VesselName", "CapturedUniversalTime" };

        static bool? _resolved;
        static Type _manager;
        static readonly Dictionary<string, PropertyInfo> Prop = new Dictionary<string, PropertyInfo>();
        static readonly Dictionary<string, MethodInfo> Method = new Dictionary<string, MethodInfo>();
        static readonly Dictionary<string, FieldInfo> RecordField = new Dictionary<string, FieldInfo>();
        static PropertyInfo _saveDelay;

        /// <summary>What went wrong last (a name that did not resolve, an exception), for the log; null when nothing did.</summary>
        public static string LastProblem { get; private set; }

        /// <summary>The names that did not resolve (empty when all of them did).</summary>
        public static string MissingNames { get; private set; } = "";

        public static bool Resolved => _resolved ??= Resolve();

        static bool Resolve()
        {
            try
            {
                _manager = Type.GetType(ManagerTypeName);
                if (_manager == null)
                {
                    LastProblem = "type " + ManagerTypeName + " not found";
                    return false;
                }
                var missing = new List<string>();
                foreach (var name in ManagerProperties)
                {
                    Prop[name] = _manager.GetProperty(name, Any);
                    if (Prop[name] == null) missing.Add(name);
                }
                Method["StartReplay"] = _manager.GetMethod("StartReplay", Any, null, new[] { typeof(string) }, null);
                Method["CompleteActiveLanding"] = _manager.GetMethod("CompleteActiveLanding", Any, null, Type.EmptyTypes, null);
                Method["ReturnToMission"] = _manager.GetMethod("ReturnToMission", Any, null, Type.EmptyTypes, null);
                foreach (var kv in Method)
                    if (kv.Value == null) missing.Add(kv.Key + "()");

                var recordType = Prop["Records"]?.PropertyType.IsGenericType == true ? Prop["Records"].PropertyType.GetGenericArguments()[0] : null;
                if (recordType == null) missing.Add("record type");
                else
                    foreach (var name in RecordFields)
                    {
                        RecordField[name] = recordType.GetField(name, Any);
                        if (RecordField[name] == null) missing.Add("record." + name);
                    }
                // The game setting (not in every Redux build): looked up by name, NaN when absent.
                _saveDelay = typeof(KSP.Game.GameManager).Assembly.GetType("KSP.Game.PersistentProfileManager")?.GetProperty("StageRecoverySaveDelaySeconds", Any);

                MissingNames = string.Join(", ", missing);
                if (missing.Count > 0) LastProblem = "names not found in this Redux: " + MissingNames;
                foreach (var name in RequiredProperties)
                    if (Prop[name] == null) return false;
                if (recordType == null) return false;
                foreach (var name in RequiredFields)
                    if (RecordField[name] == null) return false;
                return true;
            }
            catch (Exception e)
            {
                LastProblem = "resolving failed: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>Redux's state now, or BridgeState.Unavailable (no manager yet, a required name missing, or a read failing; LastProblem says which).</summary>
        public static BridgeState Read()
        {
            try
            {
                if (!Resolved) return BridgeState.Unavailable;
                object manager = Prop["Instance"].GetValue(null);
                if (manager == null) return BridgeState.Unavailable;

                var state = new BridgeState
                {
                    Available = true,
                    Phase = BridgeNames.ParsePhase(Prop["SessionPhase"].GetValue(manager)?.ToString()),
                    IsCapturePending = ReadBool(manager, "IsCapturePending"),
                    IsTransitioning = ReadBool(manager, "IsTransitioning"),
                    IsReplayActive = ReadBool(manager, "IsReplayActive"),
                    CanRetry = ReadBool(manager, "CanRetryRecovery"),
                    CreatePoints = ReadBool(manager, "CreateRecoveryPoints"),
                    ActiveReplayDocked = ReadBool(manager, "IsActiveReplayDocked"),
                    SaveDelaySeconds = ReadSaveDelay(),
                };
                object active = Prop["ActiveRecord"].GetValue(manager);
                state.ActiveRecordId = active == null ? null : RecordField["Id"].GetValue(active) as string;

                var list = new List<BridgeRecord>();
                if (Prop["Records"].GetValue(manager) is IEnumerable records)
                    foreach (object r in records)
                        if (r != null) list.Add(ReadRecord(r));
                state.Records = list;
                return state;
            }
            catch (Exception e)
            {
                LastProblem = "read failed: " + e.GetType().Name + ": " + e.Message;
                return BridgeState.Unavailable;
            }
        }

        static bool ReadBool(object manager, string name)
            => Prop.TryGetValue(name, out var p) && p != null && p.GetValue(manager) is bool b && b;

        static float ReadSaveDelay()
        {
            try
            {
                if (_saveDelay != null && _saveDelay.GetValue(null) is float f) return f;
            }
            catch (Exception) { }
            return float.NaN;
        }

        static T Field<T>(object record, string name) where T : class
            => RecordField.TryGetValue(name, out var f) && f != null ? f.GetValue(record) as T : null;

        /// <summary>One Redux StageRecoveryRecord as a BridgeRecord (internal for the tests, which build a real record).</summary>
        internal static BridgeRecord ReadRecord(object r)
        {
            RecordField.TryGetValue("CapturedUniversalTime", out var ut);
            RecordField.TryGetValue("CapturedMissionElapsedTime", out var met);
            RecordField.TryGetValue("CompletedUniversalTime", out var done);
            RecordField.TryGetValue("Origin", out var origin);
            RecordField.TryGetValue("Status", out var status);
            return new BridgeRecord
            {
                Id = Field<string>(r, "Id"),
                VesselGuid = Field<string>(r, "VesselGuid"),
                ParentVesselGuid = Field<string>(r, "ParentVesselGuid"),
                VesselName = Field<string>(r, "VesselName"),
                CapturedUT = ut?.GetValue(r) is double d ? d : double.NaN,
                CapturedMET = met?.GetValue(r) as double?,
                CompletedUT = done?.GetValue(r) as double?,
                Origin = origin?.GetValue(r)?.ToString(),
                Status = BridgeNames.ParseStatus(status?.GetValue(r)?.ToString()),
                LastError = Field<string>(r, "LastError"),
            };
        }

        // The three calls: internal, so only the guarded StageBridge methods (and the tests) can reach them.

        internal static bool StartReplay(string recordId) => Call("StartReplay", recordId);
        internal static bool CompleteActiveLanding() => Call("CompleteActiveLanding");
        internal static bool ReturnToMission() => Call("ReturnToMission");

        static bool Call(string name, params object[] args)
        {
            try
            {
                if (!Resolved) return false;
                var method = Method[name];
                if (method == null)
                {
                    LastProblem = name + " not found in this Redux";
                    return false;
                }
                object manager = Prop["Instance"].GetValue(null);
                return manager != null && method.Invoke(manager, args.Length == 0 ? null : args) is bool b && b;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                LastProblem = name + " threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }
    }
}
