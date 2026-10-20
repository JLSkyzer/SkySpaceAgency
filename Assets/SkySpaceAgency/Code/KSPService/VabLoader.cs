using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.VabTools;
using KSP.IO;
using KSP.OAB;
using KSP.Sim;
using KTools;
using Newtonsoft.Json.Linq;

namespace K2D2.KSPService
{
    // The game calls of the import (see the plan's loader table). Every deserialization is checked
    // against the JSON's part count, so a silently misread file can never clear the workspace.
    public static class VabLoader
    {
        public static JObject CurrentSnapshot(ObjectAssemblyBuilder builder)
        {
            OABHistoricalSnapshot snapshot = builder.Historian.GenerateSnapshot(OABActionType.None);
            return (JObject)VabJson.Parse(IOProvider.ToJson(snapshot));
        }

        public static OABHistoricalSnapshot ToSnapshot(JObject json)
        {
            var snapshot = IOProvider.FromJson<OABHistoricalSnapshot>(VabJson.WriteCompact(json));
            int expected = VabSnapshot.TotalPartCount(json);
            int read = snapshot?.Assemblies?.Sum(a => a?.Assembly?.parts?.Count ?? 0) ?? -1;
            if (read != expected)
                throw new InvalidOperationException($"the game read {read} of {expected} part(s) of the workspace");
            return snapshot;
        }

        public static SerializedAssembly ToAssembly(JObject json)
        {
            var assembly = IOProvider.FromJson<SerializedAssembly>(VabJson.WriteCompact(json));
            int expected = VabSnapshot.PartCount(json);
            int read = assembly?.parts?.Count ?? -1;
            if (read != expected)
                throw new InvalidOperationException($"the game read {read} of {expected} part(s) of the assembly");
            return assembly;
        }

        // Reads every assembly (and the whole workspace for a workspace file) the way the game will,
        // before anything changes.
        public static List<SerializedAssembly> ReadAssemblies(VabImportFile file)
        {
            List<SerializedAssembly> list = file.Assemblies.Select(a => ToAssembly(a)).ToList();
            if (file.Format == VabImportFormat.WorkspaceSnapshot)
                ToSnapshot(file.Root);
            return list;
        }

        // The game's own unlock check ("true if all parts are unlocked or the science-unlock feature is disabled").
        public static bool AllUnlockedForGame(IEnumerable<SerializedAssembly> assemblies)
        {
            var oab = GeneralTools.Game?.OAB;
            return oab == null || assemblies.All(a => oab.AreAllAssemblyPartsUnlocked(a));
        }

        // Method A: the whole workspace from a snapshot. Deserialized first, so a bad snapshot throws
        // before ClearAll.
        public static void ApplyWorkspace(ObjectAssemblyBuilder builder, JObject snapshotJson)
        {
            OABHistoricalSnapshot snapshot = ToSnapshot(snapshotJson);
            builder.ActivePartTracker.ClearAll();
            builder.UtilFileIO.ApplySnapshot(builder.ActivePartTracker, snapshot, true);
        }

        // Method B: delete the main rocket, then load the assembly as the main one. Deleting first
        // keeps the two copies from sharing GUIDs when a rocket is re-imported. Returns the workspace
        // part count to expect afterwards, read from the VAB as it is now: B is also the fallback of
        // the restore, when the workspace may be half-cleared by a failed method A.
        public static int LoadAsMain(ObjectAssemblyBuilder builder, JObject assemblyJson)
        {
            SerializedAssembly assembly = ToAssembly(assemblyJson);
            IObjectAssembly oldMain = VabReader.MainAssembly(builder);
            int others = builder.ActivePartTracker.PartsMasterList.Count - (oldMain?.Anchor != null ? oldMain.Parts?.Count ?? 0 : 0);
            if (oldMain?.Anchor != null)
                builder.ActivePartTracker.DeletePart(oldMain.Anchor);
            builder.UtilFileIO.LoadSerializedAssembly(assembly);
            return others + VabSnapshot.PartCount(assemblyJson);
        }

        // Main rocket with expectedMainParts parts and expectedTotalParts in the workspace; with
        // expectedMainParts <= 0 only the total counts. staleMain (method B): the old main object must be gone.
        // Never throws: the tracker is read in the middle of a transition, and a throw would leave the
        // workspace half-cleared with no restore. A failed read is "not settled".
        public static bool Settled(ObjectAssemblyBuilder builder, int expectedMainParts, int expectedTotalParts,
                                   IObjectAssembly staleMain = null)
        {
            if (builder == null)
                return false;
            try
            {
                if (expectedMainParts > 0)
                {
                    IObjectAssembly main = VabReader.MainAssembly(builder);
                    if (main?.Parts == null || main.Parts.Count != expectedMainParts)
                        return false;
                    if (staleMain != null && ReferenceEquals(main, staleMain))
                        return false;
                }
                bool settled = builder.ActivePartTracker.PartsMasterList.Count == expectedTotalParts;
                _reported = false;
                return settled;
            }
            catch (Exception e)
            {
                ReportOnce("Settled", e);
                return false;
            }
        }

        public static string Describe(ObjectAssemblyBuilder builder)
        {
            if (builder == null)
                return "the VAB is closed";
            try
            {
                IObjectAssembly main = VabReader.MainAssembly(builder);
                ObjectAssemblyPartTracker tracker = builder.ActivePartTracker;
                return $"main {main?.Parts?.Count ?? 0} part(s), workspace {tracker.PartsMasterList.Count} part(s) " +
                       $"in {tracker.PartAssemblyCount()} assembly(ies)";
            }
            catch (Exception e)
            {
                ReportOnce("Describe", e);
                return "unknown (the VAB state could not be read)";
            }
        }

        static bool _reported;

        static void ReportOnce(string where, Exception e)
        {
            if (_reported)
                return;
            _reported = true;
            K2D2_Plugin.logger.LogInfo($"[VabTools] import: {where} could not read the VAB state: {e.Message}");
        }
    }
}
