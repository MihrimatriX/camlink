using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CamLink.Desktop;

/// <summary>
/// Pencerenin WebView2 kurulumu. Arayüz <c>http://127.0.0.1:4468/</c>'ten açılır: yerel API aynı kökenden
/// gelen isteği kabul eder (sanal ana bilgisayar ya da file:// çapraz site sayılır ve reddedilir).
/// Kaynak: deckpad 48923a0 studiohub/src/StudioHub/WebShell.cs.
/// </summary>
internal static class WebShell
{
    static Task<CoreWebView2Environment>? _env;

    static Task<CoreWebView2Environment> Env() => _env ??= CoreWebView2Environment.CreateAsync(
        browserExecutableFolder: null,
        // Uygulama Program Files'ta olsa da yazılabilir bir klasör kullanılır.
        userDataFolder: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CamLink", "WebView2"));

    public static async Task<bool> InitAsync(WebView2 view, string url)
    {
        try
        {
            view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0B, 0x15, 0x30);
            await view.EnsureCoreWebView2Async(await Env());
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or FileNotFoundException or DllNotFoundException)
        {
            App.Log("WebView2 bulunamadı: " + e.Message);
            return false;
        }
        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
        // CamLink dışındaki adresler (ör. VB-Cable indirme sayfası) varsayılan tarayıcıda açılır.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(origin + "/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        view.Source = new Uri(url);
        return true;
    }

    public static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true })?.Dispose();
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Windows 10/11'de başlık çubuğunu koyu yapar; Windows 11'de gövde rengine boyar.</summary>
    public static void DarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var color = 0x30 << 16 | 0x15 << 8 | 0x0B; // COLORREF 0x00BBGGRR, #0B1530
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref color, sizeof(int));
    }
}
