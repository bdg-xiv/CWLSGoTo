using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace GatherTally;

/// <summary>How fast each candidate item actually sells on the player's home world, so a
/// grind can be pointed at something worth keeping rather than the first thing the node
/// tables happen to offer.
///
/// Items are collected from the planner's candidate pools and fetched in batches; when
/// results land the planner re-picks. Market boards are per world, not per data centre,
/// so this only ever asks about the world the player would actually be selling on.</summary>
public static class MarketVelocity
{
    private const int BatchSize = 100;
    private const string UserAgent = "GatherTally-Dalamud-Plugin";

    // Sales velocity moves slowly; there is no reason to ask again within the hour.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly Dictionary<uint, double> salesPerDay = [];
    private static readonly HashSet<uint> requested = [];
    private static readonly HashSet<uint> wanted = [];

    private static DateTime fetchedAt;
    private static bool fetching;

    /// <summary>Set when a fetch has added data, so the planner knows to re-pick.</summary>
    public static bool Dirty { get; private set; }

    public static bool Fetching => fetching;
    public static int KnownItems => salesPerDay.Count;

    public static void ClearDirty() => Dirty = false;

    /// <summary>Sales per day, or null when the item has never been asked about or has no
    /// recorded sales.</summary>
    public static double? For(uint itemId)
        => salesPerDay.TryGetValue(itemId, out var value) ? value : null;

    /// <summary>Notes items whose velocity would help, to be fetched on the next pass.</summary>
    public static void Want(IEnumerable<uint> itemIds)
    {
        foreach (var id in itemIds)
        {
            if (!requested.Contains(id))
                wanted.Add(id);
        }
    }

    public static void Reset()
    {
        salesPerDay.Clear();
        requested.Clear();
        wanted.Clear();
        fetchedAt = default;
        Dirty = true;
    }

    /// <summary>Fetches anything outstanding. Safe to call every frame.</summary>
    public static void Pump()
    {
        if (fetching || wanted.Count == 0)
            return;

        if (fetchedAt != default && DateTime.UtcNow - fetchedAt > CacheLifetime)
        {
            // Everything on hand has gone stale; start over rather than mixing ages.
            Reset();
            return;
        }

        var world = HomeWorld();
        if (world == null)
            return;

        var batch = wanted.Take(BatchSize).ToList();
        foreach (var id in batch)
        {
            wanted.Remove(id);
            requested.Add(id);
        }

        fetching = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(world, batch).ConfigureAwait(false);
                fetchedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Universalis lookup failed: {ex.Message}");
            }
            finally
            {
                fetching = false;
            }
        });
    }

    private static async Task FetchAsync(string world, List<uint> batch)
    {
        var url = $"https://universalis.app/api/v2/{world}/{string.Join(',', batch)}?listings=0&entries=200";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", UserAgent);

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        // A single id comes back as a bare item rather than the keyed "items" map.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (doc.RootElement.TryGetProperty("items", out var itemsNode))
        {
            foreach (var id in batch)
            {
                if (itemsNode.TryGetProperty(id.ToString(), out var node))
                    salesPerDay[id] = ReadVelocity(node, now);
                else
                    salesPerDay[id] = 0;
            }
        }
        else if (batch.Count == 1)
        {
            salesPerDay[batch[0]] = ReadVelocity(doc.RootElement, now);
        }

        Dirty = true;
    }

    /// <summary>Units sold per day, measured over the window the history feed actually
    /// covers - it is capped, so a busy item's feed spans hours and a quiet one's spans
    /// weeks. Dividing by a fixed number of days would flatter the quiet ones.</summary>
    private static double ReadVelocity(JsonElement node, long now)
    {
        if (!node.TryGetProperty("recentHistory", out var history))
            return 0;

        var unitsSold = 0L;
        var oldest = now;
        foreach (var sale in history.EnumerateArray())
        {
            if (sale.TryGetProperty("quantity", out var quantity))
                unitsSold += quantity.GetInt32();
            if (sale.TryGetProperty("timestamp", out var timestamp))
                oldest = Math.Min(oldest, timestamp.GetInt64());
        }

        if (unitsSold == 0)
            return 0;

        var days = Math.Max((now - oldest) / 86400.0, 0.25);
        return unitsSold / days;
    }

    private static string? HomeWorld()
    {
        var world = Svc.PlayerState.HomeWorld;
        if (world.RowId == 0)
            return null;

        var name = world.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
