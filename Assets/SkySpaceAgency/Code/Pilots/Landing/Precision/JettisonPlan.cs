using System.Collections.Generic;
using System.Linq;

namespace K2D2.Landing.Precision
{
    // One part of the vessel's tree with its staging, as JettisonPlan needs it.
    public struct JettisonPart
    {
        public CapsulePart Part;      // tree, decoupler and parachute flags (CapsuleSide's input)
        // PartComponent.ActivationStage: the index, in StagingComponent.AvailableStages, of the
        // stage that activates the part (-1 = not staged). A decoupler fires when its stage does.
        public int ActivationStage;
        // PartComponent.DecoupleStage: the stage at which the game expects the part to leave the
        // vessel. Logged as a cross-check only; the plan decides from the tree and the decouplers.
        public int DecoupleStage;
        public bool IsHeatshield;
        public bool IsControl;        // the vessel's control owner (VesselComponent.GetControlOwner)
        public bool IsDockingPort;
    }

    public struct JettisonPlanResult
    {
        public bool Ok;
        public string Reason;               // why not, when !Ok
        public string Message;              // the refusal as shown to the player, when !Ok
        public int Stage;                   // the stage to activate (the next one)
        public HashSet<string> CapsuleIds;  // what stays: CapsuleSide's capsule
        public HashSet<string> ServiceIds;  // what leaves: every other part
        public List<string> Decouplers;     // the next stage's decouplers that cut the capsule free
        public List<string> OtherActivated; // the other parts the next stage activates (all on the service side)
        public int Heatshields, HeatshieldsKept;
        public int ServiceWithDecoupleStage; // service parts whose DecoupleStage is the jettison stage
    }

    // Can the next stage jettison the service module, and nothing else that matters? The capsule
    // is CapsuleSide's (the smallest piece holding every parachute); the service module is the
    // rest. Ok only when:
    // - there is a next stage;
    // - the capsule holds every parachute, and is not the whole vessel;
    // - the capsule stays the active vessel: it holds the control owner, or the root part when
    //   there is none (see below);
    // - every tree edge between the capsule and the rest is cut by a decoupler of the next stage
    //   (otherwise part of the service module stays attached), each with a known explosive node;
    // - the next stage activates no parachute, no docking port, and nothing on the capsule but
    //   its boundary decouplers;
    // - a heat shield is not dropped unless another one stays on the capsule.
    // One stage only: the caller activates it once. Pure, tested in EditMode.
    //
    // The active vessel after the split, from the IL of Module_Decouple.PerformPartDecouple
    // (Redux 26w42c): the split keeps the root part's piece in the original vessel
    // (IsPathToPart(RootPart) picks the side handed to SplitCombinedVesselSimObject). When the
    // split vessel was the active one and the decoupler is not omni, it passes the control owner
    // read before the split to SpaceSimulation.SetPartActiveVesselAfterTopologyChange, and
    // PromoteOrphanedParts calls ViewController.SetActiveVehicle on that part's vessel. So the
    // active vessel afterwards is the control owner's piece, or the root's piece without one.
    public static class JettisonPlan
    {
        public const string Refusal =
            "Cannot jettison the service module safely: stage it yourself after the deorbit, or reorder the stages.";
        public const string HeatshieldRefusal =
            "Cannot jettison the service module safely: the heat shield would be dropped with it.";

        // The stage StagingComponent.ActivateNextStage fires, from the IL (Redux 26w42c): with
        // L = StageCount - 1, if stage L is active (the vessel has staged once) it activates
        // stage L - 1 (and moves the still-active parts into it, removing stage L); otherwise
        // (nothing staged yet) it activates stage L. -1 = nothing left to fire.
        public static int NextStage(int stageCount, bool lastStageActive)
        {
            if (stageCount <= 0)
                return -1;
            int last = stageCount - 1;
            return lastStageActive ? last - 1 : last;
        }

