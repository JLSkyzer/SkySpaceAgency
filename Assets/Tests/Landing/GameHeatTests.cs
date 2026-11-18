using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Heating;
using K2D2.Landing.Precision;
using KSP.Sim;
using NUnit.Framework;

// The game's heat difficulty options (HeatEnabled, HeatPartDestruction, HeatScaling) and the
// "Disable Thermodynamics" cheat, as the landing's heating model takes them (Heating/GameHeat.cs).
public class GameHeatTests
{
    static GameHeatOptions Normal => new GameHeatOptions { HeatEnabled = true, PartDestruction = true, HeatScaling = 1 };

    [Test]
    public void Normal_IsJudged()
    {
        GameHeatPolicy p = GameHeat.Decide(Normal);
        Assert.That(p.Simulated, Is.True);
        Assert.That(p.Destroys, Is.True);
        Assert.That(p.Judged, Is.True);
        Assert.That(p.HeatScale, Is.EqualTo(1));
        Assert.That(p.IgnoredNote, Is.Null);
        Assert.That(p.Note, Does.StartWith("game heat on"));
    }

    [Test]
    public void HeatDisabled_IsNotSimulatedNorJudged()
    {
        GameHeatOptions o = Normal;
        o.HeatEnabled = false;
        GameHeatPolicy p = GameHeat.Decide(o);
        Assert.That(p.Simulated, Is.False);
        Assert.That(p.Judged, Is.False);
        Assert.That(p.Note, Does.StartWith("game heat disabled, "));
        Assert.That(p.Note, Does.Contain("HeatEnabled off"));
        Assert.That(p.IgnoredNote, Is.EqualTo(p.Note));
    }

    [Test]
    public void ThermodynamicsCheat_IsHeatDisabled()
    {
        GameHeatOptions o = Normal;
        o.CheatNoThermodynamics = true;
        GameHeatPolicy p = GameHeat.Decide(o);
        Assert.That(p.Simulated, Is.False);
        Assert.That(p.Judged, Is.False);
        Assert.That(p.Note, Does.StartWith("game heat disabled, "));
        Assert.That(p.Note, Does.Contain("Disable Thermodynamics"));
    }

    [Test]
    public void NoPartDestruction_IsSimulatedButNotJudged()
    {
        GameHeatOptions o = Normal;
        o.PartDestruction = false;
        GameHeatPolicy p = GameHeat.Decide(o);
        Assert.That(p.Simulated, Is.True);
        Assert.That(p.Destroys, Is.False);
        Assert.That(p.Judged, Is.False);
        Assert.That(p.Note, Does.StartWith("game part destruction by heat off, "));
        Assert.That(p.IgnoredNote, Is.EqualTo(p.Note));
    }

    [Test]
    public void HeatScaling_IsTakenAsTheGameDoes()
    {
        GameHeatOptions o = Normal;
        o.HeatScaling = 0.5f;
        Assert.That(GameHeat.Decide(o).HeatScale, Is.EqualTo(0.5).Within(1e-9));
        o.HeatScaling = 0;
        Assert.That(GameHeat.Decide(o).HeatScale, Is.EqualTo(0), "0 cuts the flux in the game: kept");
        o.HeatScaling = null;
        Assert.That(GameHeat.Decide(o).HeatScale, Is.EqualTo(1), "absent: 1, as the game does");
        o.HeatScaling = float.NaN;
        Assert.That(GameHeat.Decide(o).HeatScale, Is.EqualTo(1));
        o.HeatScaling = -2;
        Assert.That(GameHeat.Decide(o).HeatScale, Is.EqualTo(1));
    }

    [Test]
    public void Unreadable_KeepsTheJudgementAndSaysSo()
    {
        GameHeatPolicy p = GameHeat.Decide(new GameHeatOptions { ReadError = "no session" });
        Assert.That(p.Judged, Is.True);
        Assert.That(p.HeatScale, Is.EqualTo(1));
        Assert.That(p.Note, Does.Contain("not read (no session)"));
        GameHeatPolicy missing = GameHeat.Decide(new GameHeatOptions { HeatScaling = 1 });
        Assert.That(missing.Judged, Is.True, "options absent: the current behaviour");
        Assert.That(missing.Note, Does.Contain("HeatEnabled not read"));
        Assert.That(missing.Note, Does.Contain("HeatPartDestruction not read"));
    }

