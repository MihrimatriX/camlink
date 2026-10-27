using System.Diagnostics;
using CamLink.Desktop.Core;

namespace CamLink.Desktop.Platform;

/// <summary>
/// USB ile bağlı telefonlar için 2 sn'de bir <c>adb devices</c>; <c>device</c> durumundaki her yeni seri no için
/// <c>adb reverse tcp:4468</c> ve <c>tcp:9101</c> (telefon 127.0.0.1'e bağlanınca bilgisayara ulaşır).
/// Cihaz düşüp geri gelince yeniden kurulur. adb uygulamayla gelir (SDK'daki sürümle aynı olmalı:
/// iki farklı adb birbirinin sunucusunu kapatır).
/// </summary>
public sealed class UsbBridge : IAsyncDisposable
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    readonly string _adb;
    readonly HashSet<string> _reversed = []; // yalnız döngü iş parçacığında
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    volatile IReadOnlyList<(string Serial, string State)> _devices = [];

    public UsbBridge(string adbPath) => _adb = adbPath;

    /// <summary>adb uygulama klasöründe var ve son çalıştırma başarılı.</summary>
    public bool Available { get; private set; }

    public IReadOnlyList<(string Serial, string State)> Devices => _devices;

    /// <summary>Son adb hatası (ör. reverse kurulamadı); yoksa null.</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        Available = File.Exists(_adb);
        if (Available) _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Available = false;
                LastError = "adb çalıştırılamadı: " + e.Message;
                _devices = [];
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    async Task PollAsync(CancellationToken ct)
    {
        var (code, output) = await AdbAsync(["devices"], ct);
        if (code != 0) throw new InvalidOperationException($"adb devices çıkış kodu {code}");
        var devices = Adb.ParseDevices(output);
        _devices = devices;
        Available = true;
        _reversed.IntersectWith(devices.Where(d => d.State == "device").Select(d => d.Serial)); // düşen cihaz geri gelince yeniden kurulur
        // Hata yalnız bu turu anlatır: adb toparlanınca, cihaz kalmayınca ya da reverse tutunca kendiliğinden silinir.
        string? error = null;
        foreach (var (serial, state) in devices)
        {
            if (state != "device" || _reversed.Contains(serial)) continue;
            var ok = true;
            foreach (var port in new[] { Info.Ctl, Info.Rtmp })
            {
                var (rc, text) = await AdbAsync(Adb.ReverseArgs(serial, port), ct);
                if (rc == 0) continue;
                ok = false;
                error = $"adb reverse tcp:{port} kurulamadı ({serial}): {text.Trim()}";
            }
            if (ok) _reversed.Add(serial);
        }
        LastError = error;
    }

    async Task<(int Code, string Output)> AdbAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_adb)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("adb başlatılamadı");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "zaman aşımı");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
