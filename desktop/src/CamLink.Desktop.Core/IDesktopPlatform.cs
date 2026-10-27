using System.Text.Json.Nodes;

namespace CamLink.Desktop.Core;

/// <summary>
/// Yalnız Windows'a bağlı işler (sanal kamera kurulumu, güvenlik duvarı, ayar yazma).
/// Çekirdek bunları bilmez; uygulama kabuğu uygular, testler sahte uygulama verir.
/// </summary>
public interface IDesktopPlatform
{
    /// <summary>Arayüzün durum sayfası için: kamera kurulu mu, güvenlik duvarı, Windows sürümü vb.</summary>
    object Status();

    Task<object> InstallCameraAsync();

    Task<object> FirewallAsync();

    /// <summary>Arayüzden gelen kısmi ayar değişikliğini doğrular, uygular ve kaydeder.</summary>
    Task<object> SaveSettingsAsync(JsonObject patch);
}
