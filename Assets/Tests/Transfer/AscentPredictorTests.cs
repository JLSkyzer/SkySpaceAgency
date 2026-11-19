using System;
using System.Collections.Generic;
using SkySpaceAgency.Lift;
using NUnit.Framework;

// The Gravity Turn's auto plan (K2-D2 1.4.0, AscentPredictor), on a fixture rocket and a fixture
// Kerbin (exponential air, 5.6 km scale height: test data, not the game's curves).
public class AscentPredictorTests
{
    static AscentPredictor.Body Kerbin() => new AscentPredictor.Body
    {
        mu = 3.5316e12,
        radius = 600000,
        atmosphere_depth = 70000,
        pressure = h => 101.325 * Math.Exp(-h / 5600),
        density = h => 1.2 * Math.Exp(-h / 5600),
    };

    // Two stages and a decoupler between them, current stage first in the game's numbering (2).
    // The first: 30 -> 14 t, 600/650 kN, Isp 310 s; the upper: 12 -> 5 t, 30/60 kN, Isp 340 s.
    static List<AscentPredictor.StageReading> Rocket()
    {
        var list = new List<AscentPredictor.StageReading>();
        var first = new AscentPredictor.StageReading
        {
            stage = 2, start_mass = 30, end_mass = 14, burn_time_s = 16 / (650 / (310 * 9.80665)),
            thrust_asl = 600, thrust_vac = 650,
        };
        first.engines.Add(new AscentPredictor.EngineReading { thrust_asl = 600, thrust_vac = 650, isp_vac = 310 });
        list.Add(first);
        list.Add(new AscentPredictor.StageReading { stage = 1, start_mass = 14, end_mass = 12 });   // decoupler
        var upper = new AscentPredictor.StageReading
        {
            stage = 0, start_mass = 12, end_mass = 5, burn_time_s = 7 / (60 / (340 * 9.80665)),
            thrust_asl = 30, thrust_vac = 60,
        };
        upper.engines.Add(new AscentPredictor.EngineReading { thrust_asl = 30, thrust_vac = 60, isp_vac = 340 });
        list.Add(upper);
        return list;
    }

    static GravityTurnControl Control() => new GravityTurnControl
    {
        kick_speed_mps = 100, kick_deg = 7.5f, hold_time_s = 45, max_throttle = 1,
        min_throttle = 0.25f, max_twr = 2.5f, max_q_kpa = 40,
    };

    // At the kick: 1.5 km up, rising at 100 m/s, launched east on the equator.
    static AscentPredictor.Start Start(double drag_area, double full_thrust = 604) => new AscentPredictor.Start
    {
        radius = 601500, vertical_speed = 100, horizontal_speed = 0, rotation_speed = 174.5,
        mass = 29, full_thrust = full_thrust, drag_area = drag_area,
    };

    static AscentPredictor Plan(List<AscentPredictor.StageReading> stages, double drag_area, double full_thrust = 604)
    {
        var p = new AscentPredictor(Kerbin(), stages, Start(drag_area, full_thrust), Control(), 80, 0.08f);
        for (int frame = 0; frame < 100 && !p.done; frame++)
            p.Continue(2);
        Assert.IsTrue(p.done);
        return p;
    }

    [Test]
    public void ThePlanReachesTheTargetApoapsis()
    {
        var p = Plan(Rocket(), 0.0008);
        Assert.IsTrue(p.found, p.failure);
        Assert.AreEqual(2, p.stage_count, "the decoupler burns nothing");
        CollectionAssert.Contains(new[] { 3f, 5, 7.5f, 10, 12.5f, 15 }, p.best_kick_deg);
        CollectionAssert.Contains(new[] { 40f, 45, 50, 60 }, p.best_hold_s);

        // Flying the choice again: it gets to 80 km (within the 0.08 km the ascent ends at) on a
        // plausible budget for Kerbin, circularization included.
        var trace = new System.Text.StringBuilder();
        var r = p.Fly(p.best_kick_deg, p.best_hold_s, 1, trace);
        Assert.IsTrue(r.ok, trace.ToString());
        Assert.AreEqual(p.best_delta_v, r.delta_v, 1e-6);
        Assert.That(r.delta_v, Is.InRange(2800, 4500));
        Assert.LessOrEqual(r.max_q_kpa, 40 * 1.15f);
        Assert.IsTrue(p.Fly(p.best_kick_deg, p.best_hold_s, AscentPredictor.PessimisticDrag).ok, "also with 1.6x the drag");
    }

    [Test]
    public void TheHeavierDragCheck_RejectsMarginalCandidates()
    {
        // At this drag the 40 s holds still reach the apoapsis with the measured drag, but not
        // with 1.6 times it: the plan takes a longer hold.
        var p = Plan(Rocket(), 0.005);
        Assert.IsTrue(p.Fly(15, 40, 1).ok);
        Assert.IsFalse(p.Fly(15, 40, AscentPredictor.PessimisticDrag).ok);
        Assert.IsTrue(p.found, p.failure);
        Assert.AreNotEqual(40f, p.best_hold_s);
        Assert.IsTrue(p.Fly(p.best_kick_deg, p.best_hold_s, AscentPredictor.PessimisticDrag).ok);

        // Here only the 60 s holds get there, and none with 1.6x the drag: no plan, the sliders fly.
        var q = Plan(Rocket(), 0.012);
        Assert.IsTrue(q.Fly(3, 60, 1).ok);
        Assert.IsFalse(q.Fly(3, 60, AscentPredictor.PessimisticDrag).ok);
        Assert.IsFalse(q.found);
        Assert.AreEqual("every choice failed in the prediction", q.failure);
    }

    // Seen in game while the readout recalculates: "42.9->0.0t, burn 0s, 600 kN, no engines".
    [Test]
    public void AStageWithThrustButNoBurnTimeNorEngines_IsIgnored()
    {
        var stages = Rocket();
        stages.Insert(0, new AscentPredictor.StageReading { stage = 3, start_mass = 42.9, end_mass = 0, burn_time_s = 0, thrust_vac = 600 });
        var with = Plan(stages, 0.0008);
        var without = Plan(Rocket(), 0.0008);
        Assert.AreEqual(2, with.stage_count);
        Assert.IsTrue(with.found, with.failure);
        Assert.AreEqual(without.best_kick_deg, with.best_kick_deg);
        Assert.AreEqual(without.best_hold_s, with.best_hold_s);
        Assert.AreEqual(without.best_delta_v, with.best_delta_v, 1e-9);
    }

    [Test]
    public void StageDataThatDoesNotMatchTheEngines_FallsBackToTheSliders()
    {
        var p = Plan(Rocket(), 0.0008, full_thrust: 900);
        Assert.IsFalse(p.found);
        StringAssert.StartsWith("stage data doesn't match the engines", p.failure);

        var none = Plan(null, 0.0008);
        Assert.IsFalse(none.found);
        Assert.AreEqual("no stage data", none.failure);
    }

    [Test]
    public void ASolidBooster_IsNotThrottled()
    {
        // Same rocket with the first stage's engine a solid booster: the plan still finds a way,
        // but the throttle can't hold max Q, so it peaks higher than with a liquid engine.
        var solid = Rocket();
        var e = solid[0].engines[0];
        e.throttle_locked = true;
        solid[0].engines[0] = e;
        var p = Plan(solid, 0.0008);
        var liquid = Plan(Rocket(), 0.0008);
        float q_solid = p.Fly(7.5f, 45, 1).max_q_kpa;
        float q_liquid = liquid.Fly(7.5f, 45, 1).max_q_kpa;
        Assert.Greater(q_solid, q_liquid);
    }
}
