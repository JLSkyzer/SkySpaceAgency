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
        public const float SettleTimeout = 3f;   // s of real time for the VAB to show the loaded rocket

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
        // keeps the two copies from sharing GUIDs when a rocket is re-imported.
        public static void LoadAsMain(ObjectAssemblyBuilder builder, JObject assemblyJson)
        {
            SerializedAssembly assembly = ToAssembly(assemblyJson);
            IObjectAssembly oldMain = VabReader.MainAssembly(builder);
            if (oldMain?.Anchor != null)
                builder.ActivePartTracker.DeletePart(oldMain.Anchor);
            builder.UtilFileIO.LoadSerializedAssembly(assembly);
        }

        // Main rocket with expectedMainParts parts, or (expectedMainParts <= 0) expectedTotalParts in the workspace.
        public static bool Settled(ObjectAssemblyBuilder builder, int expectedMainParts, int expectedTotalParts)
        {
            if (builder == null)
                return false;
            if (expectedMainParts > 0)
            {
                IObjectAssembly main = VabReader.MainAssembly(builder);
                return main?.Parts != null && main.Parts.Count == expectedMainParts;
            }
            return builder.ActivePartTracker.PartsMasterList.Count == expectedTotalParts;
        }

        public static string Describe(ObjectAssemblyBuilder builder)
        {
            if (builder == null)
                return "the VAB is closed";
            IObjectAssembly main = VabReader.MainAssembly(builder);
            ObjectAssemblyPartTracker tracker = builder.ActivePartTracker;
            return $"main {main?.Parts?.Count ?? 0} part(s), workspace {tracker.PartsMasterList.Count} part(s) " +
                   $"in {tracker.PartAssemblyCount()} assembly(ies)";
        }
    }
}
