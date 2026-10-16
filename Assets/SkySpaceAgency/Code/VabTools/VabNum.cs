using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public static class VabNum
    {
        // value rounded to digits decimals, or JSON null for NaN and infinities (JSON has no NaN,
        // and Newtonsoft would write a bare NaN token that most parsers reject).
        public static JToken Round(double value, int digits)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return JValue.CreateNull();
            return new JValue(Math.Round(value, digits, MidpointRounding.AwayFromZero));
        }

        // 0 for NaN and infinities, for sums.
        public static double Finite(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;

        // Short invariant text for messages: 5, 1.6, -1.
        public static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        // A JSON string, or JSON null (not a null-valued string token) for null.
        public static JToken Json(string value) => value == null ? JValue.CreateNull() : new JValue(value);
    }
}
