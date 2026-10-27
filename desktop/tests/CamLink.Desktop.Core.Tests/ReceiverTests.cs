using CamLink.Desktop.Core;

namespace CamLink.Desktop.Core.Tests;

public class ReceiverTests
{
    static readonly ReceiverSettings S = new(1920, 1080, 30);

    [Fact] public void Preview_downscale_samples_luma_and_chroma_pairs()
    {
        // 8x4 NV12: Y = 10*satır + sütun; UV satırı r, çift p: U = 100+10r+p, V = 200+10r+p
        const int w = 8, h = 4;
        var f = new byte[w * h * 3 / 2];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) f[y * w + x] = (byte)(10 * y + x);
        for (var r = 0; r < h / 2; r++) for (var p = 0; p < w / 2; p++) { f[w * h + r * w + 2 * p] = (byte)(100 + 10 * r + p); f[w * h + r * w + 2 * p + 1] = (byte)(200 + 10 * r + p); }
        var (o, pw, ph) = Receiver.Downscale(f, w, h, 4);
        Assert.Equal((4, 2), (pw, ph));
        Assert.Equal(new byte[] { 0, 2, 4, 6, 20, 22, 24, 26 }, o[..8]);   // satır 0 ve 2, her ikinci sütun
        Assert.Equal(new byte[] { 100, 200, 102, 202 }, o[8..]);            // UV satırı 0, çift 0 ve 2
    }

    [Fact] public void Gap_stats_keep_last_minute_max_and_stall_count()
    {
        var g = new GapStats();
        const long t0 = 1_000_000_000;
        g.Add(t0, 33);
        g.Add(t0 + 10, 250);
        g.Add(t0 + 5_000, 101);
        g.Add(t0 + 5_001, 100);   // eşik dahil değil
        Assert.Equal((250, 2), g.Read(t0 + 30_000));
        Assert.Equal((101, 1), g.Read(t0 + 60_500));   // ilk saniye pencereden çıktı
        Assert.Equal((0, 0), g.Read(t0 + 120_000));
        g.Add(t0 + 60_000, 40);   // aynı kova yeni saniyede sıfırlanır
        Assert.Equal((40, 0), g.Read(t0 + 65_000));
    }

    [Fact] public void Srt_args_listen_and_output_nv12()
    {
        var a = FfmpegArgs.Build(Transport.Srt, 9100, S, @"\\.\pipe\camlink-audio-srt");
        // Tur 1 kararı (Ruling 10): &timeout= eklendi; ilk kareden önce susan eş 5 sn sonra bırakılır.
        Assert.Equal("srt://0.0.0.0:9100?mode=listener&latency=120000&timeout=5000000", a[a.IndexOf("-i") + 1]);
        Assert.Equal("nv12", a[a.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2", a[a.IndexOf("-vf") + 1]);
        Assert.DoesNotContain("-hwaccel", a);
        Assert.Equal("0", a[a.IndexOf("-analyzeduration") + 1]);
        Assert.Equal(@"\\.\pipe\camlink-audio-srt", a[^1]);
        Assert.DoesNotContain("-listen", a);
    }

    [Fact] public void Rtmp_args_put_listen_before_input()
    {
        var a = FfmpegArgs.Build(Transport.Rtmp, 9101, S, @"\\.\pipe\camlink-audio-rtmp");
        Assert.Equal("1", a[a.IndexOf("-listen") + 1]);
        Assert.True(a.IndexOf("-listen") < a.IndexOf("-i"));
        Assert.Equal("rtmp://0.0.0.0:9101/camlink", a[a.IndexOf("-i") + 1]);
        Assert.Equal(@"\\.\pipe\camlink-audio-rtmp", a[^1]);
    }

    // Tur 1 kararı (Ruling 10): RTMP dinleyicisi boşta kalan eşi -rw_timeout olmadan sonsuza dek bekler.
    [Fact] public void Rtmp_args_have_read_timeout_before_input()
    {
        var a = FfmpegArgs.Build(Transport.Rtmp, 9101, S, null);
        Assert.Equal("5000000", a[a.IndexOf("-rw_timeout") + 1]);
        Assert.True(a.IndexOf("-rw_timeout") < a.IndexOf("-i"));
        Assert.DoesNotContain("-rw_timeout", FfmpegArgs.Build(Transport.Srt, 9100, S, null));
    }

    // Boru alıcıda önceden açılır; FFmpeg onu "dosya var" sayıp çıkmasın diye -y şart (telefonsuz duman testinde yakalandı).
    [Fact] public void Audio_pipe_output_overwrites_existing_pipe()
    {
        var a = FfmpegArgs.Build(Transport.Srt, 9100, S, @"\\.\pipe\camlink-audio-srt");
        Assert.True(a.IndexOf("-y") > a.IndexOf("pipe:1"));
        Assert.True(a.IndexOf("-y") < a.IndexOf("f32le"));
    }

    [Fact] public void Args_without_audio_pipe_omit_audio_output()
    {
        var a = FfmpegArgs.Build(Transport.Srt, 9100, S, null);
        Assert.DoesNotContain("f32le", a);
        Assert.Equal("pipe:1", a[^1]);
    }

    // Windows'taki FFmpeg 9.0.2 port çakışmasını "Address already in use" diye değil, Winsock kodu olarak yazar
    // (SRT/UDP: -10048 WSAEADDRINUSE, RTMP/TCP: -10013 WSAEACCES); satırlar gerçek çıktıdan alındı.
    [Theory]
    [InlineData("[in#0 @ 000001f4997c3980] Error opening input: Error number -10048 occurred", true)]
    [InlineData("[in#0 @ 000001f4997c3980] Error opening input: Error number -10013 occurred", true)]
    [InlineData("bind: Address already in use", true)]
    [InlineData("[out#1/f32le @ 00000233957b4b00] Output file does not contain any stream", false)]
    public void Port_conflict_is_recognised_from_ffmpeg_stderr(string line, bool expected) =>
        Assert.Equal(expected, Receiver.IsPortConflict(line));

    [Fact] public async Task PumpFrames_stops_cleanly_on_partial_frame()
    {
        var frame = 1920 * 1080 * 3 / 2;
        var src = new MemoryStream(new byte[frame * 2 + frame / 2]);
        var n = 0;
        var got = await Receiver.PumpFramesAsync(src, new byte[frame], _ => n++, CancellationToken.None);
        Assert.Equal(2, got);
        Assert.Equal(2, n);
    }

    // BT.601 sınırlı aralık: kırmızı (81,90,240), beyaz (235,128,128), siyah (16,128,128).
    [Theory]
    [InlineData(81, 90, 240, 255, 0, 0)]
    [InlineData(235, 128, 128, 255, 255, 255)]
    [InlineData(16, 128, 128, 0, 0, 0)]
    public void Preview_converts_nv12_to_bt601_limited_rgb(byte y, byte u, byte v, int r, int g, int b)
    {
        const int w = 4, h = 4;
        var nv12 = new byte[w * h * 3 / 2];
        Array.Fill(nv12, y, 0, w * h);
        for (var i = w * h; i < nv12.Length; i += 2) { nv12[i] = u; nv12[i + 1] = v; }
        var bmp = Receiver.Nv12ToBmp(nv12, w, h, w);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.InRange(bmp[56], r - 1, r + 1); // BMP sırası B,G,R
        Assert.InRange(bmp[55], g - 1, g + 1);
        Assert.InRange(bmp[54], b - 1, b + 1);
    }

    // ---- Owns: iki alıcıdan sanal kameraya yazan ----

    [Theory]
    [InlineData(ReceiverState.Receiving, 0, ReceiverState.Waiting, 0, true)]   // diğeri görüntü getirmiyor
    [InlineData(ReceiverState.Waiting, 0, ReceiverState.Receiving, 5, false)]  // ben görüntü getirmiyorum
    [InlineData(ReceiverState.Receiving, 1, ReceiverState.Receiving, 2, true)] // ikisi de getiriyor, ben önce başladım
    [InlineData(ReceiverState.Receiving, 2, ReceiverState.Receiving, 1, false)] // diğeri önce başladı
    public void Owns_goes_to_the_receiver_that_started_first(ReceiverState meState, int meSec, ReceiverState otherState, int otherSec, bool expected)
    {
        var t0 = DateTimeOffset.UtcNow;
        var me = Make();
        var other = Make(Transport.Rtmp);
        me.State = meState;
        me.ReceivingSince = meState == ReceiverState.Receiving ? t0.AddSeconds(meSec) : null;
        other.State = otherState;
        other.ReceivingSince = otherState == ReceiverState.Receiving ? t0.AddSeconds(otherSec) : null;
        Assert.Equal(expected, Receiver.Owns(me, other));
    }

    // ---- NextWithAudio: sesli/sessiz geçişleri (Ruling 3) ----

    [Theory]
    [InlineData(true, true, 0, false)]    // sesli döngü "does not contain any stream" ile bitti: sessize geç
    [InlineData(false, false, 120, true)] // sessiz yayın kare verdi, sonra telefon koptu: sesliye dön
    [InlineData(false, false, 0, false)]  // kare gelmeyen başarısız döngü sessizi sesliye çevirmez (gidip gelme yok)
    [InlineData(true, false, 0, true)]    // kare gelmeyen başarısız döngü sesliyi bozmaz
    [InlineData(false, true, 0, false)]   // sessizde de uyarı görüldüyse sessiz kalır
    public void Next_cycle_audio_choice(bool current, bool noAudioSeen, long frames, bool expected) =>
        Assert.Equal(expected, Receiver.NextWithAudio(current, noAudioSeen, frames));

    // ---- Ayar doğrulama ----

    [Theory]
    [InlineData(1920, 1080, 30, false)]
    [InlineData(2, 2, 1, false)]
    [InlineData(1280, 720, 120, false)]
    [InlineData(0, 1080, 30, true)]
    [InlineData(1920, -2, 30, true)]
    [InlineData(1921, 1080, 30, true)]  // tek sayı: NV12 UV düzlemi tutmaz
    [InlineData(1920, 1081, 30, true)]
    [InlineData(1920, 1080, 0, true)]
    [InlineData(1920, 1080, 121, true)]
    public void Settings_are_validated(int w, int h, int fps, bool invalid)
    {
        var err = Receiver.SettingsError(new ReceiverSettings(w, h, fps));
        Assert.Equal(invalid, err != null);
        if (invalid) Assert.StartsWith("Geçersiz", err);
    }

    [Fact] public async Task Invalid_settings_end_in_error_without_starting_ffmpeg()
    {
        var started = 0;
        var r = Make(FakeFfmpeg, settings: () => new ReceiverSettings(0, 0, 0));
        r.ProcessStarted = _ => Interlocked.Increment(ref started);
        r.Start();
        Assert.True(await WaitUntil(() => r.State == ReceiverState.Error));
        Assert.StartsWith("Geçersiz çözünürlük", r.LastError);
        Assert.Equal(0, started);
        await r.StopAsync(); // 3 sn'lik hata beklemesi iptal edilir
        Assert.Equal(ReceiverState.Stopped, r.State);
    }

    [Fact] public async Task Throwing_settings_end_in_error_not_a_dead_loop()
    {
        var r = Make(FakeFfmpeg, settings: () => throw new InvalidOperationException("ayar okunamadı"));
        r.Start();
        Assert.True(await WaitUntil(() => r.State == ReceiverState.Error));
        Assert.Equal("ayar okunamadı", r.LastError);
        await r.StopAsync();
    }

    // ---- Sink / ses hedefi hataları görünür olur, ama her karede metin üretilmez ----

    [Fact] public void Sink_failure_is_recorded_once_and_cleared_by_a_successful_write()
    {
        var sink = new FakeSink { Ok = false };
        var r = Make(sink: sink);
        var frame = new byte[6];
        r.WriteFrame(frame, 2, 2);
        var first = r.LastError;
        Assert.Equal("Sanal kameraya yazılamadı.", first);
        r.WriteFrame(frame, 2, 2);
        Assert.Same(first, r.LastError); // ikinci başarısızlıkta yeniden üretilmedi
        sink.Ok = true;
        r.WriteFrame(frame, 2, 2);
        Assert.Null(r.LastError);
    }

    [Fact] public void Sink_exception_message_is_kept_and_other_errors_survive_success()
    {
        var sink = new FakeSink { Throw = new IOException("paylaşılan bellek yok") };
        var r = Make(sink: sink);
        r.WriteFrame(new byte[6], 2, 2);
        Assert.Equal("Sanal kameraya yazılamadı: paylaşılan bellek yok", r.LastError);
        sink.Throw = null;
        r.LastError = "başka bir hata"; // ör. ses hatası araya girdi
        r.WriteFrame(new byte[6], 2, 2);
        Assert.Equal("başka bir hata", r.LastError); // başarılı yazım yalnız kendi hatasını siler
    }

    [Fact] public void CanWrite_false_skips_the_sink()
    {
        var sink = new FakeSink();
        var r = Make(sink: sink);
        r.CanWrite = () => false;
        r.WriteFrame(new byte[6], 2, 2);
        Assert.Equal(0, sink.Calls);
    }

    [Fact] public void Audio_sink_failure_is_recorded_once_and_the_pipe_is_still_drained()
    {
        var audio = new FakeAudio { Throw = new InvalidOperationException("aygıt yok") };
        var r = Make(audio: audio);
        r.OnAudioChunk(new byte[3840]);
        var first = r.LastError;
        Assert.Equal("Ses çıkışına yazılamadı: aygıt yok", first);
        r.OnAudioChunk(new byte[3840]);
        Assert.Same(first, r.LastError);
        Assert.Equal(7680, r.AudioBytes); // hata olsa da borudan okunan bayt sayılır
    }

    // ---- Başlat/durdur yeniden girişi ----

    [Fact] public async Task Overlapping_restarts_leave_no_orphan_loop()
    {
        // Sahte "ffmpeg": ping.exe bilinmeyen seçenekle anında çıkar; döngü her turda ProcessStarted'ı tetikler.
        var started = 0;
        var r = Make(FakeFfmpeg, Transport.Rtmp);
        r.ProcessStarted = _ => Interlocked.Increment(ref started);
        r.Start();
        await Task.Delay(300);
        await Task.WhenAll(r.RestartAsync(), r.RestartAsync());
        await r.StopAsync();
        var n = Volatile.Read(ref started);
        await Task.Delay(1500);
        Assert.Equal(ReceiverState.Stopped, r.State);
        Assert.Equal(n, Volatile.Read(ref started)); // yetim döngü olsaydı sayaç artmaya devam ederdi
    }

    // ---- yardımcılar ----

    static readonly string FakeFfmpeg = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    static Receiver Make(Transport t = Transport.Srt, Func<ReceiverSettings>? settings = null, IFrameSink? sink = null, IAudioSink? audio = null) =>
        Make(FakeFfmpeg, t, settings, sink, audio);

    static Receiver Make(string ffmpeg, Transport t = Transport.Srt, Func<ReceiverSettings>? settings = null, IFrameSink? sink = null, IAudioSink? audio = null) =>
        new(ffmpeg, t, t == Transport.Srt ? 9100 : 9101, settings ?? (() => S), sink ?? new FakeSink(), audio ?? new FakeAudio());

    static async Task<bool> WaitUntil(Func<bool> cond, int ms = 3000)
    {
        for (var i = 0; i < ms / 20 && !cond(); i++) await Task.Delay(20);
        return cond();
    }

    sealed class FakeSink : IFrameSink
    {
        public bool Ok = true;
        public Exception? Throw;
        public int Calls;
        public bool WriteNv12(ReadOnlySpan<byte> nv12, int width, int height)
        {
            Calls++;
            if (Throw != null) throw Throw;
            return Ok;
        }
    }

    sealed class FakeAudio : IAudioSink
    {
        public Exception? Throw;
        public void Write(ReadOnlySpan<byte> f32Stereo48k)
        {
            if (Throw != null) throw Throw;
        }
    }
}
