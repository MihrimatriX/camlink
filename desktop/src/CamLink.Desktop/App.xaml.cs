using System.Diagnostics;
using System.Windows;
using CamLink.Desktop.Core;
using CamLink.Desktop.Platform;

namespace CamLink.Desktop;

/// <summary>
/// Giriş noktası. Tek kopya çalışır (ikinci yazar paylaşılan belleğin seqlock'unu bozar, portlar çakışır):
/// ikinci açılış var olan pencereyi öne getirir, <c>--quit</c> ile açılış onu kapatır.
/// <c>--elevated</c> ile açılış yalnız yönetici işini yapıp çıkar. <c>--minimized</c>: pencere açılmaz, tepside başlar.
/// </summary>
public partial class App : Application
{
    // Global: paylaşılan bellek (Global\CamLinkVcam) ve portlar oturumlar arası tek; ikinci oturum ikinci yazar olmamalı.
    const string MutexName = @"Global\CamLink.Desktop";
    const string ShowEventName = @"Local\CamLink.Desktop.Goster";
    const string QuitEventName = @"Local\CamLink.Desktop.Kapat";

    Mutex? _single;
    AppHost? _host;
    Tray? _tray;
    MainWindow? _main;

    public static string DataDir { get; } = Path.GetDirectoryName(DesktopSettings.DefaultPath)!;

    public AppHost Host => _host!;

    public bool IsQuitting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 3 && e.Args[0] == "--elevated")
        {
            Shutdown(Elevated.Run(e.Args[1], e.Args[2]));
            return;
        }

        var quit = e.Args.Contains("--quit");
        bool first;
        try { _single = new Mutex(true, MutexName, out first); }
        catch (UnauthorizedAccessException) { first = false; } // başka kullanıcının oturumunda açık (DACL açmaya izin vermiyor)
        if (!first || quit)
        {
            // Zaten açıksa var olan kopyaya haber ver: pencereyi göster ya da kapan. Olaylar oturuma özel (Local).
            var signalled = Signal(quit ? QuitEventName : ShowEventName);
            if (first) _single!.ReleaseMutex();
            else if (!signalled && !quit)
                MessageBox.Show("CamLink bu bilgisayarda başka bir oturumda açık. Sanal kamerayı aynı anda yalnız bir oturum kullanabilir.",
                    "CamLink", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        Listen(ShowEventName, ShowMain);
        Listen(QuitEventName, Quit);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log("Beklenmeyen hata: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log("Beklenmeyen hata: " + ex.ExceptionObject);

        _host = new AppHost();
        try
        {
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            Log("Başlatılamadı: " + ex);
            MessageBox.Show(ex.Message, "CamLink başlatılamadı", MessageBoxButton.OK, MessageBoxImage.Error);
            try { await _host.DisposeAsync(); } catch (Exception dex) { Log("Kapanış: " + dex.Message); }
            _single?.ReleaseMutex();
            Shutdown(1);
            return;
        }

        _tray = new Tray(this);
        if (!e.Args.Contains("--minimized")) ShowMain();
        _ = Updater.CheckAsync(); // 10 sn sonra, arka planda; yalnız Setup ile kurulu kopyada
    }

    static bool Signal(string name)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(name);
            return ev.Set();
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adlandırılmış olay gelince <paramref name="action"/>'ı arayüz iş parçacığında çalıştırır.</summary>
    void Listen(string name, Action action)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.BeginInvoke(action);
        }) { IsBackground = true }.Start();
    }

    public void ShowMain()
    {
        if (_host == null || IsQuitting) return;
        if (_main == null)
        {
            _main = new MainWindow($"http://127.0.0.1:{Info.Ctl}/");
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void Hint(string title, string text) => _tray?.Balloon(title, text);

    /// <summary>Tepsideki "Çık": sanal kamera kalkar, alıcılar ve FFmpeg durur, paylaşılan bellek bırakılır.</summary>
    public async void Quit()
    {
        if (IsQuitting || _host == null) return;
        IsQuitting = true;
        _tray?.Dispose();
        _tray = null;
        _main?.Close();
        try { await _host.DisposeAsync(); }
        catch (Exception ex) { Log("Kapanış: " + ex); }
        _single?.ReleaseMutex();
        Updater.ApplyOnExit();
        Shutdown();
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "camlink.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Debug.WriteLine(line);
    }
}
