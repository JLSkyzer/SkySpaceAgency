using NUnit.Framework;
using SkySpaceAgency.AppUI;
using UnityEngine;

// The new window's pure logic (docs/superpowers/plans/2026-11-19-new-ui-foundations.md, task 1).
public class AppUiLogicTests
{
    [Test]
    public void Scale_ClampsAndSnaps()
    {
        Assert.AreEqual(0.75f, UiScale.Clamp(0.5f), 1e-5f);
        Assert.AreEqual(1.5f, UiScale.Clamp(3f), 1e-5f);
        Assert.AreEqual(1.15f, UiScale.Clamp(1.13f), 1e-5f);
        Assert.AreEqual(1.1f, UiScale.Clamp(1.1f), 1e-5f);
        Assert.AreEqual(1f, UiScale.Clamp(float.NaN));
        Assert.AreEqual(1f, UiScale.Clamp(float.PositiveInfinity));
        Assert.AreEqual(1f, UiScale.Clamp(0));
        Assert.AreEqual(1f, UiScale.Clamp(-2));
    }

    [Test]
    public void Scale_CombinesWithTheGameUiScale()
    {
        Assert.AreEqual(1.32f, UiScale.Combine(1.2f, 1.1f), 1e-5f);
        Assert.AreEqual(1.1f, UiScale.Combine(0, 1.1f), 1e-5f, "no stock scale read: 1");
        Assert.AreEqual(0.9f, UiScale.Combine(0.9f, float.NaN), 1e-5f);
        Assert.IsTrue(UiScale.NeedsApply(1f, 1.1f));
        Assert.IsFalse(UiScale.NeedsApply(1.1f, 1.10001f));
    }

    [Test]
    public void Scale_Percent()
    {
        Assert.AreEqual("110 %", UiScale.Percent(1.1f));
        Assert.AreEqual("75 %", UiScale.Percent(0.1f));
        Assert.AreEqual("100 %", UiScale.Percent(float.NaN));
    }

    static readonly Vector2 Panel = new Vector2(1920, 1080);

