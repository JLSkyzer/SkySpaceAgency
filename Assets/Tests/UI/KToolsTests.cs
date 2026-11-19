using System.Collections.Generic;
using K2UI;
using KTools;
using NUnit.Framework;

public class KToolsTests
{
    [Test]
    public void DurationToString_TenthsDigit()
    {
        // 1 min 5.005 s: the digit after the point is tenths, not the milliseconds.
        Assert.AreEqual("00:01:05.0", StrTool.DurationToString(65.005));
        Assert.AreEqual("00:01:05.9", StrTool.DurationToString(65.95));
        Assert.AreEqual("01:00:00.5", StrTool.DurationToString(3600.5));
    }

    static SettingsFile File(string key, string value) =>
        SettingsFile.InMemory(new Dictionary<string, string> { { key, value } });

    [Test]
    public void ClampSetting_ValueReadFromTheFile_IsClamped()
    {
        var file = File("t.x", "12");
        var s = new ClampSetting<float>("t.x", 1, 0, 10, file);
        Assert.AreEqual(10f, s.V);
        var low = new ClampSetting<float>("t.y", 1, 2, 10, File("t.y", "-3"));
        Assert.AreEqual(2f, low.V);
        var inRange = new ClampSetting<float>("t.z", 1, 0, 10, File("t.z", "4.5"));
        Assert.AreEqual(4.5f, inRange.V);
    }

    [Test]
    public void ClampSetting_FileLoadedAfterConstruction_IsClampedOnRead()
    {
        var file = File("t.x", "50");
        file.loaded = false;
        var s = new ClampSetting<int>("t.x", 10, 5, 20, file);
        file.loaded = true;
        // The value arrives with the file's load event (the setting subscribed to it).
        var evt = typeof(SettingsFile).GetField("onloaded_event",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = (SettingsFile.onLoaded)evt.GetValue(file);
        handler();
        Assert.AreEqual(20, s.V);
    }

    [Test]
    public void ClampSetting_WriteOutOfRange_StoresClampedAndNotifies()
    {
        var file = SettingsFile.InMemory();
        var s = new ClampSetting<float>("t.x", 5, 0, 10, file);
        var heard = new List<float>();
        s.listeners += v => heard.Add(v);

        s.V = 15;
        Assert.AreEqual(10f, s.V);
        Assert.AreEqual(10f, file.GetFloat("t.x", -1));
        CollectionAssert.AreEqual(new[] { 10f, 10f }, heard); // the change, then the clamp notice

        // Already at the maximum: the stored value does not change, the field is still told.
        heard.Clear();
        s.V = 30;
        CollectionAssert.AreEqual(new[] { 10f }, heard);

        // In range: one notice, no extra.
        heard.Clear();
        s.V = 3;
        CollectionAssert.AreEqual(new[] { 3f }, heard);
    }

    [Test]
    public void ClampSetting_ChangingTheRange_ClampsTheValue()
    {
        var file = File("t.x", "8");
        var s = new ClampSetting<float>("t.x", 5, 0, 10, file);
        s.max = 6;
        Assert.AreEqual(6f, s.V);
        Assert.AreEqual(6f, file.GetFloat("t.x", -1));
        // Lowering the minimum keeps the value (the old setter set V to the new minimum).
        s.min = -5;
        Assert.AreEqual(6f, s.V);
    }

    [Test]
    public void ClampSetting_Reset_GoesBackToTheDefault()
    {
        var file = File("t.x", "8");
        var s = new ClampSetting<float>("t.x", 5, 0, 10, file);
        file.Reset();
        Assert.AreEqual(5f, s.V);
    }

    [Test]
    public void K2Slider_FormatValue()
    {
        Assert.AreEqual("1.23", K2Slider.FormatValue(1.2345f, null));
        Assert.AreEqual("1.23", K2Slider.FormatValue(1.2345f, ""));
        Assert.AreEqual("1.23", K2Slider.FormatValue(1.2345f, "N2"));
        Assert.AreEqual("1", K2Slider.FormatValue(1.2345f, "N0"));
        Assert.AreEqual("1.235", K2Slider.FormatValue(1.2346f, "N3"));
        Assert.AreEqual("45", K2Slider.FormatValue(0.45f, "percent"));
        Assert.AreEqual("1,235", K2Slider.FormatValue(1234.6f, "N0"));
    }

    // K2-D2 1.4.0 renamed warp.elevation/heading to attitude.*: an old file keeps its value.
    [Test]
    public void Renamed_OldKeyOnly_ValueCarriedOver()
    {
        var file = File("warp.elevation", "35");
        var s = new ClampSetting<float>(file.Renamed("attitude.elevation", "warp.elevation"), 0, -90, 90, file);
        Assert.AreEqual(35f, s.V);
        Assert.AreEqual(35f, file.GetFloat("attitude.elevation", -1), "written under the new key");
        Assert.AreEqual(35f, file.GetFloat("warp.elevation", -1), "the old key stays for older versions");
    }

    [Test]
    public void Renamed_NewKeyPresent_OldKeyIgnored()
    {
        var file = SettingsFile.InMemory(new Dictionary<string, string> { { "warp.heading", "90" }, { "attitude.heading", "270" } });
        var s = new ClampSetting<float>(file.Renamed("attitude.heading", "warp.heading"), 0, 0, 360, file);
        Assert.AreEqual(270f, s.V);

        // Read once: a later change of the new key is not overwritten by the old one.
        s.V = 180;
        var again = new ClampSetting<float>(file.Renamed("attitude.heading", "warp.heading"), 0, 0, 360, file);
        Assert.AreEqual(180f, again.V);
    }

    [Test]
    public void Renamed_NeitherKey_Default()
    {
        var file = SettingsFile.InMemory();
        var s = new ClampSetting<float>(file.Renamed("attitude.elevation", "warp.elevation"), 12, -90, 90, file);
        Assert.AreEqual(12f, s.V);
    }

    [Test]
    public void Renamed_FileLoadedAfterConstruction_CopiedBeforeTheSettingReads()
    {
        var file = File("warp.elevation", "-20");
        file.loaded = false;
        var s = new ClampSetting<float>(file.Renamed("attitude.elevation", "warp.elevation"), 0, -90, 90, file);
        file.loaded = true;
        var evt = typeof(SettingsFile).GetField("onloaded_event",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((SettingsFile.onLoaded)evt.GetValue(file))();
        Assert.AreEqual(-20f, s.V);
    }
}
