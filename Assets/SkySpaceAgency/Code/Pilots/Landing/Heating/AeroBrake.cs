using System;
using System.Collections.Generic;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;

namespace K2D2.Landing.Heating
{
    public struct AeroBrakeResult
    {
        public bool Ok;
        public string Reason;
        public BrakeStatus Status;
        public double StartUT;        // latest safe brake start
        public double DeltaV;         // m/s of the brake
        public double BurnDuration;
        public int SampleIndex;       // the sample the brake is planned from
        public double SampleAltitude, SampleAirSpeed;
        public int Probes;            // brake simulations run
        public StopPrediction Stop;   // StopPointPredictor from the sample (staged details included)

        public override string ToString() => Ok
            ? FormattableString.Invariant($"brake from {SampleAltitude / 1000:0.0} km at {SampleAirSpeed:0} m/s, start T{StartUT:F0}, Δv {DeltaV:0} m/s, {Probes} probes")
            : FormattableString.Invariant($"{Status}: {Reason}, {Probes} probes");
    }

    // The powered brake at the end of an unpowered atmospheric descent (HeatingPredictor's samples):
    // the lowest sample from which a brake started at once, in vacuum, still stops above the stop
    // height (BrakeSimulator.Simulate, bisection over the samples), then StopPointPredictor from that
    // sample for the latest start and the Δv. Pessimistic: the drag after that sample is not counted,
    // so the real brake ends higher and costs less. Pure maths, tested in EditMode.
    public static class AeroBrake
    {
        public const int CoarseSteps = 24;

        /// <param name="ut">The UT the body's TerrainHeight orientation refers to (the prediction's start).</param>
        public static AeroBrakeResult Find(IReadOnlyList<DescentSample> samples, Body body, double ut, Vehicle vehicle)
        {
            var result = new AeroBrakeResult { Status = BrakeStatus.Impossible, StartUT = double.NaN, DeltaV = double.NaN, SampleIndex = -1 };
            if (samples == null || samples.Count == 0)
            {
                result.Reason = "No descent to brake on";
                return result;
            }
            double landingThrust = vehicle.Staged ? vehicle.Stages[vehicle.Stages.Length - 1].ThrustKN : vehicle.ThrustKN;
            if (!(landingThrust > 0) || !(vehicle.MassT > 0))
            {
                result.Reason = "No thrust: activate an engine";
                return result;
            }
            double stopHeight = vehicle.StopHeight > 0 ? vehicle.StopHeight : BrakeSimulator.SafeAltitude;

            bool CanStop(int i)
            {
                result.Probes++;
                BrakeInput input = InputAt(samples[i], body, ut, vehicle);
                BrakeRun run = BrakeSimulator.Simulate(input, samples[i].UT);
                return run.Stopped && run.StopAltitude >= stopHeight;
            }

            // The touchdown sample (altitude 0) never stops: search the ones above it.
            int last = samples.Count - 1;
            while (last > 0 && !(samples[last].Altitude > 0))
                last--;
            if (!(samples[0].Altitude > 0) || !CanStop(0))
            {
                result.Reason = "Cannot stop before the ground even braking from the start of the descent";
                return result;
            }
            // Not monotone over the whole descent: high up, a horizontal hypersonic sample can fail
            // (the vacuum brake falls too far) while a later, slower one stops. So walk back from the
            // ground in CoarseSteps strides to the last sample that stops, then bisect the stride.
            int lo = 0, hi = last;
            if (CanStop(hi))
                lo = hi;
            else
            {
                int stride = Math.Max(1, last / CoarseSteps);
                for (int i = last - stride; i > 0; i -= stride)
                {
                    if (CanStop(i))
                    {
                        lo = i;
                        break;
                    }
                    hi = i;
                }
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    if (CanStop(mid)) lo = mid; else hi = mid;
                }
            }

            DescentSample s = samples[lo];
            StopPrediction stop = StopPointPredictor.Predict(s.R, s.V, s.UT, BodyAt(body, ut, s.UT), vehicle, null);
            result.Probes++;
            result.Stop = stop;
            result.SampleIndex = lo;
            result.SampleAltitude = s.Altitude;
            result.SampleAirSpeed = s.AirSpeed;
            if (!stop.Ok)
            {
                // Braking at the sample itself stops (CanStop): only the later start was refused.
                result.Status = BrakeStatus.TooLate;
                result.Ok = true;
                result.StartUT = s.UT;
                result.DeltaV = stop.BrakeDeltaV > 0 ? stop.BrakeDeltaV : double.NaN;
                result.Reason = stop.Reason;
                if (double.IsNaN(result.DeltaV))
                {
                    BrakeRun run = BrakeSimulator.Simulate(InputAt(s, body, ut, vehicle), s.UT);
                    result.DeltaV = run.DeltaV;
                    result.BurnDuration = run.BurnDuration;
                }
                return result;
            }
            result.Ok = true;
            result.Status = BrakeStatus.Ok;
            result.StartUT = stop.BrakeStartUT;
            result.DeltaV = stop.BrakeDeltaV;
            result.BurnDuration = stop.StopUT - stop.BrakeStartUT;
            return result;
        }

        // The body with its terrain read in the orientation at `at` instead of `ut`.
        internal static Body BodyAt(Body body, double ut, double at)
        {
            if (body.TerrainHeight == null || at == ut)
                return body;
            Body moved = body;
            Func<Vector3d, double> atUT = body.TerrainHeight;
            Vector3d omega = body.Omega;
            double turn = at - ut;
            moved.TerrainHeight = p => atUT(SurfaceGeometry.AtTime(p, omega, -turn));
            return moved;
        }

        static BrakeInput InputAt(DescentSample s, Body body, double ut, Vehicle vehicle)
        {
            Body b = BodyAt(body, ut, s.UT);
            return new BrakeInput
            {
                Position = s.R,
                Velocity = s.V,
                UT = s.UT,
                Mu = b.Mu,
                BodyRadius = b.Radius,
                AngularVelocity = b.Omega,
                ThrustKN = vehicle.ThrustKN,
                MassT = vehicle.MassT,
                Isp = vehicle.Isp,
                ImpactUT = s.UT,
                TerrainHeight = b.TerrainHeight ?? (p => 0),
                StopHeight = vehicle.StopHeight,
                Stages = vehicle.Staged ? vehicle.Stages : null,
                DropHeight = vehicle.Staged ? vehicle.DropHeight : 0,
            };
        }
    }
}
