using System.Collections.Generic;

namespace K2D2.Transfer
{
    // The mod drops an empty stage during a burn, but never one that would drop a docking port, a
    // parachute or the command part: the player stages those by hand.
    public static class StagingGuard
    {
        // reason completes "the next stage would drop {reason}"; null when the stage may be dropped.
        public static bool MayDrop(StageDv stage, out string reason)
        {
            var kept = new List<string>();
            if (stage.HasDockingPort) kept.Add("a docking port");
            if (stage.HasParachute) kept.Add("a parachute");
            if (stage.HasCommandPart) kept.Add("the command part");

            if (kept.Count == 0)
            {
                reason = null;
                return true;
            }
            reason = kept.Count == 1
                ? kept[0]
                : string.Join(", ", kept.GetRange(0, kept.Count - 1)) + " and " + kept[kept.Count - 1];
            return false;
        }
    }
}
