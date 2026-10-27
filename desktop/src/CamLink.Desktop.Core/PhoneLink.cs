using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CamLink.Desktop.Core;

/// <summary>Bağlantı durumunun tek kilit altında alınmış anlık görüntüsü (<see cref="PhoneLink.Snapshot"/>).</summary>
public readonly record struct PhoneSnapshot(bool Connected, string? Name, JsonObject? Caps, JsonObject? State);

/// <summary>
/// Telefonun kontrol kanalı (<c>ws://PC:4468/phone</c>). Aynı anda tek telefon: yuva <c>hello</c> ile alınır.
/// Yuva sahibi <c>StaleAfter</c> süredir sessizse (örn. Wi-Fi'den düşmüş hayalet soket) yeni <c>hello</c> yuvayı devralır;
/// sahibi canlıysa yeni bağlantı 4009 ile kapatılır.
/// Telefon <c>caps</c> ve <c>state</c> yollar; masaüstü <c>set</c>/<c>start</c>/<c>stop</c> iletir.
/// Komutları doğrulayan telefondur; burası yalnız iletir.
/// </summary>
public sealed class PhoneLink
{
    /// <summary>WS kapanış kodu: başka telefon bağlı.</summary>
    const WebSocketCloseStatus Busy = (WebSocketCloseStatus)4009;

    const int MaxMessage = 64 * 1024;
    const int MaxNameLength = 40;
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    readonly Func<string> _pcName;
    readonly object _gate = new(); // aşağıdaki durum alanlarını korur
    readonly SemaphoreSlim _sendLock = new(1, 1); // WebSocket aynı anda tek SendAsync kabul eder
    WebSocket? _ws; // yuvanın sahibi; yalnız hello'dan sonra atanır
    string? _phoneName;
    long _lastRxTicks; // sahibinden son ileti (Environment.TickCount64)
    JsonObject? _caps;
    JsonObject? _state;
    int _session; // her yeni telefon bağlantısında (hello ile yuva alınınca) artar
    volatile QualityRequest? _qualityRequest; // alıcı iş parçacığı da okur; tek kayıt, bu yüzden volatile

    public PhoneLink(Func<string> pcName) => _pcName = pcName;

    /// <summary>
    /// Telefon yayına ses izi koysun mu (`welcome` ve `config` iletisindeki `audio`): sesi çalacak aygıt (VB-Cable) yoksa
    /// false. Ses izi olan akışta FFmpeg görüntüyü ~200 ms geç veriyor (perf-report Tur 2–3).
    /// </summary>
    public Func<bool> AudioWanted { get; set; } = () => true;

    /// <summary>Hello için tanınan süre. Testler kısaltabilsin diye ayarlanabilir.</summary>
    internal TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Yuva sahibi bu süredir hiç ileti yollamadıysa hayalet sayılır ve yeni hello devralır.
    /// Telefon en geç 2 sn'de bir <c>state</c> yolladığı için 6 sn güvenli. Testler kısaltabilsin diye ayarlanabilir.
    /// </summary>
    internal TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Bir telefon hello yolladı ve yuvayı tutuyor.</summary>
    public bool Connected { get { lock (_gate) return _ws != null; } }

    public string? PhoneName { get { lock (_gate) return _phoneName; } }

    /// <summary>Son <c>caps</c> iletisi. Saklandıktan sonra değiştirilmez; yalnız okunur.</summary>
    public JsonObject? Caps { get { lock (_gate) return _caps; } }

    /// <summary>Son <c>state</c> iletisi. Saklandıktan sonra değiştirilmez; yalnız okunur.</summary>
    public JsonObject? State { get { lock (_gate) return _state; } }

    /// <summary>Bağlantı sayacı: yuvayı alan her yeni telefon (aynı telefonun yeniden bağlanması dahil) onu artırır.</summary>
    public int Session { get { lock (_gate) return _session; } }

    /// <summary>
    /// Masaüstünün telefondan en son istediği kalite (<see cref="SetAsync"/> <c>key=quality</c> ile kaydeder; arayüzden
    /// elle seçim ve otomatik kip aynı yoldan geçer). Telefon state'i yeni kaliteyi geç bildirdiği için çıkış boyutu
    /// buna bakar (<see cref="Quality.Effective"/>). Telefon gidince ya da yenisi bağlanınca silinir.
    /// </summary>
    public QualityRequest? LastQualityRequest => _qualityRequest;

