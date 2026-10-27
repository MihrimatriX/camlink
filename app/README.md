# CamLink (telefon uygulaması)

> Deponun genel tanıtımı ve mimarisi için [kök README](../README.md). OBS eklentisi: [`../obs-plugin`](../obs-plugin/README.md).

Bu Android uygulaması telefonunu bilgisayar için kameraya çevirir; Wi‑Fi ile ya da USB kabloyla çalışır. Görüntüyü telefonun donanımıyla (H.264) sıkıştırır ve bilgisayara düşük gecikmeyle gönderir: Wi‑Fi'de SRT, USB'de RTMP ile. Görüntü iki yere gidebilir:

- Windows'ta web kamerası olarak, OBS olmadan. [CamLink masaüstü](../desktop/README.md) açıkken görüntü Windows 11'de **CamLink Kamera** adıyla kamera listesinde görünür (Zoom, Teams gibi uygulamalarla henüz denenmedi). Önerilen yol budur, kurulumu da en kısa olandır.
- OBS'e: OBS'teki "CamLink telefon kamerası" kaynağına (CamLink OBS eklentisi) ya da OBS'in kendi Medya Kaynağına. Bu yol Ayarlar'dadır ve ikinci seçenektir.

## Hızlı başlangıç (APK)

### Windows'ta web kamerası

