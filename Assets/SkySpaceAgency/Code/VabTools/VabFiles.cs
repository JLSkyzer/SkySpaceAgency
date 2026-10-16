using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace K2D2.VabTools
{
    // File and folder names of the VAB tools, under <mod folder>/vab/ (spec, "Fichiers").
    public static class VabFiles
    {
        public const string ExportsFolder = "exports";
        public const string ImportsFolder = "imports";
        public const string DumpsFolder = "dumps";
        public const int MaxNameLength = 60;

        // Characters Windows refuses in a file name. Path.GetInvalidFileNameChars depends on the OS
        // the code runs on, so the list is fixed here.
        const string InvalidChars = "\"<>|:*?\\/";

        // name with invalid characters replaced by '_', at most MaxNameLength characters, without
        // trailing dots or spaces; fallback when nothing usable is left.
        public static string SafeName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
                sb.Append(char.IsControl(c) || InvalidChars.IndexOf(c) >= 0 ? '_' : c);
            string safe = sb.ToString();
            if (safe.Length > MaxNameLength)
                safe = safe.Substring(0, MaxNameLength);
            safe = safe.Trim().TrimEnd('.', ' ');
            return safe.Length == 0 ? fallback : safe;
        }

        public static string Stamp(DateTime time) =>
            time.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

        public static string IsoTime(DateTime time) =>
            time.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

        public static string ExportFileName(string vesselName, DateTime time) =>
            $"{SafeName(vesselName, "vessel")}_{Stamp(time)}.json";

        public static string BackupFileName(string vesselName, DateTime time) =>
            $"before_import_{SafeName(vesselName, "vessel")}_{Stamp(time)}.json";

        public static string DumpFileName(string campaignName, DateTime time) =>
            $"parts_{SafeName(campaignName, "campaign")}_{Stamp(time)}.json";

        // folder/fileName, or folder/<stem>_2.json, _3... when that path is taken (two exports
        // in the same second).
        public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
        {
            string path = Path.Combine(folder, fileName);
            if (!exists(path))
                return path;
            string stem = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 5)
                : fileName;
            for (int i = 2; ; i++)
            {
                path = Path.Combine(folder, $"{stem}_{i}.json");
                if (!exists(path))
                    return path;
            }
        }

        // Entries of the import list: "imports/<file>" then "exports/<file>", .json files only,
        // each folder sorted by name. An entry is also the path relative to the vab folder.
        public static List<string> ImportChoices(IEnumerable<string> importFiles, IEnumerable<string> exportFiles)
        {
            var choices = new List<string>();
            choices.AddRange(JsonFiles(importFiles).Select(f => ImportsFolder + "/" + f));
            choices.AddRange(JsonFiles(exportFiles).Select(f => ExportsFolder + "/" + f));
            return choices;
        }

        static IEnumerable<string> JsonFiles(IEnumerable<string> files) =>
            (files ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrEmpty(f) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }
}
