using Newtonsoft.Json.Linq;

// SerializedAssembly-shaped JSON trees with the fields the VAB tools read. Shape taken from a real VAB
// workspace file of 2026-10-16 ("fusée pour orbite V2.json"); GUIDs from that file.
public static class VabFixtures
{
    public const string PodGuid = "8dda8ca2-e636-41fa-bb90-0ddf0c1c25dd";
    public const string TankGuid = "8f48c048-5c1e-4bf3-90c9-a81874d16a19";
    public const string EngineGuid = "caf67144-78bd-4747-ab17-362337921716";
    public const string DetachedGuid = "a70f45c8-ae64-4d3a-8ee5-3e923895e4e7";
    public const string NewGuid = "76806f3c-8248-4e80-adae-3d9341fd588e";
    public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    public const string Pod = "pod_2v_lander_crew";
    public const string Tank = "fueltank_2v_inline_methalox_1x2";
    public const string Engine = "engine_2v_methalox_poodle";

    public static JObject Id(string guid) => new JObject { ["Guid"] = guid, ["DebugName"] = JValue.CreateNull() };

    public static JObject Node(string nodeId, string otherGuid, string otherNode) => new JObject
    {
        ["nodeId"] = nodeId,
        ["IsDynamic"] = false,
        ["attachedPartGuid"] = JValue.CreateNull(),
        ["AttachedPartGuid"] = Id(otherGuid ?? ZeroGuid),
        ["attachedPartNodeID"] = otherNode ?? "",
    };

    public static JObject Resource(string name, double stored, double capacity) => new JObject
    {
        ["name"] = name, ["storedUnits"] = stored, ["capacityUnits"] = capacity,
    };

    public static JObject Part(string guid, string name, JArray nodes, JObject resources = null) => new JObject
    {
        ["partGuid"] = JValue.CreateNull(),
        ["PartGuid"] = Id(guid),
        ["partName"] = name,
        ["partState"] = new JObject
        {
            ["partStatus"] = "NONE",
            ["attachNodeStates"] = nodes ?? new JArray(),
            ["resources"] = (JToken)resources ?? JValue.CreateNull(),
            ["StageIndex"] = 0,
            ["Temperature"] = "-Infinity",
            ["partPropertyOverrides"] = JValue.CreateNull(),
        },
        ["PartModulesState"] = new JArray(),
        ["PartSwitchOverrides"] = JValue.CreateNull(),
    };

    // pod (root) - top/bottom - tank - bottom/top - engine, as in the real file.
    public static JObject Rocket()
    {
        JObject pod = Part(PodGuid, Pod, new JArray(Node("bottom", TankGuid, "top")));
        JObject tank = Part(TankGuid, Tank,
            new JArray(Node("top", PodGuid, "bottom"), Node("bottom", EngineGuid, "top"), Node("srfAttach", null, null)),
            new JObject { ["Methane"] = Resource("Methane", 1.6, 1.6), ["Oxidizer"] = Resource("Oxidizer", 6.4, 6.4) });
        JObject engine = Part(EngineGuid, Engine, new JArray(Node("top", TankGuid, "bottom")));
        return new JObject
        {
            ["Version"] = "0.1",
            ["AssemblyOABConfig"] = new JObject { ["RootGuid"] = Id(PodGuid) },
            ["AssemblyDefinition"] = new JObject { ["assemblyName"] = "Bon vol" },
            ["parts"] = new JArray(pod, tank, engine),
        };
    }

    public static JObject SinglePart(string guid, string name) => new JObject
    {
        ["AssemblyOABConfig"] = new JObject { ["RootGuid"] = Id(guid) },
        ["parts"] = new JArray(Part(guid, name, null)),
    };

    // An OABHistoricalSnapshot as the game writes it (workspace file, before_import backup).
    public static JObject Workspace(params (JObject assembly, bool main)[] entries)
    {
        var list = new JArray();
        foreach (var (assembly, main) in entries)
            list.Add(new JObject
            {
                ["assembly"] = "",
                ["Assembly"] = assembly,
                ["isMainAssembly"] = main,
                ["position"] = new JObject { ["x"] = 10.7356882, ["y"] = 41.5753136, ["z"] = 51.5100632 },
                ["boundsSize"] = new JObject { ["x"] = 2.78, ["y"] = 6.12, ["z"] = 2.78 },
            });
        return new JObject
        {
            ["Metadata"] = new JObject { ["VehicleName"] = "Bon vol" },
            ["ActionType"] = "None",
            ["oabOrientation"] = "VAB",
            ["Assemblies"] = list,
        };
    }
}
