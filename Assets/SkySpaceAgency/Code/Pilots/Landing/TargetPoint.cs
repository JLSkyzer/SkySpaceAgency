using System;

namespace K2D2.Landing
{
    /// <summary>
    /// Checks on a point picked on the map: coordinates sane and normalized, body the one the
    /// vessel orbits. Pure logic, tested in the EditMode tests.
    /// </summary>
    public static class TargetPoint
    {
        /// <summary>False for a non-finite value or a latitude outside [-90, 90]; longitude wrapped into [-180, 180).</summary>
        public static bool TryNormalize(double latitude, double longitude, out double lat, out double lon)
        {
            lat = latitude;
            lon = longitude;
            if (double.IsNaN(latitude) || double.IsInfinity(latitude)
                || double.IsNaN(longitude) || double.IsInfinity(longitude)
                || Math.Abs(latitude) > 90)
                return false;
            double wrapped = ((longitude + 180) % 360 + 360) % 360 - 180;
            lon = wrapped;
            return true;
        }

        /// <summary>Null when the picked body is the current one (ordinal), else the message to show.</summary>
        public static string Accept(string pickedBody, string currentBody) =>
            string.Equals(pickedBody, currentBody, StringComparison.Ordinal)
                ? null
                : $"Picked point is on {pickedBody}, not {currentBody}: ignored.";
    }
}
