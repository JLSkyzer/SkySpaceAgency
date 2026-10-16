using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    // A VAB workspace snapshot (OABHistoricalSnapshot as JSON): {"Assemblies": [{"Assembly": ..., "isMainAssembly": ...,
    // "position": ..., "boundsSize": ...}], ...}.
    public static class VabSnapshot
    {
        public static JObject MainEntry(JObject snapshot)
        {
            if (!(snapshot?["Assemblies"] is JArray list))
                return null;
            foreach (JToken e in list)
                if (e is JObject entry && entry["isMainAssembly"]?.Type == JTokenType.Boolean && (bool)entry["isMainAssembly"])
                    return entry;
            return null;
        }

        // Puts a copy of assembly in place of the main entry's, keeping its placement and the other
        // (detached) assemblies. False, and nothing changed, when the snapshot has no main entry.
        public static bool ReplaceMain(JObject snapshot, JObject assembly)
        {
            JObject entry = MainEntry(snapshot);
            if (entry == null || assembly == null)
                return false;
            entry["Assembly"] = assembly.DeepClone();
            if (entry["assembly"]?.Type == JTokenType.String)
                entry["assembly"] = "";   // obsolete string form: never let it shadow the new assembly
            return true;
        }

        public static int PartCount(JObject assembly) => assembly?["parts"] is JArray parts ? parts.Count : 0;

        public static int MainPartCount(JObject snapshot) =>
            MainEntry(snapshot)?["Assembly"] is JObject assembly ? PartCount(assembly) : 0;

        public static int TotalPartCount(JObject snapshot)
        {
            int total = 0;
            if (snapshot?["Assemblies"] is JArray list)
                foreach (JToken e in list)
                    if (e is JObject entry && entry["Assembly"] is JObject assembly)
                        total += PartCount(assembly);
            return total;
        }
    }
}
