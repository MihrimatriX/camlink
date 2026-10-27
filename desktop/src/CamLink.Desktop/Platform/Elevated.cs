using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CamLink.Desktop.Core;

namespace CamLink.Desktop.Platform;

/// <summary>
/// Yönetici gerektiren iki iş, tek UAC onayıyla: sanal kamerayı kurmak ve güvenlik duvarına izin eklemek.
/// Uygulama kendini <c>CamLink.Desktop.exe --elevated &lt;iş&gt; &lt;sonuç.json&gt;</c> ile yönetici olarak çalıştırır.
/// Kaynak yol ve portlar istekte taşınmaz, sabittir: yönetici süreci dışarıdan yol almaz.
/// Kaynak: deckpad 48923a0 studiohub/src/StudioHub/Platform/Elevated.cs.
/// </summary>
public static class Elevated
{
    public const string Camera = "camera", Firewall = "firewall";
    const int UacCancelled = 1223;

    sealed record Step(bool Ok, string Message);

    /// <summary>Yönetici sürecinde çalışır; çıkış kodu 0 = başarı. Sonuç ayrıca JSON olarak yazılır.</summary>
    public static int Run(string job, string resultPath)
    {
        Step r;
        try
        {
            r = job switch
            {
                Camera => InstallCamera(),
                Firewall => AddFirewallRules(),
                _ => new(false, "Bilinmeyen iş: " + job),
            };
        }
        catch (Exception e)
        {
            r = new(false, e.Message);
        }
        try { File.WriteAllText(resultPath, JsonSerializer.Serialize(r)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return r.Ok ? 0 : 1;
    }

    /// <summary>Normal süreçten: UAC ister, yönetici sürecinin bitmesini bekler ve sonucunu döner.</summary>
    public static async Task<(bool Ok, string Message)> RunAsync(string job)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CamLink");
        Directory.CreateDirectory(dir);
        var result = Path.Combine(dir, $"sonuc-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--elevated {job} \"{result}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using (var p = Process.Start(psi)!) await p.WaitForExitAsync().ConfigureAwait(false);
            if (!File.Exists(result)) return (false, "İşlemin sonucu alınamadı");
            var r = JsonSerializer.Deserialize<Step>(await File.ReadAllTextAsync(result).ConfigureAwait(false));
            return r == null ? (false, "İşlemin sonucu alınamadı") : (r.Ok, r.Message);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == UacCancelled)
        {
            return (false, "İzin verilmedi, işlem yapılmadı");
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or JsonException)
        {
            return (false, e.Message);
        }
        finally
        {
            try { File.Delete(result); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// DLL Program Files'a kopyalanır (Frame Server LocalService olarak çalışır, kullanıcı profilini okuyamaz)
    /// ve <c>regsvr32 /s</c> ile HKLM'ye kaydedilir.
    /// </summary>
    static Step InstallCamera()
    {
        var src = VirtualCamera.BundledPath;
        if (!File.Exists(src)) return new(false, "Sanal kamera dosyası eksik. CamLink'i yeniden indir.");
        var dst = VirtualCamera.InstalledPath;
        Directory.CreateDirectory(VirtualCamera.InstallDir);
        foreach (var old in Directory.GetFiles(VirtualCamera.InstallDir, "*.old"))
        {
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // hâlâ yüklüyse sonraki kurulumda silinir
        }
        try
        {
            File.Copy(src, dst, overwrite: true);
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(dst))
        {
            // Frame Server eski DLL'i yüklü tutuyor olabilir: yüklü dosyanın üzerine yazılamaz ama adı değiştirilebilir.
            File.Move(dst, $"{dst}.{DateTime.UtcNow.Ticks}.old");
            File.Copy(src, dst);
        }
        var code = Exec(Path.Combine(Environment.SystemDirectory, "regsvr32.exe"), "/s", dst);
        return code == 0 ? new(true, "Sanal kamera kuruldu") : new(false, $"Sanal kamera kurulamadı (hata kodu {code})");
    }

    /// <summary>
    /// Telefonun Wi‑Fi'den ulaşması gereken iki port: TCP 4468 (kontrol) ve UDP 9100 (SRT). USB (RTMP 9101) yerel kalır.
    /// Kurallar yalnız aynı yerel ağa izin verir; Wi‑Fi "Ortak" işaretli olsa da çalışsın diye tüm profillerde geçerli.
    /// </summary>
    static Step AddFirewallRules()
    {
        var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        var errors = new List<string>();
        void Rule(string name, string proto, int port)
        {
            Exec(netsh, "advfirewall", "firewall", "delete", "rule", $"name={name}");
            var code = Exec(netsh, "advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow",
                $"protocol={proto}", $"localport={port}", "remoteip=localsubnet", "profile=any");
            if (code != 0) errors.Add($"{name} için hata kodu {code}");
        }
        Rule("CamLink (TCP)", "TCP", Info.Ctl);
        Rule("CamLink (UDP)", "UDP", Info.Srt);
        return errors.Count == 0 ? new(true, "Güvenlik duvarı izni verildi") : new(false, "Güvenlik duvarı izni verilemedi: " + string.Join(", ", errors));
    }

    static int Exec(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (p.WaitForExit(30000)) return p.ExitCode;
        try { p.Kill(); } catch (InvalidOperationException) { }
        return -1;
    }
}
