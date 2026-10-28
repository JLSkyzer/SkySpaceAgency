using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

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
