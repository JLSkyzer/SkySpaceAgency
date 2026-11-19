using System;
using System.Collections.Generic;
using KSP.Modules;
using KSP.Sim.impl;

namespace SkySpaceAgency.Landing
{
    // One part's temperature, as EntryHeatLimit reads it. Key identifies the part from one reading
    // to the next (the PartComponent in game, anything in the tests).
    public struct PartHeat
    {
        public object Key;
        public string Name;
        public double Temperature;
        public double MaxTemp;
        public bool HeatShield;

        public PartHeat(object key, string name, double temperature, double max_temp, bool heat_shield)
        {
            Key = key;
            Name = name;
            Temperature = temperature;
            MaxTemp = max_temp;
            HeatShield = heat_shield;
        }
    }

    // Upper limit on the entry angle of attack from part temperatures (K2-D2 1.4.0, IanMealworm;
    // the readings come in as PartHeat here so the rule is tested without the game).
    //
    // Each part's temperature is extrapolated a little ahead from how fast it's rising; once any
    // part is expected to get near its maximum the limit steps down, and once everything has
    // stayed comfortably cool for a while it steps back up. The steps are coarse on purpose: every
    // change of attitude makes Redux start its prediction over.
    //
    // It only lowers the boost-back entry's angle of attack. Our heat guard (HeatGuard, in
    // LandingPilotDescent.cs) stays the authority: while it is on, the entry flies straight into the
    // airflow at its throttle (AtmoEntryGuidance.heat_guard_throttle).
    public class EntryHeatLimit
    {
        // (20 s forecast the eighth test flight's parts at 90-116% while they were really at 40-80%
        // and still well short of where they'd level off.)
        public const double lookahead_s = 10;
        public const double check_interval_s = 1;
        // Fractions of a part's maximum temperature. (At 0.8/0.6 the fourth test flight sat at 0°
        // from 24 km down with the hottest part at 79% and cooling, where the player could raise
        // it by hand without trouble.)
        public const double hot = 0.9;
        public const double cool = 0.82;
        public const double step_deg = 2.5;
        public const double raise_wait_s = 10;

        // How fast each part is heating is smoothed over about this long: from one second to the
        // next it jumps around enough to fake a part heading past its limit (the sixth test flight
        // stepped the angle of attack down and back up a dozen times on reaction wheel readings
        // like that).
        public const double rate_smoothing_s = 3;

        readonly Dictionary<object, double> previous_temperature = new Dictionary<object, double>();
        readonly Dictionary<object, double> heating_rate = new Dictionary<object, double>();
        double limit_deg;
        double last_check_UT, last_change_UT;

        public double hottest_fraction { get; private set; }
        public double hottest_predicted { get; private set; }
        public string hottest_part { get; private set; } = "-";

        public void Reset(double max_deg)
        {
            limit_deg = max_deg;
            previous_temperature.Clear();
            heating_rate.Clear();
            last_check_UT = last_change_UT = double.NaN;
            hottest_fraction = hottest_predicted = 0;
            hottest_part = "-";
        }

        // The game's parts. Heat shields are flagged (see Update).
        public static IEnumerable<PartHeat> Read(VesselComponent vessel)
        {
            foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
            {
                if (part == null)
                    continue;
                yield return new PartHeat(part, part.PartName, part.Temperature, part.MaxTemp,
                    part.TryGetModule<PartComponentModule_Heatshield>(out _));
            }
        }

        public double Update(VesselComponent vessel, double now, double current_deg, double max_deg) =>
            Update(Read(vessel), now, current_deg, max_deg);

        // Returns the current limit. current_deg: the angle of attack being flown, which a hot
        // part steps down from.
        public double Update(IEnumerable<PartHeat> parts, double now, double current_deg, double max_deg)
        {
            limit_deg = Math.Min(limit_deg, max_deg);
            if (!double.IsNaN(last_check_UT) && now - last_check_UT < check_interval_s)
                return limit_deg;
            double dt = double.IsNaN(last_check_UT) ? 0 : now - last_check_UT;
            last_check_UT = now;
            if (double.IsNaN(last_change_UT))
                last_change_UT = now;

            hottest_fraction = hottest_predicted = 0;
            foreach (PartHeat part in parts)
            {
                // Heat shields are built to face the hot flow and lowering the angle of attack only
                // turns them further into it, so they don't count. (The second test flight dropped
                // straight to 0° on a heat shield heating up fast but ending at 61% of its limit.)
                if (part.HeatShield || part.MaxTemp <= 0)
                    continue;
                double temperature = part.Temperature;
                double rate = 0;
                if (dt > 0 && previous_temperature.TryGetValue(part.Key, out double before))
                {
                    double measured = (temperature - before) / dt;
                    rate = heating_rate.TryGetValue(part.Key, out double smoothed)
                        ? smoothed + (measured - smoothed) * Math.Min(1, dt / rate_smoothing_s)
                        : measured;
                    heating_rate[part.Key] = rate;
                }
                previous_temperature[part.Key] = temperature;

                double predicted = (temperature + Math.Max(0, rate) * lookahead_s) / part.MaxTemp;
                if (predicted > hottest_predicted)
                {
                    hottest_predicted = predicted;
                    hottest_fraction = temperature / part.MaxTemp;
                    hottest_part = part.Name;
                }
            }

            if (hottest_predicted > hot && Math.Min(limit_deg, current_deg) > 0)
            {
                limit_deg = Math.Max(0, Math.Min(limit_deg, current_deg) - step_deg);
                last_change_UT = now;
            }
            else if (hottest_predicted < cool && limit_deg < max_deg && now - last_change_UT > raise_wait_s)
            {
                // A step above what's being flown (the limit may be well above it already).
                limit_deg = Math.Min(max_deg, Math.Max(limit_deg, current_deg) + step_deg);
                last_change_UT = now;
            }
            return limit_deg;
        }
    }
}
