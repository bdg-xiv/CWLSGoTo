using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Svc.Log.Error(ex, "Failed to hand a renamed preset to AutoHook");
            return false;
        }
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
