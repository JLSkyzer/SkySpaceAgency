using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The braking simulation with a chosen landing stage: the lower stage brakes first and leaves
// when dry or at the drop height.
public class StagedBrakeTests
{
    // Mun.
    const double Mu = 6.5138e10;
    const double R = 200000;
    const double G0 = 9.80665;
    const double Eta = 0.85;

    static Vector3d Zero => new Vector3d(0, 0, 0);

    // Booster (stage index 0) under a lander (index 1).
    internal static BrakeStage[] TwoStages(double boosterDv, double landerDv = 900)
    {
        return new[]
        {
            new BrakeStage { Stage = 3, ThrustKN = 60, MassT = 10, Isp = 300, DeltaV = boosterDv },
            new BrakeStage { Stage = 2, ThrustKN = 20, MassT = 4, Isp = 320, DeltaV = landerDv },
        };
    }

    internal static BrakeInput Input(Vector3d r, Vector3d v, BrakeStage[] stages, double dropHeight = 0)
    {
        var input = new BrakeInput
        {
            Position = r,
            Velocity = v,
            UT = 1000,
            Mu = Mu,
            BodyRadius = R,
            AngularVelocity = Zero,
            ThrustKN = stages?[0].ThrustKN ?? 0,
            MassT = stages?[0].MassT ?? 0,
            Isp = stages?[0].Isp ?? 0,
            TerrainHeight = p => 0,
            Stages = stages,
            DropHeight = dropHeight,
        };
        input.ImpactUT = ImpactUT(input);
        return input;
    }

    static double ImpactUT(BrakeInput input)
    {
        for (double dt = 0.5; dt < 20000; dt += 0.5)
        {
            KeplerPropagator.Propagate(input.Position, input.Velocity, input.Mu, dt, out var r, out _);
            if (r.magnitude - R <= 0)
                return input.UT + dt + 0.5;
        }
        return input.UT;
    }

    // A shallow approach from a 15 km orbit lowered to a -2 km periapsis.
    internal static void Approach(out Vector3d r, out Vector3d v)
    {
        double ra = R + 15000, rp = R - 2000;
        r = new Vector3d(ra, 0, 0);
        v = new Vector3d(0, Math.Sqrt(Mu * (2 / ra - 2 / (ra + rp))), 0);
    }

    [Test]
    public void OneStage_MatchesTheOneStageSimulation()
    {
        Approach(out Vector3d r, out Vector3d v);
        BrakeInput old = Input(r, v, null);
        old.ThrustKN = 30;
        old.MassT = 10;
        old.Isp = 300;
        old.ImpactUT = ImpactUT(old);
        BrakeInput staged = old;
        // The landing stage is never limited by its Δv, as before.
        staged.Stages = new[] { new BrakeStage { Stage = 1, ThrustKN = 30, MassT = 10, Isp = 300, DeltaV = 10 } };

        BrakeResult a = BrakeSimulator.FindStart(old);
        BrakeResult b = BrakeSimulator.FindStart(staged);

        Assert.AreEqual(BrakeStatus.Ok, a.Status);
        Assert.AreEqual(a.Status, b.Status);
        Assert.AreEqual(a.StartUT, b.StartUT);
        Assert.AreEqual(a.BurnDuration, b.BurnDuration);
        Assert.AreEqual(a.DeltaVNeeded, b.DeltaVNeeded);
        Assert.AreEqual(a.StopAltitude, b.StopAltitude);
        Assert.AreEqual(0, b.Drops.Count);
        Assert.AreEqual(0, b.FinalStageIndex);
        Assert.AreEqual(b.DeltaVNeeded, b.FinalStageDeltaV);
    }

