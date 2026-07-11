namespace DiyanetFetcher.Services;

/// <summary>
/// Eski donem veri dosyalarini temizler; boylece GitHub Pages / git
/// deposu her ayki cekim ile sonsuza kadar sismez.
///
/// Guvenli olmasinin sebebi: Worker daima GUNCEL donemi ister
/// (monthKey/yearKey/todayKey). Silinen bir eski dosya istenirse
/// Worker 3. katmana (canli Diyanet API) fallback yapar; yani eski
/// dosyalari silmek API sozlesmesini bozmaz.
///
/// Dosya adlari kronolojik olarak siralanabilir oldugu icin
/// (yyyy-MM, yyyy, yyyy-MM-dd) "isme gore en yeni N dosyayi tut"
/// yaklasimini kullaniyoruz. Bu; tarih matematigi gerektirmez ve
/// workflow bir sure calismasa bile son bilinen veriyi korur.
/// </summary>
public class RetentionService
{
    /// <summary>
    /// <paramref name="rootDir"/> altindaki HER alt klasorde (orn. sehir
    /// bazli "prayer-times/{cityId}") isme gore azalan sirada en yeni
    /// <paramref name="keep"/> dosyayi tutar, gerisini siler.
    /// </summary>
    /// <returns>Silinen toplam dosya sayisi.</returns>
    public int PrunePerSubdirectory(string rootDir, int keep, string pattern = "*.json")
    {
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), "keep en az 1 olmali (tum veriyi silmeyi onlemek icin)");
        if (!Directory.Exists(rootDir)) return 0;

        var deleted = 0;
        foreach (var subDir in Directory.EnumerateDirectories(rootDir))
            deleted += PruneDirectory(subDir, keep, pattern);

        return deleted;
    }

    /// <summary>
    /// Tek bir klasorde isme gore azalan sirada en yeni
    /// <paramref name="keep"/> dosyayi tutar, gerisini siler.
    /// </summary>
    /// <returns>Silinen dosya sayisi.</returns>
    public int PruneDirectory(string dir, int keep, string pattern = "*.json")
    {
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), "keep en az 1 olmali (tum veriyi silmeyi onlemek icin)");
        if (!Directory.Exists(dir)) return 0;

        // Ordinal (kultur-bagimsiz) siralama: yyyy-MM / yyyy / yyyy-MM-dd
        // formatlari icin leksikografik sira = kronolojik siradir.
        var stale = Directory.GetFiles(dir, pattern)
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(keep);

        var deleted = 0;
        foreach (var file in stale)
        {
            File.Delete(file);
            deleted++;
        }
        return deleted;
    }
}
