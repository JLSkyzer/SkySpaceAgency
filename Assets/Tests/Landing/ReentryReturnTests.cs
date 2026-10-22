using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The pure pieces of the parachute precision return flown by LandingPilot: the jettison-aware
// predictor, the expected accuracy, the drag scale calibration after a landing, the failure policy
// and the start checks. Synthetic Kerbin and capsule of ReentryPredictorTests.
public class ReentryReturnTests
{
    const double Mu = ReentryPredictorTests.Mu;
    const double R = ReentryPredictorTests.R;
    const double UT = ReentryPredictorTests.UT;

    static Body Kerbin => ReentryPredictorTests.Kerbin(true);
    static Atmosphere Air => ReentryPredictorTests.Air();

    // 100 km apoapsis on +x, periapsis at the reentry descent periapsis (35 % of the atmosphere).
    static void Descent(out Vector3d r, out Vector3d v)
    {
        double ra = R + 100000;
        double rp = DeorbitSearch.ReentryPeriapsis(Kerbin, ReentryPredictorTests.Depth);
        r = new Vector3d(ra, 0, 0);
        v = new Vector3d(0, Math.Sqrt(2 * Mu * rp / (ra * (ra + rp))), 0);
    }

    static ReentryVehicle Scaled(ReentryVehicle c, double scale)
    {
        c.DragScale = scale;
        return c;
    }

    // ---- JettisonAwarePredictor ----

    [Test]
    public void BeforeTheJettison_AnInterfaceTooSoonIsRefused_AfterItTheSameCoastIsOk()
    {
        Descent(out Vector3d r, out Vector3d v);
        var predictor = new JettisonAwarePredictor(Kerbin, Air, ReentryPredictorTests.Capsule());
        StopPrediction far = predictor.Predict(r, v, UT, null, out ReentryTrace trace);
        Assert.IsTrue(far.Ok, far.Reason);

        // 60 s before the interface: no room for the 120 s jettison lead.
        double late = trace.InterfaceUT - 60;
        KeplerPropagator.Propagate(r, v, Mu, late - UT, out Vector3d rl, out Vector3d vl);
        StopPrediction refused = predictor.Predict(rl, vl, late, null, out _);
        Assert.IsFalse(refused.Ok);
        StringAssert.Contains("No time to jettison", refused.Reason);

        predictor.Jettisoned = true;
        StopPrediction after = predictor.Predict(rl, vl, late, null, out _);
        Assert.IsTrue(after.Ok, after.Reason);
        Assert.AreEqual(far.StopUT, after.StopUT, 1.0);
        Assert.AreEqual(3, predictor.Calls);
        Assert.AreEqual(0, predictor.NeverOpen);
        Assert.AreEqual(2, predictor.Succeeded);
        Assert.AreEqual(trace.InterfaceUT - 120, JettisonAwarePredictor.JettisonUT(trace), 1e-9);
    }

    [Test]
    public void ChutesThatNeverOpen_AreCountedFromTheTrace()
    {
        Descent(out Vector3d r, out Vector3d v);
        // A minimum opening speed far above any speed in the air.
        var predictor = new JettisonAwarePredictor(Kerbin, Air, ReentryPredictorTests.Capsule(minSpeed: 3000));
        StopPrediction p = predictor.Predict(r, v, UT, null, out ReentryTrace trace);
        Assert.IsFalse(p.Ok);
        Assert.IsTrue(double.IsNaN(trace.SemiUT));
        Assert.IsTrue(JettisonAwarePredictor.ChutesNeverOpen(p, trace));
        Assert.AreEqual(1, predictor.NeverOpen);

        // A refusal for another reason (no time to jettison) is not "never open".
        var ok = new JettisonAwarePredictor(Kerbin, Air, ReentryPredictorTests.Capsule());
        ok.Predict(r, v, UT, null, out ReentryTrace t2);
        KeplerPropagator.Propagate(r, v, Mu, t2.InterfaceUT - 10 - UT, out Vector3d rl, out Vector3d vl);
        StopPrediction late = ok.Predict(rl, vl, t2.InterfaceUT - 10, null, out ReentryTrace t3);
        Assert.IsFalse(late.Ok);
        Assert.IsFalse(JettisonAwarePredictor.ChutesNeverOpen(late, t3));
        Assert.AreEqual(0, ok.NeverOpen);
    }

    // ---- ReentryAccuracy ----

