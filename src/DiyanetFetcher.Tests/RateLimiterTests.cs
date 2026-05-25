using System.Diagnostics;
using DiyanetFetcher.Services;
using FluentAssertions;

namespace DiyanetFetcher.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task Limit_altinda_bekletmez()
    {
        var limiter = new RateLimiter(maxRequestsPerWindow: 5, window: TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
            await limiter.WaitAsync("bucket-a");
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "ilk 5 istek pencere dolmadigi icin beklemeden geciyor");
    }

    [Fact]
    public async Task Limit_asilinca_pencere_kayma_kadar_bekler()
    {
        var window = TimeSpan.FromMilliseconds(500);
        var limiter = new RateLimiter(maxRequestsPerWindow: 2, window: window);

        // 2 istek hemen gecer
        await limiter.WaitAsync("bucket-b");
        await limiter.WaitAsync("bucket-b");

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync("bucket-b");
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400,
            "3. istek penceredeki en eski hit'in dusmesini bekler");
    }

    [Fact]
    public async Task Farkli_bucketler_bagimsiz_calisir()
    {
        var limiter = new RateLimiter(maxRequestsPerWindow: 1, window: TimeSpan.FromSeconds(10));

        await limiter.WaitAsync("a");
        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync("b"); // baska bucket, beklememeli
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100);
    }
}
