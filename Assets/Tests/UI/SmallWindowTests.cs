using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using UnityEngine;
using UnityEngine.UIElements;

// "Small window: Reflow / Scale" (plan 2026-11-19-new-ui-tab-migration, addendum 2026-11-20).
public class SmallWindowTests
{
    static readonly Vector2 FullHd = new Vector2(1920, 1080);
    static readonly string[] Codes = AppTabs.Create(AppTabContext.Editor()).Select(t => t.Code).ToArray();

    // ---- the factor ----

    [Test]
    public void Factor_IsOneAtTheReferenceOrBigger()
    {
        Assert.AreEqual(1f, SmallWindowScale.Factor(SmallWindowScale.ReferenceInner));
        Assert.AreEqual(1f, SmallWindowScale.Factor(new Vector2(2500, 1400)), "no enlarging");
        Assert.AreEqual(1f, SmallWindowScale.Factor(new Vector2(3000, SmallWindowScale.ReferenceInner.y)));
    }

    [Test]
    public void Factor_IsTheSmallerRatio()
    {
        Vector2 r = SmallWindowScale.ReferenceInner;
        Assert.AreEqual(0.8f, SmallWindowScale.Factor(new Vector2(r.x * 0.8f, r.y * 0.9f)), 1e-4f);
        Assert.AreEqual(0.7f, SmallWindowScale.Factor(new Vector2(r.x * 0.9f, r.y * 0.7f)), 1e-4f);
        // The minimum window (760 x 480 minus the frame) is above the floor.
        float atMin = SmallWindowScale.Factor(new Vector2(760 - 38, 480 - 25));
        Assert.Greater(atMin, SmallWindowScale.Floor);
        Assert.Less(atMin, 0.7f);
    }

    [Test]
    public void Factor_NeverBelowTheFloor_AndSurvivesBadSizes()
    {
        Assert.AreEqual(SmallWindowScale.Floor, SmallWindowScale.Factor(new Vector2(100, 100)));
        foreach (var bad in new[] { Vector2.zero, new Vector2(-5, 300), new Vector2(float.NaN, 300), new Vector2(float.PositiveInfinity, 300) })
            Assert.AreEqual(1f, SmallWindowScale.Factor(bad), bad.ToString());
    }

    [Test]
    public void LogicalSize_FillsTheWindowAndIsNeverSmallerThanTheReference()
    {
        Vector2 r = SmallWindowScale.ReferenceInner;
        foreach (var inner in new[] { new Vector2(722, 455), new Vector2(962, 575), new Vector2(1322, 740), new Vector2(1000, 740), new Vector2(1322, 400) })
        {
            float s = SmallWindowScale.Factor(inner);
            Vector2 logical = SmallWindowScale.LogicalSize(inner, s);
            Assert.AreEqual(inner.x, logical.x * s, 0.01f);
            Assert.AreEqual(inner.y, logical.y * s, 0.01f);
            if (s > SmallWindowScale.Floor + 1e-4f)
            {
                Assert.GreaterOrEqual(logical.x, r.x - 0.5f, inner.ToString());
                Assert.GreaterOrEqual(logical.y, r.y - 0.5f, inner.ToString());
            }
        }
        Assert.AreEqual(new Vector2(10, 20), SmallWindowScale.LogicalSize(new Vector2(10, 20), 0), "a bad factor counts as 1");
    }

    [Test]
    public void Setting_DefaultsToScale()
    {
        Assert.AreEqual(SmallWindowMode.Scale, AppSettings.SmallWindow.V);
        Assert.AreEqual("app_small_window", AppSettings.SmallWindow.path);
    }

    // ---- the window ----

    static AppUiHarness.Opened Open(string tab, Vector2 size, SmallWindowMode mode) =>
        AppUiHarness.Open(tab, new Rect(Vector2.zero, size), FullHd, 1, mode);

