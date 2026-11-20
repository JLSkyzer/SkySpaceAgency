using System;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using UnityEngine;
using UnityEngine.UIElements;

// The VAB tools window in the new interface (plan 2026-11-19-new-ui-tab-migration, step 3b, 2026-11-20).
public class VabToolsViewTests
{
    // ---- journal and status words (pure) ----

    [Test]
    public void Journal_KeepsTheNewestFirst_AndForgetsTheOldest()
    {
        var j = new VabJournal();
        var t = new DateTime(2026, 11, 20, 8, 5, 9);
        for (int i = 1; i <= VabJournal.Capacity + 3; i++)
            j.Add("message " + i, t.AddSeconds(i));
        Assert.AreEqual(VabJournal.Capacity, j.Lines.Count);
        StringAssert.EndsWith("message " + (VabJournal.Capacity + 3), j.Lines[0]);
        StringAssert.StartsWith("08:05:", j.Lines[0]);
        StringAssert.EndsWith("message 4", j.Lines[VabJournal.Capacity - 1]);
        Assert.AreEqual(VabJournal.Capacity - 1, j.Earlier.Split('\n').Length);
    }

    [Test]
    public void Journal_IgnoresEmpty_FlattensLines()
    {
        var j = new VabJournal();
        j.Add(null);
        j.Add("   ");
        Assert.AreEqual(0, j.Lines.Count);
        j.Add("first line\r\nsecond line");
        Assert.AreEqual(1, j.Lines.Count);
        StringAssert.EndsWith("first line second line", j.Lines[0]);
        Assert.AreEqual("", j.Earlier, "one message: nothing earlier");
    }

    [TestCase("Ready.", StatusKind.Neutral)]
    [TestCase("Importing heavy.json...", StatusKind.Info)]
    [TestCase("Exported to C:/x/vab/exports/a.json", StatusKind.Ok)]
    [TestCase("Dumped 2001 parts (312 unlocked) to C:/x", StatusKind.Ok)]
    [TestCase("Imported as the main rocket (212 parts). Previous workspace saved to exports/b.json.", StatusKind.Ok)]
    [TestCase("Refused: the game cannot read this assembly (x). Your rocket is unchanged.", StatusKind.Alert)]
    [TestCase("Export failed: disk full", StatusKind.Alert)]
    [TestCase("Cannot open the folder: nope", StatusKind.Alert)]
    [TestCase("The VAB is not ready yet.", StatusKind.Alert)]
    [TestCase("No rocket in the VAB: nothing to export.", StatusKind.Alert)]
    [TestCase("Import stopped by an unexpected error (X: y). See the log.", StatusKind.Alert)]
    [TestCase("Put a .json file in vab/imports first (Open folder), then press Refresh.", StatusKind.Neutral)]
    [TestCase("", StatusKind.Neutral)]
    public void Status_KindFollowsTheWords(string text, StatusKind expected)
    {
        Assert.AreEqual(expected, VabStatus.KindOf(text));
    }

    // ---- the view ----

    static VabToolsView Build(out int[] clicks)
    {
        var c = new int[6];
        clicks = c;
        return new VabToolsView("SkySpaceAgency", "VAB tools",
            () => c[0]++, () => c[1]++, () => c[2]++, () => c[3]++, () => c[4]++, () => c[5]++);
    }

