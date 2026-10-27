using System.Text.Json.Nodes;
using CamLink.Desktop.Core;

namespace CamLink.Desktop.Core.Tests;

public class QualityTests
{
    [Theory]
    [InlineData("2160p30", 3840, 2160, 30)]
    [InlineData("1440p30", 2560, 1440, 30)]
    [InlineData("1080p60", 1920, 1080, 60)]
    [InlineData("1080p30", 1920, 1080, 30)]
    [InlineData("720p60", 1280, 720, 60)]
    [InlineData("720p30", 1280, 720, 30)]
    [InlineData(null, 1920, 1080, 30)]
    [InlineData("xyz", 1920, 1080, 30)]
    [InlineData("", 1920, 1080, 30)]
    public void ToOutput_maps_quality_ids_to_size_and_fps(string? id, int w, int h, int fps) =>
        Assert.Equal(new ReceiverSettings(w, h, fps), Quality.ToOutput(id));

    [Theory]
    [InlineData("720p30,1080p30,1080p60", 3840, 2160, 30, "1080p30", null)]   // telefonun en iyisi zaten mevcut: yükseltme yok
    [InlineData("720p30,1080p30", 1920, 1080, 30, "720p30", "1080p30")]
    [InlineData("720p30,1080p30,1080p60", 1280, 720, 60, "720p30", "1080p60")]
    [InlineData("720p30,1080p30", 640, 360, 30, "1080p30", null)]             // düşürmez
    [InlineData("", 1920, 1080, 30, null, null)]
    [InlineData("1080p30", 0, 0, 0, null, null)]                              // tüketici yok
    [InlineData("1080p30", 1920, 0, 30, null, null)]                          // eksik istek de yok sayılır
    public void Pick_follows_the_request(string supported, int w, int h, int fps, string? current, string? expected) =>
        Assert.Equal(expected, Quality.Pick(supported.Split(',', StringSplitOptions.RemoveEmptyEntries), w, h, fps, current));

    [Fact] public void Pick_prefers_smallest_mode_that_covers_the_request()
    {
        string[] all = ["2160p30", "1440p30", "1080p60", "1080p30", "720p60", "720p30"];
        Assert.Equal("1440p30", Quality.Pick(all, 2560, 1440, 30, "720p30"));
        Assert.Equal("1080p60", Quality.Pick(all, 1920, 1080, 60, "720p30"));   // 1080p30'un fps'i yetmez
        Assert.Equal("720p60", Quality.Pick(all, 1280, 720, 60, "720p30"));
    }

    [Fact] public void Pick_without_full_cover_takes_the_tallest_that_has_enough_fps_then_the_best_available()
    {
        // 4K/60 isteniyor, telefonun listesinde 60 fps yalnız 1080p60: fps'i yeten en yüksek
        Assert.Equal("1080p60", Quality.Pick(["720p30", "1080p60", "1080p30"], 3840, 2160, 60, "720p30"));
        // hiçbiri fps'i yetirmiyor (60 isteniyor, hepsi 30): en yüksek yükseklik
        Assert.Equal("1440p30", Quality.Pick(["720p30", "1440p30"], 3840, 2160, 60, "720p30"));
        // fps'i yeten, yüksekliği eşit olanlardan düşük fps (30 isteniyor): 1080p30, 1080p60 değil
        Assert.Equal("1080p30", Quality.Pick(["1080p60", "1080p30"], 3840, 2160, 30, "720p30"));
    }

    [Fact] public void Pick_ignores_unknown_ids_and_never_acts_on_an_unknown_current()
    {
        Assert.Equal("1080p30", Quality.Pick(["xyz", "1080p30"], 1920, 1080, 30, null));   // henüz bildirilmedi: yükselt
        Assert.Null(Quality.Pick(["xyz"], 1920, 1080, 30, null));
        // telefon tanımadığımız bir kimlik bildirdi (örn. gelecekteki 2160p60): büyüklüğünü bilmeden "düşürme"
        Assert.Null(Quality.Pick(["720p30", "1080p30", "1080p60"], 1920, 1080, 30, "2160p60"));
        Assert.Null(Quality.Pick(["1080p30"], 1920, 1080, 30, "bilinmeyen"));
    }

