# CamLink OBS eklentisi

> Deponun genel tanıtımı için [kök README](../README.md).

Bu eklenti OBS'e **CamLink telefon kamerası** kaynağını ekler. Kaynak, telefonun SRT ile gönderdiği görüntüyü ve sesi alır. Eklenti ayrıca telefonların bilgisayarı kendiliğinden bulabilmesi için yerel ağa duyuru gönderir.

Eklenti aşağıdaki gibi elle kurulur. StudioHub'ın "OBS'i hazırla" adımı 2026-10-26'dan beri bu eklentiyi kurmuyor.

OBS 30 ve sonraki sürümlerle (30.x, 31.x, 32.x) çalışır. Eklenti OBS 30 başlıklarıyla derlenir.

## Elle kurulum (Windows)

1. OBS'i kapat.
2. `out/camlink` klasörünü (`./build.sh` üretir) `C:\ProgramData\obs-studio\plugins\` içine kopyala.
   Sonuç: `C:\ProgramData\obs-studio\plugins\camlink\bin\64bit\camlink.dll`
3. OBS'i aç ve **Kaynaklar › + › CamLink telefon kamerası** ile kaynağı ekle.

Sahneleri, ses efektlerini ve slaytları kuran Lua betiği DeckPad'e ait, bu yüzden deckpad deposunda duruyor (`obs-script/`). Betikteki "Telefon kamerasını bu sahneye ekle" düğmesi, bu eklenti kuruluysa onun kaynağını kullanır.

## Nasıl çalışır

- Her CamLink kaynağı ayrı bir UDP portunu dinler (ilki 9000, sonra 9001 ve devamı). Portu kaynak özelliklerinden değiştirebilirsin; seçilen port ayara kaydedilir.
- Kaynağın içinde OBS'in kendi Medya Kaynağı (`ffmpeg_source`) gizli bir alt kaynak olarak SRT dinleyici modunda çalışır: `srt://0.0.0.0:PORT?mode=listener&latency=…`, mpegts.
  Görüntü, kaynağın kendi görüntüsü gibi çizilir. Ses, yakalama geri çağrısıyla kaynağa aktarılır ve mikserde kaynağın adıyla görünür.
- Eklenti 2 saniyede bir UDP 4467'ye duyuru gönderir:
  `{"app":"camlink-obs","v":1,"name":"PC","cams":[{"name":"Telefon kamerası","port":9000,"live":false}]}`.
  CamLink bu listeyi gösterir. Adresi paketin geldiği yerden, portu listeden alır. `live`, kaynağın o an görüntü alıp almadığını gösterir ("Kullanımda" rozeti).
- Kaynak özelliklerinde port, gecikme kaydırıcısı (20-1000 ms), elle bağlanma bilgisi (bilgisayarın IP'siyle) ve **Bağlantıyı yeniden başlat** düğmesi var.
- Metinler OBS'in dil ayarına göre Türkçe ya da İngilizce çıkar (`data/locale`).

## Kaynaktan derleme

```bash
./build.sh   # MinGW-w64 ile camlink.dll; Linux'ta libobs-dev varsa test için camlink.so
```

- OBS 30.0 libobs başlıkları ilk çalıştırmada `build/obs-30` klasörüne indirilir (yalnız `libobs` klasörü, seyrek klon). Başka bir kopya kullanmak için: `OBS30=/yol/libobs ./build.sh`.
- `obs.dll`'e bağlanmak için bir içe aktarma kitaplığı gerekir. Betik, kullanılan fonksiyonlardan bir `.def` dosyası üretir ve kitaplığı `dlltool` ile oluşturur. OBS'i Windows'ta derlemek gerekmez.
- Windows'ta MSYS2'nin `mingw-w64-x86_64-gcc` paketiyle aynı betik çalışır.
- OBS, kendisinden daha yeni bir libobs ile derlenmiş eklentiyi yüklemez. Bu yüzden eklenti OBS 30 başlıklarıyla derlenir ve OBS 30 ile sonraki sürümlerde yüklenir.

## Doğrulama

Linux'ta gerçek OBS 30.0.2 ile sanal ekranda (Xvfb) denendi. SRT ile gelen test görüntüsü kaynağın ekran görüntüsünde doğru çıktı, ses kaynağın ses ölçerine geldi ve duyuru paketi `live: true` gösterdi.

Windows DLL'i aynı kaynaktan MinGW ile derlenir. Dışa aktarımlar ve bağımlılıklar `objdump` ile kontrol edildi. Eklenti Windows'ta OBS içinde henüz denenmedi.
