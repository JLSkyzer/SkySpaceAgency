using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

// Lays out every tab of the window at its real size (UiLayoutHarness) and fails on overlapping
// siblings, squeezed controls, clipped labels and elements outside their parent.
public class UiLayoutTests
{
    static IEnumerable<TestCaseData> Cases()
    {
        foreach (var s in UiLayoutHarness.Scenarios)
            foreach (float h in new[] { UiLayoutHarness.WindowHeight, UiLayoutHarness.TallWindowHeight })
                yield return new TestCaseData(s, h).SetName($"Layout_{s.Name}_{h:0}px");
    }

    [Test]
    public void Theme_IsTheSdkKerbalUiTheme()
    {
        Assert.IsNotNull(UiLayoutHarness.LoadTheme(), UiLayoutHarness.ThemePath);
    }

    [Test]
    public void Window_HasReduxResizeHandleOnMainFrame()
    {
        using var w = UiLayoutHarness.Open(UiLayoutHarness.Scenarios[0], UiLayoutHarness.WindowHeight);
        var handle = w.Shell.Q(className: "window-resize-handle");
        Assert.IsNotNull(handle, "WindowResizeHandle missing from SkySpaceAgency_Window.uxml");
        Assert.AreEqual("UitkForKsp2.Controls.WindowResizeHandle", handle.GetType().FullName);
        Assert.AreEqual("MainFrame", handle.GetType().GetProperty("TargetName")?.GetValue(handle));
    }

    // Lesson 2026-10-2x: KerbalUI.uss grows every dropdown (flex-grow 1). The Lift tab's ascent
    // profile dropdown (K2-D2 1.4.0) is one row high, like the goal dropdown above it.
    [Test]
    public void Lift_AscentProfileDrop_IsOneRowHigh()
    {
        var scenario = UiLayoutHarness.Scenarios.First(s => s.Name == "lift_gravity_turn_open");
        using var w = UiLayoutHarness.Open(scenario, UiLayoutHarness.TallWindowHeight);
        var goal = w.Page.Q<DropdownField>("goal");
        var drop = w.Page.Q<DropdownField>("ascent_profile_drop");
        Assert.IsTrue(UiLayoutHarness.IsDisplayed(drop));
        Assert.AreEqual(goal.layout.height, drop.layout.height, 0.5f);
        Assert.LessOrEqual(drop.layout.height, 40f);
        Assert.AreEqual(goal.layout.width, drop.layout.width, 0.5f);
    }

    [TestCaseSource(nameof(Cases))]
    public void Tab_HasNoLayoutIssue(UiLayoutHarness.Scenario scenario, float height)
    {
        using var w = UiLayoutHarness.Open(scenario, height);
        // The harness itself: the window has its real size and the page was laid out.
        Assert.AreEqual(UiLayoutHarness.WindowWidth, w.Shell.layout.width, 0.5f, "window width");
        Assert.Greater(w.Page.layout.height, 20, "the page was not laid out");

        List<string> issues = UiLayoutHarness.Check(w);
        foreach (string issue in issues)
            Debug.Log($"[UiLayout] {scenario.Name} @{height:0}: {issue}");
        Assert.IsEmpty(issues, $"{issues.Count} layout issue(s) in {scenario.Name}:\n" + string.Join("\n", issues));
    }
}
