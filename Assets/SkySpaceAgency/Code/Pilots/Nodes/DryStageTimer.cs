using System;

namespace K2D2.Node
{
    /// <summary>
    /// When a closed-loop burn should stage: the engines have given no thrust for DryDelay seconds
    /// of game time while thrust was commanded and Δv is still to go, and the last staging was at
    /// least MinInterval seconds ago. Only callers that opt in stage (TargetOrbitBurn.stage_when_dry,
    /// the transfer pilot). Pure, tested in EditMode.
    /// </summary>
    public sealed class DryStageTimer
    {
        public const double DryDelay = 0.5;
        public const double MinInterval = 1.0;
        // Measured thrust under this share of the commanded one counts as none (flamed out,
        // deprived, or the engines of the next stage not yet ignited).
        public const double DryShare = 0.01;

        double dry_since = double.NaN;
        double last_staging = double.NaN;

        public void Reset()
        {
            dry_since = double.NaN;
            last_staging = double.NaN;
        }

        /// <summary>
        /// Thrust commanded (throttle in (0, 1], the expected full-throttle acceleration in m/s²)
        /// but none measured. False when nothing is commanded or no engine could push.
        /// </summary>
        public static bool IsDry(double throttle, double expectedAccel, double measuredAccel)
        {
            if (!(throttle > 0) || !(expectedAccel > 0))
                return false;
            double measured = double.IsNaN(measuredAccel) ? 0 : measuredAccel;
            return measured <= DryShare * throttle * expectedAccel;
        }

        /// <summary>
        /// Called every burning frame. True when the caller should stage now; it then calls Staged
        /// once it has. Time going back (a quickload) restarts the wait.
        /// </summary>
        public bool ShouldStage(double ut, bool dry, bool deltaVLeft)
        {
            if (!double.IsNaN(last_staging) && ut < last_staging)
                last_staging = double.NaN;
            if (!dry || !deltaVLeft)
            {
                dry_since = double.NaN;
                return false;
            }
            if (double.IsNaN(dry_since) || ut < dry_since)
                dry_since = ut;
            if (ut - dry_since < DryDelay - 1e-9)
                return false;
            return double.IsNaN(last_staging) || ut - last_staging >= MinInterval - 1e-9;
        }

        /// <summary>The stage was fired at ut: the dry wait starts again.</summary>
        public void Staged(double ut)
        {
            last_staging = ut;
            dry_since = double.NaN;
        }
    }
}
