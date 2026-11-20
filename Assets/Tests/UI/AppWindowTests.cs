using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// The new window laid out without the game (AppUiHarness): plans 2026-11-19-new-ui-foundations (task 3) and
// 2026-11-19-new-ui-tab-migration (every tab at its mockup, default and minimum size).
public class AppWindowTests
{
    static readonly Vector2 FullHd = new Vector2(1920, 1080);

    static readonly string[] Codes = AppTabs.Create(AppTabContext.Editor()).Select(t => t.Code).ToArray();

    static IEnumerable<TestCaseData> Tabs()
    {
        foreach (string code in Codes)
            yield return new TestCaseData(code).SetName("Tab_" + code + "_DefaultSize");
    }

    static IEnumerable<TestCaseData> MockupTabs()
    {
        foreach (string code in Codes)
            yield return new TestCaseData(code).SetName("Tab_" + code + "_MockupSize");
    }

    static IEnumerable<TestCaseData> NarrowTabs()
    {
        foreach (string code in Codes)
            yield return new TestCaseData(code).SetName("Tab_" + code + "_MinimumSize");
    }

    static void AssertNoIssue(AppUiHarness.Opened o, string what)
    {
        var issues = AppUiHarness.Check(o);
        foreach (string issue in issues)
            Debug.Log($"[AppUi] {what}: {issue}");
        Assert.IsEmpty(issues, $"{issues.Count} layout issue(s) in {what}:\n" + string.Join("\n", issues));
    }

