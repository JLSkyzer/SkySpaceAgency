using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.Bridge;
using SkySpaceAgency.Controller;
using SkySpaceAgency.Node;
using UnityEngine;
using UnityEngine.UIElements;

// SETTINGS, the planned RETURN page and the compact flight bar of the new window
// (plan 2026-11-19-new-ui-tab-migration, task "SETTINGS"). No game: AppTabContext.Editor().
public class SettingsTabTests
{
    // The settings touched here are the mod's static ones: put them back after each test.
    bool _auto, _debug, _compact, _bridge;
    float _freeze, _scale;
    BurnWarpMode _warp;

    [SetUp]
    public void Save()
    {
        _auto = StagingSettings.auto_staging.V;
        _freeze = StagingSettings.freeze_duration.V;
        _debug = SkySpaceAgencySettings.debug_mode.V;
        _compact = AppSettings.CompactBar.V;
        _scale = AppSettings.Scale.V;
        _warp = BurnWarpSettings.mode.V;
        _bridge = StageBridgeSettings.Enabled.V;
    }

    [TearDown]
    public void Restore()
    {
        StagingSettings.auto_staging.V = _auto;
        StagingSettings.freeze_duration.V = _freeze;
        SkySpaceAgencySettings.debug_mode.V = _debug;
        AppSettings.CompactBar.V = _compact;
        AppSettings.Scale.V = _scale;
        BurnWarpSettings.mode.V = _warp;
        StageBridgeSettings.Enabled.V = _bridge;
    }

    static SettingsTab BuildSettings(AppTabContext context = null)
    {
        var tab = new SettingsTab(context ?? AppTabContext.Editor());
        tab.Build();
        return tab;
    }

    // ---- SETTINGS ----

