using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SkySpaceAgency;
using UnityEngine;

// SkySpaceAgency and K2-D2 can be installed together (user decision 2026-11-19). Each value of
// ours that the game or another mod sees must differ from K2-D2's. K2-D2's values are written
// here literally, from IanMealworm/K2D2Redux upstream/main (8273fb8, K2-D2 1.4.1) and from the
// pull-request branch pr/k2d2 (K2-D2 1.5.0, our code with the K2-D2 identity), both read in the
// clone D:/KSPReduxModding/.k2d2-pr on 2026-11-19. Collision inventory:
// docs/superpowers/notes/2026-11-19-coexistence-with-k2d2.md. These tests describe SkySpaceAgency
// only: a K2-D2 build of this code would fail them on purpose.
public class CoexistenceTests
{
    // K2-D2's Addressables group GUIDs (m_GUID of Assets/AddressableAssetsData/AssetGroups/*.asset,
    // upstream/main, the same on pr/k2d2). The internal AssetBundle name is a hash of the group GUID
    // only (BuildScriptPackedMode), and Unity refuses a second bundle with the same internal name.
    static readonly string[] K2D2GroupGuids =
    {
        "9afb0fadf3d6795439ea32f39e70e741", // addressables_K2D2_all
        "605953b5181a8c146804cfcd0dcfd3eb", // K2D2_parts
        "748891d104a6f204ba15bfd420ffe00a", // K2D2_missions
        "111f7dfcadee2ad40bbad4db13d87496", // K2D2_cbs
        "b69f7b180df76904cb72021867432fd9", // K2D2_experiments
        "c1fe17431cd42504e93c8bb3e71621af", // K2D2_tech_nodes
        "791be43363cd60e4ba38681287a00367", // K2D2_resources
    };

    // addressables_K2D2_all.asset, upstream/main and pr/k2d2.
    const string K2D2WindowAddress = "Assets/UI/K2D2_UI/K2D2_Window.uxml";

    static string AssetsPath(params string[] parts) => Path.Combine(new[] { Application.dataPath }.Concat(parts).ToArray());

    [Test]
    public void WindowShortcut_IsNotK2D2s()
    {
        // K2-D2: Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O) (K2D2_Plugin.cs).
        var ours = ModIdentity.DefaultWindowShortcut;
        Assert.AreEqual(KeyCode.Y, ours.Main);
        CollectionAssert.AreEqual(new[] { KeyCode.LeftAlt }, ours.Modifiers);
        Assert.AreNotEqual(KeyCode.O, ours.Main);
        Assert.AreEqual("LeftAlt+Y", ours.ToString(), "the text Redux's settings show and save");
    }

    [Test]
    public void Ids_AreNotK2D2s()
    {
        // K2-D2 1.4.1 K2D2_Plugin.cs and swinfo.json; pr/k2d2 ModIdentity.cs.
        Assert.AreNotEqual("K2D2", ModIdentity.ModId, "mod_id (Redux skips a second mod with the same id)");
        Assert.AreNotEqual("K2D2", ModIdentity.WindowId);
        Assert.AreNotEqual("K2D2VabTools", ModIdentity.VabWindowId);
        Assert.AreNotEqual("BTN-K2D2", ModIdentity.ToolbarButtonPrefix);
        CollectionAssert.DoesNotContain(new[] { "BTN-K2D2Flight", "BTN-K2D2OAB", "BTN-K2D2KSC" }, ModIdentity.ToolbarFlightButtonId);
        CollectionAssert.DoesNotContain(new[] { "BTN-K2D2Flight", "BTN-K2D2OAB", "BTN-K2D2KSC" }, ModIdentity.ToolbarOabButtonId);
        CollectionAssert.DoesNotContain(new[] { "BTN-K2D2Flight", "BTN-K2D2OAB", "BTN-K2D2KSC" }, ModIdentity.ToolbarKscButtonId);
        Assert.AreNotEqual("K2D2", ModIdentity.LogPrefix);
        Assert.AreNotEqual("K2D2", ModIdentity.VabFormatPrefix);
        CollectionAssert.DoesNotContain(new[] { "k2d2_settings.json", "k2d2_landing_atmo.json", "k2d2_landing_vac.json" }, ModIdentity.SettingsFileName);
        CollectionAssert.DoesNotContain(new[] { "k2d2_settings.json", "k2d2_landing_atmo.json", "k2d2_landing_vac.json" }, ModIdentity.LandingAtmoFileName);
        CollectionAssert.DoesNotContain(new[] { "k2d2_settings.json", "k2d2_landing_atmo.json", "k2d2_landing_vac.json" }, ModIdentity.LandingVacFileName);
        Assert.AreNotEqual(K2D2WindowAddress, ModIdentity.MainWindowAddress);
    }

