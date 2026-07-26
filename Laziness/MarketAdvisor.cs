using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Laziness;

/// <summary>
/// Ranks vendor items by what they realistically fetch on your world's market board,
/// from Universalis sales history. "Realistically" leans on the current lowest listing
/// rather than the average sale price: you won't be the only one selling these, so
/// you end up pricing into the existing competition rather than above it.
/// </summary>
internal static class MarketAdvisor
{
    private const double AfterTax = 0.95; // retainer sales are taxed 5%
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal record Candidate(uint ItemId, string Name, int Price, double UnitsPerDay, double GilPerUnitCost);

    internal static async Task<List<Candidate>> Rank(string world, IReadOnlyDictionary<uint, string> items, int unitCost)
    {
        // Per world, not per data centre: boards aren't shared, so only the listings
        // and sales on the world being sold to say anything about the price.
        var url = $"https://universalis.app/api/v2/{world}/{string.Join(',', items.Keys)}?listings=20&entries=200";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Laziness-Dalamud-Plugin");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("items", out var itemsNode))
            return [];

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ranked = new List<Candidate>();
        foreach (var (id, name) in items)
        {
            if (!itemsNode.TryGetProperty(id.ToString(), out var node))
                continue;

            var minListing = node.TryGetProperty("minPrice", out var min) ? min.GetInt32() : 0;
            if (minListing <= 0)
                continue;

            var prices = new List<int>();
            var unitsSold = 0L;
            var oldest = now;
            if (node.TryGetProperty("recentHistory", out var history))
            {
                foreach (var sale in history.EnumerateArray())
                {
                    prices.Add(sale.GetProperty("pricePerUnit").GetInt32());
                    unitsSold += sale.GetProperty("quantity").GetInt32();
                    oldest = Math.Min(oldest, sale.GetProperty("timestamp").GetInt64());
                }
            }

            if (prices.Count == 0)
                continue;

            prices.Sort();
            var medianSale = prices[prices.Count / 2];

            // The history feed is capped, so the window it covers is the real divisor.
            var days = Math.Max((now - oldest) / 86400.0, 0.25);
            var unitsPerDay = unitsSold / days;

            // Whichever is lower: the price the market is clearing at, or what you'd
            // have to match to be the cheapest listing.
            var price = Math.Min(minListing, medianSale);
            ranked.Add(new Candidate(id, name, price, unitsPerDay, price * AfterTax / Math.Max(unitCost, 1)));
        }

        return ranked.OrderByDescending(c => c.GilPerUnitCost).ToList();
    }
}
