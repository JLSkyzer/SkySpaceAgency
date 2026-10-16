using System;
using System.Linq;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabDumpTests
{
    static readonly DateTime At = new DateTime(2026, 10, 16, 5, 7, 9);

    static VabDumpPart Engine() => new VabDumpPart
    {
        Name = "engine_2v_methalox_poodle", Title = "Poodle", Category = "Engine", Family = "methalox",
        TechNode = "tNode_2v_methalox", Unlocked = false, Cost = 1500, MassT = 0.9, Size = "Medium",
        CrewCapacity = 0, MaxTemp = 2000, CrashTolerance = 8, AttachNodes = { "top", "bottom" },
        Engine = new VabEngineInfo
        {
            ThrustVacKN = 60.04, ThrustAslKN = double.NaN, IspVac = 350.04, IspAsl = 90.06, Propellants = { "Methalox" },
        },
        Modules = { "Data_Engine" },
        RawJson = "{\"data\":{\"partName\":\"engine_2v_methalox_poodle\"}}",
    };

    static VabDumpPart Tank(string name, bool unlocked) => new VabDumpPart
    {
        Name = name, Title = "Tank " + name, Category = "FuelTank", Unlocked = unlocked, CampaignUnlocked = unlocked,
        MassT = 0.25, Resources = { new VabResource { Name = "Methane", Capacity = 1.6 } },
        RawJson = "{\"data\":{\"partName\":\"" + name + "\"}}",
    };

    static JObject Build(VabDumpPart[] parts, JToken example = null, bool unlockedOnly = false,
                         VabCampaign campaign = null) =>
        VabDump.Build(campaign ?? new VabCampaign { Name = "Skyzer Adventure" }, At, "0.2.9", "1.3.0",
                      parts, example, unlockedOnly);

    static JObject Part(JObject dump, string name) =>
        (JObject)((JArray)dump["parts"]).First(p => (string)p["name"] == name);

    [Test]
    public void Build_SortsPartsAndCountsUnlocked()
    {
        JObject dump = Build(new[] { Tank("zeta", true), Tank("alpha", false), Tank("mid", true) });
        Assert.AreEqual("SkySpaceAgency.PartsDump", (string)dump["format"]);
        Assert.AreEqual(1, (int)dump["version"]);
        Assert.AreEqual("2026-10-16T05:07:09", (string)dump["exported_at"]);
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "zeta" },
            ((JArray)dump["parts"]).Select(p => (string)p["name"]).ToArray());
        Assert.AreEqual(3, (int)dump["part_count"]);
        Assert.AreEqual(2, (int)dump["unlocked_count"]);
        Assert.IsFalse((bool)Part(dump, "alpha")["unlocked"]);
        Assert.IsTrue((bool)Part(dump, "zeta")["unlocked"]);
    }

    [Test]
    public void Build_WritesPartFields()
    {
        JObject dump = Build(new[] { Engine(), Tank("tank", true) });
        JObject engine = Part(dump, "engine_2v_methalox_poodle");
        Assert.AreEqual("Poodle", (string)engine["title"]);
        Assert.AreEqual("Engine", (string)engine["category"]);
        Assert.AreEqual("methalox", (string)engine["family"]);
        Assert.AreEqual("tNode_2v_methalox", (string)engine["tech_node"]);
        Assert.IsFalse((bool)engine["unlocked"]);
        Assert.AreEqual(JTokenType.Null, engine["campaign_unlocked"].Type);
        Assert.AreEqual(1500.0, (double)engine["cost"], 1e-9);
        Assert.AreEqual(0.9, (double)engine["mass_t"], 1e-9);
        Assert.AreEqual("Medium", (string)engine["size"]);
        Assert.AreEqual(2000.0, (double)engine["max_temp"], 1e-9);
        CollectionAssert.AreEqual(new[] { "top", "bottom" }, ((JArray)engine["attach_nodes"]).ToObject<string[]>());
        Assert.AreEqual(60.0, (double)engine["engine"]["thrust_vac_kN"], 1e-9);
        Assert.AreEqual(JTokenType.Null, engine["engine"]["thrust_asl_kN"].Type);
        Assert.AreEqual(350.0, (double)engine["engine"]["isp_vac"], 1e-9);
        Assert.AreEqual(90.1, (double)engine["engine"]["isp_asl"], 1e-9);
        CollectionAssert.AreEqual(new[] { "Methalox" }, ((JArray)engine["engine"]["propellants"]).ToObject<string[]>());
        CollectionAssert.AreEqual(new[] { "Data_Engine" }, ((JArray)engine["modules"]).ToObject<string[]>());

        JObject tank = Part(dump, "tank");
        Assert.AreEqual(JTokenType.Null, tank["engine"].Type);          // spec: null when no engine data
        Assert.AreEqual(JTokenType.Null, tank["tech_node"].Type);
        Assert.IsTrue((bool)tank["campaign_unlocked"]);
        Assert.AreEqual("Methane", (string)tank["resources"][0]["name"]);
        Assert.AreEqual(1.6, (double)tank["resources"][0]["capacity"], 1e-9);
        Assert.IsNull(tank["resources"][0]["amount"]);                  // capacity only in a dump
    }

    [Test]
    public void Build_TemplatesAreParsedJsonOrRawText()
    {
        VabDumpPart broken = Tank("broken", true);
        broken.RawJson = "{oops";
        VabDumpPart none = Tank("none", true);
        none.RawJson = null;
        var templates = (JObject)Build(new[] { Engine(), broken, none })["part_templates"];
        Assert.AreEqual("engine_2v_methalox_poodle", (string)templates["engine_2v_methalox_poodle"]["data"]["partName"]);
        Assert.AreEqual(JTokenType.String, templates["broken"].Type);
        Assert.AreEqual("{oops", (string)templates["broken"]);
        Assert.IsNull(templates["none"]);
    }

    [Test]
    public void Build_TemplatesCanBeLimitedToUnlockedParts()
    {
        var templates = (JObject)Build(new[] { Tank("alpha", false), Tank("zeta", true) }, null, true)["part_templates"];
        CollectionAssert.AreEqual(new[] { "zeta" }, templates.Properties().Select(p => p.Name).ToArray());
    }

    // Spec, choix 1: example_assembly is the open rocket's game_assembly, or null with instructions
    // asking for a small rocket first.
    [Test]
    public void Build_ExampleAssembly()
    {
        var example = new JObject { ["parts"] = new JArray(new JObject { ["partName"] = "pod_2v_lander_crew" }) };
        JObject withExample = Build(new[] { Tank("t", true) }, example);
        Assert.IsTrue(JToken.DeepEquals(example, withExample["example_assembly"]));
        Assert.AreEqual(VabDump.Instructions, (string)withExample["instructions_for_ai"]);

        JObject without = Build(new[] { Tank("t", true) });
        Assert.AreEqual(JTokenType.Null, without["example_assembly"].Type);
        StringAssert.Contains("example_assembly is null", (string)without["instructions_for_ai"]);
        StringAssert.Contains("RootGuid", VabDump.Instructions);
        StringAssert.Contains("attachNodeStates", VabDump.Instructions);
        StringAssert.Contains("SymmetrySets", VabDump.Instructions);
    }

    [Test]
    public void Build_CampaignListsTechNodes()
    {
        var campaign = new VabCampaign
        {
            Name = "Skyzer Adventure", Mode = "exploration", UnlockAllParts = true,
            UnlockedTechNodes = { "tNode_1v_start", "tNode_2v_methalox" },
        };
        JObject c = (JObject)Build(new[] { Tank("t", true) }, null, false, campaign)["campaign"];
        Assert.AreEqual("Skyzer Adventure", (string)c["name"]);
        Assert.IsTrue((bool)c["unlock_all_parts"]);
        CollectionAssert.AreEqual(new[] { "tNode_1v_start", "tNode_2v_methalox" },
            ((JArray)c["unlocked_tech_nodes"]).ToObject<string[]>());
    }
}
