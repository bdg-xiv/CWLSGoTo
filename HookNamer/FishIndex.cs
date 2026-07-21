using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HookNamer;

/// <summary>Item lookup for choosing a preset's target fish.
///
/// The fish a preset is *for* is not recorded anywhere inside it - AutoHook only stores
/// hooking rules for fish you might reel in along the way (the "[Hunt] Zona Seeker
/// (Glimmerscale)" preset contains Copperfish and Maiden Carp, not Glimmerscale). The
/// only machine-readable hint is the preset's own name, so we guess from that and let
/// the user search for anything else.</summary>
internal static class FishIndex
{
    private static List<(string Name, uint Id)>? items;

    private static List<(string Name, uint Id)> Items => items ??= Build();

    private static List<(string Name, uint Id)> Build()
    {
        var list = new List<(string, uint)>();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet == null)
            return list;

        foreach (var item in sheet)
        {
            var name = item.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                list.Add((name, item.RowId));
        }

        return list;
    }

    public static string NameOf(uint id)
    {
        if (id == 0)
            return "";
        var item = Svc.Data.GetExcelSheet<Item>()?.GetRowOrDefault(id);
        var name = item?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"#{id}" : name;
    }

    /// <summary>Best matches for a search box, exact hits first.</summary>
    public static List<(string Name, uint Id)> Search(string query, int limit = 20)
    {
        if (query.Length < 2)
            return [];

        return Items
            .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => string.Equals(i.Name, query, StringComparison.OrdinalIgnoreCase) ? 0
                        : i.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i.Name.Length)
            .Take(limit)
            .ToList();
    }

    /// <summary>Guesses the target from a preset name. Community presets tend to put the
    /// fish in trailing parentheses ("[Hunt] Zona Seeker (Glimmerscale)"); failing that we
    /// try the name with any leading "[...]" tag and trailing " - 2" suffix removed.</summary>
    public static uint Guess(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return 0;

        foreach (Match match in Regex.Matches(presetName, @"\(([^)]+)\)"))
        {
            var id = ExactMatch(match.Groups[1].Value.Trim());
            if (id != 0)
                return id;
        }

        var stripped = Regex.Replace(presetName, @"^\s*\[[^\]]*\]\s*", "");
        stripped = Regex.Replace(stripped, @"\s*-\s*\d+\s*$", "").Trim();
        return ExactMatch(stripped);
    }

    private static uint ExactMatch(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;
        foreach (var item in Items)
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                return item.Id;
        }

        return 0;
    }
}