    [Test]
    public void Geometry_DefaultWhenNothingSaved()
    {
        // The settings default (-1000, -1000, -1000), as for the classic window's position.
        Rect r = AppWindowGeometry.Restore(new Vector2(-1000, -1000), new Vector2(-1000, -1000), Panel);
        Assert.AreEqual(new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), r);
    }

    [Test]
    public void Geometry_RejectsInvalidSaved()
    {
        Rect r = AppWindowGeometry.Restore(new Vector2(float.NaN, 10), new Vector2(float.PositiveInfinity, 700), Panel);
        Assert.AreEqual(AppWindowGeometry.DefaultPosition, r.position);
        Assert.AreEqual(AppWindowGeometry.DefaultSize, r.size);
        // Smaller than the minimum: grown to it.
        r = AppWindowGeometry.Restore(new Vector2(10, 10), new Vector2(200, 100), Panel);
        Assert.AreEqual(AppWindowGeometry.MinSize, r.size);
    }

    [Test]
    public void Geometry_ClampsToSmallerPanel()
    {
        // Saved on a 2560 x 1440 panel, then the scale went up (the panel is smaller in panel pixels).
        Rect r = AppWindowGeometry.Restore(new Vector2(1500, 900), new Vector2(2000, 1200), new Vector2(1280, 720));
        Assert.AreEqual(new Rect(0, 0, 1280, 720), r);
        r = AppWindowGeometry.Clamp(new Rect(1500, 900, 1000, 600), Panel);
        Assert.AreEqual(new Rect(920, 480, 1000, 600), r, "moved back fully inside");
        // A panel smaller than the minimum size: the panel wins.
        r = AppWindowGeometry.Clamp(new Rect(0, 0, 800, 500), new Vector2(640, 400));
        Assert.AreEqual(new Rect(0, 0, 640, 400), r);
        // No panel yet: untouched.
        Assert.AreEqual(new Rect(5, 5, 10, 10), AppWindowGeometry.Clamp(new Rect(5, 5, 10, 10), Vector2.zero));
    }

    [Test]
    public void Geometry_IsNarrowAndShouldSave()
    {
        Assert.IsTrue(AppWindowGeometry.IsNarrow(900));
        Assert.IsFalse(AppWindowGeometry.IsNarrow(1100));
        Assert.IsFalse(AppWindowGeometry.IsNarrow(0), "hidden window");
        Assert.IsFalse(AppWindowGeometry.ShouldSave(new Rect(0, 0, 100, 100), new Rect(0.4f, 0, 100.5f, 100)));
        Assert.IsTrue(AppWindowGeometry.ShouldSave(new Rect(0, 0, 100, 100), new Rect(0, 0, 102, 100)));
        Assert.IsFalse(AppWindowGeometry.ShouldSave(new Rect(0, 0, 100, 100), new Rect(10, 10, 0, 0)));
    }

    [Test]
    public void Tabs_UnknownSavedTab_FallsBackToFirst()
    {
        var tabs = new TabModel(new[] { "lift", "orbit", "settings" });
        Assert.AreEqual("lift", tabs.Current);
        Assert.AreEqual("orbit", tabs.Restore("orbit"));
        Assert.AreEqual("lift", tabs.Restore("dock"));
        Assert.AreEqual("lift", tabs.Restore(null));
    }

    [Test]
    public void Tabs_DisabledTabNotSelectable()
    {
        var tabs = new TabModel(new[] { "lift", "orbit" });
        tabs.Select("orbit");
        tabs.SetEnabled("orbit", false);
        Assert.AreEqual("lift", tabs.Current, "disabling the current tab moves the selection");
        Assert.IsFalse(tabs.Select("orbit"));
        Assert.IsFalse(tabs.Select("nothing"));
        Assert.AreEqual("lift", tabs.Restore("orbit"));
        tabs.SetEnabled("orbit", true);
        Assert.IsTrue(tabs.Select("orbit"));
    }

    [Test]
    public void Timeline_States()
    {
        // The Lift mockup: six phases, Gravity turn current.
        var t = new PhaseTimelineModel(new[] { "Launch window", "Vertical climb", "Gravity turn", "Coast to Ap", "Circularize", "In orbit" }, 2);
        Assert.AreEqual(PhaseState.Done, t.StateOf(0));
        Assert.AreEqual(PhaseState.Done, t.StateOf(1));
        Assert.AreEqual(PhaseState.Current, t.StateOf(2));
        Assert.AreEqual(PhaseState.Pending, t.StateOf(5));
        Assert.AreEqual(0.4f, t.Progress, 1e-5f);
        Assert.AreEqual(1f, t.PositionOf(5), 1e-5f);
    }

    [Test]
    public void Timeline_ClampsCurrent()
    {
        Assert.AreEqual(2, new PhaseTimelineModel(new[] { "a", "b", "c" }, 9).Current);
        Assert.AreEqual(0, new PhaseTimelineModel(new[] { "a", "b", "c" }, -3).Current);
        var one = new PhaseTimelineModel(new[] { "only" }, 0);
        Assert.AreEqual(0f, one.Progress);
        Assert.AreEqual(0.5f, one.PositionOf(0));
        var none = new PhaseTimelineModel(null, 4);
        Assert.AreEqual(0, none.Current);
        Assert.AreEqual(0f, none.Progress);
    }

    [Test]
    public void Pill_Classes()
    {
        Assert.AreEqual("ssa-pill--ok", StatusKinds.ClassFor(StatusKind.Ok));
        Assert.AreEqual("ssa-pill--alert", StatusKinds.ClassFor(StatusKind.Alert));
        Assert.AreEqual("ssa-pill--info", StatusKinds.ClassFor(StatusKind.Info));
        Assert.AreEqual("ssa-pill--neutral", StatusKinds.ClassFor(StatusKind.Neutral));
        Assert.AreEqual(4, StatusKinds.AllClasses.Length);
    }
}
