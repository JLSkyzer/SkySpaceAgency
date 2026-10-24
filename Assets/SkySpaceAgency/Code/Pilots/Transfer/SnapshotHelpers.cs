using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.Sim;

namespace K2D2.Transfer
{
    // One part of the vessel as the stage list needs it, read from the game's DeltaVPartInfo.
    // ActivationStage and DecoupleStage are on the scale of DeltaVStageInfo.Stage: the game's
    // DeltaVStageInfo.PartsActivateInStage and PartsDecoupledInStage compare them to it directly.
    public struct StagePart
    {
        public string Name;
        public int ActivationStage;   // -1: not staged
        public int DecoupleStage;     // -1: never decoupled
        public bool IsDockingPort, IsParachute, IsCommand;
    }

    // Pure helpers of the game snapshot (GameBodySystem, VesselSnapshot), tested in EditMode.

    // The vessel's stages in firing order, with the part flags StagingGuard reads.
    public static class StageList
    {
        // Firing order is Stage descending: StagingComponent.ActivateNextStage fires StageCount-1
        // first. The order of the game's StageInfo list is never trusted.
        //
        // A stage is flagged for a part kind when a part of that kind activates with it or is
        // decoupled at it. The two are counted together on purpose: which of the two "firing the
        // stage drops" means in the DeltaV numbering is not verified in game, and a guard that
        // fires too often only asks the player to stage by hand.
        public static List<StageDv> InFiringOrder(IEnumerable<(int stage, double deltaVVac)> stages,
            IEnumerable<StagePart> parts, out string error)
        {
            if (stages == null) throw new ArgumentNullException(nameof(stages));
            var partList = (parts ?? Enumerable.Empty<StagePart>()).ToList();
            var result = new List<StageDv>();
            var seen = new HashSet<int>();
            foreach (var (stage, dv) in stages)
            {
                if (!seen.Add(stage))
                {
                    error = $"The game lists stage {stage} twice.";
                    return null;
                }
                bool At(StagePart p) => p.ActivationStage == stage || p.DecoupleStage == stage;
                result.Add(new StageDv
                {
                    Stage = stage,
                    DeltaVVac = double.IsNaN(dv) ? 0 : dv,
                    HasDockingPort = partList.Any(p => p.IsDockingPort && At(p)),
                    HasParachute = partList.Any(p => p.IsParachute && At(p)),
                    HasCommandPart = partList.Any(p => p.IsCommand && At(p)),
                });
            }
            result.Sort((a, b) => b.Stage.CompareTo(a.Stage));
            error = null;
            return result;
        }

        // "stage 3: 1250 m/s, stage 2: 0 m/s [parachute, command part], …" for the log.
        public static string Describe(IReadOnlyList<StageDv> stages)
        {
            if (stages == null || stages.Count == 0)
                return "no stage";
            return string.Join(", ", stages.Select(s =>
            {
                var kept = new List<string>();
                if (s.HasDockingPort) kept.Add("docking port");
                if (s.HasParachute) kept.Add("parachute");
                if (s.HasCommandPart) kept.Add("command part");
                string flags = kept.Count > 0 ? " [" + string.Join(", ", kept) + "]" : "";
                return $"stage {s.Stage}: {s.DeltaVVac.ToString("F0", CultureInfo.InvariantCulture)} m/s{flags}";
            }));
        }
    }

    public static class TerrainGrid
    {
        // About 'count' unit vectors spread evenly over the sphere (Fibonacci lattice), for sampling
        // a body's terrain when the game gives no maximum height.
        public static List<Vector3d> Directions(int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var dirs = new List<Vector3d>(count);
            double golden = Math.PI * (3 - Math.Sqrt(5));
            for (int i = 0; i < count; i++)
            {
                double z = 1 - 2 * (i + 0.5) / count;
                double rho = Math.Sqrt(Math.Max(0, 1 - z * z));
                double phi = golden * i;
                dirs.Add(new Vector3d(rho * Math.Cos(phi), rho * Math.Sin(phi), z));
            }
            return dirs;
        }
    }
}
