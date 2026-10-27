using System.Text.Json;

namespace CamLink.Desktop.Core;

/// <summary>
/// Masaüstü ayarları. <c>PcName</c> boşsa kabuk bilgisayar adını kullanır.
/// <c>QualityMode</c>: <c>"auto"</c> (varsayılan; Windows uygulamasının istediği kaliteyi telefondan kendiliğinden ister)
/// ya da <c>"fixed"</c> (kullanıcının seçtiği kalite kalır). Width/Height/Fps kullanılmaz: çıkış boyutu telefonun kalitesinden gelir.
/// Kaydetme çağrılarını çağıran sıraya koymalıdır (geçici dosya adı sabittir).
/// </summary>
public sealed record DesktopSettings(int Width = 1920, int Height = 1080, int Fps = 30, string PcName = "", string QualityMode = "auto")
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary><c>%APPDATA%\CamLink\settings.json</c></summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamLink", "settings.json");

    /// <summary>Dosya yoksa ya da bozuksa varsayılanları döndürür; hiçbir zaman fırlatmaz.</summary>
    public static DesktopSettings Load(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), Json);
            return s == null ? new DesktopSettings() : s with { PcName = s.PcName ?? "", QualityMode = s.QualityMode == "fixed" ? "fixed" : "auto" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopSettings();
        }
    }

    /// <summary>Klasörü oluşturur; geçici dosyaya yazıp üstüne taşır, yarım dosya kalmaz.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
