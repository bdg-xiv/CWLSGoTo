using ECommons.DalamudServices;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HookNamer;

/// <summary>Reads AutoHook's saved presets and re-emits them under a new name.
///
/// GatherBuddy Reborn looks a preset up with an exact string comparison against the
/// target fish's item id (see AutoGather.AutoHook.cs: presetName.Equals(fishId,
/// StringComparison.Ordinal)), so a usable preset has to be named literally "4948".
/// Rather than rewrite AutoHook.json underneath a running AutoHook - which keeps its
/// config in memory and would overwrite us - we hand a renamed copy back through
/// AutoHook's own import IPC and let it persist the result.</summary>
internal static class AutoHookPresets
{
    /// <summary>AutoHook's import strings are "AH4_" + base64(gzip(json)).</summary>
    private const string ExportPrefix = "AH4_";

    /// <summary>AutoHook preset folder the id-named copies are filed into.</summary>
    private const string FolderName = "HookNamer";

    internal sealed record PresetInfo(string Name, int[] FishIds, int[] BaitIds, JsonNode Node)
    {
        /// <summary>True when the name is already just an item id, i.e. GatherBuddy
        /// Reborn would find it as-is.</summary>
        public bool IsIdNamed => int.TryParse(Name, out _);
    }

    public static string ConfigPath
    {
        get
        {
            var dir = Plugin.PluginInterface.ConfigDirectory.Parent?.FullName;
            return dir == null ? "" : Path.Combine(dir, "AutoHook.json");
        }
    }

    /// <summary>Loads every custom preset with the fish and bait ids found inside it.
    /// Returns an empty list (and a reason) when AutoHook's config isn't readable.</summary>
    public static List<PresetInfo> Load(out string error)
    {
        error = "";
        var result = new List<PresetInfo>();

        var path = ConfigPath;
        if (path.Length == 0 || !File.Exists(path))
        {
            error = "AutoHook.json not found - is AutoHook installed?";
            return result;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["HookPresets"]?["CustomPresets"] is not JsonArray presets)
            {
                error = "No custom presets found in AutoHook's config.";
                return result;
            }

            foreach (var preset in presets)
            {
                if (preset == null)
                    continue;

                var name = preset["PresetName"]?.GetValue<string>() ?? "";
                result.Add(new PresetInfo(name, CollectIds(preset, "ListOfFish", "Fish"),
                    CollectIds(preset, "ListOfBaits", "BaitFish"), preset));
            }
        }
        catch (Exception ex)
        {
            error = $"Could not read AutoHook.json: {ex.Message}";
        }

        return result;
    }

    /// <summary>Pulls the ids out of one of the preset's rule lists; entries share the
    /// shape { "&lt;holder&gt;": { "Id": n } }. Placeholder ids (-1 / -99) are dropped.</summary>
    private static int[] CollectIds(JsonNode preset, string listName, string holder)
    {
        if (preset[listName] is not JsonArray list)
            return [];

        var ids = new List<int>();
        foreach (var entry in list)
        {
            if (entry?[holder]?["Id"] is not JsonValue value || !value.TryGetValue<int>(out var id))
                continue;
            if (id > 0 && !ids.Contains(id))
                ids.Add(id);
        }

        return [.. ids];
    }

    /// <summary>Sends a copy of <paramref name="preset"/> to AutoHook named after
    /// <paramref name="fishId"/>. The copy gets a fresh UniqueId so AutoHook treats it as
    /// a new preset rather than an edit of the original.</summary>
    public static bool CreateIdNamedCopy(PresetInfo preset, uint fishId, out string error)
    {
        error = "";
        try
        {
            var clone = JsonNode.Parse(preset.Node.ToJsonString());
            if (clone == null)
            {
                error = "Could not copy the preset.";
                return false;
            }

            clone["PresetName"] = fishId.ToString();
            clone["UniqueId"] = Guid.NewGuid().ToString();

            var payload = ExportPrefix + Compress(clone.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            Svc.PluginInterface.GetIpcSubscriber<string, object>("AutoHook.ImportAndSelectPreset").InvokeAction(payload);
            TryMoveToFolder(fishId.ToString());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Svc.Log.Error(ex, "Failed to hand a renamed preset to AutoHook");
            return false;
        }
    }

    /// <summary>Files the freshly imported preset into a "HookNamer" folder inside
    /// AutoHook's preset list, creating the folder on first use. AutoHook's IPC can't
    /// do this (ImportAndSelectFolder always creates a brand-new folder), so this
    /// reaches into the loaded AutoHook instance via reflection - same pattern the
    /// repo already uses for Penumbra and Hunt Train Assistant. Best effort: if
    /// AutoHook's internals changed, the preset just stays at the top level.</summary>
    private static void TryMoveToFolder(string presetName)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .LastOrDefault(a => a.GetName().Name == "AutoHook");
            var serviceType = assembly?.GetType("AutoHook.Service");
            var config = serviceType?.GetProperty("Configuration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (config == null)
            {
                Svc.Log.Warning("HookNamer: AutoHook internals not found, the preset stays at the top level");
                return;
            }

            // AutoHook guards config writes with this lock; take it like its own IPC does.
            var sync = config.GetType().GetField("SerializationSync", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                       ?? new object();
            lock (sync)
            {
                var hookPresets = GetMember(config, "HookPresets");
                var folders = GetMember(hookPresets, "Folders") as IList;
                if (hookPresets == null || folders == null || GetMember(hookPresets, "CustomPresets") is not IEnumerable presetsList)
                    return;

                // The import appended our copy; the last preset with the id-name is it.
                object? imported = null;
                foreach (var preset in presetsList)
                {
                    if (Equals(GetMember(preset, "PresetName"), presetName))
                        imported = preset;
                }

                if (imported == null || GetMember(imported, "UniqueId") is not Guid presetId)
                    return;

                object? folder = null;
                foreach (var f in folders)
                {
                    if (Equals(GetMember(f, "FolderName"), FolderName))
                    {
                        folder = f;
                        break;
                    }
                }

                if (folder == null)
                {
                    var folderType = assembly!.GetType("AutoHook.Classes.PresetFolder");
                    if (folderType == null)
                        return;
                    folder = Activator.CreateInstance(folderType, FolderName);
                    folders.Add(folder);
                }

                folder!.GetType().GetMethod("AddPreset")?.Invoke(folder, [presetId]);
            }

            serviceType!.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"HookNamer: could not file the preset into the \"{FolderName}\" folder: {ex.Message}");
        }
    }

    private static object? GetMember(object? obj, string name)
    {
        if (obj == null)
            return null;
        var type = obj.GetType();
        return type.GetField(name)?.GetValue(obj) ?? type.GetProperty(name)?.GetValue(obj);
    }

    public static bool AutoHookAvailable()
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<bool>("AutoHook.GetPluginState").InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Compress(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var gs = new GZipStream(ms, CompressionMode.Compress))
            gs.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray());
    }
}
