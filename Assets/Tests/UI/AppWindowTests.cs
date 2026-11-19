using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// The new window laid out without the game (AppUiHarness), plan 2026-11-19-new-ui-foundations, task 3.
public class AppWindowTests
{
    static readonly Vector2 FullHd = new Vector2(1920, 1080);

    static IEnumerable<TestCaseData> Tabs()
    {
        foreach (var (code, _) in AppDemo.FlightTabs)
            yield return new TestCaseData(code).SetName("Demo_DefaultSize_HasNoLayoutIssue_" + code);
        yield return new TestCaseData("settings").SetName("Demo_DefaultSize_HasNoLayoutIssue_settings");
    }

    static IEnumerable<TestCaseData> NarrowTabs()
    {
        foreach (string code in new[] { "lift", "orbit", "settings" })
            yield return new TestCaseData(code).SetName("Demo_MinimumSize_HasNoLayoutIssue_" + code);
    }

    static void AssertNoIssue(AppUiHarness.Opened o, string what)
    {
        var issues = AppUiHarness.Check(o);
        foreach (string issue in issues)
            Debug.Log($"[AppUi] {what}: {issue}");
        Assert.IsEmpty(issues, $"{issues.Count} layout issue(s) in {what}:\n" + string.Join("\n", issues));
    }

    [TestCaseSource(nameof(Tabs))]
    public void Demo_DefaultSize(string tab)
    {
        using var o = AppUiHarness.Open(tab, new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        Assert.AreEqual(AppWindowGeometry.DefaultSize.x, o.View.layout.width, 0.5f);
        Assert.AreEqual(AppWindowGeometry.DefaultSize.y, o.View.layout.height, 0.5f);
        Assert.IsFalse(o.View.Narrow);
        AssertNoIssue(o, tab + " default");
    }

    [TestCaseSource(nameof(NarrowTabs))]
    public void Demo_MinimumSize(string tab)
    {
        using var o = AppUiHarness.Open(tab, new Rect(Vector2.zero, AppWindowGeometry.MinSize), FullHd);
        Assert.IsTrue(o.View.Narrow, "narrow below " + AppWindowGeometry.NarrowWidth);
        Assert.IsTrue(o.View.ClassListContains(AppWindowView.NarrowClass));
        AssertNoIssue(o, tab + " minimum");
    }

    [Test]
    public void Demo_WideColumns_FollowTheWidth()
    {
        using var small = AppUiHarness.Open("lift", new Rect(0, 0, 1200, 700), FullHd);
        using var big = AppUiHarness.Open("lift", new Rect(0, 0, 1800, 1000), FullHd);
        var page = (AppPage)small.View.PageFor("lift");
        var bigPage = (AppPage)big.View.PageFor("lift");
        Assert.AreEqual(340, page.Plan.layout.width, 1, "the plan column keeps its width");
        Assert.AreEqual(340, bigPage.Plan.layout.width, 1);
        Assert.Greater(bigPage.View.layout.width, page.View.layout.width + 500, "the view takes the extra width");
        Assert.Greater(bigPage.View.layout.height, page.View.layout.height + 150, "and the extra height (the 700 px one already scrolls)");
        Assert.Less(page.Plan.layout.xMax, page.View.layout.xMin);
        Assert.Less(page.View.layout.xMax, page.Side.layout.xMin, "three columns side by side");
    }

    [Test]
    public void Demo_HasHeaderTabsAndLiftPage()
    {
        using var o = AppUiHarness.Open("lift", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        var v = o.View;
        Assert.AreEqual(ModIdentity.Title.ToUpperInvariant(), v.Header.Q<Label>(className: "ssa-brand__title").text);
        Assert.AreEqual("FLY FURTHER", v.Header.Q<Label>(className: "ssa-brand__slogan").text);
        Assert.AreEqual(4, v.Header.Chips.childCount);
        Assert.AreEqual(8, v.Tabs.Model.Codes.Count);
        Assert.AreEqual("lift", v.Tabs.Current);
        var page = (AppPage)v.PageFor("lift");
        Assert.AreEqual(DisplayStyle.Flex, page.resolvedStyle.display);
        Assert.AreEqual(6, page.Timeline.Dots.Count);
        Assert.AreEqual(2, page.Timeline.Model.Current);
        Assert.AreEqual(3, page.Actions.Buttons.Count);
        Assert.AreEqual(AppButton.Kind.Main, page.Actions.Buttons.Last().ButtonKind);
        Assert.AreEqual(3, page.Query<AppSwitch>().ToList().Count);
        Assert.AreEqual(3, page.Query<AppSlider>().ToList().Count);
        Assert.AreEqual(4, page.Query<AppTile>().ToList().Count);
        Assert.IsNotNull(page.Q<AscentProfileView>());
        // Right-aligned SETTINGS is right of the badge, which is right of NODES.
        Assert.Greater(v.Tabs.TabElement("settings").worldBound.xMin, v.Tabs.TabElement("nodes").worldBound.xMax + 100);
        // Switching tab hides the others.
        Assert.IsTrue(v.Select("settings"));
        o.Layout();
        Assert.AreEqual(DisplayStyle.None, page.resolvedStyle.display);
    }

    [Test]
    public void Demo_SettingsScale_RaisesSnappedScale()
    {
        using var o = AppUiHarness.Open("settings", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), FullHd);
        float got = -1;
        o.Hooks.SetScale = s => got = s;
        var slider = o.View.Q<AppSlider>("interface-scale");
        slider.Value = 112.3f;
        Assert.AreEqual(1.1f, got, 1e-4f);
        Assert.AreEqual(110f, slider.Value, 1e-3f, "the slider snaps to 5 %");
        Assert.AreEqual("110 %", slider.Q<Label>(className: "ssa-slider__value").text);
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
    public void ClassicWindow_HasNewUiButton()
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ModIdentity.MainWindowAddress);
        var root = asset.Instantiate();
        var toolbar = root.Q("toolbar");
        Assert.IsNotNull(toolbar.Q<Button>("new-ui-button"));
        Assert.IsNotNull(toolbar.Q<Button>("info-toggle"), "the About button is still there");
    }

    [Test]
    public void AppUxml_IsAnAddressablesEntry()
    {
        string group = System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath,
            "AddressableAssetsData/AssetGroups/addressables_" + ModIdentity.ModId + "_all.asset"));
        StringAssert.Contains("m_Address: " + ModIdentity.AppWindowAddress, group);
        StringAssert.Contains("m_Address: " + ModIdentity.MainWindowAddress, group);
    }
}