    [Fact] public void Effective_prefers_a_fresh_own_request_over_the_phones_state()
    {
        var state = JsonNode.Parse("""{"quality":"720p30"}""") as JsonObject;
        var req = new QualityRequest("1080p60", At: 1_000);
        Assert.Equal("1080p60", Quality.Effective(state, req, 1_000));
        Assert.Equal("1080p60", Quality.Effective(state, req, 1_000 + Quality.RequestHoldMs - 1));
        Assert.Equal("720p30", Quality.Effective(state, req, 1_000 + Quality.RequestHoldMs));   // süre doldu: telefonun bildirdiği
        Assert.Equal("720p30", Quality.Effective(state, null, 1_000));
        Assert.Equal("1080p60", Quality.Effective(null, req, 1_500));   // state henüz yok
        Assert.Null(Quality.Effective(null, null, 1_000));
    }

    [Theory]
    [InlineData("1080p60", "1080p30", true)]    // aynı yükseklikte fps büyük
    [InlineData("1080p30", "1080p60", false)]
    [InlineData("1440p30", "1080p60", true)]    // yükseklik fps'e baskın
    [InlineData("720p60", "1080p30", false)]
    [InlineData("2160p30", "720p30", true)]
    [InlineData("720p30", "2160p30", false)]
    [InlineData("1080p30", "1080p30", false)]   // eşit: büyük değil
    public void IsGreater_compares_height_then_fps(string a, string b, bool expected) =>
        Assert.Equal(expected, Quality.IsGreater(Quality.ToOutput(a), Quality.ToOutput(b)));

    // ---- AutoQuality: gönderme kararı ----

    static readonly string[] Phone = ["720p30", "1080p30", "1080p60"];

    [Fact] public void Auto_sends_the_pick_and_suppresses_the_same_one_for_ten_seconds()
    {
        long t = 5_000;
        var a = new AutoQuality(() => t);
        Assert.Equal("1080p60", a.Next(true, Phone, 1920, 1080, 60, "720p30"));
        t += AutoQuality.ResendMs - 1;
        Assert.Null(a.Next(true, Phone, 1920, 1080, 60, "720p30"));            // telefon henüz geçmedi: yinelenmez
        t += 1;
        Assert.Equal("1080p60", a.Next(true, Phone, 1920, 1080, 60, "720p30")); // 10 sn doldu: yeniden
    }

