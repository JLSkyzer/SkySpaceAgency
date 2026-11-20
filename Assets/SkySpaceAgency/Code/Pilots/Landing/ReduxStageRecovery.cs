namespace SkySpaceAgency.Landing
{
    // What Redux's stage recovery is doing (Redux.StageRecovery.StageRecoveryManager in
    // Ksp2Redux, all internal - so read through reflection, like ReduxWaypoints).
    //
    // When stages separate, Redux saves a snapshot of the dropped stage if it has a probe core and
    // can be controlled. Replaying one saves the mission, loads that snapshot at the moment of
    // separation and hands the player the stage as the active vessel ("FlyingStage"); after it
    // lands Redux records the result and loads the mission back. The mod only reads this - here
    // for the landing's log (K2-D2 also shows it in its boost-back landing's status line); later the
    // booster return will need it to know it flies a recovered stage.
    public struct ReduxReplay
    {
        public bool available;
        // Redux's StageRecoverySessionPhase: Idle, SavingMission, LoadingStage, FlyingStage,
        // LoadingMission.
        public string phase;
        public bool flying_stage => phase == "FlyingStage";
        public string vessel_name;
        public double captured_UT;
    }

    public static class ReduxStageRecovery
    {
        // Reads through the bridge's one reflection class (StageRecoveryAccess), which resolves the
        // names of Redux 26w47a once and never throws.
        public static ReduxReplay Get()
        {
            var replay = new ReduxReplay { phase = "Unavailable" };
            var state = SkySpaceAgency.Bridge.StageRecoveryAccess.Read();
            if (!state.Available)
                return replay;

            replay.available = true;
            replay.phase = state.Phase.ToString();
            var active = SkySpaceAgency.Bridge.BridgeRules.Find(state, state.ActiveRecordId);
            if (active != null)
            {
                replay.vessel_name = active.VesselName;
                replay.captured_UT = active.CapturedUT;
            }
            return replay;
        }
    }
}
