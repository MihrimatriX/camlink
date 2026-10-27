using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CamLink.Desktop.Platform;

/// <summary>
/// "CamLink Kamera" sanal kamerası: kurulu DLL'i (<c>C:\Program Files\CamLink\camlink-vcam.dll</c>) yükleyip
/// <c>VcamStart</c>/<c>VcamStop</c>'u çağırır. Kamera oturum ömürlüdür; bu süreç kapanınca Windows kaldırır.
/// DLL mfsensorgroup.dll'e bağlıdır: Windows 10'da yüklenmez, bu yüzden önce <see cref="IsSupported"/> denetlenir.
/// DLL çağrıları ayrı iş parçacığında ve süre sınırıyla yapılır: Windows Kamera Çerçeve Sunucusu çökünce
/// <c>VcamStop</c> geri dönmüyor (gözlendi). Süre dolarsa kamera "yanıt vermiyor" sayılır ve bir daha çağrılmaz;
/// durum okumaları (<see cref="Running"/>, <see cref="Error"/>, <see cref="NeedsUpdate"/>) hiç beklemez.
/// </summary>
public sealed class VirtualCamera
{
    public const string Name = "CamLink Kamera";
    public const string NeedsWindows11 = "Sanal kamera için Windows 11 gerekiyor";
    const string HungMessage = "Sanal kamera yanıt vermiyor. CamLink'i kapatıp yeniden aç.";
    const string ClsidKey = @"SOFTWARE\Classes\CLSID\{8B6175EB-569A-4692-A61C-C449F9F5DE5F}\InprocServer32";
    const int E_FAIL = unchecked((int)0x80004005);
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CamLink");
    public static string InstalledPath => Path.Combine(InstallDir, "camlink-vcam.dll");
    /// <summary>Uygulamayla gelen kopya; kurulum bunu Program Files'a kopyalar.</summary>
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "vcam", "camlink-vcam.dll");

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    readonly object _lock = new(); // DLL çağrıları sırayla; yalnız Invoke'un iş parçacığı tutar
    IntPtr _lib;
    volatile bool _hung;
    volatile bool _running;
    volatile bool _needsUpdate;
    volatile string? _error;

    public bool Running => _running;

    /// <summary>Bir DLL çağrısı süre sınırında dönmedi; kamera bu çalıştırmada bir daha çağrılmaz.</summary>
    public bool Hung => _hung;

    /// <summary>Son başlatma hatası (Türkçe); çalışıyorsa null.</summary>
    public string? Error => _error;

    /// <summary>Kurulu DLL'in <c>VcamVersion()</c>'ı uygulamayla gelenden küçük. DLL ilk yüklenince ölçülür.</summary>
    public bool NeedsUpdate => _needsUpdate;

    /// <summary>HKLM'de CLSID kayıtlı, kayıtlı yol Program Files'taki kopya ve dosya orada.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ClsidKey);
                return key?.GetValue(null) is string path
                    && string.Equals(path, InstalledPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(InstalledPath);
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Kamerayı oluşturup başlatır; HRESULT döner (≥ 0 başarı). Zaten çalışıyorsa DLL S_OK der. En çok 10 sn sürer.</summary>
    public unsafe int Start(string name)
    {
        if (!IsSupported) return Fail(E_FAIL, NeedsWindows11);
        if (!IsInstalled) return Fail(E_FAIL, "Sanal kamera kurulu değil");
        var (hr, error) = Invoke(() =>
        {
            if (_lib == IntPtr.Zero)
            {
                _lib = NativeLibrary.Load(InstalledPath);
                _needsUpdate = InstalledIsOlder(_lib);
            }
            var start = (delegate* unmanaged[Stdcall]<char*, int>)NativeLibrary.GetExport(_lib, "VcamStart");
            fixed (char* p = name) return start(p);
        });
        if (error != null) return Fail(hr, error);
        if (hr < 0) return Fail(hr, $"Sanal kamera başlatılamadı (hata kodu 0x{hr:X8})");
        _running = true;
        _error = null;
        return hr;
    }

    public unsafe void Stop()
    {
        Invoke(() =>
        {
            if (_lib != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<void>)NativeLibrary.GetExport(_lib, "VcamStop"))();
            return 0;
        });
        _running = false;
    }

    /// <summary>Kurulumdan önce: kamerayı durdurur ve DLL'i bırakır ki dosya bu süreçte kilitli kalmasın.</summary>
    public unsafe void Unload()
    {
        Invoke(() =>
        {
            if (_lib == IntPtr.Zero) return 0;
            ((delegate* unmanaged[Stdcall]<void>)NativeLibrary.GetExport(_lib, "VcamStop"))();
            NativeLibrary.Free(_lib);
            _lib = IntPtr.Zero;
            return 0;
        });
        _running = false;
    }

    /// <summary>Program Files'a kopyalar ve kaydeder (UAC). Başarısızlıkta <see cref="Error"/> nedeni söyler; kamera durmuş kalır.</summary>
    public async Task<bool> InstallAsync()
    {
        await Task.Run(Unload).ConfigureAwait(false);
        var (ok, message) = await Elevated.RunAsync(Elevated.Camera).ConfigureAwait(false);
        if (!ok) _error = message;
        return ok;
    }

    int Fail(int hr, string message)
    {
        _running = false;
        _error = message;
        return hr;
    }

    /// <summary>
    /// DLL çağrısını süre sınırıyla yapar. Süre dolarsa (ya da önceki çağrı takılı kaldıysa) kamera bir daha çağrılmaz;
    /// takılan çağrı dönünce kilitte sırada bekleyenler de DLL'e girmez (kilit içinde yeniden bakılır).
    /// </summary>
    (int Hr, string? Error) Invoke(Func<int> call)
    {
        if (_hung) return (E_FAIL, HungMessage);
        var t = Task.Factory.StartNew(() =>
        {
            lock (_lock) return _hung ? (int?)null : call();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (t.Wait(CallTimeout)) return t.Result is int hr ? (hr, null) : (E_FAIL, HungMessage);
        }
        catch (AggregateException e) when (e.InnerException is { } inner)
        {
            return (inner.HResult, Describe(inner));
        }
        // ponytail: takılan iş parçacığı bırakılır (DLL'in kendi kilidini tutuyor); kurtuluş uygulamayı yeniden açmak.
        _hung = true;
        App.Log("Sanal kamera DLL çağrısı 10 sn içinde dönmedi; kamera devre dışı.");
        return (E_FAIL, HungMessage);
    }

    /// <summary>
    /// Yükleme hatası: DLL mfsensorgroup.dll'deki MFCreateVirtualCamera'ya bağlı. O işlev yoksa (Windows 11 öncesi)
    /// spec'teki metin, yoksa ayrıntılı ileti.
    /// </summary>
    static string Describe(Exception e)
    {
        if (e is DllNotFoundException or EntryPointNotFoundException && !HasVirtualCameraApi()) return NeedsWindows11;
        return e is DllNotFoundException or BadImageFormatException ? "Sanal kamera dosyası yüklenemedi: " + e.Message : "Sanal kamera çalıştırılamadı: " + e.Message;
    }

    static bool HasVirtualCameraApi()
    {
        if (!NativeLibrary.TryLoad("mfsensorgroup.dll", out var lib)) return false;
        try { return NativeLibrary.TryGetExport(lib, "MFCreateVirtualCamera", out _); }
        finally { NativeLibrary.Free(lib); }
    }

    /// <summary>Kurulu DLL'in sürümü uygulamayla gelenden küçük mü (gelen kopya kısa süre yüklenip bırakılır).</summary>
    static unsafe bool InstalledIsOlder(IntPtr installed)
    {
        if (!File.Exists(BundledPath)) return false;
        try
        {
            var bundled = NativeLibrary.Load(BundledPath); // tam yol: kurulu kopyayla karışmaz
            try
            {
                static int Version(IntPtr lib) => ((delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(lib, "VcamVersion"))();
                return Version(installed) < Version(bundled);
            }
            finally { NativeLibrary.Free(bundled); }
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