    /// <summary>Dört alanı birbiriyle tutarlı okur (ayrı özellik okumaları arasında bağlantı değişebilir).</summary>
    public PhoneSnapshot Snapshot()
    {
        lock (_gate) return new(_ws != null, _phoneName, _caps, _state);
    }

    /// <summary>Bağlantı, caps ya da state değişti (telefonun okuma döngüsü iş parçacığında çağrılır; hafif tutun).</summary>
    public event Action? Changed;

    /// <summary>Bağlı telefona ileti yollar. Telefon yoksa ya da gönderim başarısızsa false.</summary>
    public Task<bool> SendAsync(JsonObject msg)
    {
        WebSocket? ws;
        lock (_gate) ws = _ws;
        return ws == null ? Task.FromResult(false) : SendToAsync(ws, msg);
    }

    /// <summary>
    /// <c>{"t":"set","key":K,"value":V}</c> yollar. <c>key=quality</c> ise isteği, telefonun cevabını beklemeden
    /// (yarış penceresi kapansın diye gönderimden önce) <see cref="LastQualityRequest"/>'e yazar; gönderilemezse,
    /// bu arada başkası değiştirmediyse, önceki isteği geri koyar.
    /// </summary>
    public async Task<bool> SetAsync(string key, JsonNode? value)
    {
        QualityRequest? request = null, previous = null;
        if (key == "quality" && value is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0)
            previous = Interlocked.Exchange(ref _qualityRequest, request = new QualityRequest(id, Environment.TickCount64));
        var ok = await SendAsync(new JsonObject { ["t"] = "set", ["key"] = key, ["value"] = value?.DeepClone() }); // DeepClone: düğüm başka bir ağaca aitse hata vermesin
        if (!ok && request != null) Interlocked.CompareExchange(ref _qualityRequest, previous, request);
        return ok;
    }

    /// <summary>Bir WebSocket bağlantısını bitene kadar yürütür; çağıran bağlantıyı kabul etmiş olmalı.</summary>
    public async Task HandleAsync(WebSocket ws, CancellationToken ct)
    {
        try { await RunAsync(ws, ct); }
        finally { Release(ws); }
    }

