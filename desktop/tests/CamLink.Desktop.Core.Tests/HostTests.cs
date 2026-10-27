using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CamLink.Desktop.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CamLink.Desktop.Core.Tests;

public class HostTests
{
    sealed class FakePlatform : IDesktopPlatform
    {
        public object Status() => new { camera = "yok" };
        public Task<object> InstallCameraAsync() => Task.FromResult<object>(new { ok = true });
        public Task<object> FirewallAsync() => Task.FromResult<object>(new { ok = true });
        public Task<object> SaveSettingsAsync(JsonObject patch) => Task.FromResult<object>(new { ok = true, patch });
    }

    // Gerçek Kestrel, rastgele port.
    sealed class Rig : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required PhoneLink Link { get; init; }
        public required int Port { get; init; }
        public required string Root { get; init; }
        public string Http(string path) => $"http://127.0.0.1:{Port}{path}";
        public string Ws => $"ws://127.0.0.1:{Port}/phone";

        public static async Task<Rig> StartAsync(TimeSpan? helloTimeout = null, TimeSpan? staleAfter = null)
        {
            var root = Directory.CreateTempSubdirectory("camlink-web").FullName;
            File.WriteAllText(Path.Combine(root, "index.html"), "<h1>CamLink</h1>");
            var link = new PhoneLink(() => "PC");
            if (helloTimeout is { } t) link.HelloTimeout = t;
            if (staleAfter is { } s) link.StaleAfter = s;
            var app = HostServer.Build(0, root, link, new FakePlatform(), () => "PC", _ => Task.FromResult<byte[]?>(null));
            await app.StartAsync();
            var port = new Uri(app.Urls.First().Replace("[::]", "127.0.0.1")).Port;
            return new Rig { App = app, Link = link, Port = port, Root = root };
        }

        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await App.StopAsync(cts.Token);
            await App.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }

    static CancellationToken T() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    static async Task<ClientWebSocket> ConnectAsync(Rig rig)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(rig.Ws), T());
        return ws;
    }

    static Task SendAsync(ClientWebSocket ws, string text, CancellationToken? ct = null) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct ?? T());

    static async Task<(WebSocketReceiveResult R, string Text)> ReceiveRawAsync(ClientWebSocket ws)
    {
        var buf = new byte[8192];
        var r = await ws.ReceiveAsync(buf, T());
        // Gerçek istemciler gibi kapanışa yanıt verir; yoksa sunucu yanıtı 2 sn bekler.
        if (r.MessageType == WebSocketMessageType.Close) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", T());
        return (r, Encoding.UTF8.GetString(buf, 0, r.Count));
    }

    static async Task<JsonObject> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var (r, text) = await ReceiveRawAsync(ws);
        Assert.Equal(WebSocketMessageType.Text, r.MessageType);
        return JsonNode.Parse(text)!.AsObject();
    }

    // Bağlanır, hello yollar, welcome'u okur.
    static async Task<ClientWebSocket> PhoneAsync(Rig rig, string name = "Pixel 8")
    {
        var ws = await ConnectAsync(rig);
        await SendAsync(ws, $$"""{"t":"hello","v":1,"app":"camlink","name":"{{name}}"}""");
        var welcome = await ReceiveJsonAsync(ws);
        Assert.Equal("welcome", (string?)welcome["t"]);
        return ws;
    }

    static async Task WaitUntilAsync(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < until, "Koşul 5 sn içinde sağlanmadı");
            await Task.Delay(20);
        }
    }

    static async Task<JsonObject> PostAsync(HttpClient h, string url, object? body = null)
    {
        var r = body == null ? await h.PostAsync(url, null) : await h.PostAsJsonAsync(url, body);
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
    }

    [Fact] public async Task Info_returns_payload_with_ports()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        var j = JsonNode.Parse(await h.GetStringAsync(rig.Http("/info")))!;
        Assert.Equal("camlink-desktop", (string?)j["app"]);
        Assert.Equal(4468, (int?)j["ctl"]);
        Assert.Equal(9100, (int?)j["srt"]);
        Assert.Equal(9101, (int?)j["rtmp"]);
        Assert.Equal("PC", (string?)j["name"]);
        Assert.False((bool?)j["busy"]);
    }

    [Fact] public async Task Hello_gets_welcome_and_link_connected()
    {
        await using var rig = await Rig.StartAsync();
        using var ws = await ConnectAsync(rig);
        await SendAsync(ws, """{"t":"hello","v":1,"app":"camlink","name":"Pixel 8"}""");
        var welcome = await ReceiveJsonAsync(ws);
        Assert.Equal("welcome", (string?)welcome["t"]);
        Assert.Equal(1, (int?)welcome["v"]);
        Assert.Equal("PC", (string?)welcome["name"]);
        Assert.True((bool?)welcome["audio"]);   // varsayılan: ses istenir
        Assert.True(rig.Link.Connected);
        Assert.Equal("Pixel 8", rig.Link.PhoneName);
    }

    [Fact] public async Task Welcome_says_no_audio_when_desktop_has_no_audio_sink()
    {
        await using var rig = await Rig.StartAsync();
        rig.Link.AudioWanted = () => false;
        using var ws = await ConnectAsync(rig);
        await SendAsync(ws, """{"t":"hello","v":1,"app":"camlink","name":"Pixel 8"}""");
        var welcome = await ReceiveJsonAsync(ws);
        Assert.False((bool?)welcome["audio"]);
    }

    [Fact] public async Task Second_phone_is_closed_with_4009()
    {
        await using var rig = await Rig.StartAsync();
        using var a = await PhoneAsync(rig, "A");
        using var b = await ConnectAsync(rig);
        await SendAsync(b, """{"t":"hello","v":1,"app":"camlink","name":"B"}""");
        var (r, _) = await ReceiveRawAsync(b);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal((WebSocketCloseStatus)4009, r.CloseStatus);
        Assert.Equal("A", rig.Link.PhoneName);
    }

    [Fact] public async Task Garbage_message_is_ignored_and_connection_stays()
    {
        await using var rig = await Rig.StartAsync();
        using var ws = await PhoneAsync(rig);
        foreach (var junk in new[] { "{{nope", """{"t":"bilinmeyen"}""", "[1,2]", "42", """{"t":5}""", """{"t":"ping","t":"x"}""", """{"t":"caps","t":"x"}""", """{"t":"caps","a":{"b":1,"b":2}}""" })
            await SendAsync(ws, junk);
        await ws.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, T());
        await SendAsync(ws, """{"t":"ping"}""");
        Assert.Equal("pong", (string?)(await ReceiveJsonAsync(ws))["t"]);
        Assert.True(rig.Link.Connected);
        Assert.Null(rig.Link.Caps); // yinelenen anahtarlı caps saklanmadı
    }

    [Fact] public async Task Caps_and_state_are_stored()
    {
        await using var rig = await Rig.StartAsync();
        var changed = 0;
        rig.Link.Changed += () => Interlocked.Increment(ref changed);
        using var ws = await PhoneAsync(rig);
        var before = changed;
        await SendAsync(ws, """{"t":"caps","torch":true,"qualities":["720p30"]}""");
        await SendAsync(ws, """{"t":"state","zoom":1.5,"streaming":false}""");
        await SendAsync(ws, """{"t":"ping"}""");
        await ReceiveJsonAsync(ws); // pong: önceki iki mesaj işlendi
        Assert.True((bool?)rig.Link.Caps!["torch"]);
        Assert.Equal(1.5, (double?)rig.Link.State!["zoom"]);
        Assert.True(changed - before >= 2);
    }

    [Fact] public async Task Set_reaches_phone()
    {
        await using var rig = await Rig.StartAsync();
        using var ws = await PhoneAsync(rig);
        Assert.True(await rig.Link.SetAsync("zoom", 2.0));
        var m = await ReceiveJsonAsync(ws);
        Assert.Equal("set", (string?)m["t"]);
        Assert.Equal("zoom", (string?)m["key"]);
        Assert.Equal(2.0, (double?)m["value"]);
    }

    [Fact] public async Task Quality_requests_are_recorded_at_SetAsync_and_dropped_with_the_connection()
    {
        await using var rig = await Rig.StartAsync();
        Assert.False(await rig.Link.SetAsync("quality", "720p30"));   // telefon yok: gönderilemedi, kayıt kalmaz
        Assert.Null(rig.Link.LastQualityRequest);

        var session = rig.Link.Session;
        using (var ws = await PhoneAsync(rig))
        {
            Assert.Equal(session + 1, rig.Link.Session);
            Assert.True(await rig.Link.SetAsync("zoom", 2.0));
            Assert.Null(rig.Link.LastQualityRequest);                   // başka anahtar kayıt etmez

            var before = Environment.TickCount64;
            Assert.True(await rig.Link.SetAsync("quality", "1080p60"));
            var r = rig.Link.LastQualityRequest!;
            Assert.Equal("1080p60", r.Id);
            Assert.InRange(r.At, before, Environment.TickCount64);

            // arayüzden elle seçim de aynı noktadan geçer
            using var h = new HttpClient();
            Assert.True((bool?)(await PostAsync(h, rig.Http("/api/phone/set"), new { key = "quality", value = "720p30" }))["ok"]);
            Assert.Equal("720p30", rig.Link.LastQualityRequest!.Id);

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", T());
        }
        await WaitUntilAsync(() => !rig.Link.Connected);
        Assert.Null(rig.Link.LastQualityRequest);                       // bir sonraki telefon eski isteği devralmaz

        using var again = await PhoneAsync(rig, "Yeni");
        Assert.Equal(session + 2, rig.Link.Session);
    }

    [Fact] public void Api_is_local_only()
    {
        static HttpContext Ctx(string? ip)
        {
            var c = new DefaultHttpContext();
            if (ip != null) c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            return c;
        }
        Assert.False(HostServer.IsLocal(Ctx("192.168.1.5")));
        Assert.False(HostServer.IsLocal(Ctx("::ffff:192.168.1.5")));
        Assert.False(HostServer.IsLocal(Ctx(null)));
        Assert.True(HostServer.IsLocal(Ctx("127.0.0.1")));
        Assert.True(HostServer.IsLocal(Ctx("127.0.0.2")));
        Assert.True(HostServer.IsLocal(Ctx("::1")));
        Assert.True(HostServer.IsLocal(Ctx("::ffff:127.0.0.1")));
    }

    // ---- Brief dışı: şartnamedeki davranışları sabitler ----

    [Fact] public async Task Host_header_that_is_not_ip_or_localhost_gets_421()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        foreach (var (host, code) in new[] { ("evil.example", 421), ("evil.example:4468", 421), ("localhost", 200), ("192.168.1.5:4468", 200), ("[::1]:4468", 200) })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, rig.Http("/info"));
            req.Headers.Host = host;
            using var res = await h.SendAsync(req);
            Assert.Equal(code, (int)res.StatusCode);
            if (code == 421) Assert.Equal("text/plain; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        }
    }

    [Fact] public async Task Oversized_message_closes_with_1009()
    {
        await using var rig = await Rig.StartAsync();
        using var ws = await PhoneAsync(rig);
        await SendAsync(ws, new string('x', 70 * 1024));
        var (r, _) = await ReceiveRawAsync(ws);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, r.CloseStatus);
        await WaitUntilAsync(() => !rig.Link.Connected);
    }

    [Fact] public async Task Missing_hello_closes_with_1008()
    {
        await using var rig = await Rig.StartAsync(helloTimeout: TimeSpan.FromMilliseconds(300));
        using var ws = await ConnectAsync(rig);
        var (r, _) = await ReceiveRawAsync(ws);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, r.CloseStatus);
        Assert.False(rig.Link.Connected);
    }

    [Fact] public async Task Disconnect_clears_state_and_frees_the_slot()
    {
        await using var rig = await Rig.StartAsync();
        var changed = 0;
        using (var ws = await PhoneAsync(rig))
        {
            await SendAsync(ws, """{"t":"caps","torch":true}""");
            await SendAsync(ws, """{"t":"state","zoom":1}""");
            await WaitUntilAsync(() => rig.Link.State != null);
            rig.Link.Changed += () => Interlocked.Increment(ref changed);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", T());
        }
        await WaitUntilAsync(() => !rig.Link.Connected);
        Assert.Null(rig.Link.PhoneName);
        Assert.Null(rig.Link.Caps);
        Assert.Null(rig.Link.State);
        Assert.Equal(1, changed);
        Assert.False(await rig.Link.SetAsync("zoom", 1));
        using var again = await PhoneAsync(rig, "Yeni"); // bağlantı yuvası boşaldı
        Assert.Equal("Yeni", rig.Link.PhoneName);
    }

    [Fact] public async Task Api_phone_commands_reach_phone_and_report_ok()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        using var ws = await PhoneAsync(rig);

        Assert.True((bool?)(await PostAsync(h, rig.Http("/api/phone/set"), new { key = "zoom", value = 2.5 }))["ok"]);
        var set = await ReceiveJsonAsync(ws);
        Assert.Equal("set", (string?)set["t"]);
        Assert.Equal("zoom", (string?)set["key"]);
        Assert.Equal(2.5, (double?)set["value"]);

        Assert.True((bool?)(await PostAsync(h, rig.Http("/api/phone/start")))["ok"]);
        Assert.Equal("start", (string?)(await ReceiveJsonAsync(ws))["t"]);
        Assert.True((bool?)(await PostAsync(h, rig.Http("/api/phone/stop")))["ok"]);
        Assert.Equal("stop", (string?)(await ReceiveJsonAsync(ws))["t"]);
    }

    [Fact] public async Task Api_phone_commands_without_phone_report_not_ok()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        Assert.False((bool?)(await PostAsync(h, rig.Http("/api/phone/set"), new { key = "zoom", value = 2 }))["ok"]);
        Assert.False((bool?)(await PostAsync(h, rig.Http("/api/phone/start")))["ok"]);
        Assert.False((bool?)(await PostAsync(h, rig.Http("/api/phone/stop")))["ok"]);
        using var bad = await h.PostAsJsonAsync(rig.Http("/api/phone/set"), new { value = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact] public async Task Api_status_reports_platform_and_phone()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        var off = JsonNode.Parse(await h.GetStringAsync(rig.Http("/api/status")))!;
        Assert.Equal("yok", (string?)off["platform"]!["camera"]);
        Assert.False((bool?)off["phone"]!["connected"]);

        using var ws = await PhoneAsync(rig);
        await SendAsync(ws, """{"t":"caps","torch":true}""");
        await WaitUntilAsync(() => rig.Link.Caps != null);
        var on = JsonNode.Parse(await h.GetStringAsync(rig.Http("/api/status")))!;
        Assert.True((bool?)on["phone"]!["connected"]);
        Assert.Equal("Pixel 8", (string?)on["phone"]!["name"]);
        Assert.True((bool?)on["phone"]!["caps"]!["torch"]);
    }

    [Fact] public async Task Api_rejects_cross_site_browser_requests()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        foreach (var (site, code) in new[] { ("cross-site", 403), ("same-site", 403), ("same-origin", 200), ("none", 200) })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, rig.Http("/api/status"));
            req.Headers.Add("Sec-Fetch-Site", site);
            using var res = await h.SendAsync(req);
            Assert.Equal(code, (int)res.StatusCode);
            if (code == 403) Assert.Equal("text/plain; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        }
    }

    [Fact] public async Task Root_serves_index_html_from_web_root()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        Assert.Contains("<h1>CamLink</h1>", await h.GetStringAsync(rig.Http("/")));
    }

    [Fact] public async Task Qr_svg_is_served_or_503_without_network()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        using var res = await h.GetAsync(rig.Http("/api/qr.svg"));
        if (NetInfo.LanAddresses().Count == 0)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        else
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/svg+xml", res.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<svg", await res.Content.ReadAsStringAsync());
        }
    }


    // ---- Tur 1: yuva hello'da, hayalet devri, çapraz site, JSON ----

    [Fact] public async Task Silent_owner_is_replaced_by_new_hello()
    {
        await using var rig = await Rig.StartAsync(staleAfter: TimeSpan.FromMilliseconds(200));
        using var old = await PhoneAsync(rig, "Eski");
        await Task.Delay(500); // eski sahip sessiz kaldı: hayalet soket
        using var fresh = await PhoneAsync(rig, "Yeni"); // welcome gelir, 4009 değil
        Assert.True(rig.Link.Connected);
        Assert.Equal("Yeni", rig.Link.PhoneName);

        // Eski soket kesilmiş olmalı (Close çerçevesi ya da kopma).
        try { Assert.Equal(WebSocketMessageType.Close, (await old.ReceiveAsync(new byte[256], T())).MessageType); }
        catch (WebSocketException) { }
        Assert.NotEqual(WebSocketState.Open, old.State);

        // Eski döngünün temizliği yeni sahibi düşürmez.
        await SendAsync(fresh, """{"t":"ping"}""");
        Assert.Equal("pong", (string?)(await ReceiveJsonAsync(fresh))["t"]);
        Assert.Equal("Yeni", rig.Link.PhoneName);
    }

    [Fact] public async Task Active_owner_is_not_replaced()
    {
        await using var rig = await Rig.StartAsync(staleAfter: TimeSpan.FromMilliseconds(1500));
        using var a = await PhoneAsync(rig, "A");
        for (var i = 0; i < 6; i++) // 1,8 sn > 1,5 sn, ama sahip her 300 ms'de konuşuyor
        {
            await Task.Delay(300);
            await SendAsync(a, """{"t":"ping"}""");
            await ReceiveJsonAsync(a);
        }
        using var b = await ConnectAsync(rig);
        await SendAsync(b, """{"t":"hello","v":1,"app":"camlink","name":"B"}""");
        var (r, _) = await ReceiveRawAsync(b);
        Assert.Equal((WebSocketCloseStatus)4009, r.CloseStatus);
        Assert.Equal("A", rig.Link.PhoneName);
    }

    [Fact] public async Task Connection_without_hello_does_not_block_a_real_phone()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        using var idle = await ConnectAsync(rig); // yükseltti ama hello yollamadı
        Assert.False((bool?)JsonNode.Parse(await h.GetStringAsync(rig.Http("/info")))!["busy"]);
        using var a = await PhoneAsync(rig, "A"); // welcome gelir
        Assert.Equal("A", rig.Link.PhoneName);
        Assert.True((bool?)JsonNode.Parse(await h.GetStringAsync(rig.Http("/info")))!["busy"]);
    }

    [Fact] public async Task Phone_websocket_rejects_cross_site_browser_upgrade()
    {
        await using var rig = await Rig.StartAsync();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Sec-Fetch-Site", "cross-site");
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(rig.Ws), T()));
        Assert.False(rig.Link.Connected);
    }

    [Fact] public async Task Api_bodies_with_duplicate_keys_are_bad_request_not_500()
    {
        await using var rig = await Rig.StartAsync();
        using var h = new HttpClient();
        foreach (var (url, json) in new[]
        {
            ("/api/phone/set", """{"key":"zoom","key":"x","value":1}"""),
            ("/api/phone/set", """{"key":"zoom","value":{"a":1,"a":2}}"""),
            ("/api/settings", """{"width":1,"width":2}"""),
        })
        {
            using var res = await h.PostAsync(rig.Http(url), new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact] public void Pair_uri_percent_encodes_the_pc_name()
    {
        Assert.Equal("camlink://pair?host=192.168.1.5&name=PC&ctl=4468&srt=9100", HostServer.PairUri("192.168.1.5", "PC"));
        Assert.Equal("camlink://pair?host=10.0.0.2&name=Ahmet%27in%20PC%27si%20%C5%9E&ctl=4468&srt=9100", HostServer.PairUri("10.0.0.2", "Ahmet'in PC'si Ş"));
    }

    [Fact] public void Beacon_encodes_the_info_payload()
    {
        var j = JsonNode.Parse(Beacon.Encode(Info.Payload("PC", true)))!.AsObject();
        Assert.Equal(new[] { "app", "busy", "ctl", "name", "rtmp", "srt", "v" }, j.Select(p => p.Key).Order());
        Assert.Equal("camlink-desktop", (string?)j["app"]);
        Assert.Equal(1, (int?)j["v"]);
        Assert.Equal("PC", (string?)j["name"]);
        Assert.Equal(9100, (int?)j["srt"]);
        Assert.Equal(9101, (int?)j["rtmp"]);
        Assert.Equal(4468, (int?)j["ctl"]);
        Assert.True((bool?)j["busy"]);
    }

    // ---- DesktopSettings ----

    [Fact] public void Settings_missing_or_corrupt_file_gives_defaults()
    {
        var dir = Directory.CreateTempSubdirectory("camlink-set").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            Assert.Equal(new DesktopSettings(), DesktopSettings.Load(path));
            File.WriteAllText(path, "{{bozuk");
            Assert.Equal(new DesktopSettings(), DesktopSettings.Load(path));
            File.WriteAllText(path, "null");
            Assert.Equal(new DesktopSettings(), DesktopSettings.Load(path));
            File.WriteAllText(path, """{"width":1280,"pcName":null}""");
            Assert.Equal(new DesktopSettings(Width: 1280), DesktopSettings.Load(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void Settings_save_creates_directory_and_round_trips()
    {
        var dir = Directory.CreateTempSubdirectory("camlink-set").FullName;
        try
        {
            var path = Path.Combine(dir, "alt", "CamLink", "settings.json");
            var s = new DesktopSettings(1280, 720, 60, "Masa");
            s.Save(path);
            s.Save(path); // var olan dosyanın üstüne yazar
            Assert.Equal(s, DesktopSettings.Load(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // geçici dosya kalmaz
        }
        finally { Directory.Delete(dir, true); }
    }
}
