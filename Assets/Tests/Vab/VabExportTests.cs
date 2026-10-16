using System;
using System.Linq;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabExportTests
{
    static VabExportMeta Meta() => new VabExportMeta
    {
        ExportedAt = new DateTime(2026, 10, 16, 5, 7, 9),
        GameVersion = "0.2.9.0.104880",
        ModVersion = "1.3.0",
        Campaign = new VabCampaign { Name = "Skyzer Adventure", Mode = "exploration", UnlockAllParts = false },
    };

    [Test]
    public void Build_HasFormatVersionAndMetadata()
    {
        JObject doc = VabExport.Build(Meta(), new JObject { ["part_count"] = 3 }, new JObject { ["parts"] = new JArray() });
        CollectionAssert.AreEqual(
            new[] { "format", "version", "exported_at", "game_version", "mod_version", "campaign",
                    "instructions_for_ai", "summary", "game_assembly" },
            doc.Properties().Select(p => p.Name).ToArray());
        Assert.AreEqual("SkySpaceAgency.VabExport", (string)doc["format"]);
        Assert.AreEqual(1, (int)doc["version"]);
        Assert.AreEqual("2026-10-16T05:07:09", (string)doc["exported_at"]);
        Assert.AreEqual("0.2.9.0.104880", (string)doc["game_version"]);
        Assert.AreEqual("1.3.0", (string)doc["mod_version"]);
        Assert.AreEqual("Skyzer Adventure", (string)doc["campaign"]["name"]);
        Assert.AreEqual("exploration", (string)doc["campaign"]["mode"]);
        Assert.IsFalse((bool)doc["campaign"]["unlock_all_parts"]);
        Assert.IsNull(doc["campaign"]["unlocked_tech_nodes"]); // only in dumps
        Assert.AreEqual(3, (int)doc["summary"]["part_count"]);
    }

    [Test]
    public void Build_EmbedsTheGameAssemblyAsACopy()
    {
        var assembly = new JObject { ["parts"] = new JArray(new JObject { ["partName"] = "pod_2v_lander_crew" }) };
        JObject doc = VabExport.Build(Meta(), new JObject(), assembly);
        Assert.IsTrue(JToken.DeepEquals(assembly, doc["game_assembly"]));
        assembly["parts"][0]["partName"] = "changed";
        Assert.AreEqual("pod_2v_lander_crew", (string)doc["game_assembly"]["parts"][0]["partName"]);
        Assert.AreEqual(JTokenType.Null, VabExport.Build(Meta(), null, null)["game_assembly"].Type);
    }

    // Spec: the instructions say what each section is, that game_assembly is what the import reads,
    // to keep GUIDs and node pairs consistent, and to use only parts marked unlocked in a dump.
    [Test]
    public void Build_InstructionsStateTheImportRules()
    {
        string text = (string)VabExport.Build(Meta(), new JObject(), new JObject())["instructions_for_ai"];
        StringAssert.Contains("summary", text);
        StringAssert.Contains("game_assembly", text);
        StringAssert.Contains("PartGuid", text);
        StringAssert.Contains("attachNodeStates", text);
        StringAssert.Contains("RootGuid", text);
        StringAssert.Contains("\"unlocked\": true", text);
        StringAssert.Contains("capacity", text);
    }
}