    [TestCaseSource(nameof(Tabs))]
    public void DefaultSize(string tab)
    {
        using var o = AppUiHarness.Open(tab, new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        Assert.AreEqual(AppWindowGeometry.DefaultSize.x, o.View.layout.width, 0.5f);
        Assert.AreEqual(AppWindowGeometry.DefaultSize.y, o.View.layout.height, 0.5f);
        Assert.IsFalse(o.View.Narrow);
        AssertNoIssue(o, tab + " default");
    }

    // As the captures: 1600 x 900 at the mockups' scale, sample data.
    [TestCaseSource(nameof(MockupTabs))]
    public void MockupSize(string tab)
    {
        var shot = AppUiHarness.Shots.First(s => s.Name == tab + "_mockup_size");
        using var o = AppUiHarness.Open(shot);
        AssertNoIssue(o, tab + " mockup size");
    }

    [TestCaseSource(nameof(NarrowTabs))]
    public void MinimumSize(string tab)
    {
        using var o = AppUiHarness.Open(tab, new Rect(Vector2.zero, AppWindowGeometry.MinSize), FullHd);
        Assert.IsTrue(o.View.Narrow, "narrow below " + AppWindowGeometry.NarrowWidth);
        Assert.IsTrue(o.View.ClassListContains(AppWindowView.NarrowClass));
        AssertNoIssue(o, tab + " minimum");
    }

    [Test]
    public void AllTabs_BuildAndTickWithoutTheGame()
    {
        using var o = AppUiHarness.Open("lift", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        foreach (var tab in o.View.TabList)
        {
            Assert.IsTrue(o.View.Select(tab.Code) || o.View.Tabs.Current == tab.Code, tab.Code);
            Assert.DoesNotThrow(() => o.View.Tick(), tab.Code);
            Assert.DoesNotThrow(() => { var _ = tab.IsRunning; var __ = tab.Hud; }, tab.Code);
        }
        Assert.IsFalse(o.View.AnyRunning, "no pilot runs without the game");
    }

    [Test]
    public void Demo_WideColumns_FollowTheWidth()
    {
        using var small = AppUiHarness.Open("lift", new Rect(0, 0, 1200, 700), FullHd);
        using var big = AppUiHarness.Open("lift", new Rect(0, 0, 1800, 1000), FullHd);
        var page = (AppPage)small.View.PageFor("lift");
        var bigPage = (AppPage)big.View.PageFor("lift");
        Assert.AreEqual(page.Plan.layout.width, bigPage.Plan.layout.width, 1, "the plan column keeps its width");
        Assert.Greater(bigPage.View.layout.width, page.View.layout.width + 500, "the view takes the extra width");
        Assert.Less(page.Plan.layout.xMax, page.View.layout.xMin);
        Assert.Less(page.View.layout.xMax, page.Side.layout.xMin, "three columns side by side");
    }

    [Test]
    public void HeaderAndTabs()
    {
        using var o = AppUiHarness.Open("lift", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        var v = o.View;
        Assert.AreEqual(ModIdentity.Title.ToUpperInvariant(), v.Header.Q<Label>(className: "ssa-brand__title").text);
        Assert.AreEqual("FLY FURTHER", v.Header.Q<Label>(className: "ssa-brand__slogan").text);
        Assert.AreEqual(4, v.Header.Chips.childCount);
        CollectionAssert.AreEqual(new[] { "lift", "orbit", "transfer", "rendezvous", "land", "return", "nodes", "settings" }, v.Tabs.Model.Codes);
        Assert.AreEqual("lift", v.Tabs.Current);
        Assert.Greater(v.Tabs.TabElement("settings").worldBound.xMin, v.Tabs.TabElement("nodes").worldBound.xMax + 100, "SETTINGS on the right");
        var page = v.PageFor("lift");
        Assert.IsTrue(v.Select("settings"));
        o.Layout();
        Assert.AreEqual(DisplayStyle.None, page.resolvedStyle.display, "switching tab hides the others");
        Assert.IsTrue(v.Select("return"));
        Assert.AreEqual(v.TabFor("return").Badge?.ToUpperInvariant() ?? "", v.Tabs.Q<Label>(className: "ssa-badge__label").text, "the badge follows the tab");
    }

    // Known defect of the foundations: the slider track started about 7 mockup px right of its label.
    [Test]
    public void Slider_TrackStartsUnderItsLabel()
    {
        using var o = AppUiHarness.Open("lift", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        var slider = o.View.PageFor("lift").Query<AppSlider>().ToList().First(s => UiLayoutHarness.IsDisplayed(s) && s.worldBound.width > 0);
        var tracker = slider.Q(className: "unity-base-slider__tracker");
        var fill = slider.Q(className: "ssa-slider__fill");
        Debug.Log($"[AppUi] slider {slider.worldBound} input {slider.Input.worldBound} tracker {tracker.worldBound} fill {fill.worldBound}");
        Assert.AreEqual(slider.worldBound.xMin, tracker.worldBound.xMin, 1, "track starts under the label");
        Assert.AreEqual(slider.worldBound.xMax, tracker.worldBound.xMax, 1, "and ends under the value");
    }

    [Test]
    public void HeaderChips_Format()
    {
        Assert.AreEqual("4 812 m/s", AppHeaderData.MetresPerSecond(4812.4));
        Assert.AreEqual("—", AppHeaderData.MetresPerSecond(double.NaN));
        Assert.AreEqual("Sub orbital", AppHeaderData.Words("SubOrbital"));
        Assert.AreEqual("Pre launch", AppHeaderData.Words("PreLaunch"));
        Assert.AreEqual("Orbiting", AppHeaderData.Words("Orbiting"));
    }

    [Test]
    public void Minimise_KeepsOnlyTheHeader()
    {
        using var o = AppUiHarness.Open("lift", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        bool? raised = null;
        o.View.minimisedChanged += m => raised = m;
        o.View.SetMinimised(true);
        Assert.AreEqual(true, raised);
        o.Layout();
        Assert.Less(o.View.layout.height, 120);
        Assert.AreEqual(DisplayStyle.None, o.View.Tabs.resolvedStyle.display);
        o.View.SetMinimised(false);
        Assert.AreEqual(false, raised);
        o.Layout();
        Assert.AreEqual(AppWindowGeometry.DefaultSize.y, o.View.layout.height, 0.5f);
        Assert.AreEqual(AppWindowGeometry.MinSize.y, o.View.style.minHeight.value.value, 0.5f, "the minimum height comes back too");
        // Minimised before any height was set inline: restoring leaves no inline height behind.
        var fresh = new AppWindowView("t", "s");
        fresh.SetMinimised(true);
        fresh.SetMinimised(false);
        Assert.AreEqual(StyleKeyword.Null, fresh.style.height.keyword);
    }

    [Test]
    public void Scale_AppliedToPanelSettings()
    {
        using var o = AppUiHarness.Open("lift", new Rect(0, 0, 900, 500), new Vector2(1600, 900));
        var tree = ((IPanel)o.Panel).visualTree;
        Assert.AreEqual(1600, tree.layout.width, 0.5f);
        Assert.IsTrue(PanelScale.Apply(o.Settings, 1.25f));
        Assert.IsFalse(PanelScale.Apply(o.Settings, 1.25f), "already applied");
        o.Layout();
        Assert.AreEqual(1280, tree.layout.width, 1, "the panel is 1 / scale as wide in panel pixels");
    }

    [Test]
    public void Theme_IsTheSdkKerbalUiTheme()
    {
        Assert.IsNotNull(UiLayoutHarness.LoadTheme(), UiLayoutHarness.ThemePath);
    }

    // The classic window is gone (2026-11-20): neither its uxml nor its Addressables entry exist.
    [Test]
    public void ClassicWindow_IsGone()
    {
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/" + ModIdentity.ModId + "_UI/" + ModIdentity.ModId + "_Window.uxml"));
        string group = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath,
            "AddressableAssetsData/AssetGroups/addressables_" + ModIdentity.ModId + "_all.asset"));
        StringAssert.DoesNotContain("_Window.uxml", group);
    }

    [Test]
    public void AppUxml_IsAnAddressablesEntry()
    {
        string group = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath,
            "AddressableAssetsData/AssetGroups/addressables_" + ModIdentity.ModId + "_all.asset"));
        StringAssert.Contains("m_Address: " + ModIdentity.AppWindowAddress, group);
    }
}
