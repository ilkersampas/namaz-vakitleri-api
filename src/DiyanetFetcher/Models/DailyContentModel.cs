namespace DiyanetFetcher.Models;

public class DailyContentModel
{
    public int Id { get; set; }
    public int DayOfYear { get; set; }
    public string? Verse { get; set; }
    public string? VerseSource { get; set; }
    public string? Hadith { get; set; }
    public string? HadithSource { get; set; }
    public string? Pray { get; set; }
    public string? PraySource { get; set; }
}
