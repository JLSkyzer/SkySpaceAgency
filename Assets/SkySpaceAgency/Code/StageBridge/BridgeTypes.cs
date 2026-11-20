using System;
using System.Collections.Generic;

namespace SkySpaceAgency.Bridge
{
    // What the bridge reads of Redux's Stage Recovery (Redux.StageRecovery in Ksp2Redux, all internal).
    // Pure types: no Unity, no game. StageRecoveryAccess fills them by reflection. Spec:
    // docs/superpowers/specs/2026-11-20-stage-recovery-bridge-design.md.

    /// <summary>Redux's StageRecoverySessionPhase (26w47a IL), plus Unknown for a name this build does not know.</summary>
    public enum BridgePhase { Unknown, Idle, SavingMission, LoadingStage, FlyingStage, LoadingMission }

    /// <summary>Redux's StageRecoveryStatus (26w47a IL), plus Unknown.</summary>
    public enum BridgeStatus { Unknown, Ready, Replaying, Landed, Splashed, Recovered, Lost, Failed }

    /// <summary>Why a call to Redux's stage recovery is allowed or refused. First match wins, in this order.</summary>
    public enum BridgeVerdict
    {
        Ok,
        ReduxUnavailable,
        Disabled,
        SessionBusy,
        CapturePending,
        Transitioning,
        NoSuchRecord,
        RecordNotReady,
        NotFlyingStage,
        NoActiveRecord,
        Docked,
        WouldLose,
    }

    public static class BridgeNames
    {
        // Names only: a number is not a phase (Enum.TryParse would accept "1").
        static bool IsName(string name) => !string.IsNullOrEmpty(name) && char.IsLetter(name[0]);

        public static BridgePhase ParsePhase(string name)
            => IsName(name) && Enum.TryParse(name, true, out BridgePhase p) && Enum.IsDefined(typeof(BridgePhase), p) ? p : BridgePhase.Unknown;

        public static BridgeStatus ParseStatus(string name)
            => IsName(name) && Enum.TryParse(name, true, out BridgeStatus s) && Enum.IsDefined(typeof(BridgeStatus), s) ? s : BridgeStatus.Unknown;
    }

    /// <summary>One recovery point (Redux's StageRecoveryRecord).</summary>
    public sealed class BridgeRecord
    {
        public string Id, VesselGuid, ParentVesselGuid, VesselName, Origin, LastError;
        public double CapturedUT;
        public double? CapturedMET, CompletedUT;
        public BridgeStatus Status;
    }

    /// <summary>Everything the bridge reads of the manager at one moment.</summary>
    public sealed class BridgeState
    {
        public bool Available, IsCapturePending, IsTransitioning, IsReplayActive, CanRetry, CreatePoints, ActiveReplayDocked;
        public BridgePhase Phase = BridgePhase.Unknown;
        public string ActiveRecordId;
        public float SaveDelaySeconds = float.NaN;
        IReadOnlyList<BridgeRecord> _records = Array.Empty<BridgeRecord>();

        /// <summary>Never null.</summary>
        public IReadOnlyList<BridgeRecord> Records
        {
            get => _records;
            set => _records = value ?? Array.Empty<BridgeRecord>();
        }

        public static BridgeState Unavailable => new BridgeState();
    }
}
