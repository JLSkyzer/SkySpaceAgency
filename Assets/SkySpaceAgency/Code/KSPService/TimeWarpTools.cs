using KSP.Sim.impl;
using KSP.Game;
using KTools;

namespace K2D2
{
    public class TimeWarpTools
    {
        static TimeWarp time_warp => GameManager.Instance?.Game?.ViewController?.TimeWarp;

        static public int CurrentRateIndex
        {
            get
            {
                if (time_warp == null) return 0;
                return time_warp.CurrentRateIndex;
            }
        }
        static public float CurrentRate
        {
            get
            {
                if (time_warp == null) return 0;
                return time_warp.CurrentRate;
            }
        }

        public static void KspWarpTo(double ut)
        {
            time_warp.WarpTo(ut);
        }

        public static float indexToRatio(int index)
        {
            var levels = time_warp.GetWarpRates();
            if (index < 0 || index >= levels.Length) return 0f;

            return levels[index].TimeScaleFactor;
        }

        public static int ratioToIndex(float ratio)
        {
            var levels = time_warp.GetWarpRates();
            for (int index = 0; index < levels.Length; index++)
            {
                float factor = levels[index].TimeScaleFactor;
                if (ratio < factor)
                    return index;
            }

            return levels.Length - 1;
        }



        // Every warp the mod asks for goes through WarpGate: rails warp only once the engines have
        // stopped pushing (throttle 0, no engine force for 1 s and 10 frames); held at 1x until then.
        public static void SetRateIndex(int rate_index, bool instant)
        {
            if (time_warp == null) return;
            int current = time_warp.CurrentRateIndex;
            rate_index = WarpGate.Allowed(rate_index, current, time_warp.IsPhysicsTimeWarp);
            if (rate_index != current)
                time_warp.SetRateIndex(rate_index, instant);
        }

        // Physics warp only (PhysicsWarp sets physics mode itself): not gated.
        public static void SetRateIndexPhysics(int rate_index, bool instant)
        {
            if (time_warp == null) return;
            if (rate_index != time_warp.CurrentRateIndex)
                time_warp.SetRateIndex(rate_index, instant);
        }

        public static void SetIsPaused(bool paused)
        {
            GeneralTools.Game.UniverseModel.SetTimePaused(paused);
        }
    }

}

