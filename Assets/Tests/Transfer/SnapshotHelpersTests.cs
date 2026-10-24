using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class SnapshotHelpersTests
{
    static StagePart P(int activation, int decouple, bool port = false, bool chute = false, bool command = false) =>
        new StagePart { Name = "p", ActivationStage = activation, DecoupleStage = decouple, IsDockingPort = port, IsParachute = chute, IsCommand = command };

    [Test]
    public void Stages_SortedByStageDescending_WhateverTheListOrder()
    {
        var stages = StageList.InFiringOrder(new[] { (1, 800.0), (3, 1200.0), (0, 0.0), (2, 950.0) }, null, out string error);
        Assert.IsNull(error);
        CollectionAssert.AreEqual(new[] { 3, 2, 1, 0 }, stages.Select(s => s.Stage).ToArray());
        CollectionAssert.AreEqual(new[] { 1200.0, 950, 800, 0 }, stages.Select(s => s.DeltaVVac).ToArray());
    }

    [Test]
    public void Flags_FromActivationOrDecoupleStage()
    {
        var parts = new[]
        {
            P(activation: 0, decouple: -1, chute: true),     // the chute opens with stage 0
            P(activation: -1, decouple: 2, port: true),      // a port dropped at stage 2
            P(activation: -1, decouple: 1, command: true),   // a probe core dropped at stage 1
            P(activation: 3, decouple: 3),                   // an engine: no flag
            P(activation: -1, decouple: -1, command: true),  // the pod, never dropped
        };
        var stages = StageList.InFiringOrder(new[] { (3, 1000.0), (2, 500.0), (1, 300.0), (0, 0.0) }, parts, out _);
        StageDv S(int n) => stages.Single(s => s.Stage == n);

        Assert.IsFalse(S(3).HasDockingPort || S(3).HasParachute || S(3).HasCommandPart);
        Assert.IsTrue(S(2).HasDockingPort);
        Assert.IsFalse(S(2).HasParachute || S(2).HasCommandPart);
        Assert.IsTrue(S(1).HasCommandPart);
        Assert.IsFalse(S(1).HasDockingPort || S(1).HasParachute);
        Assert.IsTrue(S(0).HasParachute);
        Assert.IsFalse(S(0).HasCommandPart, "a command part never decoupled flags no stage");
        Assert.IsFalse(StagingGuard.MayDrop(S(2), out string reason));
        Assert.AreEqual("a docking port", reason);
    }

    [Test]
    public void DuplicateStageNumber_Refused()
    {
        var stages = StageList.InFiringOrder(new[] { (1, 100.0), (1, 200.0) }, null, out string error);
        Assert.IsNull(stages);
        StringAssert.Contains("stage 1", error);
    }

    [Test]
    public void Describe_ListsDeltaVAndFlags()
    {
        var stages = StageList.InFiringOrder(new[] { (0, 0.0), (1, 812.4) }, new[] { P(0, -1, chute: true) }, out _);
        Assert.AreEqual("stage 1: 812 m/s, stage 0: 0 m/s [parachute]", StageList.Describe(stages));
        Assert.AreEqual("no stage", StageList.Describe(new List<StageDv>()));
    }

    [Test]
    public void TerrainGrid_UnitVectorsCoverTheSphere()
    {
        var dirs = TerrainGrid.Directions(400);
        Assert.AreEqual(400, dirs.Count);
        foreach (var d in dirs)
            Assert.That(d.magnitude, Is.EqualTo(1).Within(1e-12));
        // Even spread: the mean is near the centre, and every axis direction has a sample within 10°.
        Vector3d mean = Vector3d.zero;
        foreach (var d in dirs)
            mean += d;
        Assert.That((mean / dirs.Count).magnitude, Is.LessThan(0.01));
        foreach (var axis in new[] { new Vector3d(1, 0, 0), new Vector3d(-1, 0, 0), new Vector3d(0, 1, 0),
                     new Vector3d(0, -1, 0), new Vector3d(0, 0, 1), new Vector3d(0, 0, -1) })
            Assert.That(dirs.Max(d => Vector3d.Dot(d, axis)), Is.GreaterThan(Math.Cos(10 * Math.PI / 180)));
    }
}
