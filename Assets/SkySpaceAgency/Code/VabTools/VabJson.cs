using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    // JSON helpers of the VAB tools. Game JSON goes through them unchanged: no date parsing
    // (a date-like string stays a string), 64-bit integers kept, nothing after the value accepted.
    public static class VabJson
    {
        public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

        // Parses one JSON value. Throws JsonException on broken JSON, on empty or null text, and on
        // anything but comments after the value.
        public static JToken Parse(string text)
        {
            if (text == null)
                throw new JsonReaderException("No text.");
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                JToken token = JToken.ReadFrom(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException($"Unexpected content after the JSON value (line {reader.LineNumber}).");
                }
                return token;
            }
        }

        public static string Write(JToken token) => token.ToString(Formatting.Indented);

        public static string WriteCompact(JToken token) => token.ToString(Formatting.None);

        // The string, or null when the token is missing or not a JSON string.
        public static string Str(JToken token) =>
            token != null && token.Type == JTokenType.String ? (string)token : null;

        // A JSON number, or a string holding one ("-Infinity" in game files); null otherwise.
        public static double? Num(JToken token)
        {
            if (token == null)
                return null;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.String:
                    return double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                        ? v
                        : (double?)null;
                default:
                    return null;
            }
        }

        // Normalized GUID ("D" format, lower case) of a game id object {"Guid": "...", "DebugName": ...},
        // or null when it is missing, not an object or not a GUID.
        public static string GuidOf(JToken idObject)
        {
            string text = idObject is JObject o ? Str(o["Guid"]) : null;
            return text != null && Guid.TryParse(text, out Guid g) ? g.ToString("D") : null;
        }
    }
}
