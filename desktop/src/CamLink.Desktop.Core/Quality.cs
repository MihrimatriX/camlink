using System.Text.Json.Nodes;

namespace CamLink.Desktop.Core;

/// <summary>Masaüstünün telefondan istediği kalite ve istek anı (<see cref="Environment.TickCount64"/>).</summary>
public sealed record QualityRequest(string Id, long At);

/// <summary>
/// Telefonun kalite kimlikleri (telefon ↔ masaüstü sözleşmesi) ve masaüstünün bunlarla yaptığı iki iş:
/// FFmpeg çıkış boyutunu belirlemek, Windows uygulamasının istediği kaliteyi seçmek.
/// </summary>
public static class Quality
{
    static readonly ReceiverSettings Default = new(1920, 1080, 30);

    // Kimlik → çıkış. Sözleşme dışı kimlikler (yeni telefon sürümü) tanınmaz: ToOutput varsayılana düşer, Pick yok sayar.
    static readonly Dictionary<string, ReceiverSettings> Modes = new()
    {
        ["2160p30"] = new(3840, 2160, 30),
        ["1440p30"] = new(2560, 1440, 30),
        ["1080p60"] = new(1920, 1080, 60),
        ["1080p30"] = new(1920, 1080, 30),
        ["720p60"] = new(1280, 720, 60),
        ["720p30"] = new(1280, 720, 30),
    };

    /// <summary>Son istek bu süreden yeniyse telefonun state'i henüz yeni kaliteyi bildirmemiş olabilir.</summary>
    public const long RequestHoldMs = 10_000;

    /// <summary>
    /// Çıkış boyutunu belirleyen kalite: masaüstü <see cref="RequestHoldMs"/>'tan kısa süre önce kendisi bir kalite
    /// istediyse o (telefon yayını durdurup state'i ancak sonra yolladığı için <c>state.quality</c> bir süre eski kalır),
    /// değilse <c>state.quality</c>. FFmpeg döngüsü ve dinleyici yeniden boyutlama aynı kaynağı okur.
    /// </summary>
    public static string? Effective(JsonObject? state, QualityRequest? request, long now) =>
        request != null && now - request.At < RequestHoldMs ? request.Id : Current(state);

    /// <summary>FFmpeg'in üreteceği boyut ve kare hızı. Bilinmeyen ya da boş kimlik 1080p30 (DLL'in varsayılan türü).</summary>
    public static ReceiverSettings ToOutput(string? id) => id != null && Modes.TryGetValue(id, out var m) ? m : Default;

    /// <summary>
    /// Windows uygulamasının (<paramref name="reqW"/>x<paramref name="reqH"/>@<paramref name="reqFps"/>) istediği moda
    /// telefonun <paramref name="supported"/> listesinden geçilecek kalite; geçilecek bir şey yoksa null.
    /// Sıra: (1) istenenin yüksekliğini ve kare hızını karşılayanların en küçüğü; (2) yoksa kare hızını karşılayanların
    /// en yükseği (eşitse düşük fps); (3) o da yoksa en yüksek mod. Yalnız yükseltir: sonuç
    /// <paramref name="current"/>'ten küçük ya da eşitse null (gereksiz yayın yeniden başlatması olmasın).
    /// Telefonun bildirdiği <paramref name="current"/> sözleşme dışıysa (yeni sürümün tanımadığımız bir modu) null:
    /// büyüklüğünü bilmediğimiz modu "yükseltme" sanıp düşürmeyiz. <paramref name="current"/> null ise (henüz bildirilmedi) yükseltilir.
    /// </summary>
    public static string? Pick(IReadOnlyList<string> supported, int reqW, int reqH, int reqFps, string? current)
    {
        if (reqW <= 0 || reqH <= 0 || reqFps <= 0) return null;   // tüketici yok
        if (current != null && !Modes.ContainsKey(current)) return null;
        var all = supported.Distinct().Where(Modes.ContainsKey).Select(id => (Id: id, M: Modes[id])).ToList();
        if (all.Count == 0) return null;
        var pool = all.Where(x => x.M.Height >= reqH && x.M.Fps >= reqFps).ToList();
        (string Id, ReceiverSettings M) b;
        if (pool.Count > 0) b = pool.MinBy(x => (x.M.Height, x.M.Fps));
        else if ((pool = all.Where(x => x.M.Fps >= reqFps).ToList()).Count > 0) b = pool.MinBy(x => (-x.M.Height, x.M.Fps));
        else b = all.MinBy(x => (-x.M.Height, -x.M.Fps));

        var cur = current != null ? Modes[current] : new ReceiverSettings(0, 0, 0);
        return IsGreater(b.M, cur) ? b.Id : null;
    }

    /// <summary>a, b'den büyük mü: yükseklik büyükse ya da yükseklik eşitken kare hızı büyükse.</summary>
    public static bool IsGreater(ReceiverSettings a, ReceiverSettings b) =>
        a.Height > b.Height || (a.Height == b.Height && a.Fps > b.Fps);

    /// <summary>Telefonun <c>state.quality</c> değeri; yok ya da metin değilse null.</summary>
    public static string? Current(JsonObject? state) =>
        state?["quality"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Telefonun <c>caps.qualities</c> listesi; metin olmayan öğeler atlanır.</summary>
    public static IReadOnlyList<string> Supported(JsonObject? caps) =>
        caps?["qualities"] is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];
}
