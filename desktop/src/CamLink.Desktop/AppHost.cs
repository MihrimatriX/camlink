using System.Text.Json.Nodes;
using CamLink.Desktop.Core;
using CamLink.Desktop.Platform;
using Microsoft.AspNetCore.Builder;

namespace CamLink.Desktop;

/// <summary>
/// Uygulamanın bütün servisleri tek yerde: kurulum sırası, bekleme karesi döngüsü, ayar değişimi ve kapanış.
/// Alıcı geri çağrıları (ayar, CanWrite, sink) alıcının iş parçacıklarında çalışır: burada arayüz iş parçacığına
/// hiç dokunulmaz. Alıcıların Start/Stop/Restart'ı arayüz iş parçacığından beklenmez (kapı + Dispatcher kilitlenir).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);
    const int SlowTicks = 20; // 20 × 250 ms = 5 sn

    readonly string _baseDir = AppContext.BaseDirectory;
    readonly string _settingsPath = DesktopSettings.DefaultPath;
    readonly JobObject _job = new();
    readonly IdleFrames _idle = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _settingsGate = new(); // DesktopSettings.Save çağrıları sıraya girmeli
    volatile DesktopSettings _settings;
    volatile bool _hadVideo; // görüntü geldi ve kesildi: bekleme karesi "koptu" der
    bool _audioSink;         // telefona son söylenen ses isteği; yalnız tick iş parçacığı (ve başlangıç)
    volatile string? _cameraError; // son günlüğe yazılan kamera hatası
    WebApplication? _web;
    Beacon? _beacon;
    Task? _tick;

    // Bekleme karesi DLL'in varsayılan türünde (1080p30); DLL ölçekleyip tüketicinin seçtiği moda uydurur.
    // Canlı yayının çıkışı telefonun kalitesinden gelir (CurrentOutput). DesktopSettings'teki Width/Height/Fps kullanılmaz.
    static readonly ReceiverSettings IdleOutput = new(1920, 1080, 30);

    const int AutoTicks = 4;               // 4 × 250 ms = 1 sn
    readonly AutoQuality _auto = new();    // gönderme kararı; yalnız tick iş parçacığı
    int _autoSession;                      // _auto'nun sayaçları hangi telefon bağlantısına ait
    readonly object _resizeGate = new();   // tick ve telefon iş parçacığı aynı anda yeniden kurmasın
    Task _resize = Task.CompletedTask;     // sürmekte olan dinleyici yeniden kurulumu

    public AppHost()
    {
        _settings = DesktopSettings.Load(_settingsPath);
        Bus = new FrameBus { Fps = IdleOutput.Fps };
        Link = new PhoneLink(PcName);
        var ffmpeg = Path.Combine(_baseDir, "ffmpeg", "ffmpeg.exe");
        Srt = NewReceiver(ffmpeg, Transport.Srt, Info.Srt, () => Owns(Transport.Srt));
        Rtmp = NewReceiver(ffmpeg, Transport.Rtmp, Info.Rtmp, () => Owns(Transport.Rtmp));
        Usb = new UsbBridge(Path.Combine(_baseDir, "adb", "adb.exe"));
        Platform = new DesktopPlatform(this);
        Link.Changed += ResizeIdleListeners; // state gelir gelmez; telefon henüz yayına bağlanmadan
        Link.AudioWanted = AudioSinkPresent;
    }

    public DesktopSettings Settings => _settings;
    public FrameBus Bus { get; }
    public VirtualCamera Camera { get; } = new();
    public AudioOut Audio { get; } = new();
    public PhoneLink Link { get; }
    public Receiver Srt { get; }
    public Receiver Rtmp { get; }
    public UsbBridge Usb { get; }
    public DesktopPlatform Platform { get; }

    public string PcName() => _settings.PcName is { Length: > 0 } n ? n : Environment.MachineName;

    public bool Receiving => Srt.State == ReceiverState.Receiving || Rtmp.State == ReceiverState.Receiving;

    /// <summary>Telefon sesini çalacak aygıt (VB-Cable) var mı; yoksa telefon ses izsiz yayınlar (PhoneLink.AudioWanted).</summary>
    bool AudioSinkPresent() => Audio.DeviceName != null;

    /// <summary>VB-Cable takıldı ya da kalktı: bağlı telefona <c>{"t":"config","audio":…}</c> (telefon yalnız değişince yeniden kurar).</summary>
    void SyncAudioSink()
    {
        var now = AudioSinkPresent();
        if (now == _audioSink) return;
        _audioSink = now;
        if (Link.Connected) _ = Link.SendAsync(new JsonObject { ["t"] = "config", ["audio"] = now });
    }

    Receiver NewReceiver(string ffmpeg, Transport t, int port, Func<bool> owns) =>
        new(ffmpeg, t, port, CurrentOutput, Bus, new OwnedAudio(Audio, owns))
        {
            // İki dinleyici her zaman açık; sanal kameraya ve hoparlöre yalnız önce görüntü getiren yazar.
            CanWrite = owns,
            AudioWanted = AudioSinkPresent,   // telefona söylenenle aynı: ses aygıtı yoksa ses çıkışı açılmaz
            ProcessStarted = _job.Add,
        };

    /// <summary>
    /// Çıkış boyutunu belirleyen kalite: kısa süre önce masaüstünün kendisi istediği kalite, yoksa telefonun bildirdiği
    /// (bkz. <see cref="Quality.Effective"/>). Yeni FFmpeg döngüsü ve dinleyici yeniden boyutlama bu tek kaynağı okur.
    /// </summary>
    string? EffectiveQuality(JsonObject? state) =>
        Quality.Effective(state, Link.LastQualityRequest, Environment.TickCount64);

    /// <summary>
    /// Yeni FFmpeg döngüsünün çıkışı (bağlı telefon yoksa 1080p30). Alıcı bunu her döngü başında çağırır; kalite
    /// değişince telefon yayını yeniden başlatır ve yeni döngü yeni boyutla açılır.
    /// </summary>
    ReceiverSettings CurrentOutput()
    {
        var o = Quality.ToOutput(EffectiveQuality(Link.State));
        Bus.Fps = o.Fps;   // başlıktaki fps, yazılan karelerle uyumlu kalsın
        return o;
    }

    bool Owns(Transport t) => t == Transport.Srt ? Receiver.Owns(Srt, Rtmp) : Receiver.Owns(Rtmp, Srt);

    /// <summary>Port 4468 doluysa (CamLink zaten açık ya da başka bir uygulama) açıklayıcı iletiyle fırlatır.</summary>
    public async Task StartAsync()
    {
        _web = HostServer.Build(Info.Ctl, Path.Combine(_baseDir, "web"), Link, Platform, PcName, NextPreview);
        try { await _web.StartAsync().ConfigureAwait(false); }
        catch (IOException e)
        {
            throw new InvalidOperationException($"CamLink zaten açık olabilir ya da başka bir uygulama CamLink'in kullanacağı bağlantıyı (port {Info.Ctl}) tutuyor. Diğerini kapatıp CamLink'i yeniden aç.\n\n{e.Message}", e);
        }
        _beacon = new Beacon(() => Info.Payload(PcName(), Link.Connected));
        _beacon.Start();
        await Task.Run(() =>
        {
            StartCamera();
            Audio.Poll();   // alıcılardan ve telefondan önce: ses aygıtı olup olmadığı ilk döngüde ve ilk welcome'da bilinsin
            _audioSink = AudioSinkPresent();
            Srt.Start();
            Rtmp.Start();
        }).ConfigureAwait(false);
        Usb.Start();
        _tick = Task.Run(() => TickAsync(_cts.Token));
    }

    /// <summary>
    /// Destekli ve kuruluysa "CamLink Kamera"yı başlatır. Arka plan iş parçacığından çağrılmalı.
    /// Çalışan kamerayı yeniden kurmaz: VcamStop yalnız kapanışta ve kurulum sırasında çağrılır.
    /// </summary>
    public void StartCamera()
    {
        if (Camera.Running || !VirtualCamera.IsSupported || !Camera.IsInstalled) return;
        if (Camera.Start(VirtualCamera.Name) >= 0 || Camera.Error == _cameraError) return;
        _cameraError = Camera.Error; // yeniden denemelerde aynı hata günlüğe bir kez yazılır
        App.Log(_cameraError ?? "Sanal kamera başlatılamadı");
    }

    /// <summary>Ayarı değiştirir ve kaydeder. <paramref name="change"/> geçersiz değerde <see cref="ArgumentException"/> atar.</summary>
    public DesktopSettings UpdateSettings(Func<DesktopSettings, DesktopSettings> change)
    {
        lock (_settingsGate)
        {
            var next = change(_settings);
            next.Save(_settingsPath);
            return _settings = next;
        }
    }

    /// <summary>Önizleme: sanal kameraya yazan alıcının sıradaki karesi; görüntü yoksa ya da 1 sn kare gelmezse null.</summary>
    async Task<byte[]?> NextPreview(CancellationToken ct)
    {
        var r = Owns(Transport.Srt) ? Srt : Owns(Transport.Rtmp) ? Rtmp : null;
        if (r == null) return null;
        try { return await r.NextPreviewBmpAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }   // yayın bitti ya da öbür taşıyıcıya geçti: sahip yeniden seçilir
    }

    /// <summary>
    /// İki alıcı da görüntü almıyorken 250 ms'de bir bekleme karesi. 5 sn'de bir: ses aygıtı yoklaması ve kamera
    /// çalışmıyorsa (başlatma hatası, kurulum sonrası) başlatma denemesi. Çalışan kameraya dokunulmaz.
    /// </summary>
    async Task TickAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(IdleInterval);
        var n = 0;
        string? lastError = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (++n % SlowTicks == 0)
                    {
                        Audio.Poll();
                        SyncAudioSink();
                        if (!Platform.Installing) StartCamera();
                    }
                    if (n % AutoTicks == 0) SyncQuality();
                    if (Receiving) _hadVideo = true;
                    else WriteIdleFrame();
                    lastError = null;
                }
                catch (Exception e) when (e.Message != lastError)
                {
                    lastError = e.Message; // aynı hata her 250 ms'de günlüğe yazılmasın
                    App.Log("Bekleme döngüsü: " + e);
                }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Telefon bağlıyken, telefondan state geldikçe (<see cref="PhoneLink.Changed"/>) ve yedek olarak saniyede bir:
    /// bekleyen dinleyici telefondan önce ya da eski kaliteyle açıldıysa boyutu eskidir, yeni boyutla baştan kurulur.
    /// Yalnız yeni boyut dinleyicinin çıkışından BÜYÜKSE kurulur: küçük akış büyük çıkışa FFmpeg/DLL'de ölçeklenir, ilk
    /// bağlanmada (mağaza varsayılanı 720p30, dinleyici 1080p30) gereksiz yeniden başlatma ve reddedilen ilk deneme olmaz.
    /// Telefonun şu an bağlanıyor olabileceği dinleyiciye dokunulmaz: yalnız telefon yayında değilken ya da yayını
    /// öbür taşıyıcıdaki alıcıya (görüntü almakta olan) veriyorken kurulur. Yayındaki akış kendi döngüsünde biter ve
    /// yeni boyutla açılır. Telefonun okuma iş parçacığından da çağrılır; hafif tutun.
    /// </summary>
    void ResizeIdleListeners()
    {
        var p = Link.Snapshot();
        var quality = EffectiveQuality(p.State);
        if (!p.Connected || quality == null) return;

        var want = Quality.ToOutput(quality);
        var streaming = p.State?["streaming"] is JsonValue sv && sv.TryGetValue<bool>(out var on) && on;
        lock (_resizeGate)
        {
            if (!_resize.IsCompleted) return;
            var stale = new[] { (Srt, Rtmp), (Rtmp, Srt) }
                .Where(x => x.Item1.State == ReceiverState.Waiting && x.Item1.Active is { } a && Quality.IsGreater(want, a)
                    && (!streaming || x.Item2.State == ReceiverState.Receiving))
                .Select(x => x.Item1)
                .ToList();
            if (stale.Count > 0) _resize = Task.WhenAll(stale.Select(r => r.RestartAsync()));
        }
    }

    /// <summary>
    /// Saniyede bir, telefon bağlıyken: dinleyici yeniden boyutlama (yedek) ve otomatik kalite. Otomatik kipte Windows
    /// uygulamasının istediği mod telefonun şu anki modundan büyükse telefondan o kaliteyi ister (<see cref="AutoQuality"/>:
    /// küçük mod istenince ya da tüketici kalmayınca düşürmez, aynı istek 10 sn içinde yinelenmez, 2 denemeden sonra vazgeçer).
    /// </summary>
    void SyncQuality()
    {
        var session = Link.Session;
        if (session != _autoSession)
        {
            _autoSession = session;
            _auto.Reset();   // yeni telefon: sayaçlar baştan
        }
        var p = Link.Snapshot();
        var current = Quality.Current(p.State);
        if (!p.Connected || current == null) return;

        ResizeIdleListeners();
        Bus.TryReadRequest(out var w, out var h, out var fps);   // okunamazsa 0/0/0: tüketici yok
        var pick = _auto.Next(_settings.QualityMode == "auto", Quality.Supported(p.Caps), w, h, fps, current);
        if (pick != null) _ = Link.SetAsync("quality", JsonValue.Create(pick)); // isteği SetAsync kaydeder; gönderilemezse 10 sn sonra yeniden
    }

    void WriteIdleFrame()
    {
        var (title, line) = IdleText();
        Bus.WriteNv12(_idle.Get(IdleOutput.Width, IdleOutput.Height, title, line), IdleOutput.Width, IdleOutput.Height);
    }

    (string Title, string Line) IdleText()
    {
        var error = Srt.State == ReceiverState.Error ? Srt.LastError : Rtmp.State == ReceiverState.Error ? Rtmp.LastError : null;
        if (error != null) return ("Görüntü alınamıyor", error);
        if (Link.Connected) return ("Telefon bağlı", "Telefonda kırmızı tuşa bas. CamLink penceresinden de başlatabilirsin.");
        if (_hadVideo) return ("Telefon bağlantısı koptu, bekleniyor", "Telefonda yayın yeniden başlayınca görüntü kendiliğinden gelir.");
        return ("Telefon bekleniyor", "Telefonda CamLink'i aç, hedef olarak bu bilgisayarı seç ve yayını başlat.");
    }

    /// <summary>Kapanış: önce sanal kamera, sonra alıcılar (FFmpeg), paylaşılan bellek, ses, USB, duyuru ve sunucu.</summary>
    public async ValueTask DisposeAsync()
    {
        Link.Changed -= ResizeIdleListeners;
        _cts.Cancel();
        if (_tick != null) await _tick.ConfigureAwait(false);
        await Task.Run(Camera.Stop).ConfigureAwait(false);
        await Task.WhenAll(Srt.DisposeAsync().AsTask(), Rtmp.DisposeAsync().AsTask()).ConfigureAwait(false);
        Bus.Dispose();
        Audio.Dispose();
        await Usb.DisposeAsync().ConfigureAwait(false);
        if (_beacon != null) await _beacon.DisposeAsync().ConfigureAwait(false);
        if (_web != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _web.StopAsync(timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            await _web.DisposeAsync().ConfigureAwait(false);
        }
        _cts.Dispose();
    }

    /// <summary>Sesi yalnız sanal kameraya yazan alıcıdan geçirir (geçiş anında iki yayın karışmasın).</summary>
    sealed class OwnedAudio(AudioOut audio, Func<bool> owns) : IAudioSink
    {
        public void Write(ReadOnlySpan<byte> f32Stereo48k)
        {
            if (owns()) audio.Write(f32Stereo48k);
        }
    }
}
