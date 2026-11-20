using UnityEngine;

using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace SkySpaceAgency.Lift
{
    /// <summary>
    /// The Guided ascent profile's pitch (degrees above the horizon) from the altitude (km): 90 up to the
    /// start altitude, then down to 45 at the mid altitude and to 1 at the end altitude. The Ascent
    /// controller flies it; the LIFT tab draws the same curve (LiftProfileModel). The classic window's
    /// graph code (GraphLine drawing, atmosphere gradient) went with the classic window.
    /// </summary>
    public class LiftAscentPath
    {
        readonly LiftSettings lift_settings;

        public LiftAscentPath(LiftSettings lift_settings)
        {
            this.lift_settings = lift_settings;
        }

        public ILogger logger = ModIdentity.Logger("LiftAscentPath");

        public float compute_elevation(float altitude_km)
        {
            float elevation;
            if (altitude_km < lift_settings.start_altitude_km.V)
            {
                elevation = 90;
            }
            else if (altitude_km < lift_settings.mid_rotate_altitude_km)
            {
                var ratio = Mathf.InverseLerp(lift_settings.start_altitude_km.V, lift_settings.mid_rotate_altitude_km, altitude_km);
                elevation = Mathf.Lerp(90, 45, ratio);
            }
            else
            {
                var ratio = Mathf.InverseLerp(lift_settings.mid_rotate_altitude_km, lift_settings.end_rotate_altitude_km, (float)altitude_km);
                elevation = Mathf.Lerp(45, 1, ratio);
            }

            return elevation;
        }
    }
}
