using System.Collections.Generic;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using F = VabFixtures;

// Spec, choix 2: an amount above the part's capacity is lowered to the capacity and reported.
public class VabClampTests
{
    static JObject Res(JObject rocket, string name) => (JObject)rocket["parts"][1]["partState"]["resources"][name];

    static double? Definition(string part, string resource) =>
        part == F.Tank ? resource == "Methane" ? 1.6 : resource == "Oxidizer" ? 6.4 : (double?)null : null;

    [Test]
    public void Clamp_LowersAmountAboveCapacity()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Methane")["storedUnits"] = 5.0;
        List<string> corrections = VabImport.ClampResources(rocket, null);
        CollectionAssert.AreEqual(new[] { $"{F.Tank} (part #2) Methane: amount 5 -> 1.6 (capacity)" }, corrections);
        Assert.AreEqual(1.6, (double)Res(rocket, "Methane")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_LowersCapacityAboveTheDefinition()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Oxidizer")["storedUnits"] = 100.0;
        Res(rocket, "Oxidizer")["capacityUnits"] = 100.0;
        List<string> corrections = VabImport.ClampResources(rocket, Definition);
        CollectionAssert.AreEqual(new[]
        {
            $"{F.Tank} (part #2) Oxidizer: capacity 100 -> 6.4 (part definition)",
            $"{F.Tank} (part #2) Oxidizer: amount 100 -> 6.4 (capacity)",
        }, corrections);
        Assert.AreEqual(6.4, (double)Res(rocket, "Oxidizer")["capacityUnits"], 1e-12);
        Assert.AreEqual(6.4, (double)Res(rocket, "Oxidizer")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_SkipsTheDefinitionForVariantParts()
    {
        JObject rocket = F.Rocket();
        rocket["parts"][1]["PartSwitchOverrides"] = new JObject { ["variant"] = "long" };
        Res(rocket, "Oxidizer")["storedUnits"] = 100.0;
        Res(rocket, "Oxidizer")["capacityUnits"] = 100.0;
        CollectionAssert.IsEmpty(VabImport.ClampResources(rocket, Definition));
        Assert.AreEqual(100.0, (double)Res(rocket, "Oxidizer")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_FixesNegativeAmountsAndLeavesValidOnes()
    {
        JObject rocket = F.Rocket();
        JToken before = rocket.DeepClone();
        CollectionAssert.IsEmpty(VabImport.ClampResources(rocket, Definition));
        Assert.IsTrue(JToken.DeepEquals(before, rocket));

        Res(rocket, "Methane")["storedUnits"] = -1.0;
        CollectionAssert.AreEqual(new[] { $"{F.Tank} (part #2) Methane: amount -1 -> 0" },
            VabImport.ClampResources(rocket, Definition));
        Assert.AreEqual(0.0, (double)Res(rocket, "Methane")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_UsesTheDefinitionWhenTheFileHasNoCapacity()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Methane").Remove("capacityUnits");
        Res(rocket, "Methane")["storedUnits"] = 10.0;
        CollectionAssert.AreEqual(new[] { $"{F.Tank} (part #2) Methane: amount 10 -> 1.6 (capacity)" },
            VabImport.ClampResources(rocket, Definition));
    }

    // An embedded game_assembly object is Root's own node: parsing must not detach it (Newtonsoft clones a
    // token that already has a parent when it is assigned).
    [Test]
    public void Parse_EmbeddedGameAssemblyStaysTheNodeOfRoot()
    {
        var export = new JObject
        {
            ["format"] = VabExport.Format, ["version"] = 1, ["summary"] = new JObject(), ["game_assembly"] = F.Rocket(),
        };
        VabImportResult r = VabImport.Parse(VabJson.WriteCompact(export));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreSame(r.File.Assembly, r.File.Root["game_assembly"]);
        Assert.AreSame(r.File.Assembly, r.File.Assemblies[0]);
    }

    // game_assembly handed back as a JSON string is parsed into its own tree: clamping the loaded
    // assembly must also show in Root, so Root and Assembly never disagree.
    [Test]
    public void Clamp_OnAStringGameAssemblyKeepsRootAndAssemblyConsistent()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Methane")["storedUnits"] = 5.0;
        var export = new JObject
        {
            ["format"] = VabExport.Format, ["version"] = 1, ["summary"] = new JObject(),
            ["game_assembly"] = VabJson.WriteCompact(rocket),
        };
        VabImportResult r = VabImport.Parse(VabJson.WriteCompact(export));
        Assert.IsTrue(r.Ok, r.Error);

        VabImport.ClampResources(r.File.Assembly, null);
        Assert.AreSame(r.File.Assembly, r.File.Root["game_assembly"]);
        Assert.AreEqual(1.6, (double)Res(r.File.Assembly, "Methane")["storedUnits"], 1e-12);
        Assert.AreEqual(1.6, (double)Res((JObject)r.File.Root["game_assembly"], "Methane")["storedUnits"], 1e-12);
    }
}
