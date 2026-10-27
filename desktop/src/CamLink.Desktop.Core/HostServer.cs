using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace CamLink.Desktop.Core;

/// <summary>Portlar ve duyuru içeriği. <c>/info</c> ile UDP duyurusu aynı nesneyi yollar.</summary>
public static class Info
{
    public const int Ctl = 4468, Srt = 9100, Rtmp = 9101;

    public static object Payload(string pcName, bool busy) =>
        new { app = "camlink-desktop", v = 1, name = pcName, srt = Srt, rtmp = Rtmp, ctl = Ctl, busy };
}

/// <summary>
/// Tek porttan (TCP 4468) telefonun WebSocket'ini (<c>/phone</c>), duyuru bilgisini (<c>/info</c>),
/// masaüstü arayüzünü (<c>/</c>) ve yerel API'yi (<c>/api/*</c>) sunar.
/// <c>/info</c> ve <c>/phone</c> ağdan erişilebilir; geri kalan her şey yalnız bu bilgisayardan.
/// </summary>
public static class HostServer
{
    /// <summary>Döndürülen uygulama başlatılmamıştır; çağıran <c>StartAsync</c> eder. <paramref name="port"/> 0 olabilir (testler).</summary>
    /// <param name="nextPreview">Sıradaki karenin önizleme BMP'si; görüntü yoksa ya da kısa sürede kare gelmezse null.</param>
    public static WebApplication Build(int port, string webRoot, PhoneLink link, IDesktopPlatform platform, Func<string> pcName,
        Func<CancellationToken, Task<byte[]?>> nextPreview)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(port);
            k.Limits.MaxRequestBodySize = 1024 * 1024; // API gövdeleri küçüktür
        });
        var app = builder.Build();

        // DNS yeniden bağlama saldırılarına karşı: Host başlığı yalnızca IP ya da localhost olabilir.
        app.Use(async (c, next) =>
        {
            var host = c.Request.Host.Host;
            if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host.Trim('[', ']'), out _)))
            {
                await Deny(c, 421, "Geçersiz Host başlığı: yalnızca IP adresi ya da localhost.");
                return;
            }
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            // Yanıt vermeyen (örn. Wi-Fi'den düşmüş) telefon yuvayı sonsuza dek tutmasın.
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });

        app.Use(async (c, next) =>
        {
            var path = c.Request.Path;
            var open = path.Equals("/info", StringComparison.OrdinalIgnoreCase) || path.Equals("/phone", StringComparison.OrdinalIgnoreCase);
            if (!open && !IsLocal(c))
            {
                await Deny(c, 403, "Bu sayfa yalnızca bilgisayarın kendisinden açılabilir.");
                return;
            }
            // Tarayıcıdaki başka bir sitenin API'ye istek yollamasını ya da /phone yuvasını almasını engeller
            // (arayüz aynı kökenden açılır; okhttp ve ClientWebSocket gibi yerel istemciler bu başlığı yollamaz).
            var site = c.Request.Headers["Sec-Fetch-Site"];
            if ((path.StartsWithSegments("/api") || path.Equals("/phone", StringComparison.OrdinalIgnoreCase)) && (site == "cross-site" || site == "same-site"))
            {
                await Deny(c, 403, "Başka bir siteden gelen istek reddedildi.");
                return;
            }
            await next();
        });

        if (Directory.Exists(webRoot))
        {
            var files = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
            });
        }
        else
        {
            app.MapGet("/{**rest}", () => Results.Text($"Arayüz dosyaları bulunamadı: {webRoot}", PlainText, statusCode: 500));
        }

        app.MapGet("/info", () => Results.Json(Info.Payload(pcName(), link.Connected)));

        var stopping = app.Lifetime.ApplicationStopping;
        app.MapGet("/phone", async (HttpContext c) =>
        {
            if (!c.WebSockets.IsWebSocketRequest)
            {
                c.Response.StatusCode = 400;
                return;
            }
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            // Uygulama kapanırken bağlantı beklemeden bırakılsın.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, stopping);
            await link.HandleAsync(ws, cts.Token);
        });

        var api = app.MapGroup("/api");

        api.MapGet("/status", () =>
        {
            var p = link.Snapshot();
            return Results.Json(new
            {
                platform = platform.Status(),
                phone = new { connected = p.Connected, name = p.Name, caps = p.Caps, state = p.State },
            });
        });

        // Komutları doğrulayan telefondur; burası yalnız iletir.
        api.MapPost("/phone/set", async (HttpContext c) =>
        {
            var body = await ReadObjectAsync(c);
            if (body?["key"] is not JsonValue kv || !kv.TryGetValue<string>(out var key) || key.Length == 0)
                return Results.BadRequest(new { ok = false, message = "key gerekli" });
            return Results.Json(new { ok = await link.SetAsync(key, body["value"]) });
        });
        api.MapPost("/phone/start", async () => Results.Json(new { ok = await link.SendAsync(new JsonObject { ["t"] = "start" }) }));
        api.MapPost("/phone/stop", async () => Results.Json(new { ok = await link.SendAsync(new JsonObject { ["t"] = "stop" }) }));

        // Önizleme: her yeni kare (BMP) ikili ileti olarak itilir. Gönderim beklenir (yolda en çok bir kare); sıradaki kare
        // gönderim sürerken istenir ki kare atlanmasın. Sayfa gizlenince ya da görüntü bitince istemci yuvayı kapatır;
        // kapanış gönderim hatası ya da RequestAborted ile anlaşılır.
        api.MapGet("/preview-ws", async (HttpContext c) =>
        {
            if (!c.WebSockets.IsWebSocketRequest)
            {
                c.Response.StatusCode = 400;
                return;
            }
            using var ws = await c.WebSockets.AcceptWebSocketAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, stopping);
            try
            {
                var next = nextPreview(cts.Token);
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var bmp = await next;
                    next = nextPreview(cts.Token);
                    if (bmp == null) await Task.Delay(250, cts.Token);
                    else await ws.SendAsync(bmp, System.Net.WebSockets.WebSocketMessageType.Binary, true, cts.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
        });

        api.MapGet("/qr.svg", () =>
        {
            var lan = NetInfo.LanAddresses().FirstOrDefault();
            if (lan == null)
                return Results.Text("Ağ adresi bulunamadı. Wi-Fi ya da kablo bağlantısını kontrol et.", PlainText, statusCode: 503);
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(PairUri(lan.Address, pcName()), QRCodeGenerator.ECCLevel.M);
            return Results.Text(new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true), "image/svg+xml");
        });

        api.MapPost("/camera/install", async () => Results.Json(await platform.InstallCameraAsync()));
        api.MapPost("/firewall", async () => Results.Json(await platform.FirewallAsync()));
        api.MapPost("/settings", async (HttpContext c) =>
        {
            var patch = await ReadObjectAsync(c);
            return patch == null
                ? Results.BadRequest(new { ok = false, message = "JSON nesnesi okunamadı" })
                : Results.Json(await platform.SaveSettingsAsync(patch));
        });

        return app;
    }

    /// <summary>Yalnız bu bilgisayardan gelen istekler: 127.0.0.0/8, ::1, ::ffff:127.x.x.x.</summary>
    internal static bool IsLocal(HttpContext c) => NetInfo.IsLoopback(c.Connection.RemoteIpAddress);

    /// <summary>QR ve eşleştirme bağlantısı: <c>camlink://pair?host=IP&amp;name=PC&amp;ctl=4468&amp;srt=9100</c>. Ad yüzde-kodlanır.</summary>
    internal static string PairUri(string ip, string name) =>
        $"camlink://pair?host={ip}&name={Uri.EscapeDataString(name)}&ctl={Info.Ctl}&srt={Info.Srt}";

    const string PlainText = "text/plain; charset=utf-8";

    static Task Deny(HttpContext c, int status, string message)
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = PlainText;
        return c.Response.WriteAsync(message);
    }

    // Gövde JSON nesnesi değilse (ya da yinelenen anahtar içeriyorsa) null; Content-Type application/json olmalı.
    static async Task<JsonObject?> ReadObjectAsync(HttpContext c)
    {
        try
        {
            var body = await c.Request.ReadFromJsonAsync<JsonObject>();
            PhoneLink.Materialize(body); // yinelenen anahtar sonradan erişimde 500 vermesin, burada yakalansın
            return body;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }
}
