using System;

namespace SkySpaceAgency.Bridge
{
    // The rules for using Redux's stage recovery (pure). CanStartReplay mirrors Redux's own start
    // conditions (StageRecoveryLifecycle.AllowsReplayStart: phase Idle and no capture pending; the
    // record must be Ready) and adds the guard (stage_bridge_enabled).
    public static class BridgeRules
    {
        public static BridgeRecord Find(BridgeState state, string id)
        {
            if (state == null || string.IsNullOrEmpty(id)) return null;
            foreach (var r in state.Records)
                if (r != null && r.Id == id) return r;
            return null;
        }

        /// <summary>The newest Ready record of that vessel (the last in the list), or null.</summary>
        public static BridgeRecord FindReady(BridgeState state, string vesselGuid)
        {
            if (state == null || string.IsNullOrEmpty(vesselGuid)) return null;
            BridgeRecord found = null;
            foreach (var r in state.Records)
                if (r != null && r.VesselGuid == vesselGuid && r.Status == BridgeStatus.Ready) found = r;
            return found;
        }

        /// <summary>Redux's capture is finished and a Ready recovery point of that vessel exists: the condition to pause.</summary>
        public static bool HasSaved(BridgeState state, string vesselGuid)
            => state != null && state.Available && !state.IsCapturePending && FindReady(state, vesselGuid) != null;

        public static BridgeVerdict CanStartReplay(BridgeState state, string recordId, bool enabled)
        {
            if (state == null || !state.Available) return BridgeVerdict.ReduxUnavailable;
            if (!enabled) return BridgeVerdict.Disabled;
            if (state.Phase != BridgePhase.Idle) return BridgeVerdict.SessionBusy;
            if (state.IsCapturePending) return BridgeVerdict.CapturePending;
            if (state.IsTransitioning) return BridgeVerdict.Transitioning;
            var record = Find(state, recordId);
            if (record == null) return BridgeVerdict.NoSuchRecord;
            if (record.Status != BridgeStatus.Ready) return BridgeVerdict.RecordNotReady;
            return BridgeVerdict.Ok;
        }

        public static BridgeVerdict CanCompleteLanding(BridgeState state, bool enabled)
        {
            var v = InSession(state, enabled);
            if (v != BridgeVerdict.Ok) return v;
            return state.ActiveReplayDocked ? BridgeVerdict.Docked : BridgeVerdict.Ok;
        }

        // Redux's ReturnToMission marks the stage Lost for good when quickloading is off (CanRetryRecovery false):
        // the rule refuses that case, the call would end the recovery.
        public static BridgeVerdict CanReturnToMission(BridgeState state, bool enabled)
        {
            var v = InSession(state, enabled);
            return v == BridgeVerdict.Ok && !state.CanRetry ? BridgeVerdict.WouldLose : v;
        }

        static BridgeVerdict InSession(BridgeState state, bool enabled)
        {
            if (state == null || !state.Available) return BridgeVerdict.ReduxUnavailable;
            if (!enabled) return BridgeVerdict.Disabled;
            if (state.Phase != BridgePhase.FlyingStage) return BridgeVerdict.NotFlyingStage;
            if (string.IsNullOrEmpty(state.ActiveRecordId) || Find(state, state.ActiveRecordId) == null) return BridgeVerdict.NoActiveRecord;
            if (state.IsTransitioning) return BridgeVerdict.Transitioning;
            return BridgeVerdict.Ok;
        }

        public static string Explain(BridgeVerdict verdict)
        {
            switch (verdict)
            {
                case BridgeVerdict.Ok: return "allowed";
                case BridgeVerdict.ReduxUnavailable: return "Redux's stage recovery is not available in this game build";
                case BridgeVerdict.Disabled: return "the stage_bridge_enabled setting is off";
                case BridgeVerdict.SessionBusy: return "a recovery session is already running";
                case BridgeVerdict.CapturePending: return "Redux is still saving a recovery point";
                case BridgeVerdict.Transitioning: return "Redux is loading or saving";
                case BridgeVerdict.NoSuchRecord: return "no recovery point with that id";
                case BridgeVerdict.RecordNotReady: return "the recovery point is not Ready";
                case BridgeVerdict.NotFlyingStage: return "no recovery stage is being flown";
                case BridgeVerdict.NoActiveRecord: return "Redux has no active recovery point";
                case BridgeVerdict.Docked: return "the recovery stage docked";
                case BridgeVerdict.WouldLose: return "quickloading is off, so returning would mark the stage lost for good";
                default: return verdict.ToString();
            }
        }
    }

    public readonly struct BridgeCallResult
    {
        public readonly BridgeVerdict Verdict;
        public readonly bool Invoked;
        public readonly bool Result;

        public BridgeCallResult(BridgeVerdict verdict, bool invoked, bool result)
        {
            Verdict = verdict;
            Invoked = invoked;
            Result = result;
        }
    }

    /// <summary>Evaluates the rule, and only on Ok calls Redux (the delegate). Nothing else may call Redux's three methods.</summary>
    public static class BridgeCalls
    {
        public static BridgeCallResult TryStart(BridgeState state, string recordId, bool enabled, Func<string, bool> invoke)
            => Run(BridgeRules.CanStartReplay(state, recordId, enabled), invoke == null ? (Func<bool>)null : () => invoke(recordId));

        public static BridgeCallResult TryComplete(BridgeState state, bool enabled, Func<bool> invoke)
            => Run(BridgeRules.CanCompleteLanding(state, enabled), invoke);

        public static BridgeCallResult TryReturn(BridgeState state, bool enabled, Func<bool> invoke)
            => Run(BridgeRules.CanReturnToMission(state, enabled), invoke);

        static BridgeCallResult Run(BridgeVerdict verdict, Func<bool> invoke)
        {
            if (verdict != BridgeVerdict.Ok || invoke == null) return new BridgeCallResult(verdict, false, false);
            return new BridgeCallResult(verdict, true, invoke());
        }
    }
}
