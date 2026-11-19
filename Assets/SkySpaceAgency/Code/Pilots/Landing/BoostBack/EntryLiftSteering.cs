using System;

namespace SkySpaceAgency.Landing
{
    // The steering rule of AtmoEntryGuidance (K2-D2 1.4.0, IanMealworm), apart from the game so it
    // is tested with a fake prediction: from Redux's miss (along the flight and across it), the lift
    // vector to fly next.
    //
    // The lift is steered as a vector: its part pointing up (stretching the flight: the landing
    // point moves further) and its part to the side, each sized from the miss on its own axis. How
    // far a unit of lift moves the landing point (range_per_lift_m) isn't known in advance, so it is
    // measured from each pair of steps (a secant estimate) and taken to be the same for both axes -
    // it's the same lift, turned. When the along-track miss stays put for several steps, the
    // estimate is cut (the steering is too timid).
    //
    // Also here: the angle of attack boost while the target is out of the lift's reach, and the
    // check of which way the vessel really lifts (a booster flying engines-first lifts away from its
    // tilt: tilt_offset_deg 180, flipped to 0 if three measurements in a row say otherwise).
    public class EntryLiftSteering
    {
        // When all the lift can't reach the target (still missing with the full angle of attack
        // setting flown), the angle of attack goes up in steps - more lift, and more drag - up to
        // this, as far as heating allows. It comes back down once the lift has room again.
        public const double max_angle_of_attack_deg = 25;
        public const double aoa_boost_step_deg = 2.5;
        public const double aoa_boost_interval_s = 5;
        // Smaller misses than this aren't worth more angle of attack.
        public const double aoa_boost_min_miss_m = 1000;

        // Bigger lift changes are split into steps this size (as a fraction of the full lift), so
        // each one settles quickly; smaller ones than the second aren't worth making - each one
        // stirs the attitude and makes Redux start over.
        public const double max_lift_step = 0.5, min_lift_change = 0.03;

        // How far (m) the landing point moves per unit of lift, to start from: the probe
        // flights saw about 20 km between lift fully up and fully down.
        public const double initial_range_per_lift_m = 10000;
        public const double min_range_per_lift_m = 500;
        public const double min_lift_change_to_measure = 0.2;
        public const double range_per_lift_blend = 0.5;
        public const double max_range_per_lift_m = 200000;
        // When the along-track miss stays put - same side, not shrinking - for this many steps,
        // the steering is too timid (the estimate above can't be measured while the vertical lift
        // barely changes), so the estimate is cut by this factor. A low, slow boost-back moves
        // the landing point far less per unit of lift than a reentry does.
        public const int stuck_steps = 4;
        public const double stuck_cut = 0.6;
        public const double along_good_enough_m = 150;

        // Measured lift has to be at least this big to tell which way it points, the vessel has
        // to be flying at least this angle of attack (near 0° there's hardly any lift and what's
        // measured is noise), and this many measurements in a row have to agree clearly (within
        // lift_agree_deg of one answer or the other). The seventh test flight measured once, at
        // 0-2.5° late in the entry, got an answer 98° off - barely past "the other way" - and
        // flipped the lean, so "lift down" became lift up for the rest of the flight.
        public const double min_lift_to_measure = 0.3;
        public const double min_aoa_to_measure_lift_deg = 5;
        public const int lift_measurements_needed = 3;
        public const double lift_agree_deg = 45;

        // The wanted lift: which way it points around the airflow (0 up, ±90 to either side, 180
        // down) and how much of the angle of attack setting is flown for it (0 to 1).
        public double lift_bank_deg { get; private set; }
        public double lift_scale { get; private set; }
        double last_side_lift, last_across_error;
        int stuck_count;
        double last_along_miss = double.NaN;

        // How far the vessel has to be tilted from where its lift should point. A booster flying
        // engines-first lifts away from its tilt: 180. Checked in flight.
        public double tilt_offset_deg { get; private set; }
        public bool lift_direction_checked { get; private set; }
        int lift_votes_same, lift_votes_flipped;

        public double range_per_lift_m { get; private set; }
        double last_vertical_lift = double.NaN, last_along_error = double.NaN;

        public double aoa_boost_deg { get; private set; }
        double last_boost_change_UT;

        // Whether the steering has run out of ways to take back an overshoot (lift already fully
        // down): only then is the landing burn brought forward to stop short (AtmoBurnPlanner).
        public bool overshoot_out_of_reach { get; private set; }
        public int updates { get; private set; }

