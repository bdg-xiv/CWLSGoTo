using clib.Services;
using Lumina.Excel.Sheets;
using Lumina.Extensions;

namespace clib.Extensions;

public static class TerritoryTypeExtensions {
    private const double Seconds = 1;
    private const double Minutes = 60 * Seconds;
    private const double WeatherPeriod = 23 * Minutes + 20 * Seconds;
    private static readonly DateTime UnixEpoch = new(1970, 1, 1);

    extension(TerritoryType row) {
        public bool AllowsFlight => row.AetherCurrentCompFlgSet.RowId != 0;
        public bool IsWorkshop => row.BGM.RowId is 328;
        public bool IsDuty => ContentFinderCondition.Any(r => row.RowId is not 0 && r.TerritoryType.RowId == row.RowId);
        public Aetheryte? PrimaryAetheryte {
            get {
                if (row.RowId == 886) // Firmament → Ishgard
                    return Aetheryte.GetRow(70); ;
                if (row.RowId == 478) // Dravanian Hinterlands → Idyllshire
                    return Aetheryte.GetRow(75);
                if (Aetheryte.Where(a => a.Territory.RowId == row.RowId && a.IsAetheryte).OrderBy(a => a.RowId).FirstOrNull() is { } primary)
                    return primary;
                if (Aetheryte.Where(a => a.Territory.RowId == row.RowId).OrderBy(a => a.RowId).FirstOrNull() is not { } shard)
                    return null;
                return Coords.FindPrimaryAetheryte(shard.RowId) is not 0 and var prime ? Aetheryte.GetRow(prime) : null;
            }
        }
        public bool IsPrimaryAetheryteUnlocked => get_PrimaryAetheryte(row)?.IsUnlocked ?? false;

        public Weather GetPreviousWeather() => GetWeatherAt(row, GetCurrentWeatherRootTime().AddSeconds(-WeatherPeriod));
        public Weather GetCurrentWeather() => GetWeatherAt(row, GetCurrentWeatherRootTime());
        public Weather GetNextWeather() => GetWeatherAt(row, GetCurrentWeatherRootTime().AddSeconds(WeatherPeriod));
    }

    // all from https://github.com/karashiiro/FFXIVWeather.Lumina/blob/master/FFXIVWeather.Lumina/FFXIVWeatherLuminaService.cs
    internal static DateTime GetCurrentWeatherRootTime(double initialOffset = 0 * Minutes) {
        var now = DateTime.UtcNow;
        var adjustedNow = now.AddMilliseconds(-now.Millisecond).AddSeconds(initialOffset);
        var rootTime = adjustedNow;
        var seconds = (long)(rootTime - UnixEpoch).TotalSeconds % WeatherPeriod;
        rootTime = rootTime.AddSeconds(-seconds);
        return rootTime;
    }

    internal static Weather GetWeatherAt(TerritoryType row, DateTime time) {
        var target = CalculateTarget(time);
        var weatherRateIndex = row.WeatherRate.Value;

        var rateAccumulator = 0;
        for (var i = 0; i < weatherRateIndex.Rate.Count; i++) {
            rateAccumulator += weatherRateIndex.Rate[i];
            if (target < rateAccumulator) {
                return weatherRateIndex.Weather[i].Value;
            }
        }
        Svc.Log.Warning($"Failed to calculate weather for #{row.RowId} {row.Name} at {time}, [t:{target}, tr:{rateAccumulator}]");
        return weatherRateIndex.Weather[0].Value;
    }

    internal static int CalculateTarget(DateTime time) {
        var unix = (int)(time - UnixEpoch).TotalSeconds;
        var bell = unix / 175;
        var increment = (uint)(bell + 8 - bell % 8) % 24;
        var totalDays = (uint)(unix / 4200);

        var calcBase = totalDays * 0x64 + increment;

        var step1 = (calcBase << 0xB) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;

        return (int)(step2 % 0x64);
    }
}
