using System;
using System.Collections.Generic;
using System.IO;
using K2D2.VabTools;
using NUnit.Framework;

public class VabFilesTests
{
    [Test]
    public void SafeName_ReplacesInvalidCharacters()
    {
        Assert.AreEqual("Bon vol_ Mk2_3_", VabFiles.SafeName("Bon vol: Mk2/3?", "vessel"));
        Assert.AreEqual("fusée pour orbite V2", VabFiles.SafeName("fusée pour orbite V2", "vessel"));
        Assert.AreEqual("a_b", VabFiles.SafeName("a\tb", "vessel"));
    }

    [Test]
    public void SafeName_FallsBackOnEmptyNames()
    {
        Assert.AreEqual("vessel", VabFiles.SafeName(null, "vessel"));
        Assert.AreEqual("vessel", VabFiles.SafeName("   ", "vessel"));
        Assert.AreEqual("vessel", VabFiles.SafeName("...", "vessel"));
        Assert.AreEqual("Rocket", VabFiles.SafeName("  Rocket. ", "vessel")); // Windows drops trailing dots
    }

    [Test]
    public void SafeName_TruncatesLongNames()
    {
        Assert.AreEqual(VabFiles.MaxNameLength, VabFiles.SafeName(new string('a', 100), "vessel").Length);
    }

    // Spec, "Fichiers": exports/<vessel>_<AAAA-MM-JJ_HHMMSS>.json, dumps/parts_<campaign>_<...>.json,
    // exports/before_import_<...>.json.
    [Test]
    public void FileNames_FollowTheSpec()
    {
        var t = new DateTime(2026, 10, 16, 5, 7, 9);
        Assert.AreEqual("Bon vol_2026-10-16_050709.json", VabFiles.ExportFileName("Bon vol", t));
        Assert.AreEqual("before_import_Bon vol_2026-10-16_050709.json", VabFiles.BackupFileName("Bon vol", t));
        Assert.AreEqual("parts_Skyzer Adventure_2026-10-16_050709.json", VabFiles.DumpFileName("Skyzer Adventure", t));
        Assert.AreEqual("vessel_2026-10-16_050709.json", VabFiles.ExportFileName("", t));
        Assert.AreEqual("parts_campaign_2026-10-16_050709.json", VabFiles.DumpFileName(null, t));
    }

    [Test]
    public void IsoTime_IsInvariant()
    {
        Assert.AreEqual("2026-10-16T05:07:09", VabFiles.IsoTime(new DateTime(2026, 10, 16, 5, 7, 9)));
        Assert.AreEqual("2026-10-16T05:07:09Z", VabFiles.IsoTime(new DateTime(2026, 10, 16, 5, 7, 9, DateTimeKind.Utc)));
    }

    [Test]
    public void UniquePath_AddsASuffixWhenTaken()
    {
        var taken = new HashSet<string> { Path.Combine("dir", "a.json"), Path.Combine("dir", "a_2.json") };
        Assert.AreEqual(Path.Combine("dir", "b.json"), VabFiles.UniquePath("dir", "b.json", taken.Contains));
        Assert.AreEqual(Path.Combine("dir", "a_3.json"), VabFiles.UniquePath("dir", "a.json", taken.Contains));
    }

    [Test]
    public void ImportChoices_ListsJsonFilesOfBothFolders()
    {
        List<string> choices = VabFiles.ImportChoices(
            new[] { "z.json", "notes.txt", "A.JSON" }, new[] { "before_import_x.json" });
        CollectionAssert.AreEqual(new[] { "imports/A.JSON", "imports/z.json", "exports/before_import_x.json" }, choices);
        CollectionAssert.IsEmpty(VabFiles.ImportChoices(null, null));
    }
}
