namespace CamLink.Desktop.Core;

/// <summary>
/// Otomatik kalite kipinin gönderme kararı: Windows uygulamasının istediği moda telefonu yükseltmek için ne zaman
/// hangi kalitenin isteneceği. Çağıran (saniyede bir, tek iş parçacığı) dönen değeri telefona yollar.
/// <list type="bullet">
/// <item>Sabit kipte hiçbir şey istenmez.</item>
/// <item>Aynı kalite <see cref="ResendMs"/> dolmadan yinelenmez (telefon yayını yeniden başlatırken beklenir).</item>
/// <item>Aynı kalite bir telefon bağlantısında en çok <see cref="MaxSends"/> kez istenir; telefon isteği kabul edip başka
/// bir kimlik bildiriyorsa (örn. desteklemediği modu yakın bir moda eşliyorsa) yayın sonsuza dek yeniden başlamasın.
/// Telefon istenen kimliğe ulaşınca o kimliğin sayacı silinir: sınır yalnız ulaşılamayan istekleri keser,
/// "elle düşür, otomatik yükseltsin" döngüsünü değil.</item>
/// </list>
/// Bilinçli davranış: otomatik kipte telefonda elle düşürülen kalite, tüketici hâlâ daha büyük mod istiyorsa
/// kısa sürede geri yükseltilir (<see cref="MaxSends"/> sınırına kadar). Düşük kalitede kalmak için arayüzden bir kalite
/// seçilir (sabit kip).
/// </summary>
public sealed class AutoQuality(Func<long>? clock = null)
{
    public const long ResendMs = 10_000;
    public const int MaxSends = 2;

    readonly Func<long> _now = clock ?? (() => Environment.TickCount64);
    readonly Dictionary<string, (int Sends, long At)> _sent = [];

    /// <summary>Telefondan istenecek kalite; istenecek bir şey yoksa null. Dönen değer gönderilmiş sayılır.</summary>
    public string? Next(bool auto, IReadOnlyList<string> supported, int reqW, int reqH, int reqFps, string? current)
    {
        if (current != null) _sent.Remove(current);   // telefon bu kimliğe ulaştı: önceki istek işe yaradı
        if (!auto) return null;
        var pick = Quality.Pick(supported, reqW, reqH, reqFps, current);
        if (pick == null) return null;

        var now = _now();
        var (sends, at) = _sent.GetValueOrDefault(pick);
        if (sends >= MaxSends || (sends > 0 && now - at < ResendMs)) return null;
        _sent[pick] = (sends + 1, now);
        return pick;
    }

    /// <summary>Yeni telefon bağlantısı: sayaçlar sıfırlanır.</summary>
    public void Reset() => _sent.Clear();
}
