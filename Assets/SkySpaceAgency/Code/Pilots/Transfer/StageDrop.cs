using System.Collections.Generic;
using System.Linq;
using K2D2.Landing.Precision;

namespace K2D2.Transfer
{
    // One part of the vessel's tree, as StageDrop needs it (read like JettisonPart, by
    // ReentryContext.ReadParts).
    public struct DropPart
    {
        public CapsulePart Part;      // tree, decoupler (explosive node) and parachute flags
        public string Name;
        // PartComponent.ActivationStage: the index, in StagingComponent.AvailableStages, of the
        // stage that activates the part (-1 = not staged). A decoupler fires when its stage does.
        public int ActivationStage;
        public bool IsControl;        // the vessel's control owner (VesselComponent.GetControlOwner)
        public bool IsCommand;        // a command part (Data_Command), the control owner included
        public bool IsDockingPort;
        // Landing legs (LandingGear.IsLeg). Only the landing pilot fills it: a transfer burn may drop legs.
        public bool IsLandingLeg;
    }

    public struct StageDropResult
    {
        public bool MayStage;
        public bool NoStageLeft;
        public string Reason;           // completes "the next stage would drop {reason}", or why the tree could not be read
        public string Message;          // the failure as shown to the player, when !MayStage
        public int Stage;
        public StageDv Flags;           // built from what the stage drops (and fires), for StagingGuard
        public List<string> DroppedIds;
        public List<string> KeptIds;
        public List<string> Decouplers; // the stage's decouplers
    }

    // What firing a stage would drop, decided from the part tree (the Task 9 ruling), not from the
    // per-part stage numbers: the stage's decouplers cut the tree at their explosive node (an
    // omni or unknown one on every side, as CapsuleSide does), and every piece but the one holding
    // the control owner (the root part without one) leaves. Which piece stays active is the IL
    // finding of JettisonPlan's header. The flags are those of the dropped parts; a parachute or a
    // docking port the stage fires on the kept side counts too (the stage would deploy or undock
    // it). StagingGuard.MayDrop then decides. Pure, tested in EditMode.
    public static class StageDrop
    {
        public const string Prefix = "Burn stopped: ";

        public static StageDropResult Decide(IList<DropPart> parts, int stage)
        {
            if (parts == null || parts.Count == 0)
                return Unreadable(stage, "the vessel has no parts.");
            if (stage < 0)
                return new StageDropResult
                {
                    MayStage = false, NoStageLeft = true, Stage = stage, Reason = "no stage left",
                    Message = Prefix + "the engines give no thrust and no stage is left.",
                    DroppedIds = new List<string>(), KeptIds = new List<string>(), Decouplers = new List<string>(),
                };

            var byId = new Dictionary<string, DropPart>();
            foreach (DropPart p in parts)
            {
                if (p.Part.Id == null || byId.ContainsKey(p.Part.Id))
                    return Unreadable(stage, "two parts share the id " + (p.Part.Id ?? "null") + ".");
                byId[p.Part.Id] = p;
            }

            var neighbours = byId.Keys.ToDictionary(id => id, id => new List<string>());
            foreach (DropPart p in parts)
            {
                string parent = p.Part.ParentId;
                if (parent == null || !byId.ContainsKey(parent))
                    continue;
                neighbours[p.Part.Id].Add(parent);
                neighbours[parent].Add(p.Part.Id);
            }

            // The stage's decouplers, each with the neighbour it separates from (null = every one).
            // An explosive node holding a part that is not a tree neighbour is treated as unknown.
            var firing = new Dictionary<string, string>();
            foreach (DropPart p in parts)
            {
                if (!p.Part.IsDecoupler || p.ActivationStage != stage)
                    continue;
                string from = p.Part.DetachesFromId;
                if (!string.IsNullOrEmpty(from) && !neighbours[p.Part.Id].Contains(from))
                    from = null;
                firing[p.Part.Id] = from;
            }
            bool Separates(string decoupler, string neighbour) =>
                firing.TryGetValue(decoupler, out string from) && (from == null || from == neighbour);

            string keep = parts.Where(p => p.IsControl).Select(p => p.Part.Id).FirstOrDefault()
                ?? parts.Where(p => p.Part.ParentId == null || !byId.ContainsKey(p.Part.ParentId)).Select(p => p.Part.Id).FirstOrDefault();
            if (keep == null)
                return Unreadable(stage, "the vessel has no control owner and no root part.");

            var kept = new HashSet<string> { keep };
            var queue = new Queue<string>(kept);
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                foreach (string n in neighbours[id])
                    if (!kept.Contains(n) && !Separates(id, n) && !Separates(n, id))
                    {
                        kept.Add(n);
                        queue.Enqueue(n);
                    }
            }

            List<DropPart> dropped = parts.Where(p => !kept.Contains(p.Part.Id)).ToList();
            List<DropPart> fired = parts.Where(p => p.ActivationStage == stage).ToList();
            var flags = new StageDv
            {
                Stage = stage,
                HasDockingPort = dropped.Any(p => p.IsDockingPort) || fired.Any(p => p.IsDockingPort),
                HasParachute = dropped.Any(p => p.Part.IsParachute) || fired.Any(p => p.Part.IsParachute),
                HasCommandPart = dropped.Any(p => p.IsCommand),
                HasLandingLeg = dropped.Any(p => p.IsLandingLeg),
            };
            bool may = StagingGuard.MayDrop(flags, out string reason);
            return new StageDropResult
            {
                MayStage = may,
                Stage = stage,
                Reason = reason,
                Message = may ? null : $"{Prefix}the next stage would drop {reason}. Stage manually.",
                Flags = flags,
                DroppedIds = dropped.Select(p => p.Part.Id).ToList(),
                KeptIds = parts.Where(p => kept.Contains(p.Part.Id)).Select(p => p.Part.Id).ToList(),
                Decouplers = firing.Keys.ToList(),
            };
        }

        static StageDropResult Unreadable(int stage, string why) => new StageDropResult
        {
            MayStage = false, Stage = stage, Reason = why,
            Message = $"{Prefix}cannot tell what the next stage would drop ({why.TrimEnd('.')}). Stage manually.",
            DroppedIds = new List<string>(), KeptIds = new List<string>(), Decouplers = new List<string>(),
        };
    }
}
