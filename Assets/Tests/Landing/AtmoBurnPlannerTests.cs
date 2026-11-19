using System;
using K2D2.Landing;
using NUnit.Framework;

// AtmoBurnPlanner (K2-D2 1.4.0): the last moment a landing burn in air still ends at the gate.
// The densities below are exponential approximations of Kerbin's and Duna's air (not the game's
// curves): only their shape and order of magnitude matter here.
public class AtmoBurnPlannerTests
{
    static readonly AtmoBurnPlanner.Air Kerbin = new AtmoBurnPlanner.Air("Kerbin", 600000, 3.5316e12, 70000,
        h => 1.225 * Math.Exp(-h / 5600));
    static readonly AtmoBurnPlanner.Air Duna = new AtmoBurnPlanner.Air("Duna", 320000, 3.0136e11, 50000,
        h => 0.14 * Math.Exp(-h / 5000));
    static readonly AtmoBurnPlanner.Air Vacuum = new AtmoBurnPlanner.Air("Mun", 200000, 6.5138e10, 0, h => 0);

    const double Gate = AtmoLandingBurn.final_height_m;
    const double StopSpeed = 5;
    const double PlanningFraction = 0.9;    // AtmoBurnPlanner.planning_thrust_fraction

    static AtmoBurnPlanner.Plan Plan(AtmoBurnPlanner.Air air, double k, double height, double vz, double vx, double accel)
    {
        var planner = new AtmoBurnPlanner();
        planner.SetDrag(k);
        return planner.Compute(air, height, 0, vx, vz, accel, Gate, StopSpeed, double.NaN);
    }

    // Independent reference for a vertical fall with no drag: fall for t, coast AtmoBurnPlanner.turn_delay_s,
    // then brake at `thrust` until the fall is down to StopSpeed; returns the height there.
    static double VacuumStopHeight(AtmoBurnPlanner.Air air, double height, double vz, double thrust, double t_ignite)
    {
        const double dt = 0.001;
        double h = height, v = vz;
        for (double t = 0; t < t_ignite + AtmoBurnPlanner.turn_delay_s && h > 0; t += dt)
            Step(air, ref h, ref v, 0, dt);
        while (h > 0 && v < -StopSpeed)
            Step(air, ref h, ref v, thrust, dt);
        return Math.Max(0, h);
    }

    static void Step(AtmoBurnPlanner.Air air, ref double h, ref double v, double thrust, double dt)
    {
        double r = air.Radius + h;
        double a = -air.Mu / (r * r) + (v < 0 ? thrust : 0);
        h += v * dt + a * dt * dt / 2;
        v += a * dt;
    }