    async Task RunAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[MaxMessage];
        var helloDeadline = Task.Delay(HelloTimeout);
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var read = ReadMessageAsync(ws, buf, ct);
                if (!IsOwner(ws) && (helloDeadline.IsCompleted || await Task.WhenAny(read, helloDeadline) == helloDeadline))
                {
                    await CloseAsync(ws, WebSocketCloseStatus.PolicyViolation, "Hello zamanında gelmedi");
                    ws.Abort(); // bekleyen okuma kapanışın yanıtı gelmese de bitsin
                    try { await read; } catch { /* bekleyen okumanın iptal hatası önemsiz */ }
                    return;
                }
                var m = await read;
                Stamp(ws);
                if (m.Type == WebSocketMessageType.Close) break;
                if (m.TooBig)
                {
                    await CloseAsync(ws, WebSocketCloseStatus.MessageTooBig, "İleti 64 KB sınırını aştı");
                    return;
                }
                if (m.Type == WebSocketMessageType.Text && !await HandleMessageAsync(ws, buf.AsMemory(0, m.Length)))
                    return;
            }
            if (ws.State == WebSocketState.CloseReceived) await CloseAsync(ws, WebSocketCloseStatus.NormalClosure, "");
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // Telefon gitti ya da uygulama kapanıyor; temizliği Release yapar.
        }
    }

    /// <returns>Bağlantı sürsün mü.</returns>
    async Task<bool> HandleMessageAsync(WebSocket ws, ReadOnlyMemory<byte> json)
    {
        JsonObject? msg;
        string? t, helloName = null;
        try
        {
            msg = JsonNode.Parse(json.Span) as JsonObject;
            t = msg?["t"] is JsonValue tv && tv.TryGetValue<string>(out var s) ? s : null;
            // Yinelenen anahtar (örn. {"t":"a","t":"b"}) ilk erişimde ArgumentException atar; burada yakalanıp ileti yok sayılır.
            if (t is "caps" or "state") Materialize(msg);
            else if (t == "hello") helloName = PhoneNameOf(msg!);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return true; // çözülemeyen ileti yok sayılır
        }
        if (t == null) return true;

        if (!IsOwner(ws))
        {
            if (t != "hello") return true; // yuva alınmadan başka bir şey işlenmez
            var (ok, stale) = TryClaim(ws, helloName!);
            if (!ok)
            {
                // welcome gönderilmez; telefon bu kodu görünce yeniden denemeyi bırakıp kullanıcıya haber verir.
                await CloseAsync(ws, Busy, "Başka bir telefon bağlı");
                return false;
            }
            // Devralınan hayalet soketi kes; eski döngünün Release'i artık etkisiz (yuva ona ait değil).
            stale?.Abort();
            RaiseChanged();
            return await SendToAsync(ws, new JsonObject { ["t"] = "welcome", ["v"] = 1, ["name"] = _pcName(), ["audio"] = AudioWanted() });
        }

        switch (t)
        {
            case "caps":
                lock (_gate) _caps = msg;
                RaiseChanged();
                break;
            case "state":
                lock (_gate) _state = msg;
                RaiseChanged();
                break;
            case "ping":
                await SendToAsync(ws, new JsonObject { ["t"] = "pong" });
                break;
        }
        return true; // bilinmeyen tür (ve yuva sahibinin tekrar hello'su) yok sayılır
    }

    bool IsOwner(WebSocket ws)
    {
        lock (_gate) return ReferenceEquals(_ws, ws);
    }

    /// <summary>Sahibinden ileti geldi: hayalet sayacını sıfırlar.</summary>
    void Stamp(WebSocket ws)
    {
        lock (_gate)
            if (ReferenceEquals(_ws, ws)) _lastRxTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Yuva boşsa ya da sahibi <see cref="StaleAfter"/> süredir sessizse yuvayı alır.
    /// <paramref name="stale"/>: devralınan eski soket (kilit dışında <c>Abort</c> edilmeli).
    /// </summary>
    (bool ok, WebSocket? stale) TryClaim(WebSocket ws, string name)
    {
        lock (_gate)
        {
            WebSocket? stale = null;
            if (_ws != null)
            {
                if (Environment.TickCount64 - _lastRxTicks <= (long)StaleAfter.TotalMilliseconds) return (false, null);
                stale = _ws;
            }
            _ws = ws;
            _phoneName = name;
            _caps = null;
            _state = null;
            _qualityRequest = null;
            _session++;
            _lastRxTicks = Environment.TickCount64;
            return (true, stale);
        }
    }

    // Ayrıştırılmış JsonNode ağacı çocuklarını ilk erişimde tembelce kurar; iki iş parçacığı aynı anda
    // ilk kez okursa yarışır. Yayınlamadan önce tümünü kurarak okuyucuları yarıştan kurtarırız.
    // Yinelenen anahtar varsa burada ArgumentException atar; çağıran yakalamalı.
    internal static void Materialize(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o: foreach (var kv in o) Materialize(kv.Value); break;
            case JsonArray a: foreach (var x in a) Materialize(x); break;
        }
    }

    static string PhoneNameOf(JsonObject hello)
    {
        var name = hello["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (name.Length == 0) return "Telefon";
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    /// <summary>Yuva bu bağlantıdaysa boşaltır, durumu siler ve <see cref="Changed"/> tetikler.</summary>
    void Release(WebSocket ws)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_ws, ws)) return;
            _ws = null;
            _phoneName = null;
            _caps = null;
            _state = null;
            _qualityRequest = null;
        }
        RaiseChanged();
    }

    void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* Bir abonenin hatası telefon bağlantısını düşürmemeli. */ }
    }

    async Task<bool> SendToAsync(WebSocket ws, JsonObject msg)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        try
        {
            using var cts = new CancellationTokenSource(SendTimeout);
            await _sendLock.WaitAsync(cts.Token);
            try
            {
                if (ws.State != WebSocketState.Open) return false;
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
                return true;
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    readonly record struct Message(WebSocketMessageType Type, int Length, bool TooBig);

    /// <summary>Tek bir iletiyi <paramref name="buf"/>'a okur; sığmazsa <c>TooBig</c> döner.</summary>
    static async Task<Message> ReadMessageAsync(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        var n = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(buf.AsMemory(n), ct);
            if (r.MessageType == WebSocketMessageType.Close) return new(WebSocketMessageType.Close, 0, false);
            n += r.Count;
            if (r.EndOfMessage) return new(r.MessageType, n, false);
            if (n == buf.Length) return new(r.MessageType, n, true);
        }
    }

    /// <summary>Kapanış el sıkışmasını kısa bir süreyle yapar; yanıt gelmezse bağlantıyı keser.</summary>
    static async Task CloseAsync(WebSocket ws, WebSocketCloseStatus code, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(CloseTimeout);
            await ws.CloseAsync(code, reason, cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            ws.Abort();
        }
    }
}
