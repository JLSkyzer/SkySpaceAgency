using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public enum VabImportFormat { ModExport, RawAssembly, WorkspaceSnapshot }

    public class VabImportFile
    {
        public VabImportFormat Format;
        public JObject Root;                      // the whole file
        public JObject Assembly;                  // the assembly to load as main; null for a workspace file
        public List<JObject> Assemblies = new();  // every assembly to check (nodes of Root, also for a string game_assembly)
    }

    public class VabImportResult
    {
        public VabImportFile File;
        public string Error;
        public bool Ok => Error == null;
    }

    public class VabMissingPart
    {
        public string Name;
        public bool Known;   // a part of the game, not offered by this VAB; false = unknown name
    }

    // Everything the import decides before the game is called (spec, "Import" and choix 2).
    public static class VabImport
    {
        public const int MaxErrors = 10;

        public static VabImportResult Parse(string text)
        {
            JToken token;
            try { token = VabJson.Parse(text); }
            catch (JsonException e) { return Fail("Not valid JSON: " + e.Message); }

            if (!(token is JObject root))
                return Fail("Not a JSON object.");

            string format = VabJson.Str(root["format"]);
            if (format == VabExport.Format)
            {
                JToken embedded = root["game_assembly"];
                bool fromString = embedded != null && embedded.Type == JTokenType.String;
                if (fromString)
                {
                    try { embedded = VabJson.Parse((string)embedded); }
                    catch (JsonException e) { return Fail("game_assembly is not valid JSON: " + e.Message); }
                }
                if (!(embedded is JObject assembly) || !(assembly["parts"] is JArray))
                    return Fail("This export has no game_assembly with a \"parts\" list.");
                // A string form is replaced by its parsed tree (which has no parent yet, so it is attached, not
                // cloned): Root and Assembly stay one. An embedded object already is Root's own node.
                if (fromString)
                    root["game_assembly"] = assembly;
                return Done(VabImportFormat.ModExport, root, assembly, new List<JObject> { assembly });
            }
            if (format == VabDump.Format)
                return Fail("This is a parts dump, not a rocket: import an export or a game assembly.");

            if (root["Assemblies"] is JArray entries)
            {
                var assemblies = new List<JObject>();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!(entries[i] is JObject entry) || !(entry["Assembly"] is JObject a) || !(a["parts"] is JArray))
                        return Fail($"Workspace entry #{i + 1} has no Assembly with a \"parts\" list.");
                    assemblies.Add(a);
                }
                if (assemblies.Count == 0)
                    return Fail("This workspace file has no assembly.");
                return Done(VabImportFormat.WorkspaceSnapshot, root, null, assemblies);
            }

            if (root["parts"] is JArray)
                return Done(VabImportFormat.RawAssembly, root, root, new List<JObject> { root });

            return Fail("Unrecognized JSON: expected a SkySpaceAgency export, a game assembly (with \"parts\") " +
                        "or a VAB workspace file (with \"Assemblies\").");
        }

        static VabImportResult Fail(string error) => new VabImportResult { Error = error };

        static VabImportResult Done(VabImportFormat format, JObject root, JObject assembly, List<JObject> assemblies) =>
            new VabImportResult
            {
                File = new VabImportFile { Format = format, Root = root, Assembly = assembly, Assemblies = assemblies },
            };

        // Problems the game's loader would trip on: names, GUIDs, root, node pairs. Empty when fine.
        public static List<string> CheckStructure(JObject assembly)
        {
            var errors = new List<string>();
            if (!(assembly?["parts"] is JArray parts))
            {
                errors.Add("The assembly has no \"parts\" list.");
                return errors;
            }
            if (parts.Count == 0)
            {
                errors.Add("The assembly has no part.");
                return errors;
            }

            var guids = new HashSet<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (!(parts[i] is JObject part))
                {
                    errors.Add($"Part #{i + 1} is not a JSON object.");
                    continue;
                }
                string name = VabJson.Str(part["partName"]);
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add($"Part #{i + 1} has no partName.");
                string guid = VabJson.GuidOf(part["PartGuid"]);
                if (guid == null || guid == VabJson.ZeroGuid)
                    errors.Add($"Part #{i + 1} ({name ?? "?"}) has no valid PartGuid.");
                else if (!guids.Add(guid))
                    errors.Add($"PartGuid {guid} is used by more than one part.");
            }

            // Like the game's IsRootResolvable: with a saved configuration, its root must be a part.
            if (assembly["AssemblyOABConfig"] is JObject config)
            {
                string rootGuid = VabJson.GuidOf(config["RootGuid"]);
                if (rootGuid == null || !guids.Contains(rootGuid))
                    errors.Add($"AssemblyOABConfig.RootGuid ({rootGuid ?? "missing"}) is not one of the parts.");
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (!(parts[i] is JObject part) || !(part["partState"] is JObject state) ||
                    !(state["attachNodeStates"] is JArray nodes))
                    continue;
                string name = VabJson.Str(part["partName"]) ?? "?";
                foreach (JToken n in nodes)
                {
                    if (!(n is JObject node))
                        continue;
                    string other = VabJson.GuidOf(node["AttachedPartGuid"]);
                    if (other == null || other == VabJson.ZeroGuid || guids.Contains(other))
                        continue;
                    errors.Add($"Part #{i + 1} ({name}) node \"{VabJson.Str(node["nodeId"]) ?? "?"}\" points to part " +
                               $"{other}, which is not in the assembly.");
                }
            }

            if (errors.Count > MaxErrors)
            {
                int extra = errors.Count - MaxErrors;
                errors = errors.Take(MaxErrors).ToList();
                errors.Add($"... and {extra} more problem(s).");
            }
            return errors;
        }

        // Distinct part names, in order of first appearance.
        public static List<string> PartNames(IEnumerable<JObject> assemblies)
        {
            var seen = new HashSet<string>();
            var names = new List<string>();
            foreach (JObject assembly in assemblies)
            {
                if (!(assembly?["parts"] is JArray parts))
                    continue;
                foreach (JToken p in parts)
                {
                    string name = p is JObject part ? VabJson.Str(part["partName"]) : null;
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                        names.Add(name);
                }
            }
            return names;
        }

        // Names not offered by this VAB. Known = the game has the part (locked in this campaign,
        // or not for this builder); unknown = no such part.
        public static List<VabMissingPart> MissingParts(IEnumerable<string> names, ICollection<string> available,
                                                        ICollection<string> known)
        {
            var missing = new List<VabMissingPart>();
            var seen = new HashSet<string>();
            foreach (string name in names)
            {
                if (name == null || !seen.Add(name) || available.Contains(name))
                    continue;
                missing.Add(new VabMissingPart { Name = name, Known = known != null && known.Contains(name) });
            }
            return missing;
        }

        public static string DescribeMissing(IList<VabMissingPart> missing) =>
            $"Refused: {missing.Count} part(s) not available in this VAB: " +
            string.Join(", ", missing.Select(m => m.Name + (m.Known ? " (not unlocked)" : " (unknown part)"))) +
            ". Your rocket was not changed.";

        // Spec, choix 2: lowers, in place, every resource amount above its capacity, and every
        // capacity above the part definition's (deviation 3). The capacity is checked against the
        // definition only for parts without variant or property overrides, whose definition
        // capacity may not apply. Returns one line per correction, for the log and the status line.
        public static List<string> ClampResources(JObject assembly, Func<string, string, double?> definitionCapacity)
        {
            var corrections = new List<string>();
            if (!(assembly?["parts"] is JArray parts))
                return corrections;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!(parts[i] is JObject part) || !(part["partState"] is JObject state) ||
                    !(state["resources"] is JObject resources))
                    continue;
                string partName = VabJson.Str(part["partName"]) ?? "?";
                bool overridden = HasContent(part["PartSwitchOverrides"]) || HasContent(state["partPropertyOverrides"]);
                foreach (JProperty property in resources.Properties())
                {
                    if (!(property.Value is JObject resource))
                        continue;
                    string resourceName = VabJson.Str(resource["name"]) ?? property.Name;
                    string where = $"{partName} (part #{i + 1}) {resourceName}";
                    double? stored = VabJson.Num(resource["storedUnits"]);
                    double? capacity = VabJson.Num(resource["capacityUnits"]);
                    double? definition = overridden || definitionCapacity == null
                        ? null
                        : definitionCapacity(partName, resourceName);

                    if (definition.HasValue && capacity.HasValue && capacity.Value > definition.Value + Tolerance(definition.Value))
                    {
                        corrections.Add($"{where}: capacity {VabNum.Text(capacity.Value)} -> {VabNum.Text(definition.Value)} (part definition)");
                        resource["capacityUnits"] = definition.Value;
                        capacity = definition;
                    }

                    double? limit = capacity ?? definition;
                    if (stored.HasValue && limit.HasValue && stored.Value > limit.Value + Tolerance(limit.Value))
                    {
                        corrections.Add($"{where}: amount {VabNum.Text(stored.Value)} -> {VabNum.Text(limit.Value)} (capacity)");
                        resource["storedUnits"] = limit.Value;
                    }
                    else if (stored.HasValue && stored.Value < 0)
                    {
                        corrections.Add($"{where}: amount {VabNum.Text(stored.Value)} -> 0");
                        resource["storedUnits"] = 0.0;
                    }
                }
            }
            return corrections;
        }

        // Float noise between the file (single precision) and the definition (double) is not a correction.
        static double Tolerance(double value) => 1e-6 * Math.Max(1.0, Math.Abs(value));

        static bool HasContent(JToken token) =>
            token != null && token.Type != JTokenType.Null && !(token is JContainer && !token.HasValues);
    }
}
