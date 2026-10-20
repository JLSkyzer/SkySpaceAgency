using K2D2.OrbitPlanning;

namespace K2D2.KSPService
{
    /// <summary>
    /// The Orbit tab plan in progress, in memory only. The Orbit tab starts it with the node of
    /// its first burn; the Node tab, after each registered burn, asks the sequence for the next
    /// one and creates its node (see NodeExPilot.ContinueOrbitPlan). Cleared when the plan ends,
    /// fails, is replaced, when its node is edited by hand, and on a vessel change.
    /// </summary>
    public static class OrbitPlanRun
    {
        public static OrbitPlanSequence Sequence { get; private set; }

        // Last progress line, shown by the Orbit tab (kept after the plan ends).
        public static string Status { get; set; } = "";

        public static bool Active => Sequence != null;

        public static void Start(OrbitPlanSequence sequence, string status)
        {
            Sequence = sequence;
            Status = status;
        }

        public static void End(string status)
        {
            Sequence = null;
            Status = status;
        }

        public static void Clear()
        {
            Sequence = null;
            Status = "";
        }
    }
}
