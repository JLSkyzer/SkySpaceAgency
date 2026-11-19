using System;

namespace K2D2.KSPService
{
    // The two height readings K2-D2 1.4.0 (IanMealworm) uses for the landing, as pure rules:
    // KSPVessel.GetGlobalGroundAltitudeWithTerrain and GetApproxAltitudeGrounded feed them the
    // game's readings. Not used to fly yet: the landing logs them next to today's readings
    // ("[Landing] altitude:") so the two can be compared in game before any caller switches.
    public static class AltitudeReading
    {
        // Below this sea-level height, a ground height equal to the sea-level one is suspect.
        public const double TerrainFallbackBelowM = 3000;
        // Ground and sea-level heights closer than this are "the same reading".
        public const double SameReadingM = 0.5;
        // A terrain height further below the vessel than this is not believed.
        public const double LowestTerrainReadingM = -50;

        // The lower of the sea-level and ground heights. Near the space center the ground height
        // can fall back to the sea-level one when the terrain or the pad isn't picked up (on the
        // pad it then reads tens of metres high); low down, the vessel's own terrain height
        // (VesselComponent.AltitudeFromTerrain) then stands in for it when it is lower.
        public static double GroundWithTerrainFallback(double sea, double ground, double fromTerrain)
        {
            double altitude = Math.Min(sea, ground);
            if (Math.Abs(ground - sea) < SameReadingM && sea < TerrainFallbackBelowM
                && !double.IsNaN(fromTerrain) && fromTerrain > LowestTerrainReadingM && fromTerrain < altitude)
                altitude = fromTerrain;
            return altitude;
        }

        // Height of the vessel's bottom above the ground, roughly: the ground height less the
        // bounding-sphere radius, never below 0, and 0 while landed or splashed down.
        public static double Grounded(bool landedOrSplashed, double groundHeight, double boundingRadius)
        {
            if (landedOrSplashed)
                return 0;
            double result = groundHeight - boundingRadius;
            return double.IsNaN(result) ? result : Math.Max(0, result);
        }
    }
}
