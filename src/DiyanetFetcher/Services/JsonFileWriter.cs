using System.Text.Json;

namespace DiyanetFetcher.Services;

/// <summary>
/// JSON dosyalarini atomik (tmp -> rename) sekilde yazar.
/// Boylece yarim yazilmis bir dosya GitHub Pages'e dusmez.
/// </summary>
public class JsonFileWriter
{
    private readonly JsonSerializerOptions _options;

    public JsonFileWriter(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public async Task WriteAsync(string path, object data, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(data, _options);

        await File.WriteAllTextAsync(tmpPath, json, cancellationToken);

        // Atomik rename: ayni filesystem icinde Move atomiktir (Windows'ta da
        // .NET 5+ ile File.Move overwrite=true atomik replace yapar)
        if (File.Exists(path))
            File.Replace(tmpPath, path, destinationBackupFileName: null);
        else
            File.Move(tmpPath, path);
    }
}
