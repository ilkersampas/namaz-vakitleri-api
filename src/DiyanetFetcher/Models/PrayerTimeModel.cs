namespace DiyanetFetcher.Models;

public class PrayerTimeModel
{
    public string? ShapeMoonUrl { get; set; }
    public string Fajr { get; set; } = string.Empty;
    public string Sunrise { get; set; } = string.Empty;
    public string Dhuhr { get; set; } = string.Empty;
    public string Asr { get; set; } = string.Empty;
    public string Maghrib { get; set; } = string.Empty;
    public string Isha { get; set; } = string.Empty;
    public string? AstronomicalSunset { get; set; }
    public string? AstronomicalSunrise { get; set; }
    public string? HijriDateShort { get; set; }
    public string? HijriDateShortIso8601 { get; set; }
    public string? HijriDateLong { get; set; }
    public string? HijriDateLongIso8601 { get; set; }
    public string? QiblaTime { get; set; }
    public string? GregorianDateShort { get; set; }
    public string? GregorianDateShortIso8601 { get; set; }
    public string? GregorianDateLong { get; set; }
    public string? GregorianDateLongIso8601 { get; set; }
    public int GreenwichMeanTimeZone { get; set; }
}
