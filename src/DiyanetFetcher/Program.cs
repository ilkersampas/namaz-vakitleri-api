using System.Diagnostics;
using DiyanetFetcher.Models;
using DiyanetFetcher.Services;

// --- AYARLAR ---
var email = Environment.GetEnvironmentVariable("DIYANET_EMAIL") ?? "";
var password = Environment.GetEnvironmentVariable("DIYANET_PASSWORD") ?? "";
var outputDir = Environment.GetEnvironmentVariable("OUTPUT_DIR")
    ?? Path.Combine("..", "..", "docs", "data");
var countryIdsArg = Environment.GetEnvironmentVariable("COUNTRY_IDS") ?? "2"; // default: Turkiye
var includeWeekly = bool.Parse(Environment.GetEnvironmentVariable("INCLUDE_WEEKLY") ?? "true");
var includeRamadan = bool.Parse(Environment.GetEnvironmentVariable("INCLUDE_RAMADAN") ?? "true");
var includeEid = bool.Parse(Environment.GetEnvironmentVariable("INCLUDE_EID") ?? "true");
var includeCityDetail = bool.Parse(Environment.GetEnvironmentVariable("INCLUDE_CITY_DETAIL") ?? "true");
var perCityDelayMs = int.Parse(Environment.GetEnvironmentVariable("PER_CITY_DELAY_MS") ?? "300");

if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("HATA: DIYANET_EMAIL ve DIYANET_PASSWORD ortam degiskenleri tanimlanmali!");
    Console.Error.WriteLine("  PowerShell:  $env:DIYANET_EMAIL='...'");
    Console.Error.WriteLine("  Bash:        export DIYANET_EMAIL=...");
    return 1;
}

var countryIds = countryIdsArg
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(int.Parse)
    .ToArray();

// Klasorler
EnsureDir(Path.Combine(outputDir, "states"));
EnsureDir(Path.Combine(outputDir, "cities"));
EnsureDir(Path.Combine(outputDir, "city-detail"));
EnsureDir(Path.Combine(outputDir, "prayer-times"));
EnsureDir(Path.Combine(outputDir, "eid"));
EnsureDir(Path.Combine(outputDir, "ramadan"));
EnsureDir(Path.Combine(outputDir, "daily-content"));

var writer = new JsonFileWriter();
var sw = Stopwatch.StartNew();
var stats = new Stats();

// Diyanet kotasi: endpoint basina ~5 istek / kisa pencere.
// Conservative: 4 istek/2sn.
var rateLimiter = new RateLimiter(maxRequestsPerWindow: 4, window: TimeSpan.FromSeconds(2));

using var client = new DiyanetApiClient(email, password, rateLimiter);

