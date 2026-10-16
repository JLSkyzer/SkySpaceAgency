using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public class VabResource
    {
        public string Name;
        public double Amount;
        public double Capacity;
        public double MassT;
    }

    public class VabPart
    {
        public string Guid;
        public string Name;          // internal part name (partName)
        public string Title;         // display title
        public string ParentGuid;    // null for the root part
        public string AttachNode;    // this part's node connected to its parent; null for the root
        public int? SymmetryGroup;   // same number = same symmetry set
        public double DryMassT;
        public double Cost;
        public List<VabResource> Resources = new();
        public List<string> Modules = new();
    }

    public class VabStage
    {
        public int Stage;                       // the game's stage number
        public List<string> PartGuids = new();  // parts of this stage
        public List<string> EngineGuids = new(); // engines burning in this stage
        public double StartMassT, EndMassT, DryMassT, FuelMassT;
        public double IspVac, IspAsl, ThrustVacKN, ThrustAslKN;
        public double TwrVac, TwrAsl, TwrActual;
        public double DeltaVVac, DeltaVAsl, DeltaVActual, BurnTimeS;
        public string SituationBody;
        public bool SituationAtmosphere;
        public double SituationAltitudeM;
    }

    public class VabVessel
    {
        public string Name;
        public double MassWetT, MassDryT;
        public double DeltaVTotalVac, DeltaVTotalActual;
        public bool DeltaVReady;
        public List<VabStage> Stages = new();  // in the game's order
        public List<VabPart> Parts = new();
    }

    // The "summary" section of an export (spec, "Contenu de l'export").
    public static class VabSummary
    {
        public const int MassDigits = 3;
        public const int DeltaVDigits = 1;
        public const int IspDigits = 1;
        public const int ThrustDigits = 1;
        public const int TwrDigits = 2;
        public const int TimeDigits = 1;
        public const int AltitudeDigits = 0;
        public const int AmountDigits = 3;
        public const int CostDigits = 0;

        public static JObject Build(VabVessel vessel)
        {
            var titles = new Dictionary<string, string>();
            foreach (VabPart p in vessel.Parts)
                if (p.Guid != null && !titles.ContainsKey(p.Guid))
                    titles[p.Guid] = DisplayName(p);

            // A part's stage: the first stage that lists it.
            var stageOf = new Dictionary<string, int>();
            foreach (VabStage s in vessel.Stages)
                foreach (string g in s.PartGuids)
                    if (g != null && !stageOf.ContainsKey(g))
                        stageOf[g] = s.Stage;

            var stages = new JArray();
            foreach (VabStage s in vessel.Stages)
                stages.Add(StageJson(s, titles));

            var parts = new JArray();
            foreach (VabPart p in vessel.Parts)
                parts.Add(PartJson(p, stageOf));

            return new JObject
            {
                ["vessel_name"] = VabNum.Json(vessel.Name),
                ["part_count"] = vessel.Parts.Count,
                ["mass_wet_t"] = VabNum.Round(vessel.MassWetT, MassDigits),
                ["mass_dry_t"] = VabNum.Round(vessel.MassDryT, MassDigits),
                ["delta_v_total_vac"] = VabNum.Round(vessel.DeltaVTotalVac, DeltaVDigits),
                ["delta_v_total_actual"] = VabNum.Round(vessel.DeltaVTotalActual, DeltaVDigits),
                ["delta_v_ready"] = vessel.DeltaVReady,
                ["stages"] = stages,
                ["parts"] = parts,
                ["resources_total"] = Totals(vessel.Parts),
            };
        }

        static string DisplayName(VabPart p) =>
            !string.IsNullOrEmpty(p.Title) ? p.Title : !string.IsNullOrEmpty(p.Name) ? p.Name : p.Guid;

        static JArray Titles(List<string> guids, Dictionary<string, string> titles)
        {
            var list = new JArray();
            foreach (string g in guids)
                if (g != null)
                    list.Add(titles.TryGetValue(g, out string title) ? title : g);
            return list;
        }

        static JObject StageJson(VabStage s, Dictionary<string, string> titles) => new JObject
        {
            ["stage"] = s.Stage,
            ["parts"] = Titles(s.PartGuids, titles),
            ["engines_active"] = Titles(s.EngineGuids, titles),
            ["start_mass_t"] = VabNum.Round(s.StartMassT, MassDigits),
            ["end_mass_t"] = VabNum.Round(s.EndMassT, MassDigits),
            ["dry_mass_t"] = VabNum.Round(s.DryMassT, MassDigits),
            ["fuel_mass_t"] = VabNum.Round(s.FuelMassT, MassDigits),
            ["isp_vac"] = VabNum.Round(s.IspVac, IspDigits),
            ["isp_asl"] = VabNum.Round(s.IspAsl, IspDigits),
            ["thrust_vac_kN"] = VabNum.Round(s.ThrustVacKN, ThrustDigits),
            ["thrust_asl_kN"] = VabNum.Round(s.ThrustAslKN, ThrustDigits),
            ["twr_vac"] = VabNum.Round(s.TwrVac, TwrDigits),
            ["twr_asl"] = VabNum.Round(s.TwrAsl, TwrDigits),
            ["twr_actual"] = VabNum.Round(s.TwrActual, TwrDigits),
            ["delta_v_vac"] = VabNum.Round(s.DeltaVVac, DeltaVDigits),
            ["delta_v_asl"] = VabNum.Round(s.DeltaVAsl, DeltaVDigits),
            ["delta_v_actual"] = VabNum.Round(s.DeltaVActual, DeltaVDigits),
            ["burn_time_s"] = VabNum.Round(s.BurnTimeS, TimeDigits),
            ["situation"] = new JObject
            {
                ["body"] = VabNum.Json(s.SituationBody),
                ["atmosphere"] = s.SituationAtmosphere,
                ["altitude_m"] = VabNum.Round(s.SituationAltitudeM, AltitudeDigits),
            },
        };

        static JObject ResourceJson(VabResource r) => new JObject
        {
            ["name"] = VabNum.Json(r.Name),
            ["amount"] = VabNum.Round(r.Amount, AmountDigits),
            ["capacity"] = VabNum.Round(r.Capacity, AmountDigits),
            ["mass_t"] = VabNum.Round(r.MassT, MassDigits),
        };

        static JObject PartJson(VabPart p, Dictionary<string, int> stageOf)
        {
            var resources = new JArray();
            double resourceMass = 0;
            foreach (VabResource r in p.Resources)
            {
                resources.Add(ResourceJson(r));
                resourceMass += VabNum.Finite(r.MassT);
            }
            return new JObject
            {
                ["guid"] = VabNum.Json(p.Guid),
                ["name"] = VabNum.Json(p.Name),
                ["title"] = VabNum.Json(p.Title),
                ["parent_guid"] = VabNum.Json(p.ParentGuid),
                ["attach_node"] = VabNum.Json(p.AttachNode),
                ["stage_index"] = p.Guid != null && stageOf.TryGetValue(p.Guid, out int stage)
                    ? new JValue(stage)
                    : JValue.CreateNull(),
                ["symmetry_group"] = p.SymmetryGroup.HasValue ? new JValue(p.SymmetryGroup.Value) : JValue.CreateNull(),
                ["mass_t"] = VabNum.Round(VabNum.Finite(p.DryMassT) + resourceMass, MassDigits),
                ["dry_mass_t"] = VabNum.Round(p.DryMassT, MassDigits),
                ["cost"] = VabNum.Round(p.Cost, CostDigits),
                ["resources"] = resources,
                ["modules"] = new JArray(p.Modules),
            };
        }

        static JArray Totals(List<VabPart> parts)
        {
            var order = new List<VabResource>();
            var byName = new Dictionary<string, VabResource>();
            foreach (VabPart p in parts)
                foreach (VabResource r in p.Resources)
                {
                    string key = r.Name ?? "?";
                    if (!byName.TryGetValue(key, out VabResource total))
                    {
                        total = new VabResource { Name = key };
                        byName[key] = total;
                        order.Add(total);
                    }
                    total.Amount += VabNum.Finite(r.Amount);
                    total.Capacity += VabNum.Finite(r.Capacity);
                    total.MassT += VabNum.Finite(r.MassT);
                }
            var list = new JArray();
            foreach (VabResource t in order)
                list.Add(ResourceJson(t));
            return list;
        }
    }
}
