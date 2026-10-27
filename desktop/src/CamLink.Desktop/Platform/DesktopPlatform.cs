using System.Text.Json.Nodes;
using CamLink.Desktop.Core;

namespace CamLink.Desktop.Platform;

/// <summary>
/// Yerel API'nin Windows'a bağlı kısmı: <c>/api/status</c>'un <c>platform</c> alanı, kamera kurulumu,
/// güvenlik duvarı ve ayar kaydı. Kestrel iş parçacıklarında çalışır.
/// </summary>
public sealed class DesktopPlatform(AppHost host) : IDesktopPlatform
{
    const int MaxNameLength = 40;
    int _installing;

    /// <summary>Kamera kurulumu sürüyor (DLL bırakıldı; yeniden başlatma denenmemeli).</summary>
    public bool Installing => Volatile.Read(ref _installing) == 1;

    public object Status()
    {
        var s = host.Settings;
        var cam = host.Camera;
        return new
        {
            os = new { build = Environment.OSVersion.Version.Build, windows11 = VirtualCamera.IsSupported },
            pcName = host.PcName(),
            address = NetInfo.LanAddresses().FirstOrDefault()?.Address, // QR'daki adres: /api/qr.svg ile aynı seçim
            settings = new { pcName = s.PcName, qualityMode = s.QualityMode },
            camera = new
            {
                name = VirtualCamera.Name,
                supported = VirtualCamera.IsSupported,
                installed = cam.IsInstalled,
                bundled = File.Exists(VirtualCamera.BundledPath),
                needsUpdate = cam.NeedsUpdate || host.Bus.NeedsUpdate, // ikincisi: Çerçeve Sunucusu hâlâ eski DLL'in küçük belleğini tutuyor
                installing = Installing,
                running = cam.Running,
                hung = cam.Hung,
                inUse = host.Bus.HasConsumer,
                error = cam.Error,
                lastOpenError = host.Bus.LastOpenError,
            },
            receivers = new { srt = Of(host.Srt, Info.Srt), rtmp = Of(host.Rtmp, Info.Rtmp) },
            audio = new { device = host.Audio.DeviceName, level = host.Audio.Level },
            usb = new
            {
                available = host.Usb.Available,
                devices = host.Usb.Devices.Select(d => new { serial = d.Serial, state = d.State }).ToArray(),
                error = host.Usb.LastError,
            },
        };
    }

    static object Of(Receiver r, int port)
    {
        var (maxGapMs, gaps100) = r.Gaps;
        return new { state = r.State.ToString(), fps = r.Fps, maxGapMs, gaps100, audioBytes = r.AudioBytes, lastError = r.LastError, port };
    }

    public async Task<object> InstallCameraAsync()
    {
        if (!VirtualCamera.IsSupported) return Result(false, VirtualCamera.NeedsWindows11);
        if (!File.Exists(VirtualCamera.BundledPath)) return Result(false, "Sanal kamera dosyası eksik. CamLink'i yeniden indir.");
        if (Interlocked.Exchange(ref _installing, 1) == 1) return Result(false, "Kurulum zaten sürüyor");
        try
        {
            var ok = await host.Camera.InstallAsync().ConfigureAwait(false);
            var message = host.Camera.Error;
            // Kurulum için durdurulan kamera, kurulum başarısız olsa da (ör. UAC reddi) yeniden başlar.
            await Task.Run(host.StartCamera).ConfigureAwait(false);
            if (!ok) return Result(false, message ?? "Sanal kamera kurulamadı");
            return host.Camera.Running ? Result(true, "Sanal kamera kuruldu") : Result(false, host.Camera.Error ?? "Sanal kamera başlatılamadı");
        }
        finally { Volatile.Write(ref _installing, 0); }
    }

    public async Task<object> FirewallAsync()
    {
        var (ok, message) = await Elevated.RunAsync(Elevated.Firewall).ConfigureAwait(false);
        return Result(ok, message);
    }

    /// <summary>
    /// Ayarlar: <c>pcName</c> (metin, boş = bilgisayar adı) ve <c>qualityMode</c> (<c>"auto"</c> | <c>"fixed"</c>).
    /// Çözünürlük ve fps ayarı yok: çıkış boyutu telefonun kalite modundan gelir.
    /// </summary>
    public Task<object> SaveSettingsAsync(JsonObject patch)
    {
        string? name = null, mode = null;
        foreach (var (k, v) in patch)
        {
            if (k == "pcName" && v is JsonValue nv && nv.TryGetValue(out name)) continue;
            if (k == "qualityMode" && v is JsonValue mv && mv.TryGetValue(out mode) && (mode == "auto" || mode == "fixed")) continue;
            return Task.FromResult(Result(false, $"Geçersiz ayar: {k}"));
        }
        name = name?.Trim();
        if (name?.Length > MaxNameLength) return Task.FromResult(Result(false, $"Bilgisayar adı en çok {MaxNameLength} karakter olabilir."));
        try
        {
            var s = host.UpdateSettings(cur => cur with { PcName = name ?? cur.PcName, QualityMode = mode ?? cur.QualityMode });
            return Task.FromResult<object>(new { ok = true, settings = new { pcName = s.PcName, qualityMode = s.QualityMode } });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result(false, "Ayarlar kaydedilemedi: " + e.Message));
        }
    }

    static object Result(bool ok, string message) => new { ok, message };
}
