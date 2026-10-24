using System.Collections.Generic;
using K2D2.Rendezvous;
using NUnit.Framework;

// A docking that fails near the target holds position before stopping; the docked message
// counts only for our docking.
public class DockingStopTests
{
    [Test]
    public void SettleBeforeFailing_CloseIn_OrAfterTheLastAbort()
    {
        Assert.IsTrue(RendezvousFlight.SettleBeforeFailing(12, false, true), "a failure inside 50 m holds first");
        Assert.IsTrue(RendezvousFlight.SettleBeforeFailing(49.9, false, true));
        Assert.IsFalse(RendezvousFlight.SettleBeforeFailing(50, false, true), "farther out it stops at once");
        Assert.IsFalse(RendezvousFlight.SettleBeforeFailing(400, false, true));
        Assert.IsTrue(RendezvousFlight.SettleBeforeFailing(400, true, true), "the guidance's Aborted always holds");
        Assert.IsFalse(RendezvousFlight.SettleBeforeFailing(double.NaN, false, true));
    }

    [Test]
    public void SettleBeforeFailing_NeedsAWorkingTranslation()
    {
        Assert.IsFalse(RendezvousFlight.SettleBeforeFailing(5, true, false));
        Assert.IsFalse(RendezvousFlight.SettleBeforeFailing(5, false, false));
    }

    [Test]
    public void SettleOver_WhenSlowEnough_OrAfterTheTimeout()
    {
        Assert.IsFalse(RendezvousFlight.SettleOver(0.3, 0.05, 10, out string why));
        Assert.IsNull(why);
        Assert.IsTrue(RendezvousFlight.SettleOver(0.04, 0.05, 10, out why));
        StringAssert.Contains("held at 0.040 m/s", why);
        Assert.IsFalse(RendezvousFlight.SettleOver(0.3, 0.05, RendezvousFlight.SettleTimeout, out _));
        Assert.IsTrue(RendezvousFlight.SettleOver(0.3, 0.05, RendezvousFlight.SettleTimeout + 0.1, out why));
        StringAssert.Contains("after 60 s", why);
        Assert.IsTrue(RendezvousFlight.SettleOver(double.NaN, 0.05, 61, out _), "an unread speed still ends on time");
    }

    [Test]
    public void DockedMessage_CountsOnlyForOurVesselTheTargetOrOurPort()
    {
        var watched = new HashSet<string> { "ours", "target" };
        var none = new List<string>();
        Assert.IsTrue(DockingReads.DockedIsOurs("ours", "x", none, none, watched, "port"));
        Assert.IsTrue(DockingReads.DockedIsOurs("x", "target", none, none, watched, "port"));
        Assert.IsTrue(DockingReads.DockedIsOurs("new1", "new2", new List<string> { "a", "port" }, none, watched, "port"),
            "a merged vessel with a new id still holds our port");
        Assert.IsFalse(DockingReads.DockedIsOurs("x", "y", new List<string> { "a" }, new List<string> { "b" }, watched, "port"));
        Assert.IsFalse(DockingReads.DockedIsOurs(null, null, null, null, watched, null));
        Assert.IsFalse(DockingReads.DockedIsOurs("x", "y", none, none, null, null));
    }
}
