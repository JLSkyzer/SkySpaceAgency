using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public class VabCampaign
    {
        public string Name;
        public string Mode;
        public bool UnlockAllParts;
        public List<string> UnlockedTechNodes = new();

        public JObject ToJson(bool withTechNodes)
        {
            var o = new JObject
            {
                ["name"] = VabNum.Json(Name),
                ["mode"] = VabNum.Json(Mode),
                ["unlock_all_parts"] = UnlockAllParts,
            };
            if (withTechNodes)
                o["unlocked_tech_nodes"] = new JArray(UnlockedTechNodes ?? new List<string>());
            return o;
        }
    }

    public class VabExportMeta
    {
        public DateTime ExportedAt;
        public string GameVersion;
        public string ModVersion;
        public VabCampaign Campaign = new();
    }

    // The export document (spec, "Contenu de l'export").
    public static class VabExport
    {
        public const string Format = "SkySpaceAgency.VabExport";
        public const int Version = 1;

        public const string InstructionsForAi =
            "Exported by the SkySpaceAgency mod from the KSP2 Redux VAB. " +
            "'summary' is a readable digest computed by the game's VAB: per stage, in the game's stage order, " +
            "the parts, the active engines, masses (t), Isp (s), thrust (kN), TWR, delta-v (m/s) in vacuum, at sea " +
            "level and in the stage's situation ('actual'), and burn time (s); then every part (GUID, name, title, " +
            "parent, attach node, stage, symmetry group, mass, cost, resources, modules) and the resource totals. " +
            "'game_assembly' is the game's own JSON for this rocket (a SerializedAssembly): it is the only section " +
            "the mod's Import reads back, the summary is ignored on import. To hand back a corrected rocket, edit " +
            "'game_assembly' and send back either this whole file or that object alone. Keep every part's PartGuid " +
            "unique; keep AssemblyOABConfig.RootGuid on an existing part; keep node pairs consistent: when part A's " +
            "attachNodeStates entry points to part B (AttachedPartGuid and attachedPartNodeID), part B must have the " +
            "matching entry pointing back to A; when you add or remove a part, update the stages (PartIds in " +
            "stagingState and AssemblyOABConfig.StagingState) and SymmetrySets too. Use only part names marked " +
            "\"unlocked\": true in a SkySpaceAgency parts dump of the same campaign: the import refuses any other " +
            "part. Resource amounts above a part's capacity are lowered to the capacity on import.";

        public static JObject Build(VabExportMeta meta, JObject summary, JToken gameAssembly)
        {
            return new JObject
            {
                ["format"] = Format,
                ["version"] = Version,
                ["exported_at"] = VabFiles.IsoTime(meta.ExportedAt),
                ["game_version"] = VabNum.Json(meta.GameVersion),
                ["mod_version"] = VabNum.Json(meta.ModVersion),
                ["campaign"] = (meta.Campaign ?? new VabCampaign()).ToJson(false),
                ["instructions_for_ai"] = InstructionsForAi,
                ["summary"] = summary == null ? new JObject() : summary.DeepClone(),
                ["game_assembly"] = gameAssembly == null ? JValue.CreateNull() : gameAssembly.DeepClone(),
            };
        }
    }
}
