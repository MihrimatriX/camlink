using System.Diagnostics;
using System.IO.Pipes;

namespace CamLink.Desktop.Core;

public enum Transport { Srt, Rtmp }

public sealed record ReceiverSettings(int Width = 1920, int Height = 1080, int Fps = 30);

public enum ReceiverState { Stopped, Waiting, Receiving, Error }

/// <summary>Çözülmüş kareleri sanal kameraya (paylaşılan belleğe) yazan hedef.</summary>
public interface IFrameSink
{
    /// <summary>NV12 kare: Y düzlemi (w×h) + araya girmiş UV (w×h/2). false = yazılamadı; alıcı yine de kareleri okur.</summary>
    bool WriteNv12(ReadOnlySpan<byte> nv12, int width, int height);
}

/// <summary>Sesi çalan hedef (VB-Cable çıkışı).</summary>
public interface IAudioSink
{
    /// <summary>f32le, 48 kHz, stereo; alıcı 3840 baytlık (10 ms) parçalar verir.</summary>
    void Write(ReadOnlySpan<byte> f32Stereo48k);
}

public static class FfmpegArgs
{
    // Eş bağlanıp sustuysa (ilk kareden önce) FFmpeg'in kendi kapanması için okuma zaman aşımı, mikrosaniye.
    // RTMP -listen 1 ve SRT dinleyicisi bunsuz sonsuza dek bekler (yerelde gözlendi, bkz. rapor Tur 1).
    const string IdleTimeoutUs = "5000000";

    /// <summary>
    /// Alıcı FFmpeg'in argümanları: video NV12 olarak stdout'a (pipe:1), ses f32le olarak adlandırılmış boruya.
    /// <paramref name="audioPipe"/> null ise ses çıkışı hiç eklenmez (yayında ses akışı yoksa
    /// "-map 0:a:0?" çıkışı akışsız bırakır ve FFmpeg "does not contain any stream" ile kapanır).
    /// </summary>
    public static List<string> Build(Transport t, int port, ReceiverSettings s, string? audioPipe)
    {
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-fflags", "nobuffer", "-flags", "low_delay",
            // Akış bilgisi (ses + görüntü) bulunur bulunmaz başla: 1 sn analiz gecikmeyi kalıcı büyütüyordu. probesize ses
            // izinin de bulunması için kalır (32 baytta ses izi bulunmuyor). Yazılım çözücü: -hwaccel auto (DXVA2/D3D11 +
            // kopya) ölçümde bir kare daha geç (2026-10-27, perf-report.md).
            "-probesize", "1000000", "-analyzeduration", "0",
        };
        if (t == Transport.Rtmp) a.AddRange(["-listen", "1", "-rw_timeout", IdleTimeoutUs]);
        a.Add("-i");
        // SRT gecikmesi FFmpeg'de mikrosaniye cinsindendir.
        a.Add(t == Transport.Srt
            ? $"srt://0.0.0.0:{port}?mode=listener&latency=120000&timeout={IdleTimeoutUs}"
            : $"rtmp://0.0.0.0:{port}/camlink");
        // fps filtresi yok: karar için sıradaki kareyi beklediğinden bir kare gecikme ve düşme/çoğaltma patlamaları ekliyordu;
        // tüketiciye kare hızını sanal kamera DLL'i (kare süresinde en çok bir örnek) uygular.
        a.AddRange(["-map", "0:v:0",
            "-vf", $"scale={s.Width}:{s.Height}:force_original_aspect_ratio=decrease,pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "nv12", "-f", "rawvideo", "pipe:1"]);
        // Boru alıcıda önceden açıldığı için FFmpeg onu "dosya var" sayar; -y olmadan "already exists. Exiting." ile çıkar.
        if (audioPipe != null) a.AddRange(["-y", "-map", "0:a:0?", "-ac", "2", "-ar", "48000", "-f", "f32le", audioPipe]);
        return a;
    }
}

/// <summary>
/// Kareler arası boşluk istatistiği, saniyelik kovalarda: son 60 sn'nin en büyük boşluğu ve 100 ms'yi aşan boşluk sayısı.
/// Takılma (donup sıçrama) ölçüsü: 30 fps'te normal boşluk ~33 ms.
/// </summary>
internal sealed class GapStats
{
    const int Window = 60, StallMs = 100;
    readonly long[] _sec = new long[Window];
    readonly int[] _max = new int[Window], _over = new int[Window];

