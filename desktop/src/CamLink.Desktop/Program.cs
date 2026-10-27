using Velopack;

namespace CamLink.Desktop;

/// <summary>
/// Giriş noktası. Velopack kurulum/güncelleme kancaları (<c>--veloapp-*</c>) WPF ve tek örnek kilidinden önce
/// çalışmalı: kanca argümanıyla açılışta işini yapıp süreci kapatır. App.xaml bu yüzden Main üretmez (csproj).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
