using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Strodio.McpAddressables
{
    public static class AddressablesAnalyzer
    {
        static AddressableAssetSettings Settings =>
            AddressableAssetSettingsDefaultObject.Settings;

        // ─── READ methods ───

        public static List<Dictionary<string, object>> ListGroups()
        {
            var result = new List<Dictionary<string, object>>();
            if (Settings == null) return result;

            foreach (var group in Settings.groups)
            {
                if (group == null) continue;
                var schemas = new List<string>();
                foreach (var schema in group.Schemas)
                {
                    if (schema != null)
                        schemas.Add(schema.GetType().Name);
                }

                result.Add(new Dictionary<string, object>
                {
                    { "name", group.Name },
                    { "entry_count", group.entries.Count },
                    { "schemas", schemas },
                    { "read_only", group.ReadOnly },
                });
            }
            return result;
        }

        public static List<Dictionary<string, object>> GetGroupEntries(string groupName)
        {
            var result = new List<Dictionary<string, object>>();
            var group = FindGroup(groupName);
            if (group == null)
            {
                result.Add(ErrorDict($"Group '{groupName}' not found"));
                return result;
            }

            foreach (var entry in group.entries)
            {
                var labels = new List<string>();
                foreach (var label in entry.labels)
                    labels.Add(label);

                result.Add(new Dictionary<string, object>
                {
                    { "address", entry.address },
                    { "guid", entry.guid },
                    { "asset_path", entry.AssetPath },
                    { "type", AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath)?.Name ?? "Unknown" },
                    { "labels", labels },
                });
            }
            return result;
        }

        public static Dictionary<string, object> GetGroupSettings(string groupName)
        {
            var group = FindGroup(groupName);
            if (group == null) return ErrorDict($"Group '{groupName}' not found");

            var result = new Dictionary<string, object>
            {
                { "name", group.Name },
                { "read_only", group.ReadOnly },
            };

            var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
            if (bundleSchema != null)
            {
                result["bundle_mode"] = bundleSchema.BundleMode.ToString();
                result["bundle_naming"] = bundleSchema.BundleNaming.ToString();
                result["compression"] = bundleSchema.Compression.ToString();
                result["include_in_build"] = bundleSchema.IncludeInBuild;
                result["build_path"] = bundleSchema.BuildPath.GetValue(Settings);
                result["load_path"] = bundleSchema.LoadPath.GetValue(Settings);
                result["include_addresses_in_catalog"] = bundleSchema.IncludeAddressInCatalog;
                result["include_guids_in_catalog"] = bundleSchema.IncludeGUIDInCatalog;
                result["include_labels_in_catalog"] = bundleSchema.IncludeLabelsInCatalog;
            }

            var updateSchema = group.GetSchema<ContentUpdateGroupSchema>();
            if (updateSchema != null)
            {
                result["static_content"] = updateSchema.StaticContent;
            }

            return result;
        }

        public static List<Dictionary<string, object>> GetEntryDependencies(string groupName)
        {
            var result = new List<Dictionary<string, object>>();
            var group = FindGroup(groupName);
            if (group == null)
            {
                result.Add(ErrorDict($"Group '{groupName}' not found"));
                return result;
            }

            foreach (var entry in group.entries)
            {
                var deps = AssetDatabase.GetDependencies(entry.AssetPath, true);
                var depList = new List<string>();
                foreach (var dep in deps)
                {
                    if (dep != entry.AssetPath)
                        depList.Add(dep);
                }

                result.Add(new Dictionary<string, object>
                {
                    { "address", entry.address },
                    { "guid", entry.guid },
                    { "asset_path", entry.AssetPath },
                    { "dependency_count", depList.Count },
                    { "dependencies", depList },
                });
            }
            return result;
        }

        public static Dictionary<string, object> FindEntryByAddress(string address)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            foreach (var group in Settings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                {
                    if (entry.address == address)
                    {
                        var labels = new List<string>();
                        foreach (var label in entry.labels)
                            labels.Add(label);

                        return new Dictionary<string, object>
                        {
                            { "address", entry.address },
                            { "guid", entry.guid },
                            { "asset_path", entry.AssetPath },
                            { "group_name", group.Name },
                            { "labels", labels },
                        };
                    }
                }
            }

            return ErrorDict($"No entry with address '{address}' found");
        }

        public static Dictionary<string, object> GetAddressablesSettings()
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            var profiles = new List<Dictionary<string, object>>();
            var profileSettings = Settings.profileSettings;
            var profileNames = profileSettings.GetAllProfileNames();
            foreach (var profileName in profileNames)
            {
                var profileId = profileSettings.GetProfileId(profileName);
                var profile = new Dictionary<string, object>
                {
                    { "name", profileName },
                    { "id", profileId },
                    { "is_active", profileId == Settings.activeProfileId },
                };
                profiles.Add(profile);
            }

            return new Dictionary<string, object>
            {
                { "group_count", Settings.groups.Count },
                { "active_profile", Settings.profileSettings.GetProfileName(Settings.activeProfileId) ?? "Unknown" },
                { "profiles", new List<Dictionary<string, object>>(profiles) },
                { "build_remote_catalog", Settings.BuildRemoteCatalog },
                { "label_count", Settings.GetLabels().Count },
            };
        }

        public static List<Dictionary<string, object>> AnalyzeGroupDependencies()
        {
            if (Settings == null) return new List<Dictionary<string, object>>();

            // Build a set of all addressable asset paths for quick lookup
            var addressablePaths = new HashSet<string>();
            foreach (var group in Settings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                    addressablePaths.Add(entry.AssetPath);
            }

            // Map: dependency asset path -> set of group names that reference it
            var depToGroups = new Dictionary<string, HashSet<string>>();

            foreach (var group in Settings.groups)
            {
                if (group == null) continue;
                foreach (var entry in group.entries)
                {
                    var deps = AssetDatabase.GetDependencies(entry.AssetPath, true);
                    foreach (var dep in deps)
                    {
                        // Skip self, skip assets that are already addressable, skip built-in
                        if (dep == entry.AssetPath) continue;
                        if (addressablePaths.Contains(dep)) continue;
                        if (dep.StartsWith("Packages/")) continue;

                        if (!depToGroups.TryGetValue(dep, out var groups))
                        {
                            groups = new HashSet<string>();
                            depToGroups[dep] = groups;
                        }
                        groups.Add(group.Name);
                    }
                }
            }

            // Filter to assets referenced by 2+ groups (these cause duplicate bundling)
            var result = new List<Dictionary<string, object>>();
            foreach (var kv in depToGroups)
            {
                if (kv.Value.Count < 2) continue;
                var groupList = new List<string>(kv.Value);
                groupList.Sort();

                var assetType = AssetDatabase.GetMainAssetTypeAtPath(kv.Key);
                result.Add(new Dictionary<string, object>
                {
                    { "asset_path", kv.Key },
                    { "type", assetType?.Name ?? "Unknown" },
                    { "referenced_by_group_count", kv.Value.Count },
                    { "referenced_by_groups", groupList },
                });
            }

            // Sort by reference count descending
            result.Sort((a, b) => ((int)b["referenced_by_group_count"]).CompareTo((int)a["referenced_by_group_count"]));
            return result;
        }

        public static List<string> ListLabels()
        {
            if (Settings == null) return new List<string>();
            return Settings.GetLabels();
        }

        // ─── WRITE methods ───

        public static Dictionary<string, object> CreateGroup(string name, List<string> schemas)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");
            if (FindGroup(name) != null) return ErrorDict($"Group '{name}' already exists");

            var schemaTypes = new List<AddressableAssetGroupSchema>();
            var group = Settings.CreateGroup(name, false, false, true, schemaTypes);

            // Add default schemas if not specified
            bool addBundled = schemas == null || schemas.Count == 0 || schemas.Contains("BundledAssetGroupSchema");
            bool addUpdate = schemas == null || schemas.Count == 0 || schemas.Contains("ContentUpdateGroupSchema");

            if (addBundled)
                group.AddSchema<BundledAssetGroupSchema>();
            if (addUpdate)
                group.AddSchema<ContentUpdateGroupSchema>();

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.GroupAdded, group, true);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", group.Name },
                { "message", $"Group '{name}' created" },
            };
        }

        public static Dictionary<string, object> MoveEntries(List<string> guids, string targetGroupName)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");
            var targetGroup = FindGroup(targetGroupName);
            if (targetGroup == null) return ErrorDict($"Target group '{targetGroupName}' not found");

            int moved = 0;
            var errors = new List<string>();

            foreach (var guid in guids)
            {
                var entry = Settings.FindAssetEntry(guid);
                if (entry == null)
                {
                    errors.Add($"Entry with GUID '{guid}' not found");
                    continue;
                }

                Settings.MoveEntry(entry, targetGroup, false, false);
                moved++;
            }

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, null, true);
            AssetDatabase.SaveAssets();

            var result = new Dictionary<string, object>
            {
                { "success", errors.Count == 0 },
                { "moved_count", moved },
                { "target_group", targetGroupName },
            };
            if (errors.Count > 0)
                result["errors"] = errors;
            return result;
        }

        public static Dictionary<string, object> SetEntryAddress(string guid, string address)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            var entry = Settings.FindAssetEntry(guid);
            if (entry == null) return ErrorDict($"Entry with GUID '{guid}' not found");

            string oldAddress = entry.address;
            entry.address = address;

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "guid", guid },
                { "old_address", oldAddress },
                { "new_address", address },
            };
        }

        public static Dictionary<string, object> AddEntry(string assetPath, string groupName, string address)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            var group = FindGroup(groupName);
            if (group == null) return ErrorDict($"Group '{groupName}' not found");

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return ErrorDict($"Asset not found at '{assetPath}'");

            // Check if already addressable
            var existing = Settings.FindAssetEntry(guid);
            if (existing != null)
                return ErrorDict($"Asset is already addressable in group '{existing.parentGroup.Name}'");

            var entry = Settings.CreateOrMoveEntry(guid, group, false, false);
            if (!string.IsNullOrEmpty(address))
                entry.address = address;

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryAdded, entry, true);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "guid", guid },
                { "address", entry.address },
                { "group_name", groupName },
                { "asset_path", assetPath },
            };
        }

        public static Dictionary<string, object> RemoveEntry(string guid)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            var entry = Settings.FindAssetEntry(guid);
            if (entry == null) return ErrorDict($"Entry with GUID '{guid}' not found");

            string address = entry.address;
            string groupName = entry.parentGroup.Name;

            Settings.RemoveAssetEntry(guid);

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryRemoved, null, true);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "guid", guid },
                { "removed_address", address },
                { "removed_from_group", groupName },
            };
        }

        public static Dictionary<string, object> SetEntryLabels(string guid, List<string> labels, bool exclusive)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            var entry = Settings.FindAssetEntry(guid);
            if (entry == null) return ErrorDict($"Entry with GUID '{guid}' not found");

            // If exclusive, remove all existing labels first
            if (exclusive)
            {
                var existingLabels = new List<string>(entry.labels);
                foreach (var label in existingLabels)
                    entry.SetLabel(label, false, false);
            }

            // Add requested labels (ensure they exist in settings)
            foreach (var label in labels)
            {
                Settings.AddLabel(label);
                entry.SetLabel(label, true, false);
            }

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);
            AssetDatabase.SaveAssets();

            var currentLabels = new List<string>();
            foreach (var label in entry.labels)
                currentLabels.Add(label);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "guid", guid },
                { "labels", currentLabels },
            };
        }

        public static Dictionary<string, object> RenameGroup(string oldName, string newName)
        {
            if (Settings == null) return ErrorDict("Addressables settings not found");

            var group = FindGroup(oldName);
            if (group == null) return ErrorDict($"Group '{oldName}' not found");

            if (FindGroup(newName) != null)
                return ErrorDict($"Group '{newName}' already exists");

            group.Name = newName;

            Settings.SetDirty(AddressableAssetSettings.ModificationEvent.GroupRenamed, group, true);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "old_name", oldName },
                { "new_name", newName },
            };
        }

        // ─── Helpers ───

        static AddressableAssetGroup FindGroup(string name)
        {
            if (Settings == null) return null;
            return Settings.groups.FirstOrDefault(g => g != null && g.Name == name);
        }

        static Dictionary<string, object> ErrorDict(string message)
        {
            return new Dictionary<string, object> { { "error", message } };
        }
    }
}
