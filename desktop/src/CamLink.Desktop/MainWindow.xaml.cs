using System.ComponentModel;
using System.Windows;

namespace CamLink.Desktop;

public partial class MainWindow : Window
{
    readonly string _url;
    static bool _hintShown;

    public MainWindow(string url)
    {
        _url = url;
        InitializeComponent();
        SourceInitialized += (_, _) => WebShell.DarkTitleBar(this);
        Loaded += async (_, _) =>
        {
            if (!await WebShell.InitAsync(Web, _url))
            {
                Web.Visibility = Visibility.Collapsed;
                Fallback.Visibility = Visibility.Visible;
            }
        };
    }

    // Pencereyi kapatmak uygulamayı kapatmaz: sanal kamera ve telefon bağlantısı tepside sürer.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { IsQuitting: false } app)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                app.Hint("CamLink arka planda çalışıyor", "Sanal kamera açık kalır. Kapatmak için görev çubuğunun sağındaki CamLink simgesine sağ tıkla ve Çık'ı seç.");
            }
        }
        base.OnClosing(e);
    }

    void OpenBrowser_Click(object sender, RoutedEventArgs e) => WebShell.OpenExternal(_url);

    void GetRuntime_Click(object sender, RoutedEventArgs e) => WebShell.OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
}
