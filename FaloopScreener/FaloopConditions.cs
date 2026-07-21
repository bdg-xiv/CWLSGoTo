using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FaloopScreener;

/// <summary>Spawn-condition windows for the hunts whose trigger depends on Eorzean
/// weather, time of day or moon phase - the "For 11m" / "In 20m" badges faloop.app
/// shows next to a mark's name.
///
/// Rules and their evaluation are ported from faloop.app's own bundle
/// (main.48dd7c05d52f96d1.js): every rule is resolved on the Eorzean clock and the
/// resulting window is converted back to real time. Weather uses the game's
/// deterministic forecast (the same calculation as FFXIVWeather), driven by each
/// zone's weather-rate table which Faloop embeds alongside the rule.</summary>
internal static class FaloopConditions
{
    /// <summary>Eorzea runs 3600/175 times faster than real time.</summary>
    private const double EorzeaRate = 3600d / 175d;

    /// <summary>32 Eorzean days, in Eorzean milliseconds.</summary>
    private const double MoonCycleMs = 2_764_800_000d;

    /// <summary>Weather is drawn in blocks of 8 Eorzean hours.</summary>
    private const int WeatherBlockSeconds = 28_800;

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Moon phases in cycle order; the rules only name "new" (1) and "full" (5).</summary>
    private static readonly Dictionary<string, int> MoonPhaseNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["new"] = 1,
        ["full"] = 5,
    };

    internal sealed record Period(double From, double To);

    internal sealed record Rule(
        string Type,
        string? Phase, Period[]? Periods,
        int[]? Hours, double Duration,
        string[]? Conditions, WeatherChance[]? Probabilities, double Offset);

    internal sealed record WeatherChance(int Chance, string Condition);

    /// <summary>One badge slot on a mark. Every rule in <see cref="Rules"/> must hold at
    /// the same time; a mark with two independent triggers gets two slots.</summary>
    internal sealed record ConditionSlot(string Icon, Rule[] Rules);

    /// <summary>The resolved window, in real UTC time.</summary>
    internal sealed record ConditionWindow(DateTime Start, DateTime End, string Icon)
    {
        public bool IsActive(DateTime utcNow) => utcNow >= Start && utcNow < End;
    }

    public static IReadOnlyDictionary<string, ConditionSlot[]> Slots { get; } = LoadSlots();

    public static bool Has(string mobId) => Slots.ContainsKey(mobId);

    /// <summary>Resolves a mark's condition to the window that matters right now: the one
    /// currently running, otherwise the one starting soonest. Null when the mark has no
    /// automatic condition (or the search failed to converge).</summary>
    public static ConditionWindow? GetWindow(string mobId, DateTime utcNow)
    {
        if (!Slots.TryGetValue(mobId, out var slots))
            return null;

        ConditionWindow? best = null;
        foreach (var slot in slots)
        {
            var window = Resolve(slot, utcNow);
            if (window == null)
                continue;

            // An active window always wins; otherwise take whichever starts first.
            if (best == null
                || (window.IsActive(utcNow) && !best.IsActive(utcNow))
                || (window.IsActive(utcNow) == best.IsActive(utcNow) && window.Start < best.Start))
            {
                best = window;
            }
        }

        return best;
    }

    private static ConditionWindow? Resolve(ConditionSlot slot, DateTime utcNow)
    {
        var eorzeaNow = ToEorzea(utcNow);

        // Several rules on one slot must all be true together: evaluate each, and while
        // they don't overlap, re-evaluate from the earliest end until they line up.
        var cursor = eorzeaNow;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var windows = new List<(DateTime Start, DateTime End)>(slot.Rules.Length);
            foreach (var rule in slot.Rules)
            {
                var w = Evaluate(rule, cursor, eorzeaNow);
                if (w == null)
                    return null;
                windows.Add(w.Value);
            }

            if (windows.Count == 1)
                return Materialise(windows[0], slot.Icon);

            var latestStart = windows.Max(w => w.Start);
            var earliestEnd = windows.Min(w => w.End);
            if (latestStart < earliestEnd)
                return Materialise((latestStart, earliestEnd), slot.Icon);

            cursor = earliestEnd;
        }

        return null;
    }

    private static ConditionWindow Materialise((DateTime Start, DateTime End) window, string icon)
        => new(ToReal(window.Start), ToReal(window.End), icon);

    private static (DateTime Start, DateTime End)? Evaluate(Rule rule, DateTime cursor, DateTime eorzeaNow)
        => rule.Type switch
        {
            "moon" => EvaluateMoon(rule, cursor),
            "time" => EvaluateTime(rule, cursor),
            "weather" => EvaluateWeather(rule, cursor),
            _ => null,
        };

    /// <summary>Walks the 32-day lunar cycle to the requested phase's 4-day block, then
    /// picks the listed sub-period (the nights the trigger is actually usable) that has
    /// not finished yet.</summary>
    private static (DateTime Start, DateTime End)? EvaluateMoon(Rule rule, DateTime cursor)
    {
        if (rule.Phase == null || !MoonPhaseNumbers.TryGetValue(rule.Phase, out var phaseNumber))
            return null;

        var probe = cursor;
        for (var cycle = 0; cycle < 40; cycle++)
        {
            var sinceEpoch = (probe - UnixEpoch).TotalMilliseconds;
            var blockStart = probe
                .AddMilliseconds(-(sinceEpoch % MoonCycleMs))
                .AddHours(-12)
                .AddDays(4 * (phaseNumber - 1));

            DateTime start, end;
            if (rule.Periods is { Length: > 0 })
            {
                start = blockStart;
                end = blockStart;
                var found = false;
                foreach (var period in rule.Periods)
                {
                    start = blockStart.AddSeconds(period.From);
                    end = blockStart.AddSeconds(period.To);
                    if (end > cursor)
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                    return (start, end);
            }
            else
            {
                start = blockStart;
                end = blockStart.AddDays(4);
                if (end > cursor)
                    return (start, end);
            }

            probe = probe.AddDays(32);
        }

        return null;
    }

    /// <summary>Finds the next occurrence of one of the listed Eorzean hours whose window
    /// (hour + duration) has not already passed.</summary>
    private static (DateTime Start, DateTime End)? EvaluateTime(Rule rule, DateTime cursor)
    {
        if (rule.Hours is not { Length: > 0 })
            return null;

        // Start a day early so a window that opened yesterday and still covers now is seen.
        var day = cursor.Date.AddDays(-1);
        for (var i = 0; i < 400; i++)
        {
            foreach (var hour in rule.Hours)
            {
                var start = day.AddHours(hour);
                var end = start.AddSeconds(rule.Duration);
                if (end > cursor)
                    return (start, end);
            }

            day = day.AddDays(1);
        }

        return null;
    }

    /// <summary>Finds the next stretch of acceptable weather long enough to satisfy the
    /// rule's dwell time (e.g. Laideronnette needs 30 real minutes of rain).</summary>
    private static (DateTime Start, DateTime End)? EvaluateWeather(Rule rule, DateTime cursor)
    {
        if (rule.Conditions is not { Length: > 0 } || rule.Probabilities is not { Length: > 0 })
            return null;

        // The dwell time is quoted in real seconds; the search runs on the Eorzean clock.
        var offset = rule.Offset * EorzeaRate;
        var probe = FindRunStart(cursor, rule);

        for (var attempt = 0; attempt < 500; attempt++)
        {
            var result = TryRunFrom(ref probe, rule, offset);
            if (result != null)
                return result;
        }

        return null;
    }

    /// <summary>Backs up to the block before the current run of matching weather, so an
    /// already-running stretch is measured from its real beginning.</summary>
    private static DateTime FindRunStart(DateTime cursor, Rule rule)
    {
        var blockMs = WeatherBlockSeconds * 1000d;
        var sinceEpoch = (cursor - UnixEpoch).TotalMilliseconds;
        var block = cursor.AddMilliseconds(-(sinceEpoch % blockMs));

        if (!Matches(block, rule))
            return block;

        block = block.AddSeconds(-WeatherBlockSeconds);
        for (var i = 0; i < 5000; i++)
        {
            if (!Matches(block, rule))
                return block;
            block = block.AddSeconds(-WeatherBlockSeconds);
        }

        return block;
    }

    /// <summary>Advances <paramref name="probe"/> to the next matching block and checks the
    /// run is long enough. Returns null (with probe advanced) when the run breaks early,
    /// so the caller can try the following run.</summary>
    private static (DateTime Start, DateTime End)? TryRunFrom(ref DateTime probe, Rule rule, double offset)
    {
        for (var i = 0; i < 1000 && !Matches(probe, rule); i++)
            probe = probe.AddSeconds(WeatherBlockSeconds);

        var remaining = offset - WeatherBlockSeconds;
        for (var i = 0; i < 1000 && remaining > 0; i++)
        {
            probe = probe.AddSeconds(WeatherBlockSeconds);
            if (!Matches(probe, rule))
                return null;   // run too short - resume searching from here
            remaining -= WeatherBlockSeconds;
        }

        var end = probe.AddSeconds(WeatherBlockSeconds);
        var start = probe.AddSeconds(remaining + WeatherBlockSeconds);

        for (var i = 0; i < 5000 && Matches(end, rule); i++)
            end = end.AddSeconds(WeatherBlockSeconds);

        probe = end;
        return (start, end);
    }

    private static bool Matches(DateTime eorzeaTime, Rule rule)
    {
        var weather = WeatherAt(eorzeaTime, rule.Probabilities!);
        return weather != null && rule.Conditions!.Contains(weather);
    }

    private static string? WeatherAt(DateTime eorzeaTime, WeatherChance[] probabilities)
    {
        var target = CalculateTarget(ToReal(eorzeaTime));
        var accumulator = 0;
        foreach (var chance in probabilities)
        {
            accumulator += chance.Chance;
            if (target < accumulator)
                return chance.Condition;
        }

        return null;
    }

    /// <summary>The game's deterministic weather roll for a point in real time (0-99).</summary>
    private static int CalculateTarget(DateTime realUtc)
    {
        var unix = (int)(realUtc - UnixEpoch).TotalSeconds;
        var bell = unix / 175;
        var increment = (uint)(bell + 8 - bell % 8) % 24;
        var totalDays = (uint)(unix / 4200);

        var calcBase = totalDays * 0x64 + increment;
        var step1 = (calcBase << 0xB) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 0x64);
    }

    private static DateTime ToEorzea(DateTime realUtc)
        => UnixEpoch.AddMilliseconds((realUtc - UnixEpoch).TotalMilliseconds * EorzeaRate);

    private static DateTime ToReal(DateTime eorzeaTime)
        => UnixEpoch.AddMilliseconds(Math.Round((eorzeaTime - UnixEpoch).TotalMilliseconds / EorzeaRate / 1000d) * 1000d);

    private static Dictionary<string, ConditionSlot[]> LoadSlots()
    {
        var result = new Dictionary<string, ConditionSlot[]>();
        using var doc = JsonDocument.Parse(ConditionsJson);
        foreach (var mob in doc.RootElement.EnumerateObject())
        {
            var slots = new List<ConditionSlot>();
            foreach (var slot in mob.Value.EnumerateArray())
            {
                var icon = slot.GetProperty("icon").GetString() ?? "";
                var rules = slot.GetProperty("rules").EnumerateArray().Select(ReadRule).ToArray();
                slots.Add(new ConditionSlot(icon, rules));
            }

            result[mob.Name] = slots.ToArray();
        }

        return result;
    }

    private static Rule ReadRule(JsonElement e)
    {
        var type = e.GetProperty("type").GetString()!;
        var phase = e.TryGetProperty("phase", out var p) ? p.GetString() : null;

        Period[]? periods = null;
        if (e.TryGetProperty("periods", out var periodsEl))
        {
            periods = periodsEl.EnumerateArray()
                .Select(x => new Period(x.GetProperty("from").GetDouble(), x.GetProperty("to").GetDouble()))
                .ToArray();
        }

        int[]? hours = null;
        if (e.TryGetProperty("hours", out var hoursEl))
            hours = hoursEl.EnumerateArray().Select(x => x.GetInt32()).ToArray();

        string[]? conditions = null;
        if (e.TryGetProperty("conditions", out var condEl))
            conditions = condEl.EnumerateArray().Select(x => x.GetString()!).ToArray();

        WeatherChance[]? probabilities = null;
        if (e.TryGetProperty("probabilities", out var probEl))
        {
            probabilities = probEl.EnumerateArray()
                .Select(x => new WeatherChance(x.GetProperty("chance").GetInt32(), x.GetProperty("condition").GetString()!))
                .ToArray();
        }

        var duration = e.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
        var offset = e.TryGetProperty("offset", out var o) ? o.GetDouble() : 0;

        return new Rule(type, phase, periods, hours, duration, conditions, probabilities, offset);
    }

    /// <summary>Rule set for the 13 S ranks with an automatic condition, transcribed from
    /// faloop.app's bundle. Each mark maps to its badge slots; a slot's rules are ANDed.</summary>
    private const string ConditionsJson =
        """
        {
          "laideronnette": [{"icon":"rain","rules":[{"type":"weather","conditions":["rain"],"probabilities":[{"chance":5,"condition":"thunder"},{"chance":15,"condition":"rain"},{"chance":10,"condition":"fog"},{"chance":10,"condition":"clouds"},{"chance":15,"condition":"fair_skies"},{"chance":30,"condition":"clear_skies"},{"chance":15,"condition":"fair_skies"}],"offset":1800}]}],
          "mindflayer": [{"icon":"moon","rules":[{"type":"moon","phase":"new","periods":[{"from":43200,"to":54000},{"from":104400,"to":140400},{"from":190800,"to":226800},{"from":277200,"to":313200}]}]}],
          "thousand_cast_theda": [{"icon":"fsh","rules":[{"type":"time","hours":[17],"duration":14400}]}],
          "zona_seeker": [{"icon":"fsh","rules":[{"type":"weather","conditions":["clear_skies","fair_skies"],"probabilities":[{"chance":40,"condition":"clear_skies"},{"chance":20,"condition":"fair_skies"},{"chance":25,"condition":"clouds"},{"chance":10,"condition":"fog"},{"chance":5,"condition":"rain"}]}]}],
          "croque_mitaine": [{"icon":"min","rules":[{"type":"time","hours":[19],"duration":10800}]}],
          "croakadile": [{"icon":"moon","rules":[{"type":"moon","phase":"full","periods":[{"from":18000,"to":54000},{"from":104400,"to":140400},{"from":190800,"to":226800},{"from":277200,"to":313200}]}]}],
          "the_garlok": [{"icon":"sun","rules":[{"type":"weather","conditions":["fog","clear_skies","fair_skies","clouds"],"probabilities":[{"chance":5,"condition":"fog"},{"chance":45,"condition":"clear_skies"},{"chance":30,"condition":"fair_skies"},{"chance":10,"condition":"clouds"},{"chance":5,"condition":"rain"},{"chance":5,"condition":"showers"}],"offset":12000}]}],
          "bonnacon": [{"icon":"btn","rules":[{"type":"time","hours":[8],"duration":10800}]}],
          "gandarewa": [{"icon":"min","rules":[{"type":"time","hours":[2,14],"duration":7200}]},{"icon":"btn","rules":[{"type":"time","hours":[4,16],"duration":7200}]}],
          "okina": [{"icon":"moon","rules":[{"type":"moon","phase":"full"}]}],
          "gamma": [{"icon":"night","rules":[{"type":"time","hours":[17],"duration":54000}]}],
          "burfurlur_the_canny": [{"icon":"day","rules":[{"type":"time","hours":[9],"duration":28800},{"type":"weather","conditions":["clear_skies","fair_skies"],"probabilities":[{"chance":15,"condition":"clear_skies"},{"chance":45,"condition":"fair_skies"},{"chance":25,"condition":"clouds"},{"chance":15,"condition":"rain"}]}]}],
          "kirlirger_the_abhorrent": [{"icon":"moon","rules":[{"type":"moon","phase":"new","periods":[{"from":43200,"to":64800},{"from":129600,"to":151200},{"from":216000,"to":237600},{"from":302400,"to":324000}]},{"type":"weather","conditions":["fog"],"probabilities":[{"chance":20,"condition":"clear_skies"},{"chance":30,"condition":"fair_skies"},{"chance":20,"condition":"clouds"},{"chance":10,"condition":"fog"},{"chance":10,"condition":"wind"},{"chance":10,"condition":"snow"}]}]}]
        }
        """;
}
