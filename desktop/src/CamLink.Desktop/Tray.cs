using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace CamLink.Desktop;

/// <summary>Bildirim alanı simgesi ve menüsü. Pencere kapalıyken CamLink buradan açılır ya da kapatılır.</summary>
internal sealed class Tray : IDisposable
{
    readonly App _app;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _status;

    public Tray(App app)
    {
        _app = app;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/camlink.ico"))!.Stream;
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        _status = new ToolStripMenuItem("…") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("CamLink'i aç", null, (_, _) => _app.ShowMain()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Çık", null, (_, _) => _app.Quit()));
        menu.Opening += (_, _) => Update();
        _icon = new NotifyIcon
        {
            Icon = new Icon(stream),
            Text = "CamLink",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.ShowMain();
        };
        _icon.MouseMove += (_, _) => Update(); // ipucu görünmeden hemen önce güncel olsun
        Update();
    }

    void Update()
    {
        var h = _app.Host;
        var phone = h.Link.Connected ? "Telefon bağlı" : "Telefon bağlı değil";
        var cam = h.Receiving ? (h.Bus.HasConsumer ? "kamera kullanımda" : "görüntü geliyor") : "görüntü bekleniyor";
        var text = $"{phone}, {cam}";
        _status.Text = text;
        var tip = "CamLink: " + text;
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