Console.WriteLine("=== DIYANET API VERI CEKME ARACI v2 ===");
Console.WriteLine($"Tarih      : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
Console.WriteLine($"Cikti      : {Path.GetFullPath(outputDir)}");
Console.WriteLine($"Ulkeler    : {string.Join(", ", countryIds)}");
Console.WriteLine($"Haftalik   : {includeWeekly} | Bayram: {includeEid} | Ramazan: {includeRamadan} | Sehir Detay: {includeCityDetail}");
Console.WriteLine();

if (!await client.LoginAsync())
{
    Console.Error.WriteLine("Login basarisiz! Cikiliyor...");
    return 1;
}

// 1) Ulkeler
Console.WriteLine("--- Ulkeler ---");
var countries = await client.GetCountriesAsync();
if (countries.Count > 0)
{
    await writer.WriteAsync(Path.Combine(outputDir, "countries.json"), new
    {
        lastUpdated = DateTime.UtcNow,
        count = countries.Count,
        data = countries,
    });
    stats.CountryCount = countries.Count;
}

// 2) Her ulke icin eyaletler -> ilceler -> namaz vakitleri
var allCities = new List<PlaceModel>();

foreach (var countryId in countryIds)
{
    Console.WriteLine();
    Console.WriteLine($"--- Eyaletler (countryId={countryId}) ---");
    var states = await client.GetStatesAsync(countryId);
    if (states.Count == 0) continue;

    var countryName = countries.FirstOrDefault(c => c.Id == countryId)?.Name ?? $"Country-{countryId}";
    await writer.WriteAsync(Path.Combine(outputDir, "states", $"{countryId}.json"), new
    {
        lastUpdated = DateTime.UtcNow,
        countryId,
        countryName,
        count = states.Count,
        data = states,
    });
    stats.StateCount += states.Count;

    foreach (var state in states)
    {
        Console.WriteLine();
        Console.WriteLine($"  >> {state.Name} (state {state.Id})");

        var cities = await client.GetCitiesAsync(state.Id);
        if (cities.Count == 0) continue;

        allCities.AddRange(cities.Select(c => new PlaceModel
        {
            Id = c.Id,
            Name = c.Name,
            Code = c.Code,
        }));

        await writer.WriteAsync(Path.Combine(outputDir, "cities", $"{state.Id}.json"), new
        {
            lastUpdated = DateTime.UtcNow,
            stateId = state.Id,
            stateName = state.Name,
            countryId,
            count = cities.Count,
            data = cities,
        });

        var monthKey = DateTime.UtcNow.ToString("yyyy-MM");
        var yearKey = DateTime.UtcNow.Year.ToString();

        foreach (var city in cities)
        {
            // Aylik vakitler (zorunlu)
            var monthly = await client.GetMonthlyPrayerTimesAsync(city.Id);
            if (monthly.Count > 0)
            {
                var dir = Path.Combine(outputDir, "prayer-times", city.Id.ToString());
                EnsureDir(dir);
                await writer.WriteAsync(Path.Combine(dir, $"{monthKey}.json"), new
                {
                    lastUpdated = DateTime.UtcNow,
                    cityId = city.Id,
                    cityName = city.Name,
                    stateId = state.Id,
                    stateName = state.Name,
                    month = monthKey,
                    count = monthly.Count,
                    data = monthly,
                });
                stats.MonthlyOk++;
            }
            else
            {
                stats.MonthlyFail++;
            }

            // Sehir detay (kible, mesafe)
            if (includeCityDetail)
            {
                var detail = await client.GetCityDetailAsync(city.Id);
                if (detail != null)
                {
                    await writer.WriteAsync(Path.Combine(outputDir, "city-detail", $"{city.Id}.json"), new
                    {
                        lastUpdated = DateTime.UtcNow,
                        data = detail,
                    });
                    stats.CityDetailOk++;
                }
            }

            // Bayram namazi
            if (includeEid)
            {
                var eid = await client.GetEidPrayerTimesAsync(city.Id);
                if (eid != null)
                {
                    var dir = Path.Combine(outputDir, "eid", city.Id.ToString());
                    EnsureDir(dir);
                    await writer.WriteAsync(Path.Combine(dir, $"{yearKey}.json"), new
                    {
                        lastUpdated = DateTime.UtcNow,
                        cityId = city.Id,
                        year = yearKey,
                        data = eid,
                    });
                    stats.EidOk++;
                }
            }

            // Ramazan imsakiye
            if (includeRamadan)
            {
                var ramadan = await client.GetRamadanTimesAsync(city.Id);
                if (ramadan.Count > 0)
                {
                    var dir = Path.Combine(outputDir, "ramadan", city.Id.ToString());
                    EnsureDir(dir);
                    await writer.WriteAsync(Path.Combine(dir, $"{yearKey}.json"), new
                    {
                        lastUpdated = DateTime.UtcNow,
                        cityId = city.Id,
                        year = yearKey,
                        count = ramadan.Count,
                        data = ramadan,
                    });
                    stats.RamadanOk++;
                }
            }

            stats.ProcessedCities++;
            if (perCityDelayMs > 0)
                await Task.Delay(perCityDelayMs);
        }
    }
}

// 3) Gunluk icerik
Console.WriteLine();
Console.WriteLine("--- Gunluk Icerik ---");
var daily = await client.GetDailyContentAsync();
if (daily != null)
{
    var todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
    await writer.WriteAsync(Path.Combine(outputDir, "daily-content", $"{todayKey}.json"), new
    {
        lastUpdated = DateTime.UtcNow,
        date = todayKey,
        data = daily,
    });
}

// 4) Sehir indeksi (arama icin)
Console.WriteLine();
Console.WriteLine("--- Sehir Indeksi ---");
await writer.WriteAsync(Path.Combine(outputDir, "city-index.json"), new
{
    lastUpdated = DateTime.UtcNow,
    description = "Tum ilcelerin arama indeksi - mobil app icin",
    count = allCities.Count,
    data = allCities
        .GroupBy(c => c.Id)
        .Select(g => g.First())
        .Select(c => new { c.Id, c.Name })
        .OrderBy(c => c.Name)
        .ToList(),
});

// 5) Meta
await writer.WriteAsync(Path.Combine(outputDir, "meta.json"), new
{
    lastUpdated = DateTime.UtcNow,
    nextUpdate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1),
    version = "2.0.0",
    source = "Diyanet Isleri Baskanligi - Awqat Salah API",
    countries = stats.CountryCount,
    states = stats.StateCount,
    cities = allCities.Count,
    currentMonth = DateTime.UtcNow.ToString("yyyy-MM"),
    stats = new
    {
        stats.ProcessedCities,
        stats.MonthlyOk,
        stats.MonthlyFail,
        stats.CityDetailOk,
        stats.EidOk,
        stats.RamadanOk,
    },
    elapsedSeconds = (int)sw.Elapsed.TotalSeconds,
});

sw.Stop();

Console.WriteLine();
Console.WriteLine("=== TAMAMLANDI ===");
Console.WriteLine($"Sure        : {sw.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine($"Sehir       : {allCities.Count}");
Console.WriteLine($"Aylik OK    : {stats.MonthlyOk}");
Console.WriteLine($"Aylik FAIL  : {stats.MonthlyFail}");
if (includeCityDetail) Console.WriteLine($"City Detay  : {stats.CityDetailOk}");
if (includeEid)        Console.WriteLine($"Bayram      : {stats.EidOk}");
if (includeRamadan)    Console.WriteLine($"Ramazan     : {stats.RamadanOk}");

return stats.MonthlyFail > 0 ? 2 : 0;

// --- HELPERS ---
static void EnsureDir(string path) => Directory.CreateDirectory(path);

internal class Stats
{
    public int CountryCount { get; set; }
    public int StateCount { get; set; }
    public int ProcessedCities { get; set; }
    public int MonthlyOk { get; set; }
    public int MonthlyFail { get; set; }
    public int CityDetailOk { get; set; }
    public int EidOk { get; set; }
    public int RamadanOk { get; set; }
}
