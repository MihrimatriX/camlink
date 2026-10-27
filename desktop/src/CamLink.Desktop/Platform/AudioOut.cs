using System.Buffers;
using System.Runtime.InteropServices;
using CamLink.Desktop.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CamLink.Desktop.Platform;

/// <summary>
/// Telefon sesini VB-Cable'ın "CABLE Input" çıkışına çalar; Windows'ta "CABLE Output" mikrofonu olarak görünür.
/// Aygıt yoksa ses okunup atılır (alıcının borusu boşalmalı). En çok 200 ms tamponlanır; taşarsa eski ses atılır.
/// <see cref="Poll"/> arka plan iş parçacığından çağrılır (COM: MTA) ve aygıtı bulur ya da kopunca yeniden açar.
/// </summary>
public sealed class AudioOut : IAudioSink, IDisposable
{
    const string TargetName = "CABLE Input";
    const int LevelWindow = 48000 * 2 / 10; // 100 ms, stereo örnek sayısı

    readonly object _lock = new();
    MMDevice? _device;
    WasapiOut? _out;
    BufferedWaveProvider? _buffer;
    volatile bool _broken;
    double _sumSquares;
    int _count;
    volatile float _level;
    long _levelTicks;

    /// <summary>Kullanılan çıkış aygıtının adı; VB-Cable yoksa null.</summary>
    public string? DeviceName { get; private set; }

    /// <summary>Son 100 ms'nin RMS seviyesi (0–1); ses gelmiyorsa 0.</summary>
    public float Level => Environment.TickCount64 - Interlocked.Read(ref _levelTicks) < 300 ? _level : 0;

    // Uç nokta kimliği → ad. Ad özellik deposundan okunur ve pahalıdır (bu makinede 3 aygıtta ~0,7 sn CPU, her 5 sn'de);
    // kimlik aynıysa ad da aynıdır. Yalnız Poll kullanır.
    readonly Dictionary<string, string> _names = [];

    /// <summary>
    /// Aygıt yoksa arar; çalma hatayla durduysa kapatıp yeniden açar. Hiç fırlatmaz. Aynı anda tek çağrı (bekleme döngüsü).
    /// Arama ve açılış kilit dışında: <see cref="Write"/> alıcının ses borusundan çağrılır; kilidi beklerse boru dolar,
    /// FFmpeg durur ve görüntü de donar (5 sn'de bir 100–700 ms takılma buydu).
    /// </summary>
    public void Poll()
    {
        lock (_lock)
        {
            if (_out != null && !_broken) return;
            Close();
        }
        MMDevice? device = null;
        WasapiOut? output = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            string? name = null;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (!_names.TryGetValue(d.ID, out var n)) _names[d.ID] = n = d.FriendlyName;
                if (device == null && n.Contains(TargetName, StringComparison.OrdinalIgnoreCase)) (device, name) = (d, n);
                else d.Dispose();
            }
            if (device == null) return;
            var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
            };
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 20);
            output.PlaybackStopped += (_, e) => { if (e.Exception != null) _broken = true; };
            output.Init(buffer);
            output.Play();
            // Alanlar yalnız başarıdan sonra atanır: yarım kalan açılış Poll'u sonsuza dek "açık" sanmasın.
            lock (_lock) (_device, _out, _buffer, DeviceName) = (device, output, buffer, name);
        }
        catch (Exception e) // aygıt sürücüsü her türlü hata verebilir; ses görüntüyü durdurmamalı
        {
            App.Log("Ses çıkışı açılamadı: " + e.Message);
            try { output?.Dispose(); } catch (Exception) { }
            device?.Dispose();
        }
    }

    public void Write(ReadOnlySpan<byte> f32Stereo48k)
    {
        Measure(MemoryMarshal.Cast<byte, float>(f32Stereo48k));
        lock (_lock)
        {
            if (_buffer == null || _broken) return;
            var tmp = ArrayPool<byte>.Shared.Rent(f32Stereo48k.Length);
            f32Stereo48k.CopyTo(tmp);
            _buffer.AddSamples(tmp, 0, f32Stereo48k.Length);
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    // Alıcılar sırayla yazar (yalnız sahibi geçer); yarış olsa bile yalnız seviye göstergesi etkilenir.
    void Measure(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) _sumSquares += s * s;
        _count += samples.Length;
        if (_count < LevelWindow) return;
        _level = (float)Math.Sqrt(_sumSquares / _count);
        _sumSquares = 0;
        _count = 0;
        Interlocked.Exchange(ref _levelTicks, Environment.TickCount64);
    }

    void Close()
    {
        try { _out?.Dispose(); } catch (COMException) { }
        _device?.Dispose();
        _out = null;
        _device = null;
        _buffer = null;
        _broken = false;
        DeviceName = null;
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }
}
