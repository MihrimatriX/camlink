using Velopack;
using Velopack.Sources;

namespace CamLink.Desktop;

/// <summary>
/// Sessiz otomatik güncelleme (Velopack, GitHub sürümleri). Yalnız Setup ile kurulmuş kopyada çalışır; geliştirme
/// kopyasında <c>IsInstalled</c> false'tur ve hiçbir şey yapılmaz. Hata yutulur, günlüğe yazılır: ağın ya da deponun
/// olmaması normal durumdur. İndirilen sürüm kullanıcı "Çık"ınca uygulanır (açıkken dosyalar değiştirilmez).
/// </summary>
static class Updater
{
    const string RepoUrl = "https://github.com/MihrimatriX/camlink";
    static readonly TimeSpan Delay = TimeSpan.FromSeconds(10); // açılış yükünden sonra
    static volatile UpdateManager? _manager;

    // ponytail: yalnız açılıştan 10 sn sonra bir kez bakar; günlerce açık kalan kopya yeniden başlatılana dek yeni sürümü görmez.
    public static async Task CheckAsync()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!manager.IsInstalled) return;
            await Task.Delay(Delay).ConfigureAwait(false);
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null) return;
            await manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _manager = manager;
            App.Log($"Güncelleme indirildi: {info.TargetFullRelease.Version}, çıkışta uygulanacak.");
        }
        catch (Exception e)
        {
            App.Log("Güncelleme denetimi: " + e.Message);
        }
    }

    /// <summary>Çıkıştan hemen önce çağrılır: indirilmiş sürüm varsa güncelleyici bu süreç kapanınca onu sessizce uygular.</summary>
    public static void ApplyOnExit()
    {
        try
        {
            if (_manager is { UpdatePendingRestart: { } pending } m)
                m.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception e)
        {
            App.Log("Güncelleme uygulanamadı: " + e.Message);
        }
    }
}
