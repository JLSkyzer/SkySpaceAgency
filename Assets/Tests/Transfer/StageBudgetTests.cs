using System.Collections.Generic;
using K2D2.Transfer;
using NUnit.Framework;

public class StageBudgetTests
{
    static StageDv S(int stage, double dv, bool port = false, bool chute = false, bool command = false) =>
        new StageDv { Stage = stage, DeltaVVac = dv, HasDockingPort = port, HasParachute = chute, HasCommandPart = command };

    static readonly int[] NoCapture = new int[0];

    [Test]
    public void EnoughEverywhere_Ok()
    {
        var r = StageBudget.Check(new[] { S(0, 1000), S(1, 800) }, new[] { 900.0, 0, 500 }, new[] { 2 }, 20);
        Assert.IsTrue(r.Ok, r.Refusal);
        Assert.AreEqual(1800, r.Available, 1e-9);
        Assert.AreEqual(1420, r.Needed, 1e-9);
        Assert.IsNull(r.Refusal);
    }

    // The total would be covered if the capture could borrow from the reserve, but the stages left
    // at the arrival (the injection stage's leftover and the last stage) are too small for the
    // capture plus its reserve. Per-stage Δv is additive, so the capture message is the one given
    // whenever the shortfall falls at the capture.
    [Test]
    public void LastStageTooSmallForTheCapture_Refused()
    {
        var r = StageBudget.Check(new[] { S(0, 1000), S(1, 300) }, new[] { 1100.0, 0, 190 }, new[] { 2 }, 20);
        Assert.IsFalse(r.Ok);
        Assert.AreEqual(1300, r.Available, 1e-9);
        Assert.AreEqual(1310, r.Needed, 1e-9);
        StringAssert.StartsWith("The stages left at the arrival have 200 m/s, the capture needs 210 m/s.", r.Refusal);
        StringAssert.Contains("stage 0: 1000", r.Refusal);
        StringAssert.Contains("stage 1: 300", r.Refusal);
    }

    // A staged arrival: each capture is checked against the stages left when it starts.
    [Test]
    public void SecondCaptureOfAStagedArrival_Refused()
    {
        // capture 1 (300) fits; the leg to the moon (100) fits; capture 2 (150 + 20) does not.
        var r = StageBudget.Check(new[] { S(0, 1000), S(1, 500) }, new[] { 950.0, 300, 100, 150 }, new[] { 1, 3 }, 20);
        Assert.IsFalse(r.Ok);
        StringAssert.StartsWith("The stages left at the arrival have 150 m/s, the capture needs 170 m/s.", r.Refusal);
    }

    [Test]
    public void StepSpanningTwoStages_AllocatedToBoth()
    {
        var r = StageBudget.Check(new[] { S(3, 400), S(2, 0), S(1, 700) }, new[] { 300.0, 500, 200 }, new[] { 2 }, 10);
        Assert.IsTrue(r.Ok, r.Refusal);
        CollectionAssert.AreEqual(
            new List<(int, int)> { (0, 3), (1, 3), (1, 1), (2, 1) },
            r.Allocation);
    }

    [Test]
    public void ZeroStepsAreNotAllocated()
    {
        var r = StageBudget.Check(new[] { S(0, 1000) }, new[] { 0.0, 300, 0 }, NoCapture, 10);
        Assert.IsTrue(r.Ok, r.Refusal);
        CollectionAssert.AreEqual(new List<(int, int)> { (1, 0) }, r.Allocation);
    }

    [Test]
    public void ReserveTipsThePlanIntoARefusal()
    {
        var stages = new[] { S(0, 600), S(1, 500) };
        var steps = new[] { 1000.0, 50 };
        Assert.IsTrue(StageBudget.Check(stages, steps, NoCapture, 40).Ok);
        var r = StageBudget.Check(stages, steps, NoCapture, 60);
        Assert.IsFalse(r.Ok);
        StringAssert.StartsWith("Not enough Δv: needs 1110 m/s, the vessel has 1100 m/s.", r.Refusal);
    }

    [Test]
    public void StepBeyondTheStages_RefusedWithTheTotal()
    {
        var r = StageBudget.Check(new[] { S(0, 500) }, new[] { 800.0, 100 }, new[] { 1 }, 10);
        Assert.IsFalse(r.Ok);
        StringAssert.StartsWith("Not enough Δv: needs 910 m/s, the vessel has 500 m/s.", r.Refusal);
        // The part that no stage covers is allocated to stage -1.
        CollectionAssert.Contains(r.Allocation, (0, -1));
    }

    [Test]
    public void Guard_AllowsAPlainStage()
    {
        Assert.IsTrue(StagingGuard.MayDrop(S(1, 500), out string reason));
        Assert.IsNull(reason);
    }

    [Test]
    public void Guard_RefusesADockingPort()
    {
        Assert.IsFalse(StagingGuard.MayDrop(S(1, 0, port: true), out string reason));
        Assert.AreEqual("a docking port", reason);
    }

    [Test]
    public void Guard_RefusesAParachute()
    {
        Assert.IsFalse(StagingGuard.MayDrop(S(1, 0, chute: true), out string reason));
        Assert.AreEqual("a parachute", reason);
    }

    [Test]
    public void Guard_RefusesTheCommandPart()
    {
        Assert.IsFalse(StagingGuard.MayDrop(S(1, 0, command: true), out string reason));
        Assert.AreEqual("the command part", reason);
    }

    [Test]
    public void Guard_NamesEveryReason()
    {
        Assert.IsFalse(StagingGuard.MayDrop(S(1, 0, port: true, chute: true, command: true), out string reason));
        Assert.AreEqual("a docking port, a parachute and the command part", reason);
    }
}