    [Test]
    public void ExpectedAccuracy_IsTheLargerStopShiftForTheDragError_AtLeast3km()
    {
        Descent(out Vector3d r, out Vector3d v);
        ReentryVehicle capsule = ReentryPredictorTests.Capsule();
        Func<double, StopPrediction> at = s => ReentryPredictor.Predict(r, v, UT, Kerbin, Air, Scaled(capsule, s), null);

        ExpectedAccuracy a = ReentryAccuracy.Estimate(at, 1.0, R);
        TestContext.Out.WriteLine($"±20 %: shift {a.ShiftLowM:F0} / {a.ShiftHighM:F0} m -> expected {a.ExpectedM:F0} m");
        Assert.IsTrue(a.Ok);
        Assert.AreEqual(0.20, a.ErrorFraction, 1e-12);
        Assert.Greater(a.ShiftLowM, 0);
        Assert.Greater(a.ShiftHighM, 0);
        Assert.AreEqual(Math.Max(3000, Math.Max(a.ShiftLowM, a.ShiftHighM)), a.ExpectedM, 1e-6);
        // Less drag lands farther: the low side moves the stop more than a few hundred metres.
        Assert.Greater(a.ShiftLowM, 500);

        // A calibrated scale (≠ 1) assumes ±10 %: smaller shifts.
        ExpectedAccuracy c = ReentryAccuracy.Estimate(at, 1.05, R);
        TestContext.Out.WriteLine($"±10 % at 1.05: shift {c.ShiftLowM:F0} / {c.ShiftHighM:F0} m -> expected {c.ExpectedM:F0} m");
        Assert.AreEqual(0.10, c.ErrorFraction, 1e-12);
        Assert.Less(c.ShiftLowM, a.ShiftLowM);
    }