    [Fact] public void Auto_gives_up_on_the_same_pick_after_two_sends_until_the_phone_reconnects()
    {
        long t = 0;
        var a = new AutoQuality(() => t);
        for (var i = 0; i < AutoQuality.MaxSends; i++)
        {
            Assert.Equal("1080p60", a.Next(true, Phone, 1920, 1080, 60, "720p30"));
            t += AutoQuality.ResendMs;
        }
        // telefon isteği kabul edip başka bir kimlik bildiriyor: 3. kez istenmez, ne kadar beklense de
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(a.Next(true, Phone, 1920, 1080, 60, "720p30"));
            t += AutoQuality.ResendMs;
        }
        // başka bir kalite etkilenmez
        Assert.Equal("1080p30", a.Next(true, Phone, 1920, 1080, 30, "720p30"));
        // yeni telefon bağlantısı: sayaçlar sıfırlanır
        a.Reset();
        Assert.Equal("1080p60", a.Next(true, Phone, 1920, 1080, 60, "720p30"));
    }

    [Fact] public void Auto_resets_a_picks_counter_once_the_phone_reaches_it()   // elle düşür → otomatik yükselt döngüsü
    {
        long t = 0;
        var a = new AutoQuality(() => t);
        for (var round = 0; round < 3; round++)   // MaxSends (2) turdan fazla: sayaç birikseydi 3. turda gönderilmezdi
        {
            Assert.Equal("1080p60", a.Next(true, Phone, 1920, 1080, 60, "720p30"));   // kullanıcı 720p'ye indirmişti
            Assert.Null(a.Next(true, Phone, 1920, 1080, 60, "1080p60"));              // telefon isteğe ulaştı: sayaç silinir
            t += 1;                                                                   // 10 sn beklemeden de yeniden istenir
        }
        // telefon isteği kabul edip BAŞKA kimlik bildirirse sayaç silinmez (M2 korunur)
        var b = new AutoQuality(() => t);
        Assert.Equal("1080p60", b.Next(true, Phone, 1920, 1080, 60, "720p30"));
        t += AutoQuality.ResendMs;
        Assert.Equal("1080p60", b.Next(true, Phone, 1920, 1080, 60, "720p30"));
        t += AutoQuality.ResendMs;
        Assert.Null(b.Next(true, Phone, 1920, 1080, 60, "720p30"));
    }

    [Fact] public void Auto_does_nothing_in_fixed_mode_or_without_a_reason()
    {
        long t = 0;
        var a = new AutoQuality(() => t);
        Assert.Null(a.Next(false, Phone, 1920, 1080, 60, "720p30"));            // sabit kip
        Assert.Equal("1080p60", a.Next(true, Phone, 1920, 1080, 60, "720p30")); // sabit kip hakkı tüketmedi
        Assert.Null(a.Next(true, Phone, 0, 0, 0, "720p30"));                    // tüketici yok
        Assert.Null(a.Next(true, Phone, 640, 360, 30, "1080p60"));              // zaten yeterli: düşürmez
        Assert.Null(a.Next(true, [], 1920, 1080, 30, "720p30"));                // telefon liste vermedi
        Assert.Null(a.Next(true, Phone, 1920, 1080, 60, "2160p60"));            // telefonun modu tanınmıyor
    }

    [Fact] public void Pick_never_returns_the_same_or_a_smaller_mode()
    {
        Assert.Null(Quality.Pick(["1080p30"], 1920, 1080, 30, "1080p30"));
        Assert.Null(Quality.Pick(["1080p30", "720p60"], 1280, 720, 60, "1080p30"));   // 720p60 yükseklikte küçük
        Assert.Equal("1080p60", Quality.Pick(["1080p30", "1080p60"], 1920, 1080, 60, "1080p30")); // eşit yükseklikte fps büyük
    }

    [Fact] public void State_and_caps_are_read_defensively()
    {
        Assert.Equal("720p60", Quality.Current(JsonNode.Parse("""{"t":"state","quality":"720p60"}""") as JsonObject));
        Assert.Null(Quality.Current(null));
        Assert.Null(Quality.Current(JsonNode.Parse("""{"quality":5}""") as JsonObject));
        Assert.Equal(new[] { "720p30", "1080p30" }, Quality.Supported(JsonNode.Parse("""{"qualities":["720p30",7,null,"1080p30"]}""") as JsonObject));
        Assert.Empty(Quality.Supported(JsonNode.Parse("""{"qualities":"x"}""") as JsonObject));
        Assert.Empty(Quality.Supported(null));
    }

    [Fact] public void Quality_mode_defaults_to_auto_and_bad_values_fall_back()
    {
        Assert.Equal("auto", new DesktopSettings().QualityMode);
        var dir = Directory.CreateTempSubdirectory("camlink-q").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"qualityMode":"fixed"}""");
            Assert.Equal("fixed", DesktopSettings.Load(path).QualityMode);
            File.WriteAllText(path, """{"qualityMode":"bozuk"}""");
            Assert.Equal("auto", DesktopSettings.Load(path).QualityMode);
            File.WriteAllText(path, """{"qualityMode":null}""");
            Assert.Equal("auto", DesktopSettings.Load(path).QualityMode);
            File.WriteAllText(path, """{"pcName":"Masa"}""");   // eski dosya: alan yok
            Assert.Equal("auto", DesktopSettings.Load(path).QualityMode);

            var s = new DesktopSettings(QualityMode: "fixed");
            s.Save(path);
            Assert.Equal(s, DesktopSettings.Load(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
