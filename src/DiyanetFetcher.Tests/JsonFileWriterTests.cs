using System.Text.Json;
using DiyanetFetcher.Services;
using FluentAssertions;

namespace DiyanetFetcher.Tests;

public class JsonFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DiyanetFetcherTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Yeni_dosya_yazilir_tmp_silinir()
    {
        var writer = new JsonFileWriter();
        var path = Path.Combine(_tempDir, "test.json");
        await writer.WriteAsync(path, new { name = "test", value = 42 });

        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".tmp").Should().BeFalse();

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("name").GetString().Should().Be("test");
        doc.RootElement.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Var_olan_dosya_atomik_replace_olur()
    {
        var writer = new JsonFileWriter();
        var path = Path.Combine(_tempDir, "replace.json");
        await writer.WriteAsync(path, new { v = 1 });
        await writer.WriteAsync(path, new { v = 2 });

        var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("v").GetInt32().Should().Be(2);
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task Eksik_klasor_otomatik_olusturulur()
    {
        var writer = new JsonFileWriter();
        var path = Path.Combine(_tempDir, "nested", "deep", "file.json");
        await writer.WriteAsync(path, new { ok = true });

        File.Exists(path).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