        public static JettisonPlanResult Plan(IList<JettisonPart> parts, int nextStage)
        {
            if (parts == null || parts.Count == 0)
                return Refuse("the vessel has no parts.");
            if (nextStage < 0)
                return Refuse("there is no stage left to activate.");

            CapsuleSideResult side = CapsuleSide.Find(parts.Select(p => p.Part).ToList());
            if (!side.Ok)
                return Refuse(side.Reason);
            if (side.WholeVessel)
                return Refuse("no decoupler separates the parachutes from the rest of the vessel.");

            var byId = parts.ToDictionary(p => p.Part.Id);
            List<string> controls = parts.Where(p => p.IsControl).Select(p => p.Part.Id).ToList();
            if (controls.Count > 0)
            {
                if (controls.Any(id => !side.Ids.Contains(id)))
                    return Refuse("the controlling part is not on the parachutes' side of the decoupler: the game would keep the service module active.");
            }
            else if (parts.Any(p => p.Part.ParentId == null && !side.Ids.Contains(p.Part.Id)))
                return Refuse("no controlling part, and the root part is on the service module: the game would keep the service module active.");

            // Every cut must be made by a decoupler whose explosive node is the other end of the cut.
            foreach (CapsuleCut cut in side.Cuts)
                foreach (string id in cut.DecouplerIds)
                {
                    string other = id == cut.InsideId ? cut.OutsideId : cut.InsideId;
                    if (byId[id].Part.DetachesFromId != other)
                        return Refuse("a decoupler between the capsule and the service module has an unknown explosive node (or is an omni decoupler).");
                }

            List<JettisonPart> inStage = parts.Where(p => p.ActivationStage == nextStage).ToList();
            int chutes = inStage.Count(p => p.Part.IsParachute);
            if (chutes > 0)
                return Refuse($"the next stage ({nextStage}) also fires {chutes} parachute(s).");
            int ports = inStage.Count(p => p.IsDockingPort);
            if (ports > 0)
                return Refuse($"the next stage ({nextStage}) also fires {ports} docking port(s).");

            var firing = new HashSet<string>(inStage.Where(p => p.Part.IsDecoupler).Select(p => p.Part.Id));
            var decouplers = new List<string>();
            int uncut = 0;
            foreach (CapsuleCut cut in side.Cuts)
            {
                List<string> by = cut.DecouplerIds.Where(firing.Contains).ToList();
                if (by.Count == 0)
                    uncut++;
                decouplers.AddRange(by.Where(id => !decouplers.Contains(id)));
            }
            if (decouplers.Count == 0)
                return Refuse($"the next stage ({nextStage}) has no decoupler between the capsule and the service module.");
            if (uncut > 0)
                return Refuse($"the next stage ({nextStage}) cuts {side.Cuts.Count - uncut} of the {side.Cuts.Count} links between the capsule " +
                              "and the rest: parts would stay attached.");

            List<string> others = inStage.Select(p => p.Part.Id).Where(id => !decouplers.Contains(id)).ToList();
            int onCapsule = others.Count(side.Ids.Contains);
            if (onCapsule > 0)
                return Refuse($"the next stage ({nextStage}) also activates {onCapsule} part(s) on the capsule.");

            int shields = parts.Count(p => p.IsHeatshield);
            int kept = parts.Count(p => p.IsHeatshield && side.Ids.Contains(p.Part.Id));
            if (shields > 0 && kept == 0)
                return new JettisonPlanResult
                {
                    Ok = false, Stage = -1, Reason = "the heat shield would be dropped with it.", Message = HeatshieldRefusal,
                };

            var service = new HashSet<string>(byId.Keys.Where(id => !side.Ids.Contains(id)));
            return new JettisonPlanResult
            {
                Ok = true,
                Stage = nextStage,
                CapsuleIds = side.Ids,
                ServiceIds = service,
                Decouplers = decouplers,
                OtherActivated = others,
                Heatshields = shields,
                HeatshieldsKept = kept,
                ServiceWithDecoupleStage = service.Count(id => byId[id].DecoupleStage == nextStage),
            };
        }

        static JettisonPlanResult Refuse(string why) =>
            new JettisonPlanResult { Ok = false, Reason = why, Message = $"{Refusal} ({why})", Stage = -1 };
    }
}
