using System.Collections.Generic;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using F = VabFixtures;

public class VabImportTests
{
    static bool Any(List<string> errors, string text) => errors.Exists(e => e.Contains(text));

    // Spec test: detection of the two formats (plus the workspace file, deviation 1).
    [Test]
    public void Parse_RawAssembly()
    {
        VabImportResult r = VabImport.Parse(VabJson.Write(F.Rocket()));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(VabImportFormat.RawAssembly, r.File.Format);
        Assert.AreEqual(1, r.File.Assemblies.Count);
        Assert.AreSame(r.File.Assembly, r.File.Assemblies[0]);
        Assert.AreEqual(3, ((JArray)r.File.Assembly["parts"]).Count);
    }

    [Test]
    public void Parse_ModExport()
    {
        var export = new JObject
        {
            ["format"] = VabExport.Format, ["version"] = 1, ["summary"] = new JObject(), ["game_assembly"] = F.Rocket(),
        };
        VabImportResult r = VabImport.Parse(VabJson.Write(export));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(VabImportFormat.ModExport, r.File.Format);
        Assert.AreEqual("Bon vol", (string)r.File.Assembly["AssemblyDefinition"]["assemblyName"]);

        // An AI may hand game_assembly back as a JSON string.
        export["game_assembly"] = VabJson.WriteCompact(F.Rocket());
        r = VabImport.Parse(VabJson.Write(export));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(3, ((JArray)r.File.Assembly["parts"]).Count);
    }

    [Test]
    public void Parse_WorkspaceSnapshot()
    {
        JObject ws = F.Workspace((F.Rocket(), true), (F.SinglePart(F.DetachedGuid, "antenna_0v_16"), false));
        VabImportResult r = VabImport.Parse(VabJson.Write(ws));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(VabImportFormat.WorkspaceSnapshot, r.File.Format);
        Assert.AreEqual(2, r.File.Assemblies.Count);
        Assert.IsNull(r.File.Assembly);
        Assert.IsNotNull(r.File.Root["Assemblies"]);
    }

    // Spec test: refusal of an invalid JSON.
    [Test]
    public void Parse_RejectsInvalidJson()
    {
        StringAssert.StartsWith("Not valid JSON", VabImport.Parse("{ \"parts\": [ ").Error);
        StringAssert.StartsWith("Not valid JSON", VabImport.Parse("").Error);
        StringAssert.StartsWith("Not valid JSON", VabImport.Parse(null).Error);
    }

    [Test]
    public void Parse_RejectsWrongShapes()
    {
        StringAssert.StartsWith("Not a JSON object", VabImport.Parse("[1, 2]").Error);
        StringAssert.StartsWith("Unrecognized JSON", VabImport.Parse("{\"a\": 1}").Error);
        StringAssert.Contains("parts dump", VabImport.Parse("{\"format\": \"SkySpaceAgency.PartsDump\"}").Error);
        StringAssert.Contains("game_assembly",
            VabImport.Parse("{\"format\": \"SkySpaceAgency.VabExport\", \"game_assembly\": null}").Error);
        StringAssert.Contains("game_assembly is not valid JSON",
            VabImport.Parse("{\"format\": \"SkySpaceAgency.VabExport\", \"game_assembly\": \"{oops\"}").Error);
        StringAssert.StartsWith("Workspace entry #1", VabImport.Parse("{\"Assemblies\": [ {\"isMainAssembly\": true} ]}").Error);
        StringAssert.Contains("no assembly", VabImport.Parse("{\"Assemblies\": []}").Error);
    }

    [Test]
    public void CheckStructure_AcceptsAConsistentAssembly()
    {
        CollectionAssert.IsEmpty(VabImport.CheckStructure(F.Rocket()));
        JObject noConfig = F.Rocket();
        noConfig.Remove("AssemblyOABConfig");   // the game takes the root from the parts then
        CollectionAssert.IsEmpty(VabImport.CheckStructure(noConfig));
    }

