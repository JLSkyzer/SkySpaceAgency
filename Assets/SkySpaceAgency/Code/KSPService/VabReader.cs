using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.VabTools;
using KSP.Game;
using KSP.Modules;
using KSP.OAB;
using KSP.Sim.Definitions;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using KTools;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace K2D2.KSPService
{
    // Reads the open VAB into the plain structures of K2D2.VabTools. Thin on purpose: rounding,
    // aggregation and every check live in the pure classes, tested in the editor.
    public static class VabReader
    {
        public static bool InVab()
        {
            var game = GeneralTools.Game;
            if (game == null)
                return false;
            // Same access as K2D2_Plugin.ValidScene.
            var gameState = game.GlobalGameState?.GetGameState();
            return gameState != null && gameState.GameState == GameState.VehicleAssemblyBuilder;
        }

        public static ObjectAssemblyBuilder Builder()
        {
            ObjectAssemblyBuilder builder = GeneralTools.Game?.OAB?.Current;
            return builder != null && builder.IsLoaded && !builder.IsShuttingDown ? builder : null;
        }

        public static IObjectAssembly MainAssembly(ObjectAssemblyBuilder builder)
        {
            if (builder == null)
                return null;
            OABSessionInformation stats = builder.Stats;
            return stats != null && stats.HasMainAssembly ? stats.MainAssembly : null;
        }

        public static string VesselName(ObjectAssemblyBuilder builder)
        {
            try { return builder.Stats.GetCurrentWorkspaceVehicleDisplayName; }
            catch (Exception) { return null; }
        }

        // The game's own JSON of the assembly (SerializedAssembly), parsed losslessly.
        public static JToken GameAssembly(ObjectAssemblyBuilder builder, IObjectAssembly main) =>
            VabJson.Parse(builder.UtilFileIO.AssemblyToString(main));

        public static VabVessel ReadVessel(ObjectAssemblyBuilder builder, IObjectAssembly main)
        {
            var vessel = new VabVessel
            {
                Name = VesselName(builder),
                MassWetT = main.GetWetMass(),
                MassDryT = main.GetDryMass(),
            };
            VesselDeltaVComponent dv = main.VesselDeltaV;
            if (dv != null)
            {
                vessel.DeltaVReady = dv.IsReady;
                vessel.DeltaVTotalVac = dv.TotalDeltaVVac;
                vessel.DeltaVTotalActual = dv.TotalDeltaVActual;
                if (dv.StageInfo != null)
                    foreach (DeltaVStageInfo s in dv.StageInfo)
                        if (s != null)
                            vessel.Stages.Add(ReadStage(s));
            }
            GameInstance game = GeneralTools.Game;
            foreach (IObjectAssemblyPart part in main.Parts)
                if (part != null)
                    vessel.Parts.Add(ReadPart(game, part));
            return vessel;
        }

        static string Id(IGGuid id) => id.Guid.ToString("D");

        static VabStage ReadStage(DeltaVStageInfo s)
        {
            var stage = new VabStage
            {
                Stage = s.Stage,
                StartMassT = s.StartMass,
                EndMassT = s.EndMass,
                DryMassT = s.DryMass,
                FuelMassT = s.FuelMass,
                IspVac = s.IspVac,
                IspAsl = s.IspASL,
                ThrustVacKN = s.ThrustVac,
                ThrustAslKN = s.ThrustASL,
                TwrVac = s.TWRVac,
                TwrAsl = s.TWRASL,
                TwrActual = s.TWRActual,
                DeltaVVac = s.DeltaVinVac,
                DeltaVAsl = s.DeltaVatASL,
                DeltaVActual = s.DeltaVActual,
                BurnTimeS = s.StageBurnTime,
            };
            var situation = s.DeltaVSituation;   // KSP.Sim.State.DeltaVSituation (struct)
            stage.SituationBody = situation.CelestialBody;
            stage.SituationAtmosphere = situation.IsAtmosphere;
            stage.SituationAltitudeM = situation.Altitude;
            if (s.Parts != null)
                foreach (DeltaVPartInfo p in s.Parts)
                    if (p?.PartRef != null)
                        stage.PartGuids.Add(Id(p.PartRef.GlobalId));
            if (s.EnginesActiveInStage != null)
                foreach (DeltaVEngineInfo e in s.EnginesActiveInStage)
                    if (e?.Part != null)
                        stage.EngineGuids.Add(Id(e.Part.GlobalId));
            return stage;
        }

        static VabPart ReadPart(GameInstance game, IObjectAssemblyPart part)
        {
            IObjectAssemblyAvailablePart available = part.AvailablePart;
            IObjectAssemblyPart parent = part.ParentPart;
            var vp = new VabPart
            {
                Guid = Id(part.UniqueId),
                Name = available?.Name,
                Title = available?.Title,
                ParentGuid = parent != null ? Id(parent.UniqueId) : null,
                AttachNode = parent != null ? part.MyNodeConnectedToParent?.NodeTag : null,
                SymmetryGroup = part.SymmetrySet?.ID,
                DryMassT = available?.Mass ?? 0f,
                Cost = available?.PartData?.cost ?? 0,
            };
            if (part.Resources != null)
                foreach (IObjectAssemblyResource r in part.Resources)
                    if (r != null)
                        vp.Resources.Add(new VabResource { Name = r.Name, Amount = r.Count, Capacity = r.Capacity, MassT = r.TotalMass });
            vp.Modules.AddRange(ModuleNames(game, vp.Name));
            return vp;
        }

        static IEnumerable<string> ModuleNames(GameInstance game, string partName)
        {
            if (partName == null || game?.Parts == null || !game.Parts.TryGet(partName, out PartCore core) ||
                core?.modules == null)
                return Enumerable.Empty<string>();
            return core.modules.Where(m => m != null).Select(m => m.GetType().Name).ToList();
        }

        public static HashSet<string> KnownPartNames(ObjectAssemblyBuilder builder)
        {
            var known = new HashSet<string>();
            if (builder.Stats.AllKnownParts != null)
                foreach (IObjectAssemblyAvailablePart p in builder.Stats.AllKnownParts)
                    if (p?.Name != null)
                        known.Add(p.Name);
            GameInstance game = GeneralTools.Game;
            if (game?.Parts?.PartData != null)
                foreach (string name in game.Parts.PartData.Keys)
                    known.Add(name);
            return known;
        }

        // Capacity of each resource container in the part definitions known to the VAB.
        public static Func<string, string, double?> CapacityLookup(ObjectAssemblyBuilder builder)
        {
            var map = new Dictionary<string, Dictionary<string, double>>();
            if (builder.Stats.AllKnownParts != null)
                foreach (IObjectAssemblyAvailablePart p in builder.Stats.AllKnownParts)
                {
                    PartData data = p?.PartData;
                    if (p?.Name == null || data?.resourceContainers == null)
                        continue;
                    var inner = new Dictionary<string, double>();
                    foreach (var container in data.resourceContainers)
                        if (container?.name != null)
                            inner[container.name] = container.capacityUnits;
                    map[p.Name] = inner;
                }
            return (part, resource) =>
                part != null && resource != null && map.TryGetValue(part, out var inner) &&
                inner.TryGetValue(resource, out double capacity)
                    ? capacity
                    : (double?)null;
        }

        public static List<VabDumpPart> ReadAllParts(ObjectAssemblyBuilder builder)
        {
            GameInstance game = GeneralTools.Game;
            var available = new HashSet<string>(builder.Stats.AvailableParts.Where(p => p?.Name != null).Select(p => p.Name));
            IReadOnlyDictionary<string, string> techOf = null;
            try { techOf = game.ScienceManager?.TechNodeDataStore?.PartIDLookup; }
            catch (Exception) { }

            var list = new List<VabDumpPart>();
            foreach (PartCore core in game.Parts.AllParts())
            {
                PartData d = core?.data;
                if (d?.partName == null)
                    continue;
                string name = d.partName;
                var part = new VabDumpPart
                {
                    Name = name,
                    Title = d.Title,
                    Category = d.category.ToString(),
                    Family = d.family,
                    Size = d.sizeCategory.ToString(),
                    Unlocked = available.Contains(name),
                    CampaignUnlocked = CampaignUnlocked(game, name),
                    Cost = d.cost,
                    MassT = d.mass,
                    CrewCapacity = d.crewCapacity,
                    MaxTemp = d.maxTemp,
                    CrashTolerance = d.crashTolerance,
                    RawJson = game.Parts.GetRawJson(name),
                };
                if (techOf != null && techOf.TryGetValue(name, out string node))
                    part.TechNode = node;
                if (d.attachNodes != null)
                    foreach (AttachNodeDefinition n in d.attachNodes)
                        part.AttachNodes.Add(n.nodeID);
                if (d.resourceContainers != null)
                    foreach (var container in d.resourceContainers)
                        if (container != null)
                            part.Resources.Add(new VabResource { Name = container.name, Capacity = container.capacityUnits });
                if (core.modules != null)
                {
                    foreach (ModuleData m in core.modules)
                        if (m != null)
                            part.Modules.Add(m.GetType().Name);
                    part.Engine = ReadEngine(core.modules.OfType<Data_Engine>().FirstOrDefault());
                }
                list.Add(part);
            }
            return list;
        }

        static bool? CampaignUnlocked(GameInstance game, string name)
        {
            try { return game.SessionManager?.IsPartUnlocked(name); }
            catch (Exception) { return null; }
        }

        // Definition values of the current engine mode. Isp = atmosphereCurve at 0 and 1 atm (not
        // verified to be seconds, see the plan); ASL thrust derived as maxThrust * IspAsl / IspVac.
        static VabEngineInfo ReadEngine(Data_Engine engine)
        {
            if (engine?.engineModes == null || engine.engineModes.Length == 0)
                return null;
            int index = Mathf.Clamp(engine.currentEngineModeIndex, 0, engine.engineModes.Length - 1);
            var mode = engine.engineModes[index];
            if (mode == null)
                return null;
            var info = new VabEngineInfo { ThrustVacKN = mode.maxThrust, IspVac = double.NaN, IspAsl = double.NaN };
            if (mode.atmosphereCurve != null)
            {
                info.IspVac = mode.atmosphereCurve.Evaluate(0f);
                info.IspAsl = mode.atmosphereCurve.Evaluate(1f);
            }
            info.ThrustAslKN = info.IspVac > 0 ? mode.maxThrust * info.IspAsl / info.IspVac : double.NaN;
            string mixture = mode.propellant?.mixtureName;
            if (!string.IsNullOrEmpty(mixture))
                info.Propellants.Add(mixture);
            return info;
        }

        public static VabCampaign ReadCampaign()
        {
            GameInstance game = GeneralTools.Game;
            var campaign = new VabCampaign();
            try { campaign.Name = game?.SessionManager?.ActiveCampaignName; } catch (Exception) { }
            try { campaign.Mode = GameManager.Instance?.GameModeManager?.GetCurrentGameModeId(); } catch (Exception) { }
            try { campaign.UnlockAllParts = game != null && game.CheatSystem != null && game.CheatSystem.GetUnlockAllParts(); } catch (Exception) { }
            try
            {
                HashSet<string> nodes = game?.SessionManager?.GetMyAgencyUnlockedTechNodes();
                if (nodes != null)
                    campaign.UnlockedTechNodes = nodes.OrderBy(n => n, StringComparer.Ordinal).ToList();
            }
            catch (Exception) { }
            return campaign;
        }

        public static VabExportMeta ExportMeta(DateTime now) => new VabExportMeta
        {
            ExportedAt = now,
            // Not VersionID.VERSION_TEXT: a const, inlined into this DLL at build time.
            GameVersion = Application.version,
            ModVersion = ModVersion(),
            Campaign = ReadCampaign(),
        };

        static string ModVersion()
        {
            try { return K2D2_Plugin.Instance.SWMetadata.SWInfo.Version; }
            catch (Exception) { return null; }
        }
    }
}