    [Test]
    public void ExpectedAccuracy_FromAStubModel()
    {
        // The stop moves 1 km per 1 % of drag scale along the equator.
        Func<double, StopPrediction> at = s =>
        {
            double angle = (s - 1) * 100 * 1000 / R;
            return new StopPrediction { Ok = true, StopDirNow = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0) };
        };
        ExpectedAccuracy a = ReentryAccuracy.Estimate(at, 1.0, R);
        Assert.AreEqual(20000, a.ExpectedM, 1);
        // Small sensitivity: the 3 km floor.
        Func<double, StopPrediction> stiff = s => new StopPrediction { Ok = true, StopDirNow = new Vector3d(1, 1e-4 * (s - 1), 0) };
        Assert.AreEqual(3000, ReentryAccuracy.Estimate(stiff, 1.0, R).ExpectedM, 1e-9);
        // Nominal refused: not Ok, the floor.
        ExpectedAccuracy none = ReentryAccuracy.Estimate(s => new StopPrediction { Ok = false }, 1.0, R);
        Assert.IsFalse(none.Ok);
        Assert.AreEqual(3000, none.ExpectedM);
        StringAssert.Contains("expected within about 20 km", ReentryAccuracy.PlanMessage(85.2, 750, 19400));
        StringAssert.Contains("T+12m 30s", ReentryAccuracy.PlanMessage(85.2, 750, 19400));
    }

    // ---- DragCalibration ----

    [Test]
    public void Calibration_RecoversTheTrueDragScale()
    {
        Descent(out Vector3d r, out Vector3d v);
        ReentryVehicle capsule = ReentryPredictorTests.Capsule();
        // The "real" flight: the same capsule with 1.3 × the modelled drag.
        StopPrediction truth = ReentryPredictor.Predict(r, v, UT, Kerbin, Air, Scaled(capsule, 1.3), null);
        Assert.IsTrue(truth.Ok, truth.Reason);
        StopPrediction model = ReentryPredictor.Predict(r, v, UT, Kerbin, Air, capsule, null);
        double missAtOne = SurfaceGeometry.GreatCircle(truth.StopDirNow, model.StopDirNow, R);

        DragCalibrationResult c = DragCalibration.Solve(
            s => ReentryPredictor.Predict(r, v, UT, Kerbin, Air, Scaled(capsule, s), null), truth.StopDirNow, R);
        TestContext.Out.WriteLine($"miss at scale 1: {missAtOne:F0} m -> suggested {c.Scale:F4}, along {c.AlongM:F1} m, " +
            $"cross {c.CrossM:F1} m, {c.Predictions} predictions, at limit {c.AtLimit}");
        Assert.Greater(missAtOne, 1000);
        Assert.IsTrue(c.Ok, c.Reason);
        Assert.IsFalse(c.AtLimit);
        Assert.AreEqual(1.3, c.Scale, 0.02);
        Assert.Less(Math.Abs(c.AlongM), 100);
        Assert.Less(c.Predictions, 30);
        Assert.AreEqual("Suggested reentry drag scale: 1.30 (current 1.00)", DragCalibration.SuggestionMessage(c.Scale, 1.0));
    }

    [Test]
    public void Calibration_BeyondTheRange_GivesTheNearestLimit()
    {
        Descent(out Vector3d r, out Vector3d v);
        ReentryVehicle capsule = ReentryPredictorTests.Capsule();
        // A touchdown far beyond anything the lowest drag reaches.
        StopPrediction low = ReentryPredictor.Predict(r, v, UT, Kerbin, Air, Scaled(capsule, 0.3), null);
        Vector3d far = SurfaceGeometry.AtTime(low.StopDirNow, new Vector3d(0, 0, 1), 0.05);  // ~30 km farther east
        DragCalibrationResult c = DragCalibration.Solve(
            s => ReentryPredictor.Predict(r, v, UT, Kerbin, Air, Scaled(capsule, s), null), far, R);
        Assert.IsTrue(c.Ok, c.Reason);
        Assert.IsTrue(c.AtLimit);
        Assert.AreEqual(DragCalibration.MinScale, c.Scale);

        DragCalibrationResult none = DragCalibration.Solve(s => new StopPrediction { Ok = false }, far, R);
        Assert.IsFalse(none.Ok);
    }

    // ---- ReentryFailurePolicy ----

    [TestCase(ReentryStage.PlaneChange, false, ReentryAction.Stop)]
    [TestCase(ReentryStage.PlaneChange, true, ReentryAction.FallBackParachute)]
    [TestCase(ReentryStage.Deorbit, false, ReentryAction.Stop)]
    [TestCase(ReentryStage.Deorbit, true, ReentryAction.FallBackParachute)]
    [TestCase(ReentryStage.MidCourse, true, ReentryAction.Skip)]
    [TestCase(ReentryStage.MidCourse, false, ReentryAction.Skip)]
    public void FailurePolicy_Burns(ReentryStage stage, bool enters, ReentryAction expected)
    {
        Assert.AreEqual(expected, ReentryFailurePolicy.Decide(stage, enters));
    }

    [Test]
    public void FailurePolicy_Jettison()
    {
        // Failed before the activation (warp, stages changed): parachutes with the service module.
        Assert.AreEqual(ReentryAction.FallBackParachute,
            ReentryFailurePolicy.Decide(ReentryStage.Jettison, true, stageActivated: false, activeHoldsCapsule: true));
        // Activated, the capsule is the active vessel but service parts stayed: parachutes.
        Assert.AreEqual(ReentryAction.FallBackParachute,
            ReentryFailurePolicy.Decide(ReentryStage.Jettison, true, stageActivated: true, activeHoldsCapsule: true));
        // Activated and the active vessel does not hold the capsule, even without capsule_lost.
        Assert.AreEqual(ReentryAction.StopCapsuleLost,
            ReentryFailurePolicy.Decide(ReentryStage.Jettison, true, stageActivated: true, activeHoldsCapsule: false));
        Assert.AreEqual(ReentryAction.StopCapsuleLost,
            ReentryFailurePolicy.Decide(ReentryStage.Jettison, true, stageActivated: true, activeHoldsCapsule: true, capsuleLost: true));
        // Before the activation the active vessel is the whole stack: the check does not apply.
        Assert.AreEqual(ReentryAction.FallBackParachute,
            ReentryFailurePolicy.Decide(ReentryStage.Jettison, true, stageActivated: false, activeHoldsCapsule: false));
    }

    [Test]
    public void FailurePolicy_MessagesAndAtmosphere()
    {
        Assert.AreEqual("Precision return aborted: the burn was refused. Landing under parachutes without target.",
            ReentryFailurePolicy.FallBackMessage("the burn was refused."));
        StringAssert.StartsWith("Precision return aborted: the capsule is not the active vessel.", ReentryFailurePolicy.CapsuleLostMessage);
        Assert.IsTrue(ReentryFailurePolicy.EntersAtmosphere(R + 30000, R, 70000, false));
        Assert.IsFalse(ReentryFailurePolicy.EntersAtmosphere(R + 80000, R, 70000, false));
        Assert.IsTrue(ReentryFailurePolicy.EntersAtmosphere(R + 80000, R, 70000, true));
    }

    // ---- ReentryStart ----

    [Test]
    public void StartChecks()
    {
        Assert.IsNull(ReentryStart.CheckOrbit(0.01, R + 80000, R, 70000));
        Assert.IsNotNull(ReentryStart.CheckOrbit(0.01, R + 74000, R, 70000));
        Assert.AreEqual(PrecisionStart.NotClosedMessage, ReentryStart.CheckOrbit(1.2, R + 80000, R, 70000));
        Assert.IsNull(ReentryStart.CheckDeltaV(100, 111));
        Assert.IsNotNull(ReentryStart.CheckDeltaV(100, 109));
        Assert.IsNull(ReentryStart.CheckDeltaV(100, double.NaN));
    }
}
