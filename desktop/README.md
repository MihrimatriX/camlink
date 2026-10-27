# CamLink masaüstü (Windows)

> Deponun genel tanıtımı için [kök README](../README.md). Telefon uygulaması: [`../app`](../app/README.md).

Bu uygulama, telefondaki CamLink'in görüntüsünü Windows 11'de **CamLink Kamera** adlı sanal kameraya (Windows'un gerçek kamera gibi gördüğü yazılım kamera) çevirir. Telefonun sesini VB-Cable üzerinden mikrofona çevirir. Telefonu bilgisayardan yönetmeni de sağlar: lens, yakınlaştırma, efekt, pozlama, beyaz dengesi, sabitleme, kalite ve mikrofon. OBS gerekmez, StudioHub'dan bağımsızdır.

- Pencerede büyük bir QR kod çıkar. Telefonda CamLink'le bu kodu okutunca telefon bağlanır ve kendiliğinden yayına başlar. Sonraki açılışlarda QR gerekmez.
- Kablo takılıysa görüntü USB'den (adb reverse + RTMP), değilse Wi‑Fi'den (SRT) gelir. Yolu telefon kendisi seçer.
- Sanal kamera yalnız bu uygulama açıkken vardır. Pencere kapanınca uygulama sistem tepsisine iner ve kamera durur; tepsideki **Çık** kamerayı kaldırır.
- Sanal kamera 640×360 ile 4K arasında 13 mod sunar (30 ve 60 fps; mod seçmeyen uygulamaya 1080p30). Görüntü, uygulamanın seçtiği moda ölçeklenir; en boy oranı farklıysa (dikey, 4:3) kenarlar siyah şeritle doldurulur. Uygulama daha büyük bir mod seçerse masaüstü telefondan o kaliteyi ister (telefonun kaldırdığı kadar).
- Aynı anda tek telefon bağlanabilir.

## Gereksinimler

- Sanal kamera için Windows 11 (derleme 22000 veya üstü). Daha eski sürümlerde görüntü alma ve telefon kontrolü çalışır, kamera kapalı kalır.
- .NET 10 SDK, PowerShell 7 (`pwsh`), Visual Studio 18 (2026) C++ iş yükü ve CMake (sanal kamera DLL'ini derlemek için).
- USB için Android SDK platform-tools (adb) gerekir. Telefonda USB hata ayıklama açık olmalı ve bu bilgisayara izin verilmiş olmalı.
- Telefon mikrofonu için [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) gerekir. Ayrıca kurulur, CamLink'le birlikte gelmez.

## Kurulum

Depo kökünden:

```powershell
# 1. FFmpeg 9.0.2 (gyan.dev essentials); özeti doğrulanır → desktop\vendor\ffmpeg\ffmpeg.exe (~100 MB indirir)
pwsh desktop\scripts\ffmpeg-indir.ps1

# 2. adb (USB için; Android SDK'dan kopyalar) → desktop\vendor\adb
pwsh desktop\scripts\adb-kopyala.ps1            # gerekirse: -Sdk "C:\yol\android-sdk"  (ya da ANDROID_HOME)

# 3. Sanal kamera DLL'i ve prob: Release, x64 → desktop\vcam\build\Release
pwsh desktop\vcam\build.ps1

# 4. Sanal kamerayı kur: C:\Program Files\CamLink\camlink-vcam.dll + HKLM kaydı (UAC sorar)
pwsh desktop\vcam\build.ps1 -Install

# 5. Uygulama
dotnet build desktop\src\CamLink.Desktop
```

Notlar:

- `-Install`, eski DLL'i bırakması için Frame Server servisini kısa süre durdurur. O sırada kamerayı kullanan uygulamanın görüntüsü kesilir. UAC penceresi arka planda kalabilir (görev çubuğunda yanıp söner). Komut bir araç ya da otomasyon altında "iptal edildi" diye düşerse kendi terminalinde çalıştır. Günlük: `desktop\vcam\build\install.log`.
- DLL bir kez kurulur. Uygulamada da **Kur** düğmesi var; kamera kurulu değilse ya da eskiyse görünür ve UAC sorar. Bu düğmenin UAC yolu henüz denenmedi.
- `adb` ve `ffmpeg.exe` depoya girmez. Kaynakları ve lisansları `vendor\*\KAYNAK.txt` dosyalarında yazar. İki farklı adb sürümü birbirinin sunucusunu kapatır; bu yüzden `adb-kopyala` SDK'daki sürümü kopyalar.
- Çalışan `CamLink.Desktop.exe` derleme çıktısını kilitler. Yeniden derlemeden önce `CamLink.Desktop.exe --quit` çalıştır.
- Kurulum dosyası ve APK: `pwsh scripts/release.ps1 -Version x.y.z`. Betik `dotnet publish` ve Velopack ile `dist/` klasörüne yazar; `-Publish` GitHub sürümüne de yükler.

## Çalıştırma

```powershell
desktop\src\CamLink.Desktop\bin\Debug\net10.0-windows\CamLink.Desktop.exe          # --minimized: tepside başlar · --quit: çalışan kopyayı kapatır
```

1. Uygulama açılınca pencerede "Telefonunda CamLink'i aç ve bu kodu okut" yazısı ve büyük bir QR kod çıkar.
2. Telefonda CamLink'i aç (kamera ve mikrofon izni ver). Ana ekrandaki **Bilgisayara bağlan** kartıyla QR kodu okut. Birkaç saniye içinde pencerede önizleme belirir. Durum satırında `Bağlı: <telefon> · USB` ya da `· Wi‑Fi` yazar.
3. Zoom, Teams, OBS (Video Yakalama Aygıtı), Windows Kamera gibi uygulamalarda kamera olarak **CamLink Kamera**'yı seç. Windows adın sonuna "(Windows Virtual Camera)" ekleyebilir.
4. Telefon bağlıyken telefonun ayarları için pencerede **Kamera ayarları**'nı aç. Yayını buradan durdurup yeniden başlatabilirsin.
5. Başka bir telefon bağlamak için **Başka telefon eşle**'ye bas. Küçük bir QR kod çıkar.

Pencerenin altındaki **Dikkat** kutusu yalnız bir sorun varken görünür (kamera kurulu değil, port dolu, USB izni yok gibi). Kutu, çözüm için en fazla bir düğme gösterir.

## Ağ

| Port | Protokol | Ne |
|---|---|---|
| 4467 | UDP yayın | Duyuru (`{"app":"camlink-desktop",…}`); telefonlar bilgisayarı bulur |
| 4468 | TCP | Kontrol (`/phone` WebSocket), `/info`, yerel arayüz ve API (yalnız 127.0.0.1) |
| 9100 | UDP | SRT (Wi‑Fi görüntü + ses) |
| 9101 | TCP | RTMP (USB görüntü + ses; `adb reverse` ile 127.0.0.1) |

Protokolün kodu: `src/CamLink.Desktop.Core/PhoneLink.cs` (telefon) ve `HostServer.cs` (yerel arayüz).

Windows Güvenlik Duvarı Wi‑Fi'de 4468/TCP ve 9100/UDP portlarını engelliyorsa pencerenin altındaki **Güvenlik duvarı izni ver** düğmesine bas. Düğme yönetici izni (UAC) ister ve "CamLink (TCP)" ile "CamLink (UDP)" kurallarını ekler. Bu düğme henüz denenmedi. Geliştirme bilgisayarında `CamLink.Desktop` ve `ffmpeg` için Public izni elle verilmişti.

## Mikrofon (VB-Cable)

Telefonun sesi, masaüstünde adında "CABLE Input" geçen ses çıkış aygıtına gönderilir. Zoom ya da Teams'te mikrofon olarak "CABLE Output" seçilir. VB-Cable kurulu değilse görüntü yine çalışır, ses kullanılmaz ve ayarlardaki mikrofon satırında indirme bağlantısı görünür. Telefonda **Mikrofon** düğmesi açık olmalı. Bu yol gerçek telefonla denenmedi, çünkü VB-Cable geliştirme bilgisayarında kurulu değil.

## USB

Telefonda **Ayarlar › Geliştirici seçenekleri › USB hata ayıklama**'yı aç, kabloyu tak ve telefonda çıkan "Bu bilgisayara izin ver" sorusunu onayla. Masaüstü 2 saniyede bir `adb devices` çalıştırır. İzin verilmiş her cihaz için `adb reverse tcp:4468 tcp:4468` ve `tcp:9101 tcp:9101` kurar. Telefon `127.0.0.1:4468` adresini bulunca yayını RTMP'ye taşır. Kabloyu çekince Wi‑Fi'ye döner; geçişte görüntü birkaç saniye donabilir. Cihaz `unauthorized` durumundaysa pencerede "Telefonda USB hata ayıklamaya izin ver" yazar.

## Sorun giderme

- **Kamera listede yok:** CamLink masaüstü açık mı? Kamera yalnız uygulama açıkken vardır. Windows 11 kullanıyor musun? **Dikkat** kutusunda "Sanal kamera kurulu değil" yazıyorsa **Kur**'a bas. Kaydı denetlemek için: `reg query "HKLM\Software\Classes\CLSID\{8B6175EB-569A-4692-A61C-C449F9F5DE5F}\InprocServer32"`.
- **İki "CamLink Kamera" görünüyor:** Eski StudioHub'ın Unity Capture DirectShow filtresi hâlâ kayıtlı. Yönetici olarak `regsvr32 /u "C:\Program Files\StudioHub\driver\UnityCaptureFilter64.dll"` çalıştırınca kaldırılır (32 bit için `…32.dll`). Adının sonunda "(Windows Virtual Camera)" olan bu uygulamanın kamerasıdır.
- **Kamera açılınca birkaç yüz ms siyah görüntü:** Paylaşılan bellek, kamera ilk açıldığında oluşur. Masaüstü onu 500 ms'de bir yoklar. Siyah görüntü geçmiyorsa masaüstünü ve kamerayı kullanan uygulamayı kapatıp yeniden aç.
- **"Sanal kamera yanıt vermiyor":** Frame Server takıldı. CamLink'i kapatıp yeniden aç. Sorun sürerse `Get-WinEvent -LogName Microsoft-Windows-MF-FrameServer/Camera_FrameServer -MaxEvents 50` günlüğünde `Watchdog Trigger` (olay 13), Application günlüğünde de `svchost.exe_FrameServer` 1000 var mı bak.
- **Port 9100 başka bir programda:** Eski StudioHub ya da başka bir program bu portu kullanıyor olabilir.
- **FFmpeg bulunamadı hatası:** `pwsh desktop\scripts\ffmpeg-indir.ps1` çalıştır, sonra uygulamayı yeniden derle. FFmpeg derleme çıktısına kopyalanır.
- **Telefon bağlanamıyor:** Telefon ve bilgisayar aynı ağda mı? Güvenlik duvarı izni verildi mi? Telefonda masaüstünün adı doğru mu? "Bu bilgisayarda başka bir telefon bağlı" yazıyorsa öbür telefonun bağlantısını kes.
- **Görüntü yaklaşık 25-30 saniyede bir kısa süre donuyor:** Bu bilinen bir sorun (200-330 ms teslim duraklaması). Görürsen `camlink.log` dosyasıyla bildir.
- Günlük: `%APPDATA%\CamLink\camlink.log`. Ayarlar: `%APPDATA%\CamLink\settings.json`.

## Doğrulama ve test

```powershell
dotnet test desktop\tests\CamLink.Desktop.Core.Tests          # 145 test: FFmpeg argümanları/alıcı döngüsü, FrameBus seqlock, adb ayrıştırma, PhoneLink/HostServer
desktop\vcam\build\Release\vcam-probe.exe --write 20          # A: test deseni yazar (kurulu DLL'i kullanır)
desktop\vcam\build\Release\vcam-probe.exe --read --expect-split   # B (3 sn sonra): kamerayı bulur, kare çeker, Y ve U/V renklerini doğrular; çıkış 0
```

`--write` kendi sanal kamerasını başlatır (aynı ad, aynı paylaşılan bellek) ve kurulu DLL'i kilitler. Bu yüzden CamLink masaüstü açıkken ya da biri kamerayı kullanırken çalıştırma. `--read` yalnız kamerayı okur.

## Dosya düzeni

```text
CamLink.Desktop.slnx
src/CamLink.Desktop.Core/    net10.0, platformdan bağımsız: Receiver (FFmpeg), FrameBus, PhoneLink, HostServer, Beacon, NetInfo
src/CamLink.Desktop/         net10.0-windows: WPF + WebView2 + tepsi; Platform/{VirtualCamera, AudioOut, UsbBridge, IdleFrames, Elevated, JobObject}
tests/CamLink.Desktop.Core.Tests/
web/                         pencere arayüzü (HTML/CSS/JS, Barlow yazı tipi)
vcam/                        C++ Media Foundation sanal kamera DLL'i (camlink-vcam.dll) ve vcam-probe
scripts/                     ffmpeg-indir.ps1, adb-kopyala.ps1
vendor/                      ffmpeg, adb (exe/dll depoya girmez)
```

## Durum

| | Doğrulandı | Henüz denenmedi |
|---|---|---|
| Telefondan Windows'a | Gerçek telefon (Huawei SNE-LX1) + Windows 11 26200: QR → otomatik bağlanma → USB'de RTMP ve Wi‑Fi'de SRT → sanal kamerada gerçek kare (`vcam-probe --read`); yeniden açınca QR'sız bağlanma; sanal kamera modları 640×480'den 4K'ya probla okundu; ses aygıtı yokken uçtan uca gecikme ~0,23 sn | Ekran kapalıyken yayın; VB-Cable varken gecikme (~0,48 sn ölçüldü) |
| Kameradan okuyan uygulamalar | `vcam-probe` (Media Foundation `IMFSourceReader`), FFmpeg dshow ve OBS kareyi okudu | Teams, Zoom, Windows Kamera uygulaması |
| Ses | FFmpeg ses borusuna gerçek zamanlı akış (~384 kB/s) | VB-Cable ile mikrofon (kurulu değil), canlı mikrofon susturma |
| Otomatik | `dotnet test`; `dotnet build` ve `vcam/build.ps1` uyarısız; Velopack kurulum dosyası | Windows 10'da "kamera devre dışı" davranışı; masaüstündeki **Kur** ve **Güvenlik duvarı izni ver** UAC düğmeleri |

## Lisans

Tüm hakları saklıdır (ayrıntı kök README'de). FFmpeg GPL v3 lisanslıdır ve ayrı bir program olarak çalışır (`vendor/ffmpeg/KAYNAK.txt`, `LICENSE.txt`). adb Apache 2.0 lisanslıdır (`vendor/adb/KAYNAK.txt`). VB-Cable uygulamayla birlikte gelmez.
