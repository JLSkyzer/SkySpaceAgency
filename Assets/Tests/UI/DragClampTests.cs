using K2D2.UI;
using NUnit.Framework;
using UnityEngine;

public class DragClampTests
{
    static readonly Vector2 Size = new Vector2(350, 500);
    static readonly Vector2 Screen = new Vector2(1920, 1080);

    [Test]
    public void OriginAtTopLeft_RangeIsTheWholeScreen()
    {
        var origin = Vector2.zero;
        Assert.AreEqual(new Vector2(0, 0), DragManipulator.ClampTranslation(new Vector2(-50, -50), origin, Size, Screen));
        Assert.AreEqual(new Vector2(1570, 580), DragManipulator.ClampTranslation(new Vector2(5000, 5000), origin, Size, Screen));
        Assert.AreEqual(new Vector2(100, 200), DragManipulator.ClampTranslation(new Vector2(100, 200), origin, Size, Screen));
    }

    [Test]
    public void LaidOutLowOnScreen_CanStillGoUpToTheTop()
    {
        // The host panel lays the window out 560 px down: translation -560 puts it at the top.
        var origin = new Vector2(11, 560);
        Assert.AreEqual(new Vector2(-11, -560), DragManipulator.ClampTranslation(new Vector2(-2000, -2000), origin, Size, Screen));
        Assert.AreEqual(new Vector2(1559, 20), DragManipulator.ClampTranslation(new Vector2(5000, 5000), origin, Size, Screen));
    }

    [Test]
    public void WindowTallerThanScreen_TopStaysOnScreen()
    {
        var tall = new Vector2(350, 1200);
        Assert.AreEqual(new Vector2(0, -36), DragManipulator.ClampTranslation(new Vector2(0, 300), new Vector2(0, 36), tall, Screen));
    }
}