    public void Add(long nowMs, int gapMs)
    {
        var s = nowMs / 1000;
        var i = (int)(s % Window);
        lock (_sec)
        {
            if (_sec[i] != s) (_sec[i], _max[i], _over[i]) = (s, 0, 0);
            if (gapMs > _max[i]) _max[i] = gapMs;
            if (gapMs > StallMs) _over[i]++;
        }
    }

    public (int MaxMs, int Over100) Read(long nowMs)
    {
        var s = nowMs / 1000;
        int max = 0, over = 0;
        lock (_sec)
            for (var i = 0; i < Window; i++)
                if (s - _sec[i] < Window)
                {
                    max = Math.Max(max, _max[i]);
                    over += _over[i];
                }
        return (max, over);
    }
}

/// <summary>
/// Telefondan gelen SRT ya da RTMP yayınını FFmpeg ile çözer; video karelerini <see cref="IFrameSink"/>'e,
/// sesi <see cref="IAudioSink"/>'e aktarır. Telefon bağlantıyı kesince FFmpeg kapanır; alıcı onu
/// <see cref="ReceiverState.Stopped"/> olmadıkça yeniden dinlemeye başlatır.
/// </summary>
public sealed class Receiver : IAsyncDisposable
{
    const int AudioChunk = 3840; // 480 örnek × 2 kanal × 4 bayt = 10 ms (48 kHz)
    const string NoAudioMessage = "does not contain any stream";
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3); // Receiving iken bu kadar kare gelmezse eş ölü sayılır

    readonly string _ffmpeg;
    readonly Transport _transport;
    readonly int _port;
    readonly Func<ReceiverSettings> _settings;
    readonly IFrameSink _sink;
    readonly IAudioSink _audio;
    readonly Queue<string> _log = new();
    // Start/Stop/Restart/Dispose birbirini bekler: iki döngü aynı anda yaşarsa port, boru ve _proc çakışır.
    readonly SemaphoreSlim _gate = new(1, 1);
    CancellationTokenSource? _cts;
    Task? _loop;
    Process? _proc;
    // Bekleyen önizleme isteği; sıradaki kare küçültülmüş NV12 ile tamamlar (bütün bekleyenler aynı kareyi alır).
    TaskCompletionSource<(byte[] Nv12, int W, int H)>? _previewNext;
    bool _disposed;
    volatile ReceiverState _state = ReceiverState.Stopped;
    long _sinceTicks;
    long _lastFrameTicks;
    long _audioBytes;
    volatile bool _noAudio;
    volatile bool _portConflict;
    volatile ReceiverSettings? _active;
    bool _sinkFailed, _audioFailed; // döngü başına; yalnız geçişte LastError yazılır
    string? _sinkError;

    public Receiver(string ffmpegPath, Transport transport, int port, Func<ReceiverSettings> settings, IFrameSink sink, IAudioSink audio)
    {
        _ffmpeg = ffmpegPath;
        _transport = transport;
        _port = port;
        _settings = settings;
        _sink = sink;
        _audio = audio;
    }

    public ReceiverState State { get => _state; internal set => _state = value; }
    public string? LastError { get; internal set; }
    public double Fps { get; private set; }

    /// <summary>Kareler arası boşluk (FFmpeg çıkışında, alıcıya varış): son 60 sn'nin en büyüğü ve 100 ms üstü sayısı.</summary>
    public (int MaxMs, int Over100) Gaps => _gaps.Read(Environment.TickCount64);
    readonly GapStats _gaps = new();
    public long AudioBytes => Interlocked.Read(ref _audioBytes);

    /// <summary>Çalışan FFmpeg döngüsünün çıkış ayarı; döngüler arasında ve durdurulmuşken null.</summary>
    public ReceiverSettings? Active => _active;

