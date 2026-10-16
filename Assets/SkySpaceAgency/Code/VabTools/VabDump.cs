using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public class VabEngineInfo
    {
        public double ThrustVacKN;
        public double ThrustAslKN;
        public double IspVac;
        public double IspAsl;
        public List<string> Propellants = new();
    }

    public class VabDumpPart
    {
        public string Name;
        public string Title;
        public string Category;
        public string Family;
        public string TechNode;          // null when no tech node lists the part
        public string Size;
        public bool Unlocked;            // offered by this VAB (Stats.AvailableParts): what the import accepts
        public bool? CampaignUnlocked;   // SessionManager.IsPartUnlocked; null when unknown
        public double Cost;
        public double MassT;
        public double MaxTemp;
        public double CrashTolerance;
        public int CrewCapacity;
        public List<string> AttachNodes = new();
        public List<VabResource> Resources = new();   // Name and Capacity
        public VabEngineInfo Engine;                  // null when the part has no engine data
        public List<string> Modules = new();
        public string RawJson;                        // Game.Parts.GetRawJson; null when unavailable
    }

    // The parts dump document (spec, "Contenu du dump", and choix 1).
    public static class VabDump
    {
        public const string Format = "SkySpaceAgency.PartsDump";
        public const int Version = 1;

        public const string Instructions =
            "Parts dump of the SkySpaceAgency mod, made in the KSP2 Redux VAB. 'parts' lists every part the game " +
            "knows: \"unlocked\": true means this VAB offers the part now and the mod's Import accepts it; never use " +
            "a part with \"unlocked\": false. Masses are in t, thrust in kN, Isp in s; engine Isp values are the " +
            "definition's atmosphereCurve evaluated at 0 and 1 atm, and thrust_asl_kN is derived from them. " +
            "'part_templates' holds each part's raw definition JSON: its attachNodes (nodeID), resourceContainers " +
            "and modules show how the part is built. To write a rocket the mod can import, produce a " +
            "SerializedAssembly shaped like 'example_assembly' (the game_assembly of the rocket that was open in the " +
            "VAB at dump time): one entry in 'parts' per part, each with its own new PartGuid, its partName and a " +
            "partState; AssemblyOABConfig.RootGuid naming the root part; every other part attached to its parent " +
            "through a pair of attachNodeStates entries, each side naming the other part's PartGuid " +
            "(AttachedPartGuid) and node (attachedPartNodeID); stages in stagingState.availableStages (StageID and " +
            "the PartIds of the stage), mirrored in AssemblyOABConfig.StagingState; symmetric copies listed " +
            "together in SymmetrySets. Copy partState and module state from example_assembly parts of the same " +
            "kind and change only what you need. Send the rocket back as a .json file holding the SerializedAssembly " +
            "at the top level, or inside a SkySpaceAgency export under 'game_assembly'.";

        public const string NoExampleNote =
            " example_assembly is null: no rocket was open in the VAB at dump time. Ask the player to open or " +
            "build a small rocket in the VAB and dump again, or to send a SkySpaceAgency export, and copy its " +
            "structure.";

        public static string InstructionsFor(bool hasExample) => hasExample ? Instructions : Instructions + NoExampleNote;

        public static JObject Build(VabCampaign campaign, DateTime exportedAt, string gameVersion, string modVersion,
                                    IEnumerable<VabDumpPart> parts, JToken exampleAssembly, bool templatesUnlockedOnly)
        {
            List<VabDumpPart> sorted = (parts ?? Enumerable.Empty<VabDumpPart>())
                .Where(p => p?.Name != null)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

            var list = new JArray();
            var templates = new JObject();
            foreach (VabDumpPart p in sorted)
            {
                list.Add(PartJson(p));
                if (p.RawJson == null || (templatesUnlockedOnly && !p.Unlocked) || templates.ContainsKey(p.Name))
                    continue;
                templates[p.Name] = Template(p.RawJson);
            }

            bool hasExample = exampleAssembly != null && exampleAssembly.Type != JTokenType.Null;
            return new JObject
            {
                ["format"] = Format,
                ["version"] = Version,
                ["exported_at"] = VabFiles.IsoTime(exportedAt),
                ["game_version"] = VabNum.Json(gameVersion),
                ["mod_version"] = VabNum.Json(modVersion),
                ["campaign"] = (campaign ?? new VabCampaign()).ToJson(true),
                ["instructions_for_ai"] = InstructionsFor(hasExample),
                ["part_count"] = sorted.Count,
                ["unlocked_count"] = sorted.Count(p => p.Unlocked),
                ["parts"] = list,
                ["part_templates"] = templates,
                ["example_assembly"] = hasExample ? exampleAssembly.DeepClone() : JValue.CreateNull(),
            };
        }

        // The raw definition as JSON, or as text when it does not parse.
        static JToken Template(string raw)
        {
            try { return VabJson.Parse(raw); }
            catch (JsonException) { return new JValue(raw); }
        }

        static JToken EngineJson(VabEngineInfo e)
        {
            if (e == null)
                return JValue.CreateNull();
            return new JObject
            {
                ["thrust_vac_kN"] = VabNum.Round(e.ThrustVacKN, VabSummary.ThrustDigits),
                ["thrust_asl_kN"] = VabNum.Round(e.ThrustAslKN, VabSummary.ThrustDigits),
                ["isp_vac"] = VabNum.Round(e.IspVac, VabSummary.IspDigits),
                ["isp_asl"] = VabNum.Round(e.IspAsl, VabSummary.IspDigits),
                ["propellants"] = new JArray(e.Propellants ?? new List<string>()),
            };
        }

        static JObject PartJson(VabDumpPart p)
        {
            var resources = new JArray();
            foreach (VabResource r in p.Resources)
                resources.Add(new JObject
                {
                    ["name"] = VabNum.Json(r.Name),
                    ["capacity"] = VabNum.Round(r.Capacity, VabSummary.AmountDigits),
                });
            return new JObject
            {
                ["name"] = p.Name,
                ["title"] = VabNum.Json(p.Title),
                ["category"] = VabNum.Json(p.Category),
                ["family"] = VabNum.Json(p.Family),
                ["tech_node"] = VabNum.Json(p.TechNode),
                ["unlocked"] = p.Unlocked,
                ["campaign_unlocked"] = p.CampaignUnlocked.HasValue ? new JValue(p.CampaignUnlocked.Value) : JValue.CreateNull(),
                ["cost"] = VabNum.Round(p.Cost, VabSummary.CostDigits),
                ["mass_t"] = VabNum.Round(p.MassT, VabSummary.MassDigits),
                ["size"] = VabNum.Json(p.Size),
                ["crew_capacity"] = p.CrewCapacity,
                ["max_temp"] = VabNum.Round(p.MaxTemp, 0),
                ["crash_tolerance"] = VabNum.Round(p.CrashTolerance, 1),
                ["attach_nodes"] = new JArray(p.AttachNodes.Where(n => n != null).ToList()),
                ["resources"] = resources,
                ["engine"] = EngineJson(p.Engine),
                ["modules"] = new JArray(p.Modules),
            };
        }
    }
}