        // The share of the lift pointing up (stretching the flight) and to the side, as fractions
        // of the lift the angle of attack setting gives.
        public double vertical_lift => lift_scale * Math.Cos(lift_bank_deg * Math.PI / 180);
        public double side_lift => lift_scale * Math.Sin(lift_bank_deg * Math.PI / 180);

        public EntryLiftSteering()
        {
            Reset();
        }

        public void Reset()
        {
            // No lift to start with: straight into the airflow.
            lift_bank_deg = 0;
            lift_scale = 0;
            last_side_lift = last_across_error = double.NaN;
            stuck_count = 0;
            last_along_miss = double.NaN;
            tilt_offset_deg = 180;
            lift_direction_checked = false;
            lift_votes_same = lift_votes_flipped = 0;
            range_per_lift_m = initial_range_per_lift_m;
            last_vertical_lift = last_along_error = double.NaN;
            aoa_boost_deg = 0;
            last_boost_change_UT = double.NaN;
            overshoot_out_of_reach = false;
            updates = 0;
        }

        // The angle of attack changed size (heat limit, boost): the range a unit of vertical lift
        // buys has changed, so the last step can't be compared with the next.
        public void ForgetLastStep() => last_vertical_lift = double.NaN;

        public struct Step
        {
            public double along;            // aim-relative along-track miss (redux_along - shortfall)
            public double across;
            public double old_up, old_side; // lift before the step
            public double new_up, new_side; // lift wanted (after the step-size limit)
            public bool changed;            // the lift changed (worth a new attitude)
        }

        // One steering step on a fresh Redux prediction. redux_along: where Redux lands along the
        // flight (positive: beyond the target); shortfall_m: how much shorter the landing burn will
        // make the flight (the steering aims that far beyond the target); nominal_aoa_deg: the
        // angle of attack setting (for the boost).
        public Step Steer(double redux_along, double across, double shortfall_m, double now, double nominal_aoa_deg)
        {
            // Secant: the change in Redux's landing point over the change in vertical lift since
            // the last step - from the landing point itself, since the overshoot moves with the
            // burn plan rather than with the lift. More lift up always means further (the tilt
            // offset takes care of which way the vessel has to lean for that), so a measurement
            // saying otherwise is noise; small changes are skipped, and measurements are blended in.
            double u = vertical_lift;
            if (!double.IsNaN(last_vertical_lift) && Math.Abs(u - last_vertical_lift) > min_lift_change_to_measure)
            {
                double measured = (redux_along - last_along_error) / (u - last_vertical_lift);
                if (measured >= min_range_per_lift_m)
                    range_per_lift_m += (Math.Min(measured, max_range_per_lift_m) - range_per_lift_m) * range_per_lift_blend;
            }
            last_vertical_lift = u;
            last_along_error = redux_along;

            // Aim beyond the target by however much shorter the landing burn will make the flight.
            double along = redux_along - shortfall_m;

            if (Math.Abs(along) > along_good_enough_m && !double.IsNaN(last_along_miss)
                && Math.Sign(along) == Math.Sign(last_along_miss) && Math.Abs(along) >= 0.9 * Math.Abs(last_along_miss))
            {
                if (++stuck_count >= stuck_steps)
                {
                    range_per_lift_m = Math.Max(min_range_per_lift_m, range_per_lift_m * stuck_cut);
                    stuck_count = 0;
                }
            }
            else
                stuck_count = 0;
            last_along_miss = along;

            return SteerLift(along, across, u, now, nominal_aoa_deg);
        }

