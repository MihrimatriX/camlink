using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CamLink.Desktop.Platform;

/// <summary>
/// Telefon görüntüsü yokken sanal kamerada görünen NV12 kare: siyah ekran yerine ne yapılması gerektiğini söyler.
/// Son kare saklanır; durum ya da boyut değişmedikçe yeniden çizilmez.
/// Kaynak: deckpad 48923a0 studiohub/src/StudioHub/Platform/IdleFrames.cs (çıktı RGBA yerine NV12).
/// </summary>
public sealed class IdleFrames
{
    readonly object _lock = new();
    readonly FontFamily _display;
    readonly FontFamily _body;
    string? _key;
    byte[]? _frame;

    public IdleFrames()
    {
        var baseUri = new Uri("pack://application:,,,/");
        _display = new FontFamily(baseUri, "./Assets/#Barlow Semi Condensed");
        // Statik yazı tipinde aile adı "Barlow SemiBold", tipografik aile adı "Barlow"dur; ikisi de denenir.
        _body = new FontFamily(baseUri, "./Assets/#Barlow SemiBold, ./Assets/#Barlow");
    }

    /// <summary><paramref name="w"/>×<paramref name="h"/> NV12 kare (w, h çift).</summary>
    public byte[] Get(int w, int h, string title, string line)
    {
        var key = $"{w}x{h}|{title}|{line}";
        lock (_lock)
        {
            if (key == _key) return _frame!;
            byte[]? frame = null;
            Exception? error = null;
            // WPF çizimi STA iş parçacığı ister; çağıran (arka plan döngüsü) beklerken ayrı iş parçacığında çizilir.
            var t = new Thread(() =>
            {
                try { frame = ToNv12(Render(w, h, title, line), w, h); }
                catch (Exception e) { error = e; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw new InvalidOperationException("Bekleme karesi çizilemedi: " + error.Message, error);
            _key = key;
            return _frame = frame!;
        }
    }

    byte[] Render(int w, int h, string title, string line)
    {
        var k = h / 720.0;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x13, 0x22, 0x4A), Color.FromRgb(0x08, 0x11, 0x2A), 90);
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

            // Ortada kamera simgesi (CamLink simgesiyle aynı çizgi): gövde, mercek, vizör ve kayıt ışığı
            var cx = w / 2.0;
            var cy = h * 0.40;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x05, 0x0C, 0x1F)), null, new Rect(cx - 96 * k, cy - 74 * k, 192 * k, 148 * k), 30 * k, 30 * k);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), null, new Rect(cx - 84 * k, cy - 52 * k, 124 * k, 104 * k), 18 * k, 18 * k);
            var finder = new StreamGeometry();
            using (var g = finder.Open())
            {
                g.BeginFigure(new Point(cx + 38 * k, cy - 14 * k), true, true);
                g.LineTo(new Point(cx + 76 * k, cy - 36 * k), true, false);
                g.LineTo(new Point(cx + 76 * k, cy + 36 * k), true, false);
                g.LineTo(new Point(cx + 38 * k, cy + 14 * k), true, false);
            }
            var lightBlue = new SolidColorBrush(Color.FromRgb(0x57, 0xD2, 0xFF));
            dc.DrawGeometry(lightBlue, null, finder);
            dc.DrawEllipse(lightBlue, new Pen(new SolidColorBrush(Color.FromRgb(0x0B, 0x15, 0x30)), 9 * k), new Point(cx - 22 * k, cy), 25 * k, 25 * k);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x4E)), null, new Point(cx - 68 * k, cy - 36 * k), 7 * k, 7 * k);

            var tr = CultureInfo.GetCultureInfo("tr-TR");
            var t = new FormattedText(title, tr, FlowDirection.LeftToRight,
                new Typeface(_display, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 52 * k,
                new SolidColorBrush(Color.FromRgb(0xDC, 0xE7, 0xFF)), 1.0)
            {
                MaxTextWidth = w * 0.9,
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(t, new Point(cx - w * 0.45, h * 0.60));
            var l = new FormattedText(line, tr, FlowDirection.LeftToRight,
                new Typeface(_body, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 26 * k,
                new SolidColorBrush(Color.FromRgb(0x7F, 0x93, 0xBD)), 1.0)
            {
                MaxTextWidth = w * 0.8,
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(l, new Point(cx - w * 0.4, h * 0.60 + t.Height + 14 * k));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var bgra = new byte[w * h * 4];
        bmp.CopyPixels(bgra, w * 4, 0);
        return bgra;
    }

    /// <summary>
    /// BGRA → NV12 (BT.601, sınırlı aralık; FFmpeg'in alıcıdaki çıktısıyla aynı). Renk, 2×2 bloğun ortalaması.
    /// Görüntü opak olduğu için ön çarpım farkı yok.
    /// </summary>
    internal static byte[] ToNv12(byte[] bgra, int w, int h)
    {
        var nv12 = new byte[w * h * 3 / 2];
        for (var i = 0; i < w * h; i++)
        {
            int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            nv12[i] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
        }
        var uv = w * h;
        for (var y = 0; y < h; y += 2)
        {
            for (var x = 0; x < w; x += 2)
            {
                int p0 = (y * w + x) * 4, p1 = p0 + 4, p2 = p0 + w * 4, p3 = p2 + 4;
                int Avg(int c) => (bgra[p0 + c] + bgra[p1 + c] + bgra[p2 + c] + bgra[p3 + c] + 2) >> 2;
                int b = Avg(0), g = Avg(1), r = Avg(2);
                nv12[uv++] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                nv12[uv++] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
        return nv12;
    }
}