    [Test]
    public void LowerStage_DroppedWhenDry()
    {
        // Vertical fall from 25 km at 300 m/s; the booster has 150 m/s.
        var input = Input(new Vector3d(R + 25000, 0, 0), new Vector3d(-300, 0, 0), TwoStages(150));

        BrakeRun run = BrakeSimulator.Simulate(input, input.UT);

        Assert.AreEqual(1, run.Drops.Count);
        BrakeDrop drop = run.Drops[0];
        Assert.IsTrue(drop.Dry);
        Assert.AreEqual(3, drop.Stage);
        // Tsiolkovsky: the booster runs dry after burning m0 (1 - e^(-dv / (Isp g0))) at its flow.
        double flow = Eta * 60 / (300 * G0);
        double dryTime = 10 * (1 - Math.Exp(-150 / (300 * G0))) / flow;
        Assert.That(drop.UT - input.UT, Is.InRange(dryTime - 1e-6, dryTime + BrakeSimulator.Step + 1e-6));
        Assert.IsTrue(run.Stopped, $"stop {run.StopAltitude}");
        Assert.AreEqual(1, run.FinalStageIndex);
        // The booster's Δv, at most one step over, plus the lander's.
        Assert.That(run.DeltaV - run.FinalStageDeltaV, Is.InRange(150.0, 150.0 + Eta * 60 / 10 * BrakeSimulator.Step * 1.2));
    }

    [Test]
    public void ForcedDrop_AtTheDropHeight_LanderStillStops()
    {
        Approach(out Vector3d r, out Vector3d v);
        // The booster could brake alone; it must leave at 3 km anyway.
        var input = Input(r, v, TwoStages(2000), dropHeight: 3000);

        BrakeResult result = BrakeSimulator.FindStart(input);

        Assert.AreEqual(BrakeStatus.Ok, result.Status);
        Assert.AreEqual(1, result.Drops.Count);
        BrakeDrop drop = result.Drops[0];
        Assert.IsFalse(drop.Dry);
        Assert.That(drop.Altitude, Is.LessThanOrEqualTo(3000));
        Assert.That(drop.Altitude, Is.GreaterThan(3000 - drop.SurfaceSpeed * BrakeSimulator.Step - 1));
        Assert.AreEqual(1, result.FinalStageIndex);
        Assert.That(result.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
        BrakeRun check = BrakeSimulator.Simulate(input, result.StartUT);
        Assert.IsTrue(check.Stopped);
        Assert.That(check.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
    }

    [Test]
    public void NoThrustDuringTheStageGap()
    {
        var input = Input(new Vector3d(R + 25000, 0, 0), new Vector3d(-300, 0, 0), TwoStages(150));
        var samples = new System.Collections.Generic.List<BrakeSample>();
        BrakeSimulator.Simulate(input, input.UT, samples.Add);
        samples.RemoveAt(samples.Count - 1);   // the end of the run, where nothing pushes
        int gapSteps = samples.FindAll(s => !s.Thrusting).Count;
        Assert.AreEqual((int)Math.Round(BrakeSimulator.StageGap / BrakeSimulator.Step), gapSteps);
    }

    [Test]
    public void LandingStageWithoutThrust_IsImpossible()
    {
        BrakeStage[] stages = TwoStages(500);
        stages[1].ThrustKN = 0;
        var input = Input(new Vector3d(R + 20000, 0, 0), new Vector3d(-400, 0, 0), stages);
        Assert.AreEqual(BrakeStatus.Impossible, BrakeSimulator.FindStart(input).Status);
    }

    [Test]
    public void Rebase_KeepsTheTerrainUnderTheSamePoint()
    {
        var input = Input(new Vector3d(R + 5000, 0, 0), new Vector3d(-100, 0, 0), TwoStages(100));
        input.AngularVelocity = new Vector3d(0, 0, 1e-3);
        Vector3d seen = Zero;
        input.TerrainHeight = p => { seen = p; return 0; };
        BrakeInput moved = BrakeSimulator.Rebase(input, input.Position, input.Velocity, input.UT + 100);
        var point = new Vector3d(R, 0, 0);
        moved.TerrainHeight(point);
        // 100 s later the body has turned by +0.1 rad: the old orientation sees the point turned back.
        Assert.AreEqual(R * Math.Cos(-0.1), seen.x, 1e-6);
        Assert.AreEqual(R * Math.Sin(-0.1), seen.y, 1e-6);
    }
}