    [Test]
    public void CheckStructure_ReportsBrokenLinks()
    {
        JObject duplicate = F.Rocket();
        duplicate["parts"][2]["PartGuid"]["Guid"] = F.PodGuid;
        List<string> errors = VabImport.CheckStructure(duplicate);
        Assert.IsTrue(Any(errors, $"PartGuid {F.PodGuid} is used by more than one part."), string.Join("\n", errors));

        JObject badRoot = F.Rocket();
        badRoot["AssemblyOABConfig"]["RootGuid"]["Guid"] = F.NewGuid;
        errors = VabImport.CheckStructure(badRoot);
        Assert.IsTrue(Any(errors, $"AssemblyOABConfig.RootGuid ({F.NewGuid}) is not one of the parts."), string.Join("\n", errors));

        JObject dangling = F.Rocket();
        ((JArray)dangling["parts"]).RemoveAt(2);  // engine removed, the tank's bottom node still points to it
        errors = VabImport.CheckStructure(dangling);
        CollectionAssert.AreEqual(new[]
        {
            $"Part #2 ({F.Tank}) node \"bottom\" points to part {F.EngineGuid}, which is not in the assembly.",
        }, errors);
    }

    [Test]
    public void CheckStructure_ReportsMissingFields()
    {
        CollectionAssert.AreEqual(new[] { "The assembly has no \"parts\" list." }, VabImport.CheckStructure(new JObject()));
        CollectionAssert.AreEqual(new[] { "The assembly has no \"parts\" list." }, VabImport.CheckStructure(null));
        CollectionAssert.AreEqual(new[] { "The assembly has no part." },
            VabImport.CheckStructure(new JObject { ["parts"] = new JArray() }));

        JObject noName = F.Rocket();
        noName["parts"][1]["partName"] = "";
        Assert.IsTrue(Any(VabImport.CheckStructure(noName), "Part #2 has no partName."));

        JObject badGuid = F.Rocket();
        badGuid["parts"][2]["PartGuid"]["Guid"] = "not-a-guid";
        Assert.IsTrue(Any(VabImport.CheckStructure(badGuid), $"Part #3 ({F.Engine}) has no valid PartGuid."));
    }

    [Test]
    public void CheckStructure_CapsTheErrorList()
    {
        var parts = new JArray();
        for (int i = 0; i < 15; i++)
            parts.Add(F.Part(F.PodGuid, "x", null));   // 14 duplicates
        var assembly = new JObject { ["AssemblyOABConfig"] = new JObject { ["RootGuid"] = F.Id(F.PodGuid) }, ["parts"] = parts };
        List<string> errors = VabImport.CheckStructure(assembly);
        Assert.AreEqual(VabImport.MaxErrors + 1, errors.Count);
        StringAssert.StartsWith("... and 4 more", errors[errors.Count - 1]);
    }

    [Test]
    public void PartNames_AreDistinctInOrder()
    {
        CollectionAssert.AreEqual(new[] { F.Pod, F.Tank, F.Engine },
            VabImport.PartNames(new[] { F.Rocket(), F.SinglePart(F.DetachedGuid, F.Pod) }));
    }

    // Spec test: list of the missing parts (name + unlocked or not).
    [Test]
    public void MissingParts_ClassifiesLockedAndUnknown()
    {
        List<VabMissingPart> missing = VabImport.MissingParts(
            new[] { F.Pod, F.Tank, F.Engine, "mystery_part" },
            new HashSet<string> { F.Pod, F.Tank },
            new HashSet<string> { F.Pod, F.Tank, F.Engine });
        Assert.AreEqual(2, missing.Count);
        Assert.AreEqual(F.Engine, missing[0].Name);
        Assert.IsTrue(missing[0].Known);
        Assert.AreEqual("mystery_part", missing[1].Name);
        Assert.IsFalse(missing[1].Known);
        Assert.AreEqual(
            "Refused: 2 part(s) not available in this VAB: engine_2v_methalox_poodle (not unlocked), " +
            "mystery_part (unknown part). Your rocket was not changed.",
            VabImport.DescribeMissing(missing));
    }

    [Test]
    public void MissingParts_NoneWhenAllAvailable()
    {
        CollectionAssert.IsEmpty(VabImport.MissingParts(
            new[] { F.Pod, F.Pod }, new HashSet<string> { F.Pod }, new HashSet<string>()));
    }
}