    [Test]
    public void Swinfo_IdAndLabels_AreNotK2D2s()
    {
        string json = File.ReadAllText(AssetsPath(ModIdentity.ModId, "swinfo.json"));
        StringAssert.Contains($"\"mod_id\": \"{ModIdentity.ModId}\"", json);
        StringAssert.Contains($"\"main_assembly\": \"{ModIdentity.ModId}.dll\"", json);
        StringAssert.DoesNotContain("\"K2D2_patches\"", json);
        StringAssert.DoesNotContain("\"K2D2_prefab_patches\"", json);
        StringAssert.Contains($"\"addressable_script_label\": \"{ModIdentity.ModId}_patches\"", json);
    }

    [Test]
    public void AddressablesGroups_HaveTheirOwnGuids()
    {
        string folder = AssetsPath("AddressableAssetsData", "AssetGroups");
        var ours = Directory.GetFiles(folder, "*.asset").Where(f => Path.GetFileName(f).Contains(ModIdentity.ModId)).ToArray();
        Assert.AreEqual(7, ours.Length, "the mod's seven groups (all, parts, missions, cbs, experiments, tech_nodes, resources)");
        foreach (string file in ours)
        {
            Match m = Regex.Match(File.ReadAllText(file), @"^  m_GUID: ([0-9a-f]{32})\s*$", RegexOptions.Multiline);
            Assert.IsTrue(m.Success, "no group GUID in " + file);
            CollectionAssert.DoesNotContain(K2D2GroupGuids, m.Groups[1].Value, Path.GetFileName(file) + " has one of K2-D2's group GUIDs");
        }

        // The other group must stay empty and out of the build (its GUID is K2-D2's too).
        string local = File.ReadAllText(Path.Combine(folder, "Default Local Group.asset"));
        StringAssert.Contains("m_SerializeEntries: []", local);
    }

    [Test]
    public void AddressablesWindowEntry_HasOurAddress()
    {
        string all = File.ReadAllText(AssetsPath("AddressableAssetsData", "AssetGroups", $"addressables_{ModIdentity.ModId}_all.asset"));
        var addresses = Regex.Matches(all, @"m_Address: (.+)$", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToArray();
        CollectionAssert.AreEqual(new[] { ModIdentity.MainWindowAddress }, addresses);
        Assert.IsTrue(File.Exists(AssetsPath(ModIdentity.MainWindowAddress.Substring("Assets/".Length))), "the window uxml is at its address");
    }

    [Test]
    public void BuiltinBundles_AreNamedAfterTheMod()
    {
        // ProjectName naming hashes the project folder's name, which a K2-D2 built from a clone of
        // this tree could share.
        string settings = File.ReadAllText(AssetsPath("AddressableAssetsData", "AddressableAssetSettings.asset"));
        StringAssert.Contains("m_BuiltInBundleNaming: 2", settings, "Custom");
        StringAssert.Contains("mBuiltInBundleCustomNaming: " + ModIdentity.ModId.ToLowerInvariant(), settings);
        StringAssert.Contains("m_MonoScriptBundleNaming: 2", settings, "Custom");
        StringAssert.Contains("m_MonoScriptBundleCustomNaming: " + ModIdentity.ModId.ToLowerInvariant(), settings);
    }

    [Test]
    public void Assembly_HasNoK2D2TypeNames()
    {
        var assembly = typeof(ModIdentity).Assembly;
        Assert.AreEqual(ModIdentity.ModId, assembly.GetName().Name);
        // FlightPlanRedux finds K2-D2 with GetType("K2D2.K2D2_Plugin") on every loaded assembly.
        Assert.IsNull(assembly.GetType("K2D2.K2D2_Plugin"));
        var offenders = assembly.GetTypes()
            .Where(t => t.Namespace != null && (t.Namespace == "K2D2" || t.Namespace.StartsWith("K2D2.") || t.Namespace == "K2UI" || t.Namespace.StartsWith("K2UI."))
                        || t.Name.StartsWith("K2D2"))
            .Select(t => t.FullName).ToArray();
        CollectionAssert.IsEmpty(offenders);
    }

    [Test]
    public void Sources_DeclareNoK2D2NamespaceOrType()
    {
        var declaration = new Regex(@"^\s*namespace\s+(K2D2|K2UI)\b|\b(class|struct|enum|interface|record)\s+K2(D2)?[A-Z_]", RegexOptions.Multiline);
        var files = Directory.GetFiles(AssetsPath(ModIdentity.ModId), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(AssetsPath("Runtime"), "*.cs", SearchOption.AllDirectories)).ToArray();
        Assert.Greater(files.Length, 100, "the scan found no source files");
        var offenders = files.Where(f => declaration.IsMatch(File.ReadAllText(f))).ToArray();
        CollectionAssert.IsEmpty(offenders);
    }
}
