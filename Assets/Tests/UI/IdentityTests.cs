using System.Collections.Generic;
using System.IO;
using System.Linq;
using K2D2;
using NUnit.Framework;
using UnityEngine;

// The mod's name is spelled in ModIdentity.cs only, so a flavour switch (SkySpaceAgency / K2-D2)
// changes one code file. Comments count too.
public class IdentityTests
{
    [Test]
    public void IdentityLiterals_OnlyInModIdentity()
    {
        string assets = Application.dataPath;
        var files = Directory.GetFiles(Path.Combine(assets, "SkySpaceAgency", "Code"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(assets, "UI"), "*.uxml", SearchOption.AllDirectories));

        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in files)
        {
            if (Path.GetFileName(file) == "ModIdentity.cs")
                continue;
            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].IndexOf("SkySpaceAgency", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    offenders.Add($"{file.Substring(assets.Length - "Assets".Length)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.Greater(scanned, 100, "the scan found no source files");
        Assert.IsEmpty(offenders, "The mod's name outside ModIdentity.cs:\n" + string.Join("\n", offenders));
    }

    [Test]
    public void VabFormats_AcceptBothFlavours()
    {
        Assert.IsTrue(ModIdentity.IsVabFormat(ModIdentity.VabFormatPrefix + ".VabExport", "VabExport"));
        Assert.IsTrue(ModIdentity.IsVabFormat("K2D2.VabExport", "VabExport"));
        Assert.IsTrue(ModIdentity.IsVabFormat("K2D2.PartsDump", "PartsDump"));
        Assert.IsFalse(ModIdentity.IsVabFormat("K2D2.PartsDump", "VabExport"));
        Assert.IsFalse(ModIdentity.IsVabFormat("Other.VabExport", "VabExport"));
        Assert.IsFalse(ModIdentity.IsVabFormat(null, "VabExport"));
    }
}