        // The lift vector from the misses: up (along the flight: further or shorter) and to the
        // side, each sized from the miss on its own axis.
        Step SteerLift(double along, double across, double u, double now, double nominal_aoa_deg)
        {
            double s = side_lift;
            if (!double.IsNaN(last_side_lift) && Math.Abs(s - last_side_lift) > min_lift_change_to_measure)
            {
                double measured = (across - last_across_error) / (s - last_side_lift);
                if (measured >= min_range_per_lift_m)
                    range_per_lift_m += (Math.Min(measured, max_range_per_lift_m) - range_per_lift_m) * range_per_lift_blend;
            }
            last_side_lift = s;
            last_across_error = across;

            double raw_u = u - along / range_per_lift_m;
            double raw_s = s - across / range_per_lift_m;
            double raw = Math.Sqrt(raw_u * raw_u + raw_s * raw_s);
            overshoot_out_of_reach = raw_u < -1 && along > aoa_boost_min_miss_m;
            AdjustAoaBoost(raw, Math.Sqrt(along * along + across * across), now, nominal_aoa_deg);

            // No more than all the lift there is, and no bigger change per step than this (each
            // one has to settle before Redux predicts it).
            double wanted_u = raw > 1 ? raw_u / raw : raw_u, wanted_s = raw > 1 ? raw_s / raw : raw_s;
            double du = wanted_u - u, ds = wanted_s - s;
            double change = Math.Sqrt(du * du + ds * ds);
            if (change > max_lift_step)
            {
                du *= max_lift_step / change;
                ds *= max_lift_step / change;
            }
            double new_u = u + du, new_s = s + ds;
            double new_scale = Math.Sqrt(new_u * new_u + new_s * new_s);

            updates++;
            var step = new Step { along = along, across = across, old_up = u, old_side = s, new_up = new_u, new_side = new_s };
            if (Math.Min(change, max_lift_step) >= min_lift_change)
            {
                lift_scale = Math.Min(1, new_scale);
                if (new_scale > 0.02)
                    lift_bank_deg = Math.Atan2(new_s, new_u) * 180 / Math.PI;
                step.changed = true;
            }
            return step;
        }

        // raw: how far outside its lift's reach the target is (above 1: out of reach).
        void AdjustAoaBoost(double raw, double miss, double now, double nominal_aoa_deg)
        {
            if (!double.IsNaN(last_boost_change_UT) && now - last_boost_change_UT < aoa_boost_interval_s)
                return;

            bool out_of_reach = raw > 1 && Math.Abs(miss) > aoa_boost_min_miss_m;
            if (out_of_reach && nominal_aoa_deg + aoa_boost_deg < max_angle_of_attack_deg)
                aoa_boost_deg = Math.Min(max_angle_of_attack_deg - nominal_aoa_deg, aoa_boost_deg + aoa_boost_step_deg);
            else if (!out_of_reach && raw < 0.7 && aoa_boost_deg > 0)
                aoa_boost_deg = Math.Max(0, aoa_boost_deg - aoa_boost_step_deg);
            else
                return;
            last_boost_change_UT = now;
        }

        // The angle of attack wanted before the heat limit: the setting plus any boost.
        public double WantedAoa(double nominal_aoa_deg) => Math.Min(max_angle_of_attack_deg, nominal_aoa_deg + aoa_boost_deg);

        // Compares where the measured lift points with where it was meant to (lift_up, lift_side:
        // the aero acceleration on the airflow axes; drag: along the flow), once there's enough air
        // to measure it, and flips the tilt offset if the vessel lifts the other way. True when
        // this measurement settled the question (measured_bank set).
        public bool CheckLiftDirection(double lift_up, double lift_side, double drag, double flown_aoa_deg, out double measured_bank)
        {
            measured_bank = double.NaN;
            if (lift_direction_checked || flown_aoa_deg < min_aoa_to_measure_lift_deg)
                return false;

            double lift = Math.Sqrt(lift_up * lift_up + lift_side * lift_side);
            if (lift < min_lift_to_measure || lift < 0.05 * drag)
                return false;

            measured_bank = Math.Atan2(lift_side, lift_up) * 180 / Math.PI;
            double difference = LandingTargeting.WrapDegrees180(measured_bank - lift_bank_deg);
            if (Math.Abs(difference) <= lift_agree_deg)
            {
                lift_votes_same++;
                lift_votes_flipped = 0;
            }
            else if (Math.Abs(difference) >= 180 - lift_agree_deg)
            {
                lift_votes_flipped++;
                lift_votes_same = 0;
            }
            else
            {
                lift_votes_same = lift_votes_flipped = 0;
                return false;
            }
            if (lift_votes_same < lift_measurements_needed && lift_votes_flipped < lift_measurements_needed)
                return false;

            lift_direction_checked = true;
            if (lift_votes_flipped >= lift_measurements_needed)
            {
                tilt_offset_deg = LandingTargeting.WrapDegrees180(tilt_offset_deg + 180);
                // The meaning of "vertical lift" just flipped, so the last step can't be compared
                // with the next one.
                last_vertical_lift = double.NaN;
                range_per_lift_m = Math.Abs(range_per_lift_m);
            }
            return true;
        }
    }
}
