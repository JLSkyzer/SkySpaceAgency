using SkySpaceAgency.UI;
using NUnit.Framework;
using UnityEngine;

public class DragClampTests
{
    static readonly Vector2 Size = new Vector2(350, 500);
    static readonly Rect Screen = new Rect(0, 0, 1920, 1080);

    [Test]
    public void InsideTheScreen_Unchanged()
    {
        Assert.AreEqual(new Vector2(100, 200), DragManipulator.ClampPosition(new Vector2(100, 200), Screen, Size));
    }

    [Test]
    public void PastAnEdge_StopsAtTheEdge()
    {
        Assert.AreEqual(new Vector2(0, 0), DragManipulator.ClampPosition(new Vector2(-50, -50), Screen, Size));
        Assert.AreEqual(new Vector2(1570, 580), DragManipulator.ClampPosition(new Vector2(5000, 5000), Screen, Size));
    }

    [Test]
    public void PanelNotAtTheOrigin_BoundsFollowIt()
    {
        // The panel's content rect is in panel space and may not start at (0, 0).
        var bounds = new Rect(20, 10, 1000, 800);
        Assert.AreEqual(new Vector2(20, 10), DragManipulator.ClampPosition(new Vector2(-500, -500), bounds, Size));
        Assert.AreEqual(new Vector2(670, 310), DragManipulator.ClampPosition(new Vector2(5000, 5000), bounds, Size));
    }

    [Test]
    public void WindowTallerThanScreen_TopStaysOnScreen()
    {
        var tall = new Vector2(350, 1200);
        Assert.AreEqual(new Vector2(0, 0), DragManipulator.ClampPosition(new Vector2(0, 300), Screen, tall));
    }
}

public class WindowSizeSaverTests
{
    static readonly Vector2 Min = new Vector2(300, 250);
    static readonly Vector2 Panel = new Vector2(1920, 1080);

    [Test]
    public void SavedSize_KeptWhenInRange()
    {
        Assert.AreEqual(new Vector2(420, 700), WindowSizeSaver.ClampSize(new Vector2(420, 700), Min, Panel));
    }

    [Test]
    public void SavedSize_ClampedToMinimumAndPanel()
    {
        Assert.AreEqual(new Vector2(300, 250), WindowSizeSaver.ClampSize(new Vector2(100, 100), Min, Panel));
        Assert.AreEqual(new Vector2(1920, 1080), WindowSizeSaver.ClampSize(new Vector2(4000, 3000), Min, Panel));
    }

    [Test]
    public void NeverSavedDimension_LeftToTheUxml()
    {
        // An old settings file has only the height (main_window_size): the width keeps the uxml default.
        Assert.AreEqual(new Vector2(-1, 600), WindowSizeSaver.ClampSize(new Vector2(-1, 600), Min, Panel));
    }

    [Test]
    public void PanelSmallerThanMinimum_MinimumWins()
    {
        Assert.AreEqual(new Vector2(300, 250), WindowSizeSaver.ClampSize(new Vector2(500, 500), Min, new Vector2(200, 200)));
    }

    [Test]
    public void ShouldSave_OnlyARealChangeOfAVisibleWindow()
    {
        Assert.IsFalse(WindowSizeSaver.ShouldSave(new Vector2(350, 500), new Vector2(350.4f, 499.7f)));
        Assert.IsTrue(WindowSizeSaver.ShouldSave(new Vector2(350, 500), new Vector2(352, 500)));
        Assert.IsTrue(WindowSizeSaver.ShouldSave(new Vector2(-1, -1), new Vector2(350, 500)));
        Assert.IsFalse(WindowSizeSaver.ShouldSave(new Vector2(-1, -1), new Vector2(0, 0)), "hidden window");
    }
}