    [Test]
    public void Settings_BuildsAndTicks_WithoutTheGame()
    {
        var tab = BuildSettings();
        Assert.AreEqual("settings", tab.Code);
        Assert.IsTrue(tab.RightAligned);
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); });
        Assert.DoesNotThrow(() => tab.ShowSample());
    }

    [Test]
    public void Settings_BindsEverySetting()
    {
        // Only this tab's keys count here; what other tabs recorded is given back afterwards.
        var before = new HashSet<string>(AppBind.Bound);
        AppBind.Bound.Clear();
        BuildSettings();
        var mine = new HashSet<string>(AppBind.Bound);
        AppBind.Bound.UnionWith(before);
        foreach (string key in new[]
        {
            AppSettings.Scale.key, AppSettings.SmallWindow.path, AppSettings.CompactBar.key,
            StagingSettings.auto_staging.key, StagingSettings.freeze_duration.key,
            BurnWarpSettings.mode.path, SkySpaceAgencySettings.debug_mode.key,
        })
            Assert.IsTrue(mine.Contains(key), key + " has no control");
    }

    [Test]
    public void Settings_Categories_ShowOnePanelAtATime()
    {
        var tab = BuildSettings();
        Assert.AreEqual("general", tab.Category);
        Assert.AreEqual(SettingsText.Categories.Length, tab.Panels.Count);
        foreach (var (code, _) in SettingsText.Categories)
        {
            Assert.IsTrue(tab.SelectCategory(code), code);
            foreach (var kv in tab.Panels)
                Assert.AreEqual(kv.Key == code ? DisplayStyle.Flex : DisplayStyle.None, kv.Value.style.display.value, kv.Key);
            Assert.IsTrue(tab.Page.Q("category-" + code).ClassListContains("ssa-category--active"));
        }
        Assert.IsFalse(tab.SelectCategory("nope"));
        Assert.AreEqual("about", tab.Category);
    }

    [Test]
    public void Settings_Switches_FollowTheSettings()
    {
        var tab = BuildSettings();
        var staging = tab.Panels["staging"].Query<AppSwitch>().First();
        StagingSettings.auto_staging.V = true;
        Assert.IsTrue(staging.value);
        StagingSettings.auto_staging.V = false;
        Assert.IsFalse(staging.value);
        staging.Toggle();
        Assert.IsTrue(StagingSettings.auto_staging.V);

        var debug = tab.Panels["logs"].Query<AppSwitch>().First();
        SkySpaceAgencySettings.debug_mode.V = true;
        Assert.IsTrue(debug.value);
    }

    [Test]
    public void Settings_Scale_GoesThroughSetScale_OnA5PercentStep()
    {
        float applied = -1;
        var tab = BuildSettings(new AppTabContext { SetScale = s => applied = s });
        // The slider's change event needs a panel; SetScalePercent is what it calls.
        tab.SetScalePercent(112);
        Assert.AreEqual(1.10f, applied, 1e-4f);
        Assert.AreEqual(110, tab.ScaleSlider.Value, 1e-3f);

        var editor = BuildSettings();
        editor.SetScalePercent(140);
        Assert.AreEqual(1.40f, AppSettings.Scale.V, 1e-4f);
        AppSettings.Scale.V = 0.8f;
        Assert.AreEqual(80, editor.ScaleSlider.Value, 1e-3f);
    }

    [Test]
    public void Settings_WarpNote_FollowsTheMode()
    {
        var tab = BuildSettings();
        BurnWarpSettings.mode.V = BurnWarpMode.Off;
        StringAssert.Contains(BurnWarpModes.Describe(BurnWarpMode.Off), tab.WarpNote.text);
        BurnWarpSettings.mode.V = BurnWarpMode.Physics;
        StringAssert.Contains(BurnWarpModes.Describe(BurnWarpMode.Physics), tab.WarpNote.text);
    }

    [Test]
    public void Settings_Reset_FirstClickOnlyArms()
    {
        var tab = BuildSettings();
        SkySpaceAgencySettings.debug_mode.V = true;
        tab.ResetButton.Click();
        Assert.IsTrue(tab.ResetArmed);
        Assert.AreEqual(SettingsTab.ResetConfirmLabel, tab.ResetButton.Text);
        Assert.IsTrue(SkySpaceAgencySettings.debug_mode.V, "one click must not reset anything");
        tab.DoneButton.Click();
        Assert.IsFalse(tab.ResetArmed);
        Assert.AreEqual(SettingsTab.ResetLabel, tab.ResetButton.Text);
    }

    sealed class FakeTab : AppTab
    {
        readonly string _code;
        public FakeTab(string code) : base(null) { _code = code; }
        public override string Code => _code;
        public override string Label => _code;
        protected override void OnBuild(AppPage page) { }
    }

    sealed class ClosingTab : AppTab
    {
        public int Closed;
        public bool Throws;
        public ClosingTab(string code) : base(null) { _code = code; }
        readonly string _code;
        public override string Code => _code;
        public override string Label => _code;
        protected override void OnBuild(AppPage page) { }
        public override void OnWindowClosed() { Closed++; if (Throws) throw new System.InvalidOperationException("boom"); }
    }

    [Test]
    public void WindowClosed_TellsEveryTab_AndOneThrowingDoesNotStopTheOthers()
    {
        var view = new AppWindowView("t", "s");
        var a = new ClosingTab("lift") { Throws = true };
        var b = new ClosingTab("land");
        view.AddTab(a);
        view.AddTab(b);
        UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Warning, new System.Text.RegularExpressions.Regex("OnWindowClosed"));
        view.WindowClosed();
        Assert.AreEqual(1, a.Closed);
        Assert.AreEqual(1, b.Closed);
    }

    [Test]
    public void Settings_Done_GoesBackToThePreviousTab()
    {
        var view = new AppWindowView("t", "s");
        view.AddTab(new FakeTab("lift"));
        view.AddTab(new FakeTab("orbit"));
        var tab = new SettingsTab(AppTabContext.Editor());
        view.AddTab(tab);
        view.Select("settings");
        tab.DoneButton.Click();
        Assert.AreEqual("lift", view.Tabs.Current, "first flight tab when nothing came before");
        view.Select("orbit");
        view.Select("settings");
        tab.DoneButton.Click();
        Assert.AreEqual("orbit", view.Tabs.Current);
    }

    [Test]
    public void Settings_About_KeepsTheCredits()
    {
        var tab = BuildSettings();
        var texts = tab.Page.Query<Label>().ToList().Select(l => l.text).ToList();
        string all = string.Join("\n", texts);
        foreach (string who in new[] { "Christophe Floutier", "IanMealworm", "LeoMarinDev", "schlosrat", "Mole", "Opus", "cheese3660", "Halbann", "KSP2Community" })
            StringAssert.Contains(who, all, who + " lost from the credits");
        foreach (string thanks in new[] { "KSP2 Modding Society", "@Munix", "@DunaColonist", "@Cheese" })
            StringAssert.Contains(thanks, all);
        StringAssert.Contains("CC BY-SA 4.0", all);
        StringAssert.Contains(ModIdentity.Title, all);
        Assert.AreEqual("development build · editor", SettingsTab.VersionText(AppTabContext.Editor()));
    }

    [TestCase("Y + LeftAlt", "Alt + Y")]
    [TestCase("F + LeftControl + LeftShift", "Ctrl + Shift + F")]
    [TestCase("Alpha5 + RightAlt", "Alt + 5")]
    [TestCase("O", "O")]
    [TestCase("", null)]
    [TestCase(null, null)]
    [TestCase("SpaceWarp2.API.Input.KeyboardShortcut", null)]
    public void Shortcut_IsWrittenLikeTheMockup(string raw, string expected)
    {
        Assert.AreEqual(expected, SettingsText.Shortcut(raw));
    }

    [Test]
    public void Shortcut_TextIsNeverEmpty()
    {
        Assert.IsFalse(string.IsNullOrEmpty(SettingsTab.ShortcutText()));
    }

    // ---- RETURN (planned) ----

    [Test]
    public void Return_BuildsAndTicks_Disabled_AndSaysItIsPlanned()
    {
        var tab = new ReturnTab(AppTabContext.Editor());
        tab.Build();
        Assert.DoesNotThrow(() => tab.Tick());
        Assert.AreEqual("return", tab.Code);
        Assert.AreEqual("Planned", tab.Badge);
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        StringAssert.Contains("Planned", tab.PlannedPill.Text);
        StringAssert.StartsWith("Planned", tab.Page.Actions.Note);
        Assert.Greater(tab.DisabledControls.Count, 5);
        foreach (var e in tab.DisabledControls)
            Assert.IsFalse(e.enabledSelf, e.GetType().Name);
        foreach (var b in tab.Page.Actions.Buttons)
            Assert.IsFalse(b.enabledSelf, b.Text);
        foreach (var s in tab.Page.Query<AppSwitch>().ToList())
            Assert.IsFalse(s.enabledInHierarchy, "switch enabled");
        Assert.AreEqual(ReturnTab.Phases.Length, tab.Page.Timeline.Model.Steps.Count);
        Assert.AreEqual("—", tab.KeepTile.Value);
    }

    [Test]
    public void Return_ShowSample_FillsTheMockupValues()
    {
        var tab = new ReturnTab(AppTabContext.Editor());
        tab.Build();
        tab.ShowSample();
        Assert.AreEqual("640", tab.KeepTile.Value);
        Assert.AreEqual("2:24", tab.SeparateTile.Value);
        Assert.AreEqual("310", tab.BoostbackTile.Value);
        Assert.AreEqual("260", tab.LandingTile.Value);
        Assert.AreEqual("KSC · Landing pad 1", tab.PointTitle.text);
        Assert.AreEqual(1, tab.Page.Timeline.Model.Current);
        Assert.AreEqual(7, tab.ProfileView.LabelCount);
        Assert.IsTrue(tab.ReturnButton.enabledSelf, "the sample shows the main button as the mockup does");
    }

    [Test]
    public void ReturnGeometry_PathsJoinTheirEvents()
    {
        var ascent = ReturnProfileGeometry.Ascent();
        Assert.AreEqual(ReturnProfileGeometry.Pad, ascent.First());
        Assert.AreEqual(ReturnProfileGeometry.UpperStage, ascent.Last());
        Assert.IsTrue(ascent.Any(p => p == ReturnProfileGeometry.Separation), "the ascent passes the separation");
        var back = ReturnProfileGeometry.Return();
        Assert.AreEqual(ReturnProfileGeometry.Separation, back.First());
        Assert.AreEqual(ReturnProfileGeometry.Pad, back.Last());
        foreach (var p in ascent.Concat(back))
        {
            Assert.That(p.x, Is.InRange(-0.01f, 1.01f));
            Assert.That(p.y, Is.InRange(-0.05f, 1.0f));
        }
    }

    [Test]
    public void ReturnGeometry_Dashes_StayOnTheLine()
    {
        var line = new List<Vector2> { Vector2.zero, new Vector2(4, 0), new Vector2(10, 0) };
        var dashes = ReturnProfileGeometry.Dashes(line, 2, 1);
        Assert.AreEqual(4, dashes.Count);   // 0-2, 3-5, 6-8, 9-10
        float on = 0;
        foreach (var (a, b) in dashes)
        {
            Assert.AreEqual(0, a.y, 1e-5f);
            Assert.AreEqual(0, b.y, 1e-5f);
            Assert.LessOrEqual(a.x, b.x);
            on += b.x - a.x;
        }
        Assert.AreEqual(7, on, 1e-4f);
        Assert.AreEqual(3, dashes[1].a.x, 1e-4f);
        Assert.IsEmpty(ReturnProfileGeometry.Dashes(null, 2, 1));
        Assert.IsEmpty(ReturnProfileGeometry.Dashes(line, 0, 1));
    }

    // ---- Compact flight bar ----

    static HudState Sample(int readouts = 3) => new HudState
    {
        Pilot = "Land",
        Phase = "Pre-brake",
        Phases = new[] { "Deorbit", "Pre-brake", "Brake", "Touch down", "Landed" },
        CurrentPhase = 1,
        Readouts = Enumerable.Range(0, readouts).Select(i => ("Readout " + i, i.ToString())).ToList(),
        GaugeLabel = "Hottest part",
        Gauge = 0.22f,
    };

    [Test]
    public void Hud_ShowsTheState_AndHidesOnNull()
    {
        var hud = new AppHudView();
        Assert.AreEqual(DisplayStyle.None, hud.style.display.value);
        hud.Show(Sample());
        Assert.AreEqual(DisplayStyle.Flex, hud.style.display.value);
        Assert.AreEqual("LAND", hud.PilotText);
        Assert.AreEqual("Pre-brake", hud.PhaseText);
        Assert.AreEqual(5, hud.DotCount);
        Assert.AreEqual(3, hud.ReadoutCount);
        Assert.IsTrue(hud.GaugeShown);
        Assert.AreEqual("22 %", hud.GaugeText);
        Assert.IsFalse(hud.Alerting);

        hud.Show(Sample(1));
        Assert.AreEqual(1, hud.ReadoutCount);
        var noGauge = Sample();
        noGauge.Gauge = null;
        hud.Show(noGauge);
        Assert.IsFalse(hud.GaugeShown);

        hud.Show(null);
        Assert.AreEqual(DisplayStyle.None, hud.style.display.value);
        Assert.IsNull(hud.State);
    }

    [Test]
    public void Hud_Alert_TurnsTheBarCoral()
    {
        var hud = new AppHudView();
        var s = Sample();
        s.Alert = "Heat guard · thrusting retrograde";
        s.AlertDetail = "engine at 78 %";
        hud.Show(s);
        Assert.IsTrue(hud.Alerting);
        Assert.IsTrue(hud.ClassListContains(AppHudView.AlertClass));
        StringAssert.Contains("Heat guard", hud.AlertText);
        s.Alert = null;
        hud.Show(s);
        Assert.IsFalse(hud.Alerting);
        Assert.IsFalse(hud.ClassListContains(AppHudView.AlertClass));
    }

    [Test]
    public void Hud_Buttons_StopAndOpen()
    {
        var hud = new AppHudView();
        int stops = 0, opens = 0;
        hud.openWindow += () => opens++;
        var s = Sample();
        s.Stop = () => stops++;
        hud.Show(s);
        hud.StopButton.Click();
        hud.OpenButton.Click();
        Assert.AreEqual(1, stops);
        Assert.AreEqual(1, opens);

        s.Stop = null;
        hud.Show(s);
        Assert.IsFalse(hud.StopButton.enabledSelf, "Stop stays shown, disabled");
        Assert.DoesNotThrow(() => hud.StopButton.Click());
        Assert.AreEqual(1, stops);
    }

    [Test]
    public void Hud_Gauge_IsClamped()
    {
        Assert.AreEqual(0, AppHudView.Fraction(float.NaN));
        Assert.AreEqual(1, AppHudView.Fraction(3));
        Assert.AreEqual(0, AppHudView.Fraction(-1));
        Assert.AreEqual(0.5f, AppHudView.Fraction(0.5f), 1e-6f);
    }

    [Test]
    public void Settings_LogsPanel_HoldsTheStageBridgeSwitchAndButton()
    {
        var tab = BuildSettings();
        Assert.IsNotNull(tab.StageBridgeSwitch);
        Assert.IsNotNull(tab.StageBridgeButton);
        Assert.AreEqual("Stage bridge: log state", tab.StageBridgeButton.Text);
        Assert.IsTrue(tab.Panels["logs"].Contains(tab.StageBridgeSwitch));
        Assert.IsTrue(tab.Panels["logs"].Contains(tab.StageBridgeButton));

        StageBridgeSettings.Enabled.V = false;
        Assert.IsFalse(tab.StageBridgeSwitch.value);
        StageBridgeSettings.Enabled.V = true;
        Assert.IsTrue(tab.StageBridgeSwitch.value, "the switch follows stage_bridge_enabled");
    }

    [Test]
    public void Settings_StageBridgeButton_LogsTheState_EvenWithTheSettingOff()
    {
        var lines = new List<string>();
        var sink = StageBridge.Sink;
        StageBridge.Sink = lines.Add;
        try
        {
            StageBridgeSettings.Enabled.V = false;
            var tab = BuildSettings();
            tab.StageBridgeButton.Click();
        }
        finally
        {
            StageBridge.Sink = sink;
        }
        Assert.IsTrue(lines.Any(l => l.StartsWith("[StageBridge] state (button)")), string.Join(Environment.NewLine, lines));
        Assert.IsFalse(lines.Any(l => l.Contains("called ")), "reading only: no call to Redux");
    }
}
