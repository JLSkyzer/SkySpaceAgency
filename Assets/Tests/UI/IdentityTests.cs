using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkySpaceAgency;
using NUnit.Framework;
using UnityEngine;

// The mod's identity literals (a string that starts with the mod id, the AppBar button prefix,
// the settings file prefix) are spelled in ModIdentity.cs only. The mod's name may appear
// elsewhere as a C# namespace or type name (SkySpaceAgency since 2026-11-19, K2D2 in the K2-D2
// flavour): those are not literals and are not searched.
public class IdentityTests
{
    [Test]
    public void IdentityLiterals_OnlyInModIdentity()
    {
        string assets = Application.dataPath;
        var files = Directory.GetFiles(Path.Combine(assets, ModIdentity.ModId, "Code"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(assets, "UI"), "*.uxml", SearchOption.AllDirectories));

        string[] names = { "\"" + ModIdentity.ModId, ModIdentity.ToolbarButtonPrefix, ModIdentity.SettingsPrefix };
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in files)
        {
            if (Path.GetFileName(file) == "ModIdentity.cs")
                continue;
            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (names.Any(n => lines[i].IndexOf(n, System.StringComparison.Ordinal) >= 0))
                    offenders.Add($"{file.Substring(assets.Length - "Assets".Length)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.Greater(scanned, 100, "the scan found no source files");
        Assert.IsEmpty(offenders, "The mod's identity literals outside ModIdentity.cs:\n" + string.Join("\n", offenders));
    }

    // The other flavour's name left in an assembly definition (a rootNamespace, say) or in a style
    // sheet outside its comments: a leftover of the rename between the two flavours.
    [Test]
    public void OtherFlavourName_NotInAsmdefOrUss()
    {
        string assets = Application.dataPath;
        var files = Directory.GetFiles(assets, "*.asmdef", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(assets, "*.uss", SearchOption.AllDirectories))
            .Where(f => f.IndexOf("StreamingAssets", System.StringComparison.Ordinal) < 0);

        string other = ModIdentity.OtherFlavourVabFormatPrefix;
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in files)
        {
            scanned++;
            string text = File.ReadAllText(file);
            if (file.EndsWith(".uss"))
                text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            if (text.IndexOf(other, System.StringComparison.Ordinal) >= 0)
                offenders.Add(file.Substring(assets.Length - "Assets".Length));
        }

        Assert.Greater(scanned, 3, "the scan found no asmdef or uss files");
        Assert.IsEmpty(offenders, $"'{other}' outside comments in:\n" + string.Join("\n", offenders));
    }

    [Test]
    public void VabFormats_AcceptBothFlavours()
    {
        Assert.IsTrue(ModIdentity.IsVabFormat(ModIdentity.VabFormatPrefix + ".VabExport", "VabExport"));
        Assert.IsTrue(ModIdentity.IsVabFormat(ModIdentity.OtherFlavourVabFormatPrefix + ".VabExport", "VabExport"));
        Assert.IsTrue(ModIdentity.IsVabFormat("K2D2.VabExport", "VabExport"));
        Assert.IsTrue(ModIdentity.IsVabFormat("K2D2.PartsDump", "PartsDump"));
        Assert.IsFalse(ModIdentity.IsVabFormat("K2D2.PartsDump", "VabExport"));
        Assert.IsFalse(ModIdentity.IsVabFormat("Other.VabExport", "VabExport"));
        Assert.IsFalse(ModIdentity.IsVabFormat(null, "VabExport"));
    }
}
