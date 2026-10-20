using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class OrbitPlanSequenceTests
{
    // Kerbin.
    const double Mu = 3.5316e12;
    const double R = 600000;
    const double Atm = 70000;
    const double Deg = Math.PI / 180;

    static readonly BodyInfo Kerbin = new BodyInfo { Mu = Mu, Radius = R, AtmosphereDepth = Atm };

    static void StateAtPeriapsis(double peAlt, double apAlt, double incDeg, out Vector3d r, out Vector3d v)
    {
        double rp = R + peAlt, ra = R + apAlt;
        double speed = Math.Sqrt(Mu * (2 / rp - 2 / (rp + ra)));
        r = new Vector3d(rp, 0, 0);
        v = new Vector3d(0, speed * Math.Cos(incDeg * Deg), speed * Math.Sin(incDeg * Deg));
    }

    // Flies a burn impulsively: coast to its time, then add its Δv.
    static void Apply(PlannedBurn burn, ref Vector3d r, ref Vector3d v, ref double ut)
    {
        KeplerPropagator.Propagate(r, v, Mu, burn.UT - ut, out var rb, out var vb);
        r = rb;
        v = vb + burn.DeltaV;
        ut = burn.UT;
    }

    static OrbitTargets Targets(double? apKm, double? peKm, double? incDeg) => new OrbitTargets
    {
        ApAltitude = apKm * 1000,
        PeAltitude = peKm * 1000,
        InclinationDeg = incDeg
    };

    [Test]
    public void Next_ReplansEachStepFromTheReachedOrbit_UntilDone()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        double ut = 1000;
        var targets = Targets(300, 300, 10);
        var plan = OrbitPlanner.Plan(r, v, ut, Kerbin, targets);
        Assert.AreEqual(3, plan.Burns.Count);

        var sequence = new OrbitPlanSequence(targets, Kerbin, plan.Burns.Count);
        for (int i = 0; i < plan.Burns.Count; i++)
        {
            var step = sequence.Next(r, v, ut, Kerbin);
            Assert.AreEqual(OrbitPlanStepOutcome.Burn, step.Outcome, $"step {i + 1}");
            Assert.AreEqual(i + 1, step.Step);
            Assert.AreEqual(plan.Burns.Count - i, step.RemainingBurns, $"remaining at step {i + 1}");
            Assert.AreEqual(plan.Burns[i].DeltaV.magnitude, step.Burn.DeltaV.magnitude,
                0.01 * plan.Burns[i].DeltaV.magnitude, $"Δv of step {i + 1}");
            Apply(step.Burn, ref r, ref v, ref ut);
        }

        var done = sequence.Next(r, v, ut, Kerbin);
        Assert.AreEqual(OrbitPlanStepOutcome.Done, done.Outcome);
        var final = OrbitMath.Elements(r, v, Mu);
        Assert.AreEqual(R + 300000, final.ApoapsisRadius, 0.001 * (R + 300000));
        Assert.AreEqual(R + 300000, final.PeriapsisRadius, 0.001 * (R + 300000));
        Assert.AreEqual(10.0, final.InclinationRad / Deg, 0.05);
    }

    [Test]
    public void Next_ShapeThenPlane_LikeTheReportedPlan()
    {
        // Reported in game: "1. Set Ap 100 km at periapsis, 2. Inclination 6° at ascending node".
        StateAtPeriapsis(90000, 95000, 0, out var r, out var v);
        double ut = 0;
        var targets = Targets(100, null, 6);
        var sequence = new OrbitPlanSequence(targets, Kerbin, 2);

        var first = sequence.Next(r, v, ut, Kerbin);
        Assert.AreEqual(OrbitPlanStepOutcome.Burn, first.Outcome);
        StringAssert.StartsWith("Set Ap", first.Burn.Label);
        Apply(first.Burn, ref r, ref v, ref ut);

        var second = sequence.Next(r, v, ut, Kerbin);
        Assert.AreEqual(OrbitPlanStepOutcome.Burn, second.Outcome);
        StringAssert.StartsWith("Inclination", second.Burn.Label);
        Assert.AreEqual(1, second.RemainingBurns);
        Apply(second.Burn, ref r, ref v, ref ut);

        Assert.AreEqual(OrbitPlanStepOutcome.Done, sequence.Next(r, v, ut, Kerbin).Outcome);
    }

    [Test]
    public void Next_AlreadyOnTarget_IsDone()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var sequence = new OrbitPlanSequence(Targets(100, 100, null), Kerbin, 1);
        var step = sequence.Next(r, v, 0, Kerbin);
        Assert.AreEqual(OrbitPlanStepOutcome.Done, step.Outcome);
        Assert.AreEqual(0, step.Step);
    }

    [Test]
    public void Next_StopsAfterTooManySteps()
    {
        // A burn that never gets flown keeps being planned: the sequence gives up instead of looping.
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var sequence = new OrbitPlanSequence(Targets(300, null, null), Kerbin, 1);
        for (int i = 0; i < sequence.MaxSteps; i++)
            Assert.AreEqual(OrbitPlanStepOutcome.Burn, sequence.Next(r, v, 0, Kerbin).Outcome, $"call {i + 1}");
        Assert.AreEqual(OrbitPlanStepOutcome.TooManySteps, sequence.Next(r, v, 0, Kerbin).Outcome);
    }

    [Test]
    public void Next_RefusesAnotherBody()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var sequence = new OrbitPlanSequence(Targets(300, null, null), Kerbin, 1);
        var mun = new BodyInfo { Mu = 6.5138398e10, Radius = 200000, AtmosphereDepth = 0 };
        Assert.AreEqual(OrbitPlanStepOutcome.BodyChanged, sequence.Next(r, v, 0, mun).Outcome);
    }

    [Test]
    public void Next_ReportsPlannerErrors()
    {
        // Pe inside Kerbin's atmosphere: the planner refuses, the sequence says why.
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var sequence = new OrbitPlanSequence(Targets(null, 50, null), Kerbin, 1);
        var step = sequence.Next(r, v, 0, Kerbin);
        Assert.AreEqual(OrbitPlanStepOutcome.Error, step.Outcome);
        Assert.AreEqual(OrbitPlanError.PeInAtmosphere, step.Error);
    }
}
