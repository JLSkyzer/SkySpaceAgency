namespace SkySpaceAgency.Node
{
    /// <summary>What BurnManeuver sets the throttle to for the Δv still to burn.</summary>
    public struct BurnThrottleStep
    {
        public float Throttle;
        /// <summary>Seconds left at full throttle on the active engines; 0 without thrust.</summary>
        public float FullBurnTime;
        /// <summary>No active engine (none lit, or all shut down).</summary>
        public bool NoThrust;
    }

    /// <summary>
    /// The node burn's throttle, from K2-D2 1.4.0 (IanMealworm): the burn time counts only the
    /// engines lit now (BurndV.active_dv, our definition of active: ignited and not shut down), not
    /// the engines still waiting in later stages; with no active engine the throttle is full, so the
    /// burn goes ahead as soon as one is staged or switched on (it used to divide by a full Δv that
    /// counted those staged engines, or by 0).
    /// </summary>
    public static class BurnThrottle
    {
        /// <param name="remainingDv">Δv still to burn, m/s.</param>
        /// <param name="activeAccel">Acceleration of the active engines at full throttle, m/s².</param>
        /// <param name="burnAdjust">Throttle per second of burn left under one second (node_ex.burn.burn_adjust).</param>
        public static BurnThrottleStep Compute(double remainingDv, double activeAccel, float burnAdjust)
        {
            var step = new BurnThrottleStep();
            if (remainingDv <= 0)
                return step;

            if (!(activeAccel > 0))
            {
                step.NoThrust = true;
                step.Throttle = 1;
                return step;
            }

            step.FullBurnTime = (float)(remainingDv / activeAccel);
            step.Throttle = step.FullBurnTime >= 1 ? 1 : step.FullBurnTime * burnAdjust;
            return step;
        }
    }
}
