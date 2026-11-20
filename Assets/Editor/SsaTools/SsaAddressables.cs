using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace SkySpaceAgency.EditorTools
{
    /// <summary>
    /// Adds the new window's UXML (Assets/UI/&lt;mod&gt;_UI/App/*.uxml) to the mod's Addressables group, with its
    /// project path as address, as the classic window's entry. Through the Addressables API, never by
    /// editing the group's YAML (the load path depends on the group's settings). Batch mode:
    ///   Unity.exe -batchmode -nographics -projectPath &lt;project&gt; -executeMethod SkySpaceAgency.EditorTools.SsaAddressables.AddAppEntries -quit
    /// </summary>
    public static class SsaAddressables
    {
        /// <summary>
        /// Removes the Addressables entries whose asset no longer exists (the deleted classic window's uxml).
        /// Batch mode: Unity.exe -batchmode -nographics -projectPath &lt;project&gt; -executeMethod SkySpaceAgency.EditorTools.SsaAddressables.RemoveMissingEntries -quit
        /// </summary>
        public static void RemoveMissingEntries()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) { Debug.LogError("[SsaAddressables] no Addressables settings"); return; }
            int removed = 0;
            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                foreach (var entry in new System.Collections.Generic.List<AddressableAssetEntry>(group.entries))
                {
                    if (!string.IsNullOrEmpty(entry.AssetPath) && File.Exists(entry.AssetPath)) continue;
                    Debug.Log($"[SsaAddressables] removing the entry of a missing asset: '{entry.address}' (group {group.Name}, guid {entry.guid})");
                    settings.RemoveAssetEntry(entry.guid, false);
                    removed++;
                }
            }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryRemoved, null, true, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SsaAddressables] {removed} entr(y/ies) removed");
        }

        public static void AddAppEntries()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) { Debug.LogError("[SsaAddressables] no Addressables settings"); return; }

            // The group that already holds the classic window (addressables_<mod>_all).
            AddressableAssetGroup group = null;
            foreach (var g in settings.groups)
                if (g != null && g.Name.StartsWith("addressables_") && g.Name.EndsWith("_all"))
                    group = g;
            if (group == null) { Debug.LogError("[SsaAddressables] no addressables_*_all group"); return; }

            int added = 0;
            foreach (string dir in Directory.GetDirectories("Assets/UI", "App", SearchOption.AllDirectories))
                foreach (string file in Directory.GetFiles(dir, "*.uxml"))
                {
                    string path = file.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) { Debug.LogError($"[SsaAddressables] {path} is not imported"); continue; }
                    var entry = settings.CreateOrMoveEntry(guid, group, false, false);
                    entry.address = path;
                    added++;
                    Debug.Log($"[SsaAddressables] {path} -> group {group.Name}, address {entry.address}");
                }
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true, true);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SsaAddressables] {added} entr(y/ies) in {group.Name}");
        }
    }
}