    /// <summary>İlk karenin geldiği an; Owns() iki alıcıdan hangisinin önce başladığına buna bakar.</summary>
    public DateTimeOffset? ReceivingSince
    {
        get { var t = Interlocked.Read(ref _sinceTicks); return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero); }
        internal set => Interlocked.Exchange(ref _sinceTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>Windows kabuğu FFmpeg'i bir iş nesnesine bağlar; uygulama çökerse FFmpeg de kapanır.</summary>
    public Action<Process>? ProcessStarted { get; set; }

    /// <summary>false dönerken kareler yine okunur ve sayılır, yalnız <see cref="IFrameSink"/> çağrılmaz.</summary>
    public Func<bool> CanWrite { get; set; } = () => true;

    /// <summary>Yeni FFmpeg döngüsü ses çıkışı açsın mı; döngü başında bir kez okunur. Telefona ses istenmediğini söyleyen
    /// masaüstü sesi beklemez (yoksa ilk döngü "çıkışta akış yok" ile düşer, yayın birkaç saniye geç gelir).</summary>
    public Func<bool> AudioWanted { get; set; } = () => true;

    string PipeName => _transport == Transport.Srt ? "camlink-audio-srt" : "camlink-audio-rtmp";

    /// <summary>İki alıcıdan sanal kameraya yalnız biri yazar: önce görüntü getiren sürer.</summary>
    public static bool Owns(Receiver me, Receiver other) =>
        me.State == ReceiverState.Receiving && (other.State != ReceiverState.Receiving || me.ReceivingSince <= other.ReceivingSince);

    /// <summary>Sürmekte olan StopAsync/RestartAsync bitene dek bekler (kısa sürer).</summary>
    public void Start()
    {
        _gate.Wait();
        try { StartCore(); }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Ayar değişince FFmpeg'i yeni boyutlarla baştan başlatır.</summary>
    public async Task RestartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            StartCore();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    void StartCore()
    {
        if (_loop != null || _disposed) return;
        if (!File.Exists(_ffmpeg))
        {
            State = ReceiverState.Error;
            LastError = "Görüntü bileşeni (FFmpeg) bulunamadı. CamLink'i klasöründeki tüm dosyalarla birlikte çalıştır.";
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    async Task StopCoreAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts != null)
        {
            cts.Cancel();
            KillProc();
            if (loop != null)
            {
                try { await loop.ConfigureAwait(false); } catch { }
            }
            cts.Dispose();
        }
        State = ReceiverState.Stopped;
        ReceivingSince = null;
        Fps = 0;
    }

    public IReadOnlyList<string> RecentLog()
    {
        lock (_log) return _log.ToList();
    }

    void Log(string line)
    {
        lock (_log)
        {
            _log.Enqueue(line);
            while (_log.Count > 20) _log.Dequeue();
        }
    }

    /// <summary>Akıştan tam <c>buf.Length</c> baytlık parçaları okur; yarım kalan sonu atar. Tam parça sayısını döner.</summary>
    internal static async Task<long> PumpFramesAsync(Stream src, byte[] buf, Action<byte[]> onFrame, CancellationToken ct)
    {
        long n = 0;
        try
        {
            while (true)
            {
                await src.ReadExactlyAsync(buf, ct);
                n++;
                onFrame(buf);
            }
        }
        catch (EndOfStreamException)
        {
            // Yayın bitti; yarım kare varsa atıldı.
        }
        return n;
    }

    /// <summary>Geçersiz ayar için Türkçe hata metni, geçerliyse null. Geçersiz değerle FFmpeg hiç başlatılmaz.</summary>
    internal static string? SettingsError(ReceiverSettings s) =>
        s.Width <= 0 || s.Height <= 0 || s.Width % 2 != 0 || s.Height % 2 != 0
            ? $"Geçersiz çözünürlük {s.Width}x{s.Height}: genişlik ve yükseklik pozitif ve çift olmalı."
            : s.Fps is < 1 or > 120 ? $"Geçersiz kare hızı {s.Fps}: 1 ile 120 arasında olmalı." : null;

    /// <summary>
    /// Sonraki döngü sesli mi başlasın. Yayında ses akışı yoksa (telefonda mikrofon kapalı) sessiz;
    /// telefon kare verdikten sonra koparsa tekrar sesli (mikrofon açılmış olabilir); kare gelmeyen
    /// başarısız döngü kararı değiştirmez (sesli/sessiz arasında gidip gelmeyi önler).
    /// </summary>
    internal static bool NextWithAudio(bool current, bool noAudioSeen, long frames) =>
        !noAudioSeen && (frames > 0 || current);

    async Task RunAsync(CancellationToken ct)
    {
        var withAudio = true;
        while (!ct.IsCancellationRequested)
        {
            long frames = 0;
            _noAudio = false;
            _portConflict = false;
            try
            {
                var s = _settings();
                var bad = SettingsError(s);
                if (bad != null)
                {
                    State = ReceiverState.Error;
                    LastError = bad;
                }
                else frames = await RunCycleAsync(s, withAudio, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                State = ReceiverState.Error;
                LastError = e.Message;
            }
            withAudio = NextWithAudio(withAudio, _noAudio, frames);
            // Hata ya da port çakışmasında FFmpeg'i her 400 ms'de yeniden başlatma.
            await Delay(State == ReceiverState.Error || _portConflict ? 3000 : 400, ct);
        }
    }

    /// <summary>FFmpeg'i bir kez başlatır ve çıkana dek kareleri aktarır. Aktarılan kare sayısını döner.</summary>
    async Task<long> RunCycleAsync(ReceiverSettings s, bool withAudio, CancellationToken ct)
    {
        int w = s.Width, h = s.Height;
        var buf = new byte[w * h * 3 / 2];
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        NamedPipeServerStream? pipe = null;
        Process? proc = null;
        Task audioTask = Task.CompletedTask, errTask = Task.CompletedTask, watchdog = Task.CompletedTask;
        long frames = 0;
        _sinkFailed = _audioFailed = false;
        _sinkError = null;
        _active = s;
        withAudio &= AudioWanted();
        try
        {
            // Boru, FFmpeg çıktıyı açmadan önce hazır olmalı.
            if (withAudio)
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                audioTask = PumpAudioAsync(pipe, cycle.Token);
            }
            var psi = new ProcessStartInfo(_ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in FfmpegArgs.Build(_transport, _port, s, withAudio ? @"\\.\pipe\" + PipeName : null)) psi.ArgumentList.Add(a);
            proc = _proc = Process.Start(psi) ?? throw new InvalidOperationException("Görüntü bileşeni (FFmpeg) başlatılamadı");
            State = ReceiverState.Waiting;
            ReceivingSince = null;
            try { ProcessStarted?.Invoke(proc); } catch { }
            var p = proc;
            errTask = Task.Run(() => PumpErrors(p), CancellationToken.None);
            watchdog = WatchdogAsync(cycle.Token);

            var sw = Stopwatch.StartNew();
            var window = 0;
            await PumpFramesAsync(proc.StandardOutput.BaseStream, buf, frame =>
            {
                var now = Stopwatch.GetTimestamp();
                if (frames > 0) _gaps.Add(Environment.TickCount64, (int)Stopwatch.GetElapsedTime(_lastFrameTicks, now).TotalMilliseconds);
                Volatile.Write(ref _lastFrameTicks, now);
                if (frames == 0)
                {
                    // Kare hızı ilk kareden itibaren ölçülür; bekleme süresi hesaba girmez.
                    ReceivingSince = DateTimeOffset.UtcNow;
                    State = ReceiverState.Receiving;
                    // Önceki döngüden kalan hata silinir; bu döngüde ses hatası ilk kareden önce geldiyse korunur.
                    if (!_audioFailed) LastError = null;
                    sw.Restart();
                    window = 0;
                }
                frames++;
                window++;
                WriteFrame(frame, w, h);
                // Önizleme yalnız bekleyen varken: burada yalnız küçültme (kopya); renk dönüşümü bekleyenin iş parçacığında.
                if (Volatile.Read(ref _previewNext) != null && Interlocked.Exchange(ref _previewNext, null) is { } next)
                    next.TrySetResult(Downscale(frame, w, h, PreviewWidth));
                if (sw.ElapsedMilliseconds >= 1000)
                {
                    Fps = Math.Round(window * 1000.0 / sw.ElapsedMilliseconds, 1);
                    window = 0;
                    sw.Restart();
                }
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (proc != null) { } // stdout koptu; boru açılamadıysa aşağıda hata olur
        catch (Exception e)
        {
            State = ReceiverState.Error;
            LastError = e.Message;
        }
        finally
        {
            // FFmpeg kendi kapandıysa son stderr satırları (ör. ses akışı yok) okunabilsin.
            if (!ct.IsCancellationRequested) await Task.WhenAny(errTask, Task.Delay(1500));
            KillProc();
            cycle.Cancel();
            try { await Task.WhenAll(audioTask, errTask, watchdog); } catch { }
            pipe?.Dispose();
            proc?.Dispose();
            _proc = null;
            _active = null;
            Fps = 0;
            if (State == ReceiverState.Receiving) State = ReceiverState.Waiting;
            ReceivingSince = null;
        }
        return frames;
    }

    /// <summary>
    /// Kareyi sink'e verir (CanWrite izin veriyorsa). LastError yalnız başarılıdan başarısıza geçişte yazılır
    /// (30 Hz'de her karede metin üretilmez); başarılı yazım kendi hatasını siler.
    /// </summary>
    internal void WriteFrame(ReadOnlySpan<byte> frame, int w, int h)
    {
        if (!CanWrite()) return;
        bool ok;
        Exception? ex = null;
        try { ok = _sink.WriteNv12(frame, w, h); }
        catch (Exception e) { ok = false; ex = e; }
        if (ok)
        {
            if (!_sinkFailed) return;
            _sinkFailed = false;
            if (LastError == _sinkError) LastError = null;
        }
        else if (!_sinkFailed)
        {
            _sinkFailed = true;
            LastError = _sinkError = "Sanal kameraya yazılamadı" + (ex == null ? "." : ": " + ex.Message);
        }
    }

    /// <summary>
    /// Ses parçasını hedefe verir. Hedef bozulsa da boru boşaltılmalı (yoksa FFmpeg yazarken takılır, görüntü de durur);
    /// döngü başına ilk hata LastError'a yazılır.
    /// </summary>
    internal void OnAudioChunk(byte[] chunk)
    {
        try { _audio.Write(chunk); }
        catch (Exception e)
        {
            if (!_audioFailed)
            {
                _audioFailed = true;
                LastError = "Ses çıkışına yazılamadı: " + e.Message;
            }
        }
        Interlocked.Add(ref _audioBytes, chunk.Length);
    }

    async Task PumpAudioAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            await PumpFramesAsync(pipe, new byte[AudioChunk], OnAudioChunk, ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Receiving iken <see cref="StallTimeout"/> boyunca kare gelmezse FFmpeg'i öldürür: eş kopmuş ama
    /// soket açık kalmış olabilir (RTMP/SRT bunu kendi başına geç ya da hiç anlamaz). Pompa EOF görür,
    /// döngü biter, durum Waiting'e döner ve FFmpeg yeniden dinler.
    /// </summary>
    async Task WatchdogAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (State != ReceiverState.Receiving || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFrameTicks)) < StallTimeout) continue;
                Log("[CamLink] 3 sn kare gelmedi; FFmpeg kapatılıyor, yeniden dinlenecek.");
                KillProc();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Port çakışması satırı; Windows'ta "Address already in use" yerine Winsock kodu yazılır (-10048, -10013).</summary>
    internal static bool IsPortConflict(string line) =>
        line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
        || line.Contains("bind", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error number -10048", StringComparison.Ordinal)
        || line.Contains("Error number -10013", StringComparison.Ordinal);

    void PumpErrors(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) != null)
            {
                Log(line);
                if (IsPortConflict(line))
                {
                    _portConflict = true;
                    LastError = $"Başka bir uygulama CamLink'in kullanacağı bağlantıyı (port {_port}) tutuyor. O uygulamayı kapat.";
                }
                else if (line.Contains(NoAudioMessage, StringComparison.OrdinalIgnoreCase))
                    _noAudio = true;
            }
        }
        catch
        {
        }
    }

    void KillProc()
    {
        var p = _proc;
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            try { p.Kill(); } catch { } // ağaç öldürme olmadıysa en azından FFmpeg'in kendisi
        }
    }

    static async Task Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { }
    }

    const int PreviewWidth = 640;

    /// <summary>
    /// Sıradaki karenin 640 px önizlemesi (24 bit BMP). Kare <paramref name="timeout"/> içinde gelmezse
    /// <see cref="TimeoutException"/>. Kare iş parçacığı yalnız küçültür; BMP dönüşümü burada, çağıranın tarafında yapılır.
    /// </summary>
    public async Task<byte[]> NextPreviewBmpAsync(TimeSpan timeout, CancellationToken ct)
    {
        var mine = new TaskCompletionSource<(byte[] Nv12, int W, int H)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = await (Interlocked.CompareExchange(ref _previewNext, mine, null) ?? mine).Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        return Nv12ToBmp(f.Nv12, f.W, f.H, f.W);
    }

    /// <summary>NV12 kareyi en yakın komşu ile <paramref name="pw"/> genişliğe küçültür (yükseklik orantılı, çift).</summary>
    internal static (byte[] Nv12, int W, int H) Downscale(ReadOnlySpan<byte> nv12, int w, int h, int pw)
    {
        pw = Math.Min(pw, w) & ~1;
        var ph = Math.Max(2, h * pw / w & ~1);
        var o = new byte[pw * ph * 3 / 2];
        for (var y = 0; y < ph; y++)
        {
            var src = nv12.Slice(y * h / ph * w, w);
            for (var x = 0; x < pw; x++) o[y * pw + x] = src[x * w / pw];
        }
        for (var y = 0; y < ph / 2; y++)
        {
            var src = nv12.Slice(w * h + (y * 2 * h / ph >> 1) * w, w);
            var dst = pw * ph + y * pw;
            for (var x = 0; x < pw; x += 2)
            {
                var sx = x * w / pw & ~1;
                o[dst + x] = src[sx];
                o[dst + x + 1] = src[sx + 1];
            }
        }
        return (o, pw, ph);
    }

    /// <summary>
    /// NV12 kareyi <paramref name="pw"/> piksel genişliğe küçültüp 24 bit BMP'ye (yukarıdan aşağı) çevirir.
    /// Renk dönüşümü yalnız önizleme içindir (BT.601, sınırlı aralık); ana yol baştan sona NV12 kalır.
    /// </summary>
    internal static byte[] Nv12ToBmp(ReadOnlySpan<byte> nv12, int w, int h, int pw)
    {
        var ph = Math.Max(1, h * pw / w);
        var rowSize = (pw * 3 + 3) & ~3;
        var dataSize = rowSize * ph;
        var bmp = new byte[54 + dataSize];
        void W16(int o, int v) { bmp[o] = (byte)v; bmp[o + 1] = (byte)(v >> 8); }
        void W32(int o, int v) { W16(o, v); W16(o + 2, v >> 16); }
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        W32(2, bmp.Length);
        W32(10, 54);
        W32(14, 40);
        W32(18, pw);
        W32(22, -ph); // negatif yükseklik: satırlar yukarıdan aşağı
        W16(26, 1);
        W16(28, 24);
        W32(34, dataSize);
        for (var y = 0; y < ph; y++)
        {
            var sy = y * h / ph;
            for (var x = 0; x < pw; x++)
            {
                var sx = x * w / pw;
                var c = 298 * (nv12[sy * w + sx] - 16) + 128;
                var uv = w * h + (sy >> 1) * w + (sx & ~1);
                int u = nv12[uv] - 128, v = nv12[uv + 1] - 128;
                var o = 54 + y * rowSize + x * 3;
                bmp[o] = (byte)Math.Clamp((c + 516 * u) >> 8, 0, 255);
                bmp[o + 1] = (byte)Math.Clamp((c - 100 * u - 208 * v) >> 8, 0, 255);
                bmp[o + 2] = (byte)Math.Clamp((c + 409 * v) >> 8, 0, 255);
            }
        }
        return bmp;
    }
}