    [Test]
    public void View_HasEveryFunctionOfTheClassicVabWindow()
    {
        var v = Build(out var clicks);
        Assert.AreEqual("Export", v.ExportButton.Text);
        Assert.AreEqual("Refresh", v.RefreshButton.Text);
        Assert.AreEqual("Import (replaces the current rocket)", v.ImportButton.Text);
        Assert.AreEqual("Dump parts", v.DumpButton.Text);
        Assert.AreEqual("Open folder", v.FolderButton.Text);
        Assert.AreEqual(AppButton.Kind.Main, v.ExportButton.ButtonKind);
        Assert.AreEqual(AppButton.Kind.Abort, v.ImportButton.ButtonKind, "the destructive action looks like one");
        v.ExportButton.Click(); v.ImportButton.Click(); v.RefreshButton.Click(); v.DumpButton.Click(); v.FolderButton.Click();
        v.Header.CloseButton.Click();
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1, 1 }, clicks);
        Assert.AreEqual("Ready.", v.StatusPill.Text);
    }

    [Test]
    public void View_Files_PlaceholderThenChoices_KeepsTheChoice()
    {
        var v = Build(out _);
        Assert.IsNull(v.SelectedFile, "no file: the placeholder is not a file");
        v.SetFiles(new[] { "a.json", "b.json" });
        Assert.AreEqual("a.json", v.SelectedFile);
        v.Files.SetIndexWithoutNotify(1);
        v.SetFiles(new[] { "a.json", "b.json", "c.json" }, v.SelectedFile);
        Assert.AreEqual("b.json", v.SelectedFile, "a refresh keeps the chosen file");
        v.SetFiles(new[] { "c.json" }, "b.json");
        Assert.AreEqual("c.json", v.SelectedFile, "a vanished choice falls back to the first");
        v.SetFiles(new string[0]);
        Assert.IsNull(v.SelectedFile);
        Assert.AreEqual(VabToolsView.NoFileChoice, v.Files.Value);
    }

    [Test]
    public void View_Status_PillAndJournal()
    {
        var v = Build(out _);
        v.SetStatus("Exported to x");
        Assert.AreEqual(StatusKind.Ok, v.StatusPill.Kind);
        v.SetStatus("Cannot read b.json: nope");
        Assert.AreEqual(StatusKind.Alert, v.StatusPill.Kind);
        Assert.AreEqual("Cannot read b.json: nope", v.StatusPill.Text);
        StringAssert.Contains("Exported to x", v.JournalNote.text, "the earlier message goes to the journal");
    }

    [Test]
    public void View_Busy_GreysEverything_AndComesBack()
    {
        var v = Build(out var clicks);
        v.SetBusy(true);
        Assert.IsTrue(v.Busy);
        Assert.IsFalse(v.ExportButton.enabledSelf);
        Assert.IsFalse(v.Files.enabledSelf);
        v.SetBusy(false);
        v.ExportButton.Click();
        Assert.AreEqual(1, clicks[0]);
        Assert.IsTrue(v.Files.enabledSelf);
        Assert.IsTrue(v.ImportButton.enabledSelf);
    }

    // ---- laid out ----

    [Test]
    public void View_Layout_HasNoIssue_AtTheUsualScales()
    {
        foreach (float scale in new[] { 1f, 1f / 0.85f, 1.5f })
        {
            using var o = AppUiHarness.OpenVab(new Vector2(700, 800), scale);
            o.Vab.SetFiles(new[] { "heavy trip-5.json", "before_import_20261119-081502.json" });
            o.Vab.SetStatus("Imported as the main rocket (212 parts). Previous workspace saved to exports/before_import_20261120.json.");
            o.Vab.SetStatus("Exported to C:/Games/KSP2/mods/SkySpaceAgency/vab/exports/heavy trip-5.json");
            o.Layout();
            var issues = AppUiHarness.Check(o);
            foreach (string issue in issues) Debug.Log($"[VabTools] scale {scale}: {issue}");
            Assert.IsEmpty(issues, $"{issues.Count} layout issue(s) at scale {scale}:\n" + string.Join("\n", issues));
            Assert.AreEqual(440, o.Vab.layout.width, 1f, "a small fixed-width window");
            Assert.Greater(o.Vab.layout.height, 300);
            Assert.Less(o.Vab.layout.height, 700);
        }
    }

    [Test]
    public void View_UsesTheNewInterfaceStyle()
    {
        using var o = AppUiHarness.OpenVab(new Vector2(560, 640));
        Assert.IsTrue(o.Vab.ClassListContains("ssa-app"), "the new interface's scope class");
        var title = o.Vab.Q<Label>(className: "ssa-brand__title");
        Assert.IsNotNull(title);
        Assert.AreEqual(ModIdentity.Title.ToUpperInvariant(), title.text);
        Assert.AreEqual(3, o.Vab.Query<AppCard>().ToList().Count, "Export, Import, Parts and files");
    }
}