    // The latest ignition (s from now) whose vacuum braking still ends at the gate, by bisection.
    static double VacuumIgnition(AtmoBurnPlanner.Air air, double height, double vz, double thrust)
    {
        double lo = 0, hi = 300;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            if (VacuumStopHeight(air, height, vz, thrust, mid) >= Gate) lo = mid; else hi = mid;
        }
        return lo;
    }

    [Test]
    public void NoDrag_EqualsTheVacuumStop()
    {
        foreach (var air in new[] { Vacuum, Kerbin, Duna })
        {
            const double height = 6000, vz = -200, accel = 25;
            var plan = Plan(air, 0, height, vz, 0, accel);
            Assert.IsTrue(plan.found, air.Name);
            double reference = VacuumIgnition(air, height, vz, accel * PlanningFraction);
            // The planner picks the last 0.2 s sample whose burn still ends at or above the gate.
            Assert.That(plan.handover_in_s, Is.InRange(reference - 0.45, reference + 0.05), air.Name);
            // A 0.2 s later start stops ~100 m lower at these speeds.
            Assert.That(plan.stop_height_m, Is.InRange(Gate, Gate + 150), air.Name);
        }
    }

    [Test]
    public void Drag_IgnitesLater_MoreInThickerAir()
    {
        const double height = 10000, vz = -250, vx = 40, accel = 25, k = 4.9e-4;
        double kerbin_delay = Plan(Kerbin, k, height, vz, vx, accel).handover_in_s - Plan(Kerbin, 0, height, vz, vx, accel).handover_in_s;
        double duna_delay = Plan(Duna, k, height, vz, vx, accel).handover_in_s - Plan(Duna, 0, height, vz, vx, accel).handover_in_s;
        Assert.Greater(kerbin_delay, 5, "Kerbin: drag slows the fall, the burn starts later");
        Assert.Greater(duna_delay, 0.2, "Duna: thin air, still later");
        Assert.Less(duna_delay, kerbin_delay, "thinner air, smaller delay");
    }

    [Test]
    public void EndsNearTheGate_KerbinAndDuna()
    {
        foreach (var air in new[] { Kerbin, Duna })
        {
            var plan = Plan(air, 4.9e-4, 12000, -300, 60, 20);
            Assert.IsTrue(plan.found, air.Name);
            Assert.Greater(plan.handover_in_s, 0, air.Name);
            Assert.That(plan.stop_height_m, Is.InRange(Gate, Gate + 200), $"{air.Name}: the planned braking stops just above the gate");
            Assert.AreEqual(StopSpeed, plan.stop_falling_speed, 1e-9);
        }
    }

    // What the landing burn flies: the throttle re-solved from the current state ends at the gate,
    // within a few metres, at the planned speed.
    [Test]
    public void ThrottleSolve_EndsAtTheGate_KerbinAndDuna()
    {
        foreach (var air in new[] { Kerbin, Duna })
        {
            var planner = new AtmoBurnPlanner();
            planner.SetDrag(4.9e-4);
            double tau = planner.ThrottleToStopAt(air, 4000, 0, 30, -180, 30, StopSpeed, Gate, out double stop, out _, out double t);
            Assert.That(tau, Is.InRange(0.05, 0.999), air.Name);
            // The braking simulation ends on 0.2 s steps: a few metres above the gate.
            Assert.That(stop, Is.InRange(Gate, Gate + 10), $"{air.Name}: stops at the gate");
            Assert.Greater(t, 0, air.Name);
        }
        var weak = new AtmoBurnPlanner();
        weak.SetDrag(4.9e-4);
        Assert.AreEqual(1, weak.ThrottleToStopAt(Kerbin, 1000, 0, 0, -250, 15, StopSpeed, Gate, out double low, out _, out _),
            "cannot stop above the gate: full throttle");
        Assert.Less(low, Gate);
    }

    [Test]
    public void AlreadyTooLow_DueNow()
    {
        // 800 m up at 250 m/s with 20 m/s²: no braking can end at the gate any more.
        var plan = Plan(Kerbin, 4.9e-4, 800, -250, 0, 20);
        Assert.IsTrue(plan.found);
        Assert.AreEqual(0, plan.handover_in_s, 1e-9);
    }

    [Test]
    public void NoThrust_OrNoDragMeasured_NoPlan()
    {
        Assert.IsFalse(Plan(Kerbin, 4.9e-4, 5000, -200, 0, 0).found, "no thrust");
        var planner = new AtmoBurnPlanner();
        Assert.IsFalse(planner.Compute(Kerbin, 5000, 0, 0, -200, 20, Gate, StopSpeed, double.NaN).found, "no drag measured");
    }

    [Test]
    public void MeasureDrag_RecoversK_AndSmooths()
    {
        var planner = new AtmoBurnPlanner();
        const double k = 3e-4, h = 8000, v = 300;
        double rho = planner.Density(Kerbin, h);
        Assert.AreEqual(1.225 * Math.Exp(-h / 5600), rho, 1e-3, "density table");
        planner.MeasureDrag(Kerbin, k * rho * v * v, 0, h, v, 100);
        Assert.AreEqual(k, planner.drag_k, k * 1e-3);
        // A value twice as big, 1.5 s later: half way there (smoothing over 3 s).
        planner.MeasureDrag(Kerbin, 2 * k * rho * v * v, 0, h, v, 101.5);
        Assert.AreEqual(1.5 * k, planner.drag_k, k * 1e-3);
        // Nonsense samples are ignored: no air speed, no deceleration, above the air.
        planner.MeasureDrag(Kerbin, 5, 0, h, 5, 102);
        planner.MeasureDrag(Kerbin, -1, 0, h, v, 102);
        planner.MeasureDrag(Kerbin, 5, 0, 80000, v, 102);
        Assert.AreEqual(1.5 * k, planner.drag_k, k * 1e-3);
    }

    [Test]
    public void Brake_MoreThrustStopsHigher()
    {
        var planner = new AtmoBurnPlanner();
        planner.SetDrag(4.9e-4);
        double previous = -1;
        foreach (double thrust in new[] { 0.0, 12, 16, 20, 30 })
        {
            planner.Brake(Kerbin, 3000, 0, 20, -150, thrust, StopSpeed, out double stop, out _, out _);
            Assert.GreaterOrEqual(stop, previous, $"thrust {thrust}");
            previous = stop;
        }
        planner.Brake(Kerbin, 3000, 0, 20, -150, 0, StopSpeed, out double unpowered, out _, out _);
        Assert.AreEqual(0, unpowered, "no thrust never stops");
    }
}