1. [CamLink.apk](https://github.com/MihrimatriX/camlink/releases/latest/download/CamLink.apk)'yı telefona indir ve kur. Kamera ve mikrofon iznini ver. Android 13 ve üstünde bildirim izni de istenir; yayın bildirimi için gerekir.
2. Bilgisayarda [CamLink masaüstünü](https://github.com/MihrimatriX/camlink/releases/latest/download/CamLink-win-Setup.exe) kur ve aç ([`../desktop/README.md`](../desktop/README.md)). Pencerede büyük bir QR kod görünür.
3. Telefonda ana ekrandaki **Bilgisayara bağlan** kartına dokun ve QR kodu okut. Yayın kendiliğinden başlar. Sonraki açılışlarda QR gerekmez.
4. USB kabloyla bağlanmak istersen telefonda USB hata ayıklamayı aç ve kabloyu tak. Uygulama USB'yi kendisi seçer. Kablo yoksa Wi‑Fi kullanılır; telefon ve bilgisayar aynı ağda olmalı.

### OBS'e göndermek

1. Bilgisayara CamLink OBS eklentisini kur ([`../obs-plugin`](../obs-plugin/README.md)). OBS'te **Kaynaklar › + › CamLink telefon kamerası** ile kaynağı ekle.
2. CamLink'te **Ayarlar › Bağlantı › OBS ya da başka hedef** bölümünü aç. Bilgisayar **Bilgisayarı bul** listesinde kendiliğinden görünür. OBS kaynağını seç.
   Görünmezse OBS WebSocket QR kodunu okut ya da adresi elle gir.
3. Ortadaki kırmızı düğmeye bas. Görüntü birkaç saniye içinde OBS'te belirir.

USB kullanmıyorsan telefon ve bilgisayar aynı Wi‑Fi ağında olmalı. Bilgisayarın IP adresi değişirse CamLink onu yeni adresinde kendisi bulur.

#### StudioHub olmadan

Kurulum ekranında **OBS WebSocket** sekmesini kullan. OBS'te **Araçlar › WebSocket Sunucu Ayarları**'nı aç ve sunucuyu etkinleştir. **Bağlantı bilgilerini göster** ile açılan QR kodu okut. CamLink, OBS'te "CamLink" adlı bir Medya Kaynağı oluşturur ve açık olan sahneye ekler.

#### OBS'i elle kurmak

Kurulum ekranında **Elle** sekmesini seç ve şunları yap:

1. OBS'te **Kaynaklar › + › Medya Kaynağı** ekle.
2. **Yerel dosya** işaretini kaldır.
3. **Giriş** alanına `srt://0.0.0.0:9000?mode=listener` yaz.
4. **Giriş biçimi** alanına `mpegts` yaz. **Ağ arabelleğe alma** değerini 1 MB yap.
5. **Kaynak etkinleştiğinde oynatmayı yeniden başlat** işaretsiz kalsın.

Birden çok telefon kullanacaksan her birine ayrı bir port ver (9000, 9001 gibi).

## Neler var

- Gecikmeyi düşük tutmak için SRT kullanılır. Anahtar kare aralığı 1 saniyedir, B-kare yoktur ve OBS tarafında arabellek sıfırdır.
- Kalite için 720p'den 4K'ya kadar 30 ve 60 fps hazır ayarlar var. Listede yalnız telefonun kamerasının ve kodlayıcısının gerçekten kaldırdığı modlar çıkar. Ağ yavaşlayınca bit hızı (saniyede gönderilen veri miktarı) kendiliğinden düşer.
- İki parmakla yakınlaştırabilirsin (telefonun desteklediği ölçüde). Ana ekranda yakınlaştırma çubuğu yok; masaüstünden de ayarlanır.
- Ön ve arka kamera aynı anda açılabilir: arka kamera tam ekran, ön kamera köşede küçük pencerede. Köşe ve boyut seçilebilir.
- 17 efekt var: güzelleştirme, sıcak, soğuk, canlı, kontrast, keskin, siyah beyaz, sepya, 70'ler, sabah, film, bulanık, çizgi film, piksel, eski TV, glitch, negatif. Bazılarının şiddeti ayarlanır. Efektler GPU'da, kodlayıcıdan önce uygulanır.
- Kamera kontrolleri: kamerayı çevirme, lens düğmeleri (telefonda birden çok lens varsa, telefonun gerçek lensleri), mikrofonu susturma, fener, ayna, pozlama ve otomatik odak. Fener, ayna, pozlama, otomatik odak ve efektler Ayarlar'dadır. Hepsi bilgisayardan da yönetilir. Beyaz dengesi ve görüntü sabitleme yalnız bilgisayardan ya da kayıtlı ayardan değişir.
- İstersen telefon mikrofonunun sesi görüntüyle birlikte gider.
- Shorts ve Reels için dikey (9:16) yayın yapılabilir.
- Pil ve ısı için ekran karartma modu, ısınma uyarısı ve düşük pil uyarısı var. Telefon çok ısınırsa kalite bir kez kendiliğinden düşürülür.
- OBS kapalıysa ya da Wi‑Fi koparsa uygulama bağlanana kadar yeniden dener. Masaüstü hedefinde kablo takılıp çıkarılınca USB ile Wi‑Fi arasında kendiliğinden geçer; geçişte görüntü birkaç saniye donar.
- Ekran kapanınca ya da uygulama arka plana geçince yayının sürmesi için bir ön plan hizmeti var ("CamLink yayında" bildirimi ve bildirimdeki "Durdur" düğmesi). Bu özellik cihazda henüz doğrulanmadı. Masaüstüne 2 dakika ulaşılamazsa arka plandaki yayın kendiliğinden kapanır.
- CamLink masaüstü ve CamLink OBS eklentisi yerel ağa duyuru gönderir (UDP 4467); telefon bilgisayarı böyle bulur. Başka bir telefonun kullandığı kaynak "Kullanımda" diye görünür. USB'de masaüstü, `127.0.0.1:4468/info` adresi yoklanarak bulunur. Masaüstüne bağlıyken kamerayı kullanan uygulama daha büyük bir mod seçerse masaüstü telefondan o kaliteyi ister.

## Kaynaktan derleme

CamLink kendi yerel (native) Kotlin modülünü içerir. Bu yüzden Expo Go'da çalışmaz, derlenmesi gerekir.

Bulutta, bilgisayara Android araçları kurmadan:

```bash
npm ci
npx eas-cli@latest build -p android --profile preview
```

Yerelde (Android Studio ve JDK 17+ gerekir):

```bash
npm ci
npx expo run:android          # telefona doğrudan kurar
# ya da
npx expo prebuild -p android
cd android && ./gradlew assembleRelease
```

Tür denetimi: `npm run typecheck`.

## Mimari

```text
Kamera (Camera2) ─► OpenGL ES filtre zinciri ─► MediaCodec H.264 ─► SRT (USB'de RTMP) ─┬► OBS (CamLink kaynağı / Medya Kaynağı)
                     [0] renk efekti                                                      └► CamLink masaüstü ─► CamLink Kamera (sanal kamera)
                     [1] ikinci kamera (resim içinde resim)
CamLink masaüstü / OBS eklentisi ──UDP 4467 duyuru──► Discovery.kt ─► "Bilgisayarı bul" listesi
Telefon ◄──WS 4468 /phone (lens, zoom, efekt, start/stop)──► CamLink masaüstü
```

```text
App.tsx                         ana ekran ("Sade": durum hapı, lens çipleri, Çevir/Mikrofon/Karart), PairCard
index.ts                        Headless JS görevi (ön plan hizmetiyle zamanlayıcılar sürsün)
src/engine.ts                   yerel motor ile arayüz arasındaki köprü; başlat/durdur sırası, masaüstü komutları
src/desktopLink.ts, control.ts  masaüstü kontrol kanalı (hello/caps/state/set) ve saf karar mantığı
src/lenses.ts                   telefonun lenslerini "0,5×/1×/3×/Ön" etiketlerine çevirir
src/obsSetup.ts                 OBS'te Medya Kaynağını otomatik kurma
src/discovery.ts, beacon.ts     yerel ağdaki bilgisayarları ve kamera hedeflerini bulma (USB yoklaması dahil)
src/pair.ts                     camlink:// ve StudioHub QR kodları
src/store.ts                    ayarlar, kalite ve efekt listesi
src/components/Sheets.tsx       QR okuyucu, Ayarlar, OBS kurulum ekranları
modules/camlink-engine/         Expo yerel modülü
  android/.../CamEngine.kt      kamera, lensler, filtreler, kodlayıcı, SRT/RTMP (GenericStream), çift kamera
  android/.../CamlinkService.kt ön plan hizmeti (camera|microphone)
  android/.../CamlinkPreviewView.kt   önizleme yüzeyi
  android/.../Discovery.kt      UDP duyuru dinleyici (MulticastLock ile)
  src/                          TypeScript arayüzü
```

Kamera, filtre ve SRT katmanı için açık kaynak [RootEncoder](https://github.com/pedroSG94/RootEncoder) (Apache 2.0) kullanılır.

## Sorun giderme

- **Üstteki durum yazısı "Bilgisayar bulunamadı" ya da "Bağlanıyor…" diyor:** CamLink masaüstünün açık olduğundan emin ol. Masaüstü penceresinin altındaki **Telefon bağlanamıyor mu? Güvenlik duvarı izni ver** bağlantısına bas. Telefonun bilgisayarla aynı ağda olduğuna bak; USB kullanıyorsan kablonun takılı ve USB hata ayıklamanın açık olduğuna bak. OBS'e gönderiyorsan CamLink kaynağının OBS sahnesinde olduğundan emin ol.
- **"Başka telefon bağlı":** CamLink masaüstü aynı anda tek telefon kabul eder. Öbür telefonda bağlantıyı kes (Ayarlar › Bağlantı › Bağlantıyı kes) ya da masaüstü uygulamasını kapatıp yeniden aç.
- **Bilgisayar listede çıkmıyor:** Misafir Wi‑Fi ağları cihazları birbirinden ayırır ve duyuru paketlerini engeller. Masaüstündeki QR kodu okut ya da USB kullan.
- **Görüntü takılıyor:** Ayarlar'dan bit hızını düşür ya da 720p'ye geç. 5 GHz Wi‑Fi daha iyi sonuç verir.
- **Çift kamera açılmıyor:** Her telefon iki kamerayı aynı anda açamaz. Telefonun desteklemiyorsa uygulama haber verir.
- **Küçük pencere yan duruyor:** Çift kamera panelindeki döndürme düğmesine bas.

## Notlar

- APK yalnız 64 bit ARM telefonlar içindir. Daha eski cihazlar için `app.json` içindeki `buildArchs` listesine `armeabi-v7a` ekleyip yeniden derle.
- Sürümlerdeki APK hep aynı yayın anahtarıyla imzalanır, bu yüzden yeni sürüm eskisinin üstüne kurulur. Kaynaktan derlerken `~/.camlink/signing.properties` yoksa hata ayıklama anahtarı kullanılır.
- Yerel modül yalnız Android için yazıldı. iOS sürümü için Swift tarafının ayrıca yazılması gerekir.