    // The game multiplies the whole external source (reentry flux included) by HeatScaling
    // (ThermalIntegrator.Evaluate: heatScale · flux.Source + ...): 0 leaves a part at the air's
    // temperature unchanged.
    [Test]
    public void ThermalStep_HeatScaleZero_AddsNoFlux()
    {
        ThermalConstants c = ThermalConstants.Defaults;
        c.HeatScale = 0;
        Assert.That(ThermalModel.Step(c, 300, 750, 0.1, 5000, 0, 300, 0), Is.EqualTo(300).Within(1e-9));
        c.HeatScale = 1;
        Assert.That(ThermalModel.Step(c, 300, 750, 0.1, 5000, 0, 300, 0), Is.GreaterThan(300.5));
    }

    static DescentInput Kerbin(DescentVehicle aero, string ignored)
    {
        HeatingPredictorTests.Circular(100000, out Vector3d r, out Vector3d v);
        return new DescentInput
        {
            R = r, V = v, UT = 1000, Body = ReentryPredictorTests.Kerbin(true),
            Vehicle = new Vehicle { ThrustKN = 200, MassT = aero.MassT, Isp = 300 },
            Atmospheric = true, Air = HeatingPredictorTests.Air(), Aero = aero, EarliestBurnUT = 1060,
            FixedPeriapsisRadius = double.NaN, HeatIgnored = ignored,
        };
    }

    // An engine already at 97 % of its max temperature is refused with heat on (DescentPlannerTests);
    // with the game's heat off or no destruction, the plan is Δv only.
    [Test]
    public void Planner_HeatIgnored_DoesNotRefuseAHotEngine()
    {
        const string note = "game part destruction by heat off, test";
        DescentPlan plan = DescentPlanner.Plan(Kerbin(HeatingPredictorTests.Stage(HeatingPredictorTests.Engine(maxTemp: 300)), note));
        Assert.That(plan.Ok, Is.True, plan.Reason + " | " + plan.Candidates);
        Assert.That(plan.NeedsPreBrake, Is.False);
        Assert.That(plan.HeatModelled, Is.False);
        Assert.That(plan.HeatIgnored, Is.EqualTo(note));
        Assert.That(plan.Candidates, Does.Contain("heating not judged (game difficulty)"));
        Assert.That(plan.HeatWarning, Does.Contain("above its max temperature"), "a warning, never a refusal");
        FeasibilityResult r = DescentFeasibility.Check(plan, 20, 9.81, 1e5, new Vehicle { ThrustKN = 200, MassT = 8, Isp = 300 });
        Assert.That(r.Ok, Is.True, r.Error);
        Assert.That(r.Warning, Is.EqualTo(plan.HeatWarning));
    }

    [Test]
    public void Planner_HeatIgnored_PlansNoPreBrakeAndCostsNoMore()
    {
        DescentVehicle fragile = HeatingPredictorTests.Stage(HeatingPredictorTests.Engine(maxTemp: 1000));
        DescentPlan judged = DescentPlanner.Plan(Kerbin(fragile, null));
        DescentPlan ignored = DescentPlanner.Plan(Kerbin(fragile, "game heat disabled, test"));
        Assert.That(judged.NeedsPreBrake, Is.True, "heat on: the fragile engine needs a pre-brake");
        Assert.That(ignored.Ok, Is.True, ignored.Reason);
        Assert.That(ignored.NeedsPreBrake, Is.False);
        Assert.That(ignored.TotalDeltaV, Is.LessThan(judged.TotalDeltaV));
        FeasibilityResult r = DescentFeasibility.Check(ignored, 20, 9.81, 1e5, new Vehicle { ThrustKN = 200, MassT = 8, Isp = 300 });
        Assert.That(r.Ok, Is.True, r.Error);
        Assert.That(r.Warning, Is.EqualTo(ignored.HeatWarning), "the game's choice itself is logged, not shown as a warning");
    }
}
