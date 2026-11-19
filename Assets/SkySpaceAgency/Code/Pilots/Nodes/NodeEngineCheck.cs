namespace SkySpaceAgency.Node
{
    /// <summary>What the node start check makes of the vessel's engines.</summary>
    public enum NodeEngineState
    {
        /// <summary>An engine is lit now (BurndV.IsActive: ignited, not shut down).</summary>
        Active,
        /// <summary>None lit by our test, but the current stage has Δv: its engines can still
        /// thrust (EngineIgnited may well read false at throttle 0, not verified in game).</summary>
        StageOnly,
        /// <summary>Nothing lit and no Δv in the current stage: no engine could thrust at all.</summary>
        None
    }

    /// <summary>
    /// The node start check. K2-D2 1.4.0 refuses a node run without an active engine; whether
    /// EngineIgnited stays true at throttle 0 is not known, so a start from a coast is refused only
    /// when the current stage has no Δv either.
    /// </summary>
    public static class NodeEngineCheck
    {
        /// <param name="hasActiveEngine">BurndV.has_active_engine.</param>
        /// <param name="currentStageDv">Δv of the current stage (game readout), m/s; NaN when unknown.</param>
        public static NodeEngineState Evaluate(bool hasActiveEngine, double currentStageDv)
        {
            if (hasActiveEngine)
                return NodeEngineState.Active;
            return currentStageDv > 0 ? NodeEngineState.StageOnly : NodeEngineState.None;
        }

        /// <summary>A node run may start: some engine could thrust.</summary>
        public static bool CanStart(NodeEngineState state) => state != NodeEngineState.None;

        /// <summary>Our engine test and the stage readout disagree (lit engine without stage Δv, or
        /// stage Δv without a lit engine): worth one log line. An unknown readout (NaN) never does.</summary>
        public static bool Disagrees(bool hasActiveEngine, double currentStageDv)
        {
            if (double.IsNaN(currentStageDv))
                return false;
            return hasActiveEngine != currentStageDv > 0;
        }
    }
}
