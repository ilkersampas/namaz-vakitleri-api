using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DiyanetFetcher.Models;

namespace DiyanetFetcher.Services;

public class DiyanetApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _email;
    private readonly string _password;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly RateLimiter _rateLimiter;
    private readonly SemaphoreSlim _authGate = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    private const string BaseUrl = "https://awqatsalah.diyanet.gov.tr";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(25); // 30 dk'lik token'i 25 dk sonra yenile

    public DiyanetApiClient(string email, string password, RateLimiter? rateLimiter = null)
    {
        _email = email;
        _password = password;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DiyanetFetcher/2.0");
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _rateLimiter = rateLimiter ?? new RateLimiter(maxRequestsPerWindow: 4, window: TimeSpan.FromSeconds(2));
    }

    // --- AUTH ---

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        return await RetryPolicy.ExecuteAsync(async () =>
        {
            var loginBody = new { email = _email, password = _password };
            var content = new StringContent(JsonSerializer.Serialize(loginBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/Auth/Login", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new DiyanetTransientException(response.StatusCode, $"Login HTTP {(int)response.StatusCode}");

            var result = JsonSerializer.Deserialize<ApiResponse<TokenModel>>(json, _jsonOptions);
            if (result?.Success == true && result.Data != null)
            {
                SetToken(result.Data.AccessToken, result.Data.RefreshToken);
                Console.WriteLine("[OK] Login basarili.");
                return true;
            }

            Console.WriteLine($"[HATA] Login basarisiz: {result?.Message ?? "bilinmeyen hata"}");
            return false;
        }, label: "Login", cancellationToken: ct);
    }

    private async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_refreshToken)) return false;

        try
        {
            return await RetryPolicy.ExecuteAsync(async () =>
            {
                var response = await _httpClient.GetAsync($"/Auth/RefreshToken/{_refreshToken}", ct);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        return false;
                    throw new DiyanetTransientException(response.StatusCode, $"Refresh HTTP {(int)response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<ApiResponse<TokenModel>>(json, _jsonOptions);
                if (result?.Success == true && result.Data != null)
                {
                    SetToken(result.Data.AccessToken, result.Data.RefreshToken);
                    Console.WriteLine("[OK] Token yenilendi.");
                    return true;
                }
                return false;
            }, maxAttempts: 2, label: "RefreshToken", cancellationToken: ct);
        }
        catch
        {
            return false;
        }
    }

    private void SetToken(string accessToken, string refreshToken)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _tokenExpiresAt = DateTime.UtcNow.Add(TokenLifetime);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private async Task EnsureValidTokenAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _tokenExpiresAt && !string.IsNullOrEmpty(_accessToken))
            return;

        await _authGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _tokenExpiresAt && !string.IsNullOrEmpty(_accessToken))
                return;

            Console.WriteLine("[INFO] Token suresi yaklasti, yenileniyor...");
            if (!await RefreshTokenAsync(ct))
            {
                Console.WriteLine("[INFO] Refresh basarisiz, tekrar login olunuyor...");
                await LoginAsync(ct);
            }
        }
        finally
        {
            _authGate.Release();
        }
    }

    // --- PLACE ---

    public Task<List<PlaceModel>> GetCountriesAsync(CancellationToken ct = default)
        => GetListAsync<PlaceModel>("/Place/Countries", "Place", "Ulkeler", ct);

    public Task<List<PlaceModel>> GetStatesAsync(int? countryId = null, CancellationToken ct = default)
    {
        var url = countryId.HasValue ? $"/Place/States/{countryId}" : "/Place/States";
        return GetListAsync<PlaceModel>(url, "Place", $"Eyaletler ({countryId?.ToString() ?? "tumu"})", ct);
    }

    public Task<List<PlaceModel>> GetCitiesAsync(int? stateId = null, CancellationToken ct = default)
    {
        var url = stateId.HasValue ? $"/Place/Cities/{stateId}" : "/Place/Cities";
        return GetListAsync<PlaceModel>(url, "Place", $"Sehirler ({stateId?.ToString() ?? "tumu"})", ct);
    }

    public Task<CityDetailModel?> GetCityDetailAsync(int cityId, CancellationToken ct = default)
        => GetSingleAsync<CityDetailModel>($"/Place/CityDetail/{cityId}", "Place", $"Sehir Detay ({cityId})", ct);

    // --- DAILY CONTENT ---

    public Task<DailyContentModel?> GetDailyContentAsync(CancellationToken ct = default)
        => GetSingleAsync<DailyContentModel>("/DailyContent", "DailyContent", "Gunluk Icerik", ct);

    // --- PRAYER TIMES ---

    public Task<List<PrayerTimeModel>> GetDailyPrayerTimesAsync(int cityId, CancellationToken ct = default)
        => GetListAsync<PrayerTimeModel>($"/PrayerTime/Daily/{cityId}", "PrayerTime/Daily", $"Gunluk ({cityId})", ct);

    public Task<List<PrayerTimeModel>> GetWeeklyPrayerTimesAsync(int cityId, CancellationToken ct = default)
        => GetListAsync<PrayerTimeModel>($"/PrayerTime/Weekly/{cityId}", "PrayerTime/Weekly", $"Haftalik ({cityId})", ct);

    public Task<List<PrayerTimeModel>> GetMonthlyPrayerTimesAsync(int cityId, CancellationToken ct = default)
        => GetListAsync<PrayerTimeModel>($"/PrayerTime/Monthly/{cityId}", "PrayerTime/Monthly", $"Aylik ({cityId})", ct);

    public Task<EidPrayerTimeModel?> GetEidPrayerTimesAsync(int cityId, CancellationToken ct = default)
        => GetSingleAsync<EidPrayerTimeModel>($"/PrayerTime/Eid/{cityId}", "PrayerTime/Eid", $"Bayram ({cityId})", ct);

    public Task<List<PrayerTimeModel>> GetRamadanTimesAsync(int cityId, CancellationToken ct = default)
        => GetListAsync<PrayerTimeModel>($"/PrayerTime/Ramadan/{cityId}", "PrayerTime/Ramadan", $"Ramazan ({cityId})", ct);

    // --- PRIVATE HELPERS ---

    private async Task<List<T>> GetListAsync<T>(string url, string bucket, string label, CancellationToken ct)
    {
        try
        {
            var json = await CallAsync(url, bucket, label, ct);
            var result = JsonSerializer.Deserialize<ApiResponse<List<T>>>(json, _jsonOptions);

            if (result?.Success == true && result.Data != null)
            {
                Console.WriteLine($"  [OK] {label}: {result.Data.Count} kayit");
                return result.Data;
            }

            Console.WriteLine($"  [UYARI] {label}: Veri alinamadi - {result?.Message}");
            return new List<T>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [HATA] {label}: {ex.Message}");
            return new List<T>();
        }
    }

    private async Task<T?> GetSingleAsync<T>(string url, string bucket, string label, CancellationToken ct) where T : class
    {
        try
        {
            var json = await CallAsync(url, bucket, label, ct);
            var result = JsonSerializer.Deserialize<ApiResponse<T>>(json, _jsonOptions);

            if (result?.Success == true && result.Data != null)
            {
                Console.WriteLine($"  [OK] {label}");
                return result.Data;
            }

            Console.WriteLine($"  [UYARI] {label}: Veri alinamadi - {result?.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [HATA] {label}: {ex.Message}");
            return null;
        }
    }

    private async Task<string> CallAsync(string url, string bucket, string label, CancellationToken ct)
    {
        return await RetryPolicy.ExecuteAsync(async () =>
        {
            await EnsureValidTokenAsync(ct);
            await _rateLimiter.WaitAsync(bucket, ct);

            var response = await _httpClient.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Console.WriteLine($"  [INFO] {label}: 401 alindi, token yenileniyor...");
                _tokenExpiresAt = DateTime.MinValue; // zorla yenile
                await EnsureValidTokenAsync(ct);
                response = await _httpClient.GetAsync(url, ct);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
            {
                throw new DiyanetTransientException(response.StatusCode, $"{label}: HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{label}: HTTP {(int)response.StatusCode}");

            return await response.Content.ReadAsStringAsync(ct);
        }, maxAttempts: 4, label: label, cancellationToken: ct);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _authGate.Dispose();
    }
}
