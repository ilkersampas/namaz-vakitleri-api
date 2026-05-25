namespace DiyanetFetcher.Models;

public class CityDetailModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? GeographicQiblaAngle { get; set; }
    public string? DistanceToKaaba { get; set; }
    public string? QiblaAngle { get; set; }
    public string? City { get; set; }
    public string? CityEn { get; set; }
    public string? Country { get; set; }
    public string? CountryEn { get; set; }
}
