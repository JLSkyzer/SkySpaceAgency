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
}