    [Test]
    public void Scale_ShrinksTheContentAndKeepsTheThreeColumns()
    {
        using var o = Open("lift", new Vector2(1000, 600), SmallWindowMode.Scale);
        var v = o.View;
        Assert.AreEqual(SmallWindowMode.Scale, v.SmallMode);
        Assert.Less(v.ContentScale, 1f);
        Assert.AreEqual(SmallWindowScale.Factor(v.contentRect.size), v.ContentScale, 1e-4f);
        Assert.IsFalse(v.Narrow, "the layout is the big one");
        var page = (AppPage)v.PageFor("lift");
        Assert.Less(page.Plan.layout.xMax, page.View.layout.xMin);
        Assert.Less(page.View.layout.xMax, page.Side.layout.xMin, "three columns side by side");
        Assert.IsNotNull(page.Timeline.parent, "the timeline stays");
        // The window itself keeps its real size (what the resize handle and the saved size read).
        Assert.AreEqual(1000, v.layout.width, 0.5f);
        Assert.AreEqual(600, v.layout.height, 0.5f);
        // The content fills the inner box exactly once drawn.
        var body = v.Q("app-body");
        Assert.AreEqual(v.contentRect.width, body.worldBound.width, 1.5f);
        Assert.AreEqual(v.contentRect.height, body.worldBound.height, 1.5f);
        Assert.AreEqual(v.LogicalSize.x, body.layout.width, 0.5f);
    }

    [Test]
    public void Scale_AtTheDefaultSize_ChangesNothing()
    {
        using var o = Open("lift", AppWindowGeometry.DefaultSize, SmallWindowMode.Scale);
        Assert.AreEqual(1f, o.View.ContentScale, 1e-4f);
        using var reflow = Open("lift", AppWindowGeometry.DefaultSize, SmallWindowMode.Reflow);
        var a = (AppPage)o.View.PageFor("lift");
        var b = (AppPage)reflow.View.PageFor("lift");
        Assert.AreEqual(b.Plan.layout.width, a.Plan.layout.width, 0.5f);
        Assert.AreEqual(b.View.layout.width, a.View.layout.width, 0.5f);
    }

    [Test]
    public void Reflow_IsTheCurrentBehaviour_NarrowBelow1100()
    {
        using var o = Open("lift", AppWindowGeometry.MinSize, SmallWindowMode.Reflow);
        Assert.AreEqual(1f, o.View.ContentScale);
        Assert.IsTrue(o.View.Narrow);
        Assert.IsTrue(o.View.ClassListContains(AppWindowView.NarrowClass));
    }

    [Test]
    public void ModeSwitch_RebuildsTheLayout_BothWays()
    {
        using var o = Open("land", new Vector2(900, 560), SmallWindowMode.Reflow);
        Assert.IsTrue(o.View.Narrow);
        o.View.SetSmallWindowMode(SmallWindowMode.Scale);
        o.Layout();
        Assert.IsFalse(o.View.Narrow);
        Assert.Less(o.View.ContentScale, 1f);
        o.View.SetSmallWindowMode(SmallWindowMode.Reflow);
        o.Layout();
        Assert.IsTrue(o.View.Narrow);
        Assert.AreEqual(1f, o.View.ContentScale);
        Assert.AreEqual(StyleKeyword.Null, o.View.Q("app-body").style.width.keyword, "no leftover inline size");
    }

    [Test]
    public void Scale_MinimisedShowsTheHeaderAtFullSize_AndComesBack()
    {
        using var o = Open("lift", AppWindowGeometry.MinSize, SmallWindowMode.Scale);
        float s = o.View.ContentScale;
        o.View.SetMinimised(true);
        o.Layout();
        Assert.AreEqual(1f, o.View.ContentScale);
        Assert.Less(o.View.layout.height, 120);
        o.View.SetMinimised(false);
        o.Layout();
        Assert.AreEqual(s, o.View.ContentScale, 1e-3f);
        Assert.AreEqual(AppWindowGeometry.MinSize.y, o.View.layout.height, 0.5f);
    }

    static IEnumerable<TestCaseData> ScaleCases()
    {
        foreach (string code in Codes)
            foreach (var size in new[] { new Vector2(760, 480), new Vector2(1000, 600), new Vector2(1360, 765) })
                yield return new TestCaseData(code, size).SetName($"Scale_{code}_{size.x:0}x{size.y:0}_HasNoLayoutIssue");
    }

    [TestCaseSource(nameof(ScaleCases))]
    public void Scale_HasNoLayoutIssue(string tab, Vector2 size)
    {
        using var o = Open(tab, size, SmallWindowMode.Scale);
        o.View.Select(tab);
        o.Layout();
        var issues = AppUiHarness.Check(o);
        foreach (string issue in issues) Debug.Log($"[SmallWindow] {tab} {size}: {issue}");
        Assert.IsEmpty(issues, $"{issues.Count} layout issue(s) in {tab} at {size}:\n" + string.Join("\n", issues));
    }
}
