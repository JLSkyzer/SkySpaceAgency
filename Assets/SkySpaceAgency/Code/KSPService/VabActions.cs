using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using K2D2.VabTools;
using KSP.OAB;
using KSP.Sim;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace K2D2.KSPService
{
    // The three VAB tools: files, checks, game calls and their "[VabTools] ..." log lines.
    // Called by UI/VabToolsWindow.
    public static class VabActions
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        const string Unchanged = " Your rocket was not changed.";

        public static string Folder() => K2D2_Plugin.VabFolder;

        static string Folder(string sub) => Path.Combine(Folder(), sub);

        public static void EnsureFolders()
        {
            Directory.CreateDirectory(Folder(VabFiles.ExportsFolder));
            Directory.CreateDirectory(Folder(VabFiles.ImportsFolder));
            Directory.CreateDirectory(Folder(VabFiles.DumpsFolder));
        }

        public static List<string> ImportChoices()
        {
            EnsureFolders();
            return VabFiles.ImportChoices(FileNames(VabFiles.ImportsFolder), FileNames(VabFiles.ExportsFolder));
        }

        static IEnumerable<string> FileNames(string sub) =>
            Directory.GetFiles(Folder(sub), "*.json").Select(f => Path.GetFileName(f));

        static void Log(string message) => K2D2_Plugin.logger.LogInfo("[VabTools] " + message);

        static void WriteJson(string path, JToken document) =>
            File.WriteAllText(path, VabJson.Write(document), Utf8NoBom);

        // ---- Export -------------------------------------------------------------------------

        public static string Export(out string message)
        {
            ObjectAssemblyBuilder builder = VabReader.Builder();
            if (builder == null) { message = "The VAB is not ready yet."; return null; }
            IObjectAssembly main = VabReader.MainAssembly(builder);
            if (main == null) { message = "No rocket in the VAB: nothing to export."; return null; }
            try
            {
                EnsureFolders();
                DateTime now = DateTime.Now;
                JToken assembly = VabReader.GameAssembly(builder, main);
                VabVessel vessel = VabReader.ReadVessel(builder, main);
                JObject document = VabExport.Build(VabReader.ExportMeta(now), VabSummary.Build(vessel), assembly);
                string path = VabFiles.UniquePath(Folder(VabFiles.ExportsFolder),
                    VabFiles.ExportFileName(vessel.Name, now), File.Exists);
                WriteJson(path, document);
                Log($"export: {vessel.Parts.Count} part(s), {vessel.Stages.Count} stage(s) in game order " +
                    $"[{string.Join(", ", vessel.Stages.Select(s => s.Stage))}], delta-v ready {vessel.DeltaVReady}, " +
                    $"total vac {VabNum.Text(vessel.DeltaVTotalVac)} m/s -> {path}");
                message = "Exported to " + path +
                          (vessel.DeltaVReady ? "" : ". Delta-v was not ready yet: export again in a moment.");
                return path;
            }
            catch (Exception e)
            {
                Log("export failed: " + e);
                message = "Export failed: " + e.Message;
                return null;
            }
        }

        // ---- Dump ---------------------------------------------------------------------------

        public static string Dump(out string message)
        {
            ObjectAssemblyBuilder builder = VabReader.Builder();
            if (builder == null) { message = "The VAB is not ready yet."; return null; }
            try
            {
                EnsureFolders();
                DateTime now = DateTime.Now;
                List<VabDumpPart> parts = VabReader.ReadAllParts(builder);
                IObjectAssembly main = VabReader.MainAssembly(builder);
                JToken example = main != null ? VabReader.GameAssembly(builder, main) : null;
                VabExportMeta meta = VabReader.ExportMeta(now);
                JObject document = VabDump.Build(meta.Campaign, now, meta.GameVersion, meta.ModVersion, parts, example, false);
                string text = VabJson.Write(document);
                string path = VabFiles.UniquePath(Folder(VabFiles.DumpsFolder),
                    VabFiles.DumpFileName(meta.Campaign.Name, now), File.Exists);
                File.WriteAllText(path, text, Utf8NoBom);
                int unlocked = parts.Count(p => p.Unlocked);
                // Spec: part_templates may be limited to unlocked parts if too heavy; measured here.
                int templatesKb = VabJson.WriteCompact(document["part_templates"]).Length / 1024;
                Log($"dump: {parts.Count} part(s), {unlocked} unlocked in this VAB, " +
                    $"{parts.Count(p => p.Engine != null)} with engine data, {parts.Count(p => p.TechNode != null)} with a tech node, " +
                    $"templates ~{templatesKb} KB compact, file ~{text.Length / 1024} KB, " +
                    $"example assembly {(example != null ? "yes" : "no")} -> {path}");
                message = $"Dumped {parts.Count} parts ({unlocked} unlocked) to {path}";
                return path;
            }
            catch (Exception e)
            {
                Log("dump failed: " + e);
                message = "Dump failed: " + e.Message;
                return null;
            }
        }

        // ---- Import -------------------------------------------------------------------------

        class Attempt
        {
            public string Name;
            public Action<ObjectAssemblyBuilder> Run;
            public int ExpectedMain;
            public int ExpectedTotal;
        }

        class SettleResult
        {
            public bool Settled;
        }

        static string TryRun(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                Log("import: exception: " + e);
                return e.GetType().Name + ": " + e.Message;
            }
        }

        static IEnumerator WaitSettled(ObjectAssemblyBuilder builder, int expectedMain, int expectedTotal, SettleResult result)
        {
            float deadline = Time.realtimeSinceStartup + VabLoader.SettleTimeout;
            while (true)
            {
                yield return null;
                if (builder == null)
                    yield break;
                if (VabLoader.Settled(builder, expectedMain, expectedTotal))
                {
                    result.Settled = true;
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                    yield break;
            }
        }

        static int HistoryCount(ObjectAssemblyBuilder builder)
        {
            try { return builder.Historian?.History?.Count ?? -1; }
            catch (Exception) { return -1; }
        }

        // The loader table of the plan.
        static List<Attempt> Plan(VabImportFile file, JObject original)
        {
            var attempts = new List<Attempt>();
            if (file.Format == VabImportFormat.WorkspaceSnapshot)
            {
                attempts.Add(new Attempt
                {
                    Name = "A (whole workspace from the file: ClearAll + ApplySnapshot)",
                    Run = b => VabLoader.ApplyWorkspace(b, file.Root),
                    ExpectedMain = VabSnapshot.MainPartCount(file.Root),
                    ExpectedTotal = VabSnapshot.TotalPartCount(file.Root),
                });
                return attempts;
            }
            int imported = VabSnapshot.PartCount(file.Assembly);
            var swapped = (JObject)original.DeepClone();
            if (VabSnapshot.ReplaceMain(swapped, file.Assembly))
                attempts.Add(new Attempt
                {
                    Name = "A (main rocket swapped in the workspace snapshot: ClearAll + ApplySnapshot)",
                    Run = b => VabLoader.ApplyWorkspace(b, swapped),
                    ExpectedMain = imported,
                    ExpectedTotal = VabSnapshot.TotalPartCount(swapped),
                });
            attempts.Add(new Attempt
            {
                Name = "B (main rocket deleted, then LoadSerializedAssembly)",
                Run = b => VabLoader.LoadAsMain(b, file.Assembly),
                ExpectedMain = imported,
                ExpectedTotal = VabSnapshot.TotalPartCount(original) - VabSnapshot.MainPartCount(original) + imported,
            });
            return attempts;
        }

        static string SuccessMessage(VabImportFile file, List<string> clamps, string backupPath)
        {
            string what = file.Format == VabImportFormat.WorkspaceSnapshot
                ? "Workspace loaded from the file"
                : $"Imported as the main rocket ({VabSnapshot.PartCount(file.Assembly)} parts)";
            string clampNote = clamps.Count == 0
                ? ""
                : $" {clamps.Count} resource value(s) lowered to capacity, first: {clamps[0]}.";
            return $"{what}. Previous workspace saved to exports/{Path.GetFileName(backupPath)}.{clampNote}";
        }

        public static IEnumerator Import(string path, Action<string> report)
        {
            ObjectAssemblyBuilder builder = VabReader.Builder();
            if (builder == null) { report("The VAB is not ready yet."); yield break; }
            string fileName = Path.GetFileName(path);

            // 1. Read and recognize the file.
            string text = null;
            string error = TryRun(() => text = File.ReadAllText(path, Encoding.UTF8));
            if (error != null)
            {
                Log($"import: cannot read {path}: {error}");
                report($"Cannot read {fileName}: {error}");
                yield break;
            }
            VabImportResult parsed = VabImport.Parse(text);
            if (!parsed.Ok)
            {
                Log($"import: {fileName}: refused: {parsed.Error}");
                report("Refused: " + parsed.Error + Unchanged);
                yield break;
            }
            VabImportFile file = parsed.File;
            Log($"import: {fileName}: recognized as {file.Format}, {file.Assemblies.Count} assembly(ies), " +
                $"{file.Assemblies.Sum(a => VabSnapshot.PartCount(a))} part(s)");

            // 2. Structure: GUIDs, root, node pairs.
            List<string> problems = file.Assemblies.SelectMany(a => VabImport.CheckStructure(a)).ToList();
            Log($"import: structure check: {problems.Count} problem(s)" + (problems.Count > 0 ? ": " + string.Join(" | ", problems) : ""));
            if (problems.Count > 0)
            {
                report("Refused: " + problems[0] +
                       (problems.Count > 1 ? $" (and {problems.Count - 1} more, see the log)." : "") + Unchanged);
                yield break;
            }

            // 3. Parts: every part must be offered by this VAB (spec: refuse and list them).
            List<string> names = VabImport.PartNames(file.Assemblies);
            var available = new HashSet<string>(builder.Stats.AvailableParts.Where(p => p?.Name != null).Select(p => p.Name));
            List<VabMissingPart> missing = VabImport.MissingParts(names, available, VabReader.KnownPartNames(builder));
            Log($"import: parts checked: {names.Count} distinct name(s), {available.Count} available in this VAB, " +
                $"{missing.Count} missing" +
                (missing.Count > 0 ? ": " + string.Join(", ", missing.Select(m => m.Name + (m.Known ? " (not unlocked)" : " (unknown)"))) : ""));
            if (missing.Count > 0)
            {
                report(VabImport.DescribeMissing(missing));
                yield break;
            }

            // 4. Resource amounts above capacity are lowered (spec, choix 2).
            Func<string, string, double?> capacity = VabReader.CapacityLookup(builder);
            List<string> clamps = file.Assemblies.SelectMany(a => VabImport.ClampResources(a, capacity)).ToList();
            Log($"import: resources: {clamps.Count} value(s) lowered");
            foreach (string c in clamps)
                Log("import: clamped " + c);

            // 5. The game must read the JSON and agree that every part is unlocked.
            List<SerializedAssembly> readable = null;
            error = TryRun(() => readable = VabLoader.ReadAssemblies(file));
            if (error != null)
            {
                Log("import: the game cannot read the file: " + error);
                report("Refused: the game cannot read this assembly (" + error + ")." + Unchanged);
                yield break;
            }
            bool unlocked = true, enough = true;
            string unlockError = TryRun(() => unlocked = VabLoader.AllUnlockedForGame(readable));
            string fundsError = TryRun(() => enough = readable.All(a => builder.HasEnoughResourcesToLoadAssembly(a)));
            Log($"import: game checks: AreAllAssemblyPartsUnlocked {unlockError ?? unlocked.ToString()}, " +
                $"HasEnoughResourcesToLoadAssembly {fundsError ?? enough.ToString()} (not enforced)");
            if (unlockError == null && !unlocked)
            {
                report("Refused: the game reports that this rocket uses parts not unlocked in this campaign." + Unchanged);
                yield break;
            }

            // 6. Backup of the whole workspace (spec, choix 3), before anything changes.
            JObject original = null;
            string backupPath = null;
            error = TryRun(() =>
            {
                original = VabLoader.CurrentSnapshot(builder);
                EnsureFolders();
                backupPath = VabFiles.UniquePath(Folder(VabFiles.ExportsFolder),
                    VabFiles.BackupFileName(VabReader.VesselName(builder), DateTime.Now), File.Exists);
                WriteJson(backupPath, original);
            });
            if (error != null)
            {
                Log("import: backup failed, nothing changed: " + error);
                report("Refused: the backup of the current rocket failed (" + error + ")." + Unchanged);
                yield break;
            }
            int originalMain = VabSnapshot.MainPartCount(original);
            int originalTotal = VabSnapshot.TotalPartCount(original);
            Log($"import: backup of the whole workspace (main {originalMain} part(s), {originalTotal} in all) -> {backupPath}");
            int historyBefore = HistoryCount(builder);

            // 7. Load, method by method; after a failure, restore the original before going on.
            string failure = null;
            foreach (Attempt attempt in Plan(file, original))
            {
                Log($"import: method {attempt.Name}: expecting main {attempt.ExpectedMain} part(s), {attempt.ExpectedTotal} in all");
                string attemptError = TryRun(() => attempt.Run(builder));
                if (attemptError == null)
                {
                    var settle = new SettleResult();
                    yield return WaitSettled(builder, attempt.ExpectedMain, attempt.ExpectedTotal, settle);
                    if (!settle.Settled)
                        attemptError = $"after {VabLoader.SettleTimeout:0} s the VAB shows {VabLoader.Describe(builder)}";
                }
                if (attemptError == null)
                {
                    Log($"import: result: success with method {attempt.Name}; {VabLoader.Describe(builder)}; " +
                        $"undo history {historyBefore} -> {HistoryCount(builder)} snapshot(s)");
                    report(SuccessMessage(file, clamps, backupPath));
                    yield break;
                }

                failure = attemptError;
                Log($"import: method {attempt.Name} failed: {attemptError}");
                string restoreError = TryRun(() => VabLoader.ApplyWorkspace(builder, original));
                if (restoreError == null)
                {
                    var settle = new SettleResult();
                    yield return WaitSettled(builder, originalMain, originalTotal, settle);
                    if (!settle.Settled)
                        restoreError = "the VAB shows " + VabLoader.Describe(builder);
                }
                if (restoreError != null)
                {
                    Log("import: restore failed: " + restoreError);
                    report($"Import failed ({attemptError}) and the previous rocket could not be restored. " +
                           $"Choose exports/{Path.GetFileName(backupPath)} in the list and import it.");
                    yield break;
                }
                Log("import: previous workspace restored: " + VabLoader.Describe(builder));
            }
            Log("import: result: failed: " + failure);
            report($"Import failed: {failure}. Your previous rocket was restored (backup: exports/{Path.GetFileName(backupPath)}).");
        }
    }
}
