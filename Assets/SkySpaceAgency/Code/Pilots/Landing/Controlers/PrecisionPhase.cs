using System;
using K2D2.Controller;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.UI;
using KTools;
using UnityEngine.UIElements;

namespace K2D2.Landing
{
    /// <summary>
    /// One burn of a precision landing (orbit preparation, plane change, deorbit, mid-course
    /// correction): waits (warping when allowed) until its planning time, asks LandingPilot for a
    /// plan made from the orbit really reached, then flies it closed loop, a TargetOrbitBurn toward
    /// the planned orbit or a CircularizeBurn at the periapsis. No maneuver node is followed. A
    /// refused plan or burn fails the phase (LandingPilot.OnPrecisionPhaseFailed: stop while the
    /// orbit clears the ground, else skip or land without target); a phase with nothing to do
    /// finishes at once.
    /// </summary>
    public class PrecisionPhase : ExecuteController
    {
        public struct Plan
        {
            public string Error;                  // refuse the phase with this reason
            public bool Skip;                     // nothing to burn: done
            public bool CircularizeAtPeriapsis;   // CircularizeBurn at the next periapsis instead of Target
            public TargetOrbit Target;
            public double StartUT;                // burn start (TargetOrbitBurn aligns before it)
            public double PlannedDeltaV;          // m/s, for the log
            public double DoneDeltaV;             // TargetOrbitBurn end threshold; 0 = its default
        }

        public string Name { get; private set; } = "";
        public double PlannedDeltaV { get; private set; }
        public bool Skipped { get; private set; }

        // Warp toward the planning time and the burn (the Land tab's auto-warp setting).
        public bool allow_warp = true;
        // Rails warp during the burns (TargetOrbitBurn.allow_rails_warp). Off: landing and reentry
        // burns stay at physics x4 at most (the user's rule, 2026-10-25).
        public bool allow_rails_warp = false;

        readonly TargetOrbitBurn orbit_burn = new TargetOrbitBurn();
        readonly CircularizeBurn circularize = new CircularizeBurn();
        readonly WarpTo warp = new WarpTo();
        TargetOrbitBurn active;
        Func<Plan> planner;
        double plan_UT;
        bool planned;
        bool warping;

        /// <summary>True while the engines may be burning (not waiting, warping or aligning).</summary>
        public bool Burning => active != null && !finished && active.mode == TargetOrbitBurn.Mode.Burn;

        public void Start(string name, double planUT, Func<Plan> planner)
        {
            base.Start();
            Name = name;
            this.planner = planner;
            plan_UT = planUT;
            planned = false;
            warping = false;
            active = null;
            PlannedDeltaV = 0;
            Skipped = false;
            status_line = $"Precision: {name}";
        }

        public override void Update()
        {
            if (finished)
                return;
            double now = GeneralTools.Current_UT;

            if (!planned)
            {
                if (now < plan_UT)
                {
                    if (allow_warp)
                    {
                        if (!warping)
                        {
                            warp.Start_Ut(plan_UT);
                            warping = true;
                        }
                        warp.UT = plan_UT;
                        warp.Update();
                        status_line = $"{Name}: planned in {StrTool.DurationToString(plan_UT - now)} - {warp.status_line}";
                    }
                    else
                        status_line = $"{Name}: planned in {StrTool.DurationToString(plan_UT - now)}";
                    return;
                }
                if (warping)
                {
                    TimeWarpTools.SetRateIndex(0, false);
                    warping = false;
                }

                planned = true;
                Plan plan;
                try
                {
                    plan = planner();
                }
                catch (Exception e)
                {
                    Fail($"{Name}: {e.Message}");
                    return;
                }
                if (plan.Error != null)
                {
                    Fail(plan.Error);
                    return;
                }
                if (plan.Skip)
                {
                    Skipped = true;
                    finished = true;
                    status_line = $"{Name}: not needed";
                    return;
                }
                PlannedDeltaV = plan.PlannedDeltaV;
                if (plan.CircularizeAtPeriapsis)
                {
                    circularize.allow_warp = allow_warp;
                    circularize.allow_rails_warp = allow_rails_warp;
                    circularize.Start(atApoapsis: false);
                    active = circularize;
                }
                else
                {
                    orbit_burn.allow_warp = allow_warp;
                    orbit_burn.allow_rails_warp = allow_rails_warp;
                    if (plan.DoneDeltaV > 0)
                        orbit_burn.Start(plan.Target, plan.StartUT, plan.DoneDeltaV);
                    else
                        orbit_burn.Start(plan.Target, plan.StartUT);
                    active = orbit_burn;
                }
                if (active.failure_reason != null)
                {
                    Fail($"{Name}: {active.failure_reason}");
                    return;
                }
            }

            active.Update();
            status_line = $"{Name}: {active.status_line}";
            if (active.failure_reason != null)
                Fail($"{Name}: {active.failure_reason}");
            else if (active.finished)
                finished = true;
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            active?.updateUI(root_el, st);
        }
    }
}
