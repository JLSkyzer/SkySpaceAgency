using System.Collections.Generic;
using System.Linq;
using K2D2.Landing.Precision;
using K2D2.Transfer;
using NUnit.Framework;

public class StageDropTests
{
    // A part of the test vessel: id, parent, the stage that activates it, and its kind.
    static DropPart Part(string id, string parent, int stage = -1, bool decoupler = false, string detachesFrom = null,
        bool control = false, bool command = false, bool chute = false, bool port = false) =>
        new DropPart
        {
            Part = new CapsulePart { Id = id, ParentId = parent, IsDecoupler = decoupler, DetachesFromId = detachesFrom, IsParachute = chute, MassT = 1 },
            Name = id,
            ActivationStage = stage,
            IsControl = control,
            IsCommand = command || control,
            IsDockingPort = port,
        };

    // pod (control) - chute - dec2 (stage 1, separates from tank2) - tank2 - engine2 (stage 1)
    //   - dec1 (stage 2, separates from tank1) - tank1 - engine1 (stage 3)
    static List<DropPart> TwoStageRocket(bool chuteOnLowerStage = false, bool probeOnLowerStage = false, bool portOnLowerStage = false)
    {
        var parts = new List<DropPart>
        {
            Part("pod", null, control: true),
            Part("chute", "pod", stage: 0, chute: true),
            Part("dec2", "pod", stage: 1, decoupler: true, detachesFrom: "tank2"),
            Part("tank2", "dec2"),
            Part("engine2", "tank2", stage: 1),
            Part("dec1", "tank2", stage: 2, decoupler: true, detachesFrom: "tank1"),
            Part("tank1", "dec1"),
            Part("engine1", "tank1", stage: 3),
        };
        if (chuteOnLowerStage) parts.Add(Part("chute1", "tank1", chute: true));
        if (probeOnLowerStage) parts.Add(Part("probe1", "tank1", command: true));
        if (portOnLowerStage) parts.Add(Part("port1", "tank1", port: true));
        return parts;
    }

    [Test]
    public void EmptyLowerStage_DroppedByTopology()
    {
        StageDropResult r = StageDrop.Decide(TwoStageRocket(), 2);
        Assert.IsTrue(r.MayStage, r.Reason);
        Assert.AreEqual(2, r.Stage);
        CollectionAssert.AreEquivalent(new[] { "tank1", "engine1" }, r.DroppedIds);
        CollectionAssert.AreEquivalent(new[] { "dec1" }, r.Decouplers);
        Assert.IsFalse(r.Flags.HasCommandPart || r.Flags.HasParachute || r.Flags.HasDockingPort);
        Assert.AreEqual(2, r.Flags.Stage);
    }

    [Test]
    public void StageWithoutDecoupler_DropsNothing()
    {
        // Stage 3 only ignites engine1.
        StageDropResult r = StageDrop.Decide(TwoStageRocket(), 3);
        Assert.IsTrue(r.MayStage, r.Reason);
        Assert.AreEqual(0, r.DroppedIds.Count);
    }

    [Test]
    public void DroppedProbeCore_Refused()
    {
        StageDropResult r = StageDrop.Decide(TwoStageRocket(probeOnLowerStage: true), 2);
        Assert.IsFalse(r.MayStage);
        Assert.IsTrue(r.Flags.HasCommandPart);
        Assert.AreEqual("Burn stopped: the next stage would drop the command part. Stage manually.", r.Message);
        CollectionAssert.Contains(r.DroppedIds, "probe1");
    }

    [Test]
    public void DroppedChuteOrPort_Refused()
    {
        StageDropResult chute = StageDrop.Decide(TwoStageRocket(chuteOnLowerStage: true), 2);
        Assert.IsFalse(chute.MayStage);
        Assert.IsTrue(chute.Flags.HasParachute);
        StageDropResult port = StageDrop.Decide(TwoStageRocket(portOnLowerStage: true), 2);
        Assert.IsFalse(port.MayStage);
        Assert.AreEqual("Burn stopped: the next stage would drop a docking port. Stage manually.", port.Message);
    }

    [Test]
    public void StageFiringAChuteOnTheKeptSide_Refused()
    {
        // Stage 0 activates the pod's parachute: never fired by the mod.
        StageDropResult r = StageDrop.Decide(TwoStageRocket(), 0);
        Assert.IsFalse(r.MayStage);
        Assert.IsTrue(r.Flags.HasParachute);
        Assert.AreEqual(0, r.DroppedIds.Count);
    }

    [Test]
    public void ControlOwnerOnTheLowerSide_UpperPartIsDropped()
    {
        // The probe controlling the vessel is below dec2: firing dec2 drops the pod and its chute.
        var parts = TwoStageRocket();
        parts[0] = Part("pod", null, command: true);
        parts.Add(Part("probe", "tank2", control: true));
        StageDropResult r = StageDrop.Decide(parts, 1);
        CollectionAssert.AreEquivalent(new[] { "pod", "chute", "dec2" }, r.DroppedIds);
        Assert.IsFalse(r.MayStage);
        Assert.AreEqual("Burn stopped: the next stage would drop a parachute and the command part. Stage manually.", r.Message);
    }

    [Test]
    public void NoControlOwner_RootSideKept()
    {
        var parts = TwoStageRocket().Select(p => { p.IsControl = false; return p; }).ToList();
        StageDropResult r = StageDrop.Decide(parts, 2);
        Assert.IsTrue(r.MayStage, r.Reason);
        CollectionAssert.AreEquivalent(new[] { "tank1", "engine1" }, r.DroppedIds);
    }

    [Test]
    public void OmniDecoupler_CutsEverySide()
    {
        // A radial booster on an omni decoupler (unknown explosive node): the decoupler leaves too.
        var parts = TwoStageRocket();
        parts.Add(Part("radial", "tank2", stage: 2, decoupler: true, detachesFrom: null));
        parts.Add(Part("booster", "radial"));
        StageDropResult r = StageDrop.Decide(parts, 2);
        Assert.IsTrue(r.MayStage, r.Reason);
        CollectionAssert.AreEquivalent(new[] { "tank1", "engine1", "radial", "booster" }, r.DroppedIds);
    }

    [Test]
    public void DecouplerFacingAway_StaysWithTheRocket()
    {
        // dec1's explosive node is toward tank1: the decoupler itself stays on tank2.
        StageDropResult r = StageDrop.Decide(TwoStageRocket(), 2);
        CollectionAssert.DoesNotContain(r.DroppedIds, "dec1");
        CollectionAssert.Contains(r.KeptIds, "dec1");
    }

    [Test]
    public void NoStageLeft_OrNoParts_Refused()
    {
        StageDropResult none = StageDrop.Decide(TwoStageRocket(), -1);
        Assert.IsFalse(none.MayStage);
        Assert.IsTrue(none.NoStageLeft);
        StringAssert.StartsWith("Burn stopped:", none.Message);
        StageDropResult empty = StageDrop.Decide(new List<DropPart>(), 1);
        Assert.IsFalse(empty.MayStage);
        StringAssert.StartsWith("Burn stopped:", empty.Message);
    }
}
