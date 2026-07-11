using DiyanetFetcher.Services;
using FluentAssertions;

namespace DiyanetFetcher.Tests;

public class RetentionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RetentionService _sut = new();

    public RetentionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RetentionTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // --- Yardimcilar ---
    private string MakeSubDirWithFiles(string subName, params string[] fileNames)
    {
        var dir = Path.Combine(_tempDir, subName);
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(dir, name), "{}");
        return dir;
    }

    private static string[] FileNamesIn(string dir) =>
        Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    // --- PrunePerSubdirectory ---

    [Fact]
    public void Her_sehir_klasorunde_sadece_en_yeni_N_ay_kalir()
    {
        MakeSubDirWithFiles("16704", "2026-01.json", "2026-02.json", "2026-03.json", "2026-04.json");
        MakeSubDirWithFiles("16706", "2025-11.json", "2025-12.json", "2026-01.json");

        var deleted = _sut.PrunePerSubdirectory(_tempDir, keep: 2);

        deleted.Should().Be(3); // 16704: 2 silinir, 16706: 1 silinir
        FileNamesIn(Path.Combine(_tempDir, "16704")).Should().Equal("2026-03.json", "2026-04.json");
        FileNamesIn(Path.Combine(_tempDir, "16706")).Should().Equal("2025-12.json", "2026-01.json");
    }

    [Fact]
    public void En_yeni_dosya_daima_korunur()
    {
        MakeSubDirWithFiles("city", "2024-05.json", "2025-05.json", "2026-05.json");

        _sut.PrunePerSubdirectory(_tempDir, keep: 1);

        FileNamesIn(Path.Combine(_tempDir, "city")).Should().Equal("2026-05.json");
    }

    [Fact]
    public void Keep_sayisindan_az_dosya_varsa_hicbir_sey_silinmez()
    {
        MakeSubDirWithFiles("city", "2026-04.json", "2026-05.json");

        var deleted = _sut.PrunePerSubdirectory(_tempDir, keep: 5);

        deleted.Should().Be(0);
        FileNamesIn(Path.Combine(_tempDir, "city")).Should().HaveCount(2);
    }

    [Fact]
    public void Yil_bazli_dosyalar_da_kronolojik_budanir()
    {
        // ramadan/eid senaryosu: yyyy.json
        MakeSubDirWithFiles("city", "2023.json", "2024.json", "2025.json", "2026.json");

        _sut.PrunePerSubdirectory(_tempDir, keep: 2);

        FileNamesIn(Path.Combine(_tempDir, "city")).Should().Equal("2025.json", "2026.json");
    }

    [Fact]
    public void Sadece_pattern_eslesen_dosyalar_dikkate_alinir()
    {
        var dir = MakeSubDirWithFiles("city", "2026-01.json", "2026-02.json", "2026-03.json");
        File.WriteAllText(Path.Combine(dir, "2026-03.json.tmp"), "yarim"); // pattern disi, korunmali

        var deleted = _sut.PrunePerSubdirectory(_tempDir, keep: 1);

        deleted.Should().Be(2); // sadece *.json sayilir; en yeni 1 (2026-03.json) kalir
        File.Exists(Path.Combine(dir, "2026-03.json")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "2026-03.json.tmp")).Should().BeTrue(); // dokunulmadi
        File.Exists(Path.Combine(dir, "2026-01.json")).Should().BeFalse();
        File.Exists(Path.Combine(dir, "2026-02.json")).Should().BeFalse();
    }

    [Fact]
    public void Olmayan_kok_klasor_sifir_dondurur_hata_vermez()
    {
        var missing = Path.Combine(_tempDir, "yok-boyle-bir-yer");

        var act = () => _sut.PrunePerSubdirectory(missing, keep: 2);

        act.Should().NotThrow();
        _sut.PrunePerSubdirectory(missing, keep: 2).Should().Be(0);
    }

    // --- PruneDirectory (daily-content senaryosu) ---

    [Fact]
    public void Tek_klasorde_en_yeni_N_dosya_tutulur()
    {
        var dir = MakeSubDirWithFiles("daily-content",
            "2026-05-20.json", "2026-05-21.json", "2026-05-22.json",
            "2026-05-23.json", "2026-05-24.json");

        var deleted = _sut.PruneDirectory(dir, keep: 3);

        deleted.Should().Be(2);
        FileNamesIn(dir).Should().Equal("2026-05-22.json", "2026-05-23.json", "2026-05-24.json");
    }

    // --- Guvenlik: keep < 1 tum veriyi silmeyi engeller ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Keep_birden_kucukse_exception_atar(int keep)
    {
        MakeSubDirWithFiles("city", "2026-05.json");

        var perSub = () => _sut.PrunePerSubdirectory(_tempDir, keep);
        var single = () => _sut.PruneDirectory(Path.Combine(_tempDir, "city"), keep);

        perSub.Should().Throw<ArgumentOutOfRangeException>();
        single.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
