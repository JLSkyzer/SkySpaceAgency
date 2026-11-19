using System.Collections.Generic;
using K2D2.Controller;
using NUnit.Framework;

public class StagingRulesTests
{
    static EngineFuel<string> Fuel(string engine, double stored, double capacity, bool starved = false) =>
        new EngineFuel<string> { Engine = engine, Stored = stored, Capacity = capacity, Starved = starved };

    // One auto-staging check, as StagingPilot runs it every frame: true when it fires a stage.
    static bool Check(StagingRules<string> rules, object vessel, params EngineFuel<string>[] fuels)
    {
        rules.SetVessel(vessel);
        if (!StagingRules<string>.ShouldStage(rules.Lowest(fuels)))
            return false;
        rules.Staged();
        return true;
    }

    [Test]
    public void EmptySrbKeptAttached_DoesNotFireTheNextStages()
    {
        var rules = new StagingRules<string>();
        var vessel = new object();
        // The SRBs burn out: the next stage (the main engine) fires.
        Assert.IsTrue(Check(rules, vessel, Fuel("srb", 0, 100), Fuel("main", 100, 100)));
        // The SRB stays on the vessel (no decoupler under it): every later stage, parachutes
        // included, would have fired one per check.
        int fired = 0;
        for (int frame = 0; frame < 20; frame++)
            if (Check(rules, vessel, Fuel("srb", 0, 100), Fuel("main", 90 - frame, 100)))
                fired++;
        Assert.AreEqual(0, fired);
        Assert.IsTrue(rules.IsSpent("srb"));
        Assert.IsFalse(rules.IsSpent("main"));
    }

    [Test]
    public void AnotherEngineRunningDry_StillStages()
    {
        var rules = new StagingRules<string>();
        var vessel = new object();
        Assert.IsTrue(Check(rules, vessel, Fuel("srb", 0, 100), Fuel("main", 100, 100)));
        Assert.IsTrue(Check(rules, vessel, Fuel("srb", 0, 100), Fuel("main", 0, 100)));
        Assert.AreEqual(2, rules.SpentCount);
    }

    [Test]
    public void ZeroCapacityPropellant_IsNotNaN()
    {
        var rules = new StagingRules<string>();
        double lowest = rules.Lowest(new[] { Fuel("a", 0, 0), Fuel("b", 50, 100) });
        Assert.IsFalse(double.IsNaN(lowest));
        Assert.AreEqual(0.5, lowest, 1e-12);
        // Nothing measured: full, no staging.
        Assert.AreEqual(1.0, rules.Lowest(new[] { Fuel("a", 0, 0) }));
        Assert.IsNull(StagingRules<string>.Level(0, 0, false));
        Assert.IsNull(StagingRules<string>.Level(5, -1, false));
        // Once the measured tank runs dry it still stages (NaN used to block it for good).
        Assert.IsTrue(Check(rules, new object(), Fuel("a", 0, 0), Fuel("b", 0, 100)));
    }

    [Test]
    public void StarvedEngine_CountsAsEmpty()
    {
        Assert.AreEqual(0.0, StagingRules<string>.Level(80, 100, true));
        Assert.AreEqual(0.8, StagingRules<string>.Level(-80, 100, false).Value, 1e-12);
    }

    [Test]
    public void VesselChange_ClearsTheSpentEngines()
    {
        var rules = new StagingRules<string>();
        var first = new object();
        Assert.IsTrue(Check(rules, first, Fuel("srb", 0, 100), Fuel("main", 100, 100)));
        Assert.IsFalse(Check(rules, first, Fuel("srb", 0, 100)));
        // Same names on another vessel: nothing carried over.
        Assert.IsTrue(Check(rules, new object(), Fuel("srb", 0, 100)));
    }

    [Test]
    public void NoStage_KeepsNothingAsSpent()
    {
        var rules = new StagingRules<string>();
        var vessel = new object();
        rules.SetVessel(vessel);
        rules.Lowest(new[] { Fuel("srb", 0, 100) });
        // Auto-staging held (suspended, staging timer): no stage fired, so the engine is not spent.
        Assert.IsFalse(rules.IsSpent("srb"));
        Assert.IsTrue(Check(rules, vessel, Fuel("srb", 0, 100)));
    }
}
