using DiyanetFetcher.Services;
using FluentAssertions;

namespace DiyanetFetcher.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task Basarili_oldugunda_tek_seferde_doner()
    {
        var attempts = 0;
        var result = await RetryPolicy.ExecuteAsync(async () =>
        {
            attempts++;
            await Task.Yield();
            return "ok";
        }, initialDelayMs: 10, maxAttempts: 3);

        result.Should().Be("ok");
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Transient_hatada_tekrar_dener()
    {
        var attempts = 0;
        var result = await RetryPolicy.ExecuteAsync<string>(async () =>
        {
            attempts++;
            if (attempts < 3)
                throw new HttpRequestException("503");
            await Task.Yield();
            return "ok";
        }, initialDelayMs: 10, maxAttempts: 4);

        result.Should().Be("ok");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Non_transient_hatada_tekrar_denemez()
    {
        var attempts = 0;
        var act = async () => await RetryPolicy.ExecuteAsync<string>(() =>
        {
            attempts++;
            throw new InvalidOperationException("kotu");
        }, initialDelayMs: 10);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Max_deneme_asilinca_son_hatayi_firlatir()
    {
        var attempts = 0;
        var act = async () => await RetryPolicy.ExecuteAsync<string>(() =>
        {
            attempts++;
            throw new HttpRequestException("503");
        }, initialDelayMs: 10, maxAttempts: 3);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.InnerException.Should().BeOfType<HttpRequestException>();
        attempts.Should().Be(3);
    }
}
