// vcam-probe: "CamLink Kamera" sanal kamerasını uçtan uca sınar.
//   vcam-probe --write <saniye> [--size WxH] [--fps N] [--no-writer] [--dll <yol>]
//     DLL'i yükler, VcamStart çağırır; paylaşılan bellek açılınca --fps (varsayılan 30, 1..120) hızında iki
//     bölgeli kare yazar (varsayılan 1920x1080; DLL tüketicinin türüne ölçekler):
//       sol yarı  Y=200 U=128 V=128 (açık gri)
//       sağ yarı  Y=50  U=90  V=160 (koyu kırmızımsı; U≠V, U/V yer değişimi de yakalanır)
//     --no-writer: kamera açık kalır ama hiç yazılmaz. Varsayılan DLL kurulu kopyadır.
//   vcam-probe --read [--mode WxH@fps] [--expect-split|--expect-black]
//     MF ile kamerayı bulur, yerel türleri listeler, 3 sn içinde kare okur. Başarıda 0, değilse 1.
//     --mode: eşleşen yerel türü seçer; geçerli türün ve kare tamponunun bu boyutta olduğunu, başlıktaki
//       reqW/reqH/reqFps'in bu mod olduğunu doğrular. Yoksa NV12 (ilk eşleşen tür) istenir. İstek her zaman
//       yazdırılır.
//     --expect-split: orta yarı satırlarda sol çeyrek Y>150, sağ çeyrek Y<100; U/V her çeyrekte yukarıdaki
//       değerlerin ±4'ü (orta satırlar: 4:3 türünde üst/alt siyah bantlar sayılmaz).
//       NV12'de UV düzlemi pitch*yükseklik ofsetinden okunur (pitch: Lock2D ya da MF_MT_DEFAULT_STRIDE);
//       ofset yanlışsa orada Y baytları (200/50) okunur ve denetim düşer.
//     --expect-black: tüm karede 12 ≤ Y ≤ 20 ve U, V = 128±4 (sıfırlarla dolu bellek geçmez).
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <timeapi.h>  // timeBeginPeriod
#include <wrl/client.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

#include "../src/frame_shm.h"

using Microsoft::WRL::ComPtr;

static const wchar_t kCameraName[] = L"CamLink Kamera";
static const wchar_t kInstalledDll[] = L"C:\\Program Files\\CamLink\\camlink-vcam.dll";

// --write'ın yazdığı ve --expect-split'in beklediği değerler.
static const BYTE kLeftY = 200, kLeftU = 128, kLeftV = 128;
static const BYTE kRightY = 50, kRightU = 90, kRightV = 160;
static const double kChromaTolerance = 4;

// Askıda kalan MF çağrılarına karşı bekçi: süre dolarsa süreci 1 ile bitirir.
static std::atomic<ULONGLONG> g_deadline{0};

static void StartWatchdog() {
  std::thread([] {
    for (;;) {
      Sleep(100);
      if (GetTickCount64() > g_deadline.load()) {
        printf("HATA: zaman aşımı (kare gelmedi ya da MF çağrısı yanıt vermedi)\n");
        fflush(stdout);
        ExitProcess(1);
      }
    }
  }).detach();
}

static int Fail(const char* msg, HRESULT hr = S_OK) {
  if (hr == S_OK) printf("HATA: %s\n", msg);
  else printf("HATA: %s (hr=0x%08lX)\n", msg, static_cast<unsigned long>(hr));
  return 1;
}

// Video yakalama aygıtları içinde adı kCameraName ile başlayanı etkinleştirir
// (Windows sanal kamera adına "Windows Virtual Camera" ekleyebilir).
static HRESULT FindCamera(ComPtr<IMFMediaSource>& source) {
  ComPtr<IMFAttributes> attrs;
  HRESULT hr = MFCreateAttributes(&attrs, 1);
  if (FAILED(hr)) return hr;
  attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
  IMFActivate** devices = nullptr;
  UINT32 count = 0;
  hr = MFEnumDeviceSources(attrs.Get(), &devices, &count);
  if (FAILED(hr)) return hr;
  hr = HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
  for (UINT32 i = 0; i < count; i++) {
    wchar_t* name = nullptr;
    UINT32 len = 0;
    if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) &&
        SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len))) {
      if (wcsncmp(name, kCameraName, wcslen(kCameraName)) == 0) {
        printf("Aygıt: %ls\n", name);
        hr = devices[i]->ActivateObject(IID_PPV_ARGS(&source));
      }
      CoTaskMemFree(name);
    }
    devices[i]->Release();
  }
  CoTaskMemFree(devices);
  return hr;
}

// Bölge toplamları: Y her pikselden; U/V NV12'de her 2x2 bloktan, RGB32'de her pikselden.
struct Acc {
  uint64_t y = 0, u = 0, v = 0, ny = 0, nc = 0;
  void AddY(unsigned val) { y += val, ny++; }
  void AddUV(unsigned uu, unsigned vv) { u += uu, v += vv, nc++; }
  double Y() const { return ny ? static_cast<double>(y) / ny : 0; }
  double U() const { return nc ? static_cast<double>(u) / nc : 0; }
  double V() const { return nc ? static_cast<double>(v) / nc : 0; }
};
struct Stats {
  Acc left, right, all;  // sol çeyrek, sağ çeyrek, tüm kare
};

// BT.601 sınırlı aralık Y/U/V (BGRA piksel; RGB32 yedeği için).
static void ToYuv(const BYTE* p, unsigned& y, unsigned& u, unsigned& v) {
  const int r = p[2], g = p[1], b = p[0];
  y = static_cast<unsigned>(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
  u = static_cast<unsigned>(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
  v = static_cast<unsigned>(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
}

static bool Measure(IMFSample* sample, bool nv12, UINT32 w, UINT32 h, LONG defaultStride, Stats& st) {
  st = Stats{};
  ComPtr<IMFMediaBuffer> buf;
  if (FAILED(sample->ConvertToContiguousBuffer(&buf))) return false;
  ComPtr<IMF2DBuffer> buf2d;
  BYTE* p = nullptr;
  LONG pitch = 0;
  // Tampon türün boyutunda bir kareyi tutmalı (DLL başka boyutta kare verirse burada düşer).
  const ULONGLONG frameBytes = nv12 ? static_cast<ULONGLONG>(w) * h * 3 / 2 : static_cast<ULONGLONG>(w) * h * 4;
  DWORD contiguous = 0;
  const bool has2d = SUCCEEDED(buf.As(&buf2d));
  if (has2d && SUCCEEDED(buf2d->GetContiguousLength(&contiguous)) && contiguous < frameBytes) return false;
  const bool is2d = has2d && SUCCEEDED(buf2d->Lock2D(&p, &pitch));
  if (!is2d) {
    DWORD len = 0;
    if (FAILED(buf->Lock(&p, nullptr, &len))) return false;
    pitch = defaultStride < 0 ? -defaultStride : defaultStride;  // satır sırası ortalamayı etkilemez
    if (len < static_cast<ULONGLONG>(pitch) * (nv12 ? h + h / 2 : h) || len < frameBytes) {
      buf->Unlock();
      return false;
    }
  }
  const UINT32 quarter = (w / 4) & ~1u;  // çift: UV çiftleri bölünmesin
  // y: Y düzlemi satırı. Sol/sağ bölgeler yalnız orta yarı satırlardan ölçülür.
  auto region = [&](UINT32 x, UINT32 y) -> Acc* {
    if (y < h / 4 || y >= h - h / 4) return nullptr;
    return x < quarter ? &st.left : (x >= w - quarter ? &st.right : nullptr);
  };
  for (UINT32 y = 0; y < h; y++) {
    const BYTE* row = p + static_cast<ptrdiff_t>(y) * pitch;
    for (UINT32 x = 0; x < w; x++) {
      Acc* r = region(x, y);
      if (nv12) {
        st.all.AddY(row[x]);
        if (r) r->AddY(row[x]);
        continue;
      }
      unsigned yy, uu, vv;
      ToYuv(row + 4 * x, yy, uu, vv);
      for (Acc* a : {&st.all, r}) {
        if (!a) continue;
        a->AddY(yy);
        a->AddUV(uu, vv);
      }
    }
  }
  if (nv12) {  // UV düzlemi: Y'nin ardından, pitch*h ofsetinde; satır başına w/2 (U,V) çifti
    const BYTE* uv = p + static_cast<ptrdiff_t>(h) * pitch;
    for (UINT32 y = 0; y < h / 2; y++) {
      const BYTE* row = uv + static_cast<ptrdiff_t>(y) * pitch;
      for (UINT32 x = 0; x + 1 < w; x += 2) {
        Acc* r = region(x, 2 * y);
        st.all.AddUV(row[x], row[x + 1]);
        if (r) r->AddUV(row[x], row[x + 1]);
      }
    }
  }
  if (is2d) buf2d->Unlock2D();
  else buf->Unlock();
  return true;
}

static bool Near(double value, double expected) {
  return value >= expected - kChromaTolerance && value <= expected + kChromaTolerance;
}

static void PrintStats(const char* prefix, int frames, const Stats& st) {
  printf("%s%d. kare | sol Y=%.1f U=%.1f V=%.1f | sağ Y=%.1f U=%.1f V=%.1f | tüm Y=%.1f U=%.1f V=%.1f\n",
         prefix, frames, st.left.Y(), st.left.U(), st.left.V(), st.right.Y(), st.right.U(), st.right.V(),
         st.all.Y(), st.all.U(), st.all.V());
}

enum class Expect { Any, Split, Black };

static bool Matches(Expect expect, const Stats& st) {
  switch (expect) {
    case Expect::Split:
      return st.left.Y() > 150 && st.right.Y() < 100 && Near(st.left.U(), kLeftU) && Near(st.left.V(), kLeftV) &&
             Near(st.right.U(), kRightU) && Near(st.right.V(), kRightV);
    case Expect::Black:
      return st.all.Y() >= 12 && st.all.Y() <= 20 && Near(st.all.U(), 128) && Near(st.all.V(), 128);
    default:
      return true;
  }
}


// Tür bilgisi: genişlik, yükseklik, kare hızı (width == 0: belirtilmedi).
struct Mode {
  UINT32 width = 0, height = 0, fps = 0;
};

// Belleği açar (DLL, kamera açılınca oluşturur); başlık hazır değilse null.
// Erişim frame_shm.h'deki sözleşmeye göre: FILE_MAP_READ | FILE_MAP_WRITE.
static ClvcHeader* OpenShm(HANDLE& mapping) {
  mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, CLVC_NAME);
  if (!mapping) return nullptr;
  auto* shm = static_cast<ClvcHeader*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, CLVC_TOTAL));
  if (shm && shm->magic == CLVC_MAGIC) return shm;
  if (shm) UnmapViewOfFile(shm);
  CloseHandle(mapping);
  mapping = nullptr;
  return nullptr;
}

// Başlıktaki tüketici isteğini (DLL'in yazdığı reqW/reqH/reqFps) yazdırır; mode verildiyse onunla eşleşmeli.
static bool CheckRequest(const Mode& mode) {
  HANDLE mapping = nullptr;
  ClvcHeader* shm = OpenShm(mapping);
  if (!shm) {
    printf("%s: paylaşılan bellek açılamadı, istek okunamadı\n", mode.width ? "HATA" : "Not");
    return !mode.width;
  }
  const volatile ClvcHeader* v = shm;
  const Mode req{v->reqW, v->reqH, v->reqFps};
  printf("İstek (başlık, sürüm %u): %ux%u@%u\n", v->version, req.width, req.height, req.fps);
  UnmapViewOfFile(shm);
  CloseHandle(mapping);
  if (mode.width && (req.width != mode.width || req.height != mode.height || req.fps != mode.fps)) {
    printf("HATA: istek seçilen modla aynı değil (%ux%u@%u)\n", mode.width, mode.height, mode.fps);
    return false;
  }
  return true;
}

static int Read(Expect expect, const Mode& mode) {
  g_deadline = GetTickCount64() + 15000;  // aygıt etkinleştirme payı
  StartWatchdog();

  ComPtr<IMFMediaSource> source;
  HRESULT hr = FindCamera(source);
  if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) return Fail("CamLink Kamera bulunamadı");
  if (FAILED(hr)) return Fail("CamLink Kamera etkinleştirilemedi", hr);

  ComPtr<IMFAttributes> ra;
  MFCreateAttributes(&ra, 1);
  ra->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);  // RGB32 yedeği için dönüştürücü
  ComPtr<IMFSourceReader> reader;
  hr = MFCreateSourceReaderFromMediaSource(source.Get(), ra.Get(), &reader);
  if (FAILED(hr)) return Fail("IMFSourceReader oluşturulamadı", hr);

  // Yerel türler (DLL'in sunduğu sırayla); --mode eşleşen NV12 türünü seçer.
  const DWORD stream = static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
  ComPtr<IMFMediaType> chosen;
  DWORD count = 0;
  printf("Yerel türler:");
  for (;; count++) {
    ComPtr<IMFMediaType> t;
    if (FAILED(reader->GetNativeMediaType(stream, count, &t))) break;
    GUID sub = GUID_NULL;
    UINT32 tw = 0, th = 0, num = 0, den = 0;
    t->GetGUID(MF_MT_SUBTYPE, &sub);
    MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &tw, &th);
    MFGetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, &num, &den);
    printf(" %ux%u@%u", tw, th, den ? num / den : 0);
    if (mode.width && !chosen && sub == MFVideoFormat_NV12 && tw == mode.width && th == mode.height && den &&
        num == mode.fps * den)
      chosen = t;
  }
  printf(" (%lu)\n", count);

  bool nv12 = true;
  if (mode.width) {
    if (!chosen) return Fail("istenen mod yerel türlerde yok");
    hr = reader->SetCurrentMediaType(stream, nullptr, chosen.Get());
    if (FAILED(hr)) return Fail("istenen mod seçilemedi", hr);
  } else {
    ComPtr<IMFMediaType> want;
    MFCreateMediaType(&want);
    want->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    want->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (FAILED(reader->SetCurrentMediaType(stream, nullptr, want.Get()))) {
      nv12 = false;
      want->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
      hr = reader->SetCurrentMediaType(stream, nullptr, want.Get());
      if (FAILED(hr)) return Fail("NV12 ya da RGB32 seçilemedi", hr);
    }
  }
  ComPtr<IMFMediaType> current;
  UINT32 w = 0, h = 0;
  if (FAILED(reader->GetCurrentMediaType(stream, &current)) ||
      FAILED(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &w, &h)) || w < 4 || h == 0)
    return Fail("kare boyutu okunamadı");
  if (mode.width && (w != mode.width || h != mode.height)) return Fail("geçerli tür istenen boyutta değil");
  UINT32 strideAttr = 0;
  const LONG stride = SUCCEEDED(current->GetUINT32(MF_MT_DEFAULT_STRIDE, &strideAttr))
                          ? static_cast<LONG>(static_cast<INT32>(strideAttr))
                          : static_cast<LONG>(nv12 ? w : w * 4);
  printf("Biçim: %s %ux%u\n", nv12 ? "NV12" : "RGB32", w, h);

  // 3 sn içinde beklenen kare gelmeli; ReadSample takılırsa bekçi bitirir.
  const ULONGLONG end = GetTickCount64() + 3000;
  g_deadline = end + 500;
  Stats st;
  int frames = 0;
  while (GetTickCount64() < end) {
    DWORD flags = 0;
    LONGLONG ts = 0;
    ComPtr<IMFSample> sample;
    hr = reader->ReadSample(stream, 0, nullptr, &flags, &ts, &sample);
    if (FAILED(hr)) return Fail("ReadSample başarısız", hr);
    if (flags & (MF_SOURCE_READERF_ERROR | MF_SOURCE_READERF_ENDOFSTREAM)) return Fail("akış bitti ya da hata verdi");
    if (!sample) continue;
    if (!Measure(sample.Get(), nv12, w, h, stride, st))
      return Fail("kare tamponu okunamadı ya da türün boyutunda değil");
    frames++;
    if (Matches(expect, st)) {
      PrintStats("TAMAM: ", frames, st);
      return CheckRequest(mode) ? 0 : 1;  // akış hâlâ açık: istek bu türü göstermeli
    }
  }
  if (frames == 0) return Fail("3 sn içinde kare gelmedi");
  PrintStats("HATA: beklenen görüntü yok; son kare: ", frames, st);
  CheckRequest(mode);
  return 1;
}

using VcamStartFn = HRESULT(__stdcall*)(const wchar_t*);
using VcamStopFn = void(__stdcall*)();

static int Write(const wchar_t* dllPath, int seconds, bool noWriter, UINT32 w, UINT32 h, UINT32 fps) {
  HMODULE dll = LoadLibraryW(dllPath);
  if (!dll) return Fail("DLL yüklenemedi", HRESULT_FROM_WIN32(GetLastError()));
  auto start = reinterpret_cast<VcamStartFn>(GetProcAddress(dll, "VcamStart"));
  auto stop = reinterpret_cast<VcamStopFn>(GetProcAddress(dll, "VcamStop"));
  if (!start || !stop) return Fail("VcamStart/VcamStop dışa aktarımı yok");
  const HRESULT hr = start(kCameraName);
  if (FAILED(hr)) return Fail("VcamStart başarısız", hr);
  printf("Sanal kamera başlatıldı: %ls (%d sn)\n", dllPath, seconds);
  fflush(stdout);

  // Y düzlemi: sol yarı kLeftY, sağ yarı kRightY. UV düzlemi (h/2 satır, satırda w/2 U,V çifti):
  // sol yarı (kLeftU, kLeftV), sağ yarı (kRightU, kRightV).
  std::vector<BYTE> frame(static_cast<size_t>(w) * h * 3 / 2);
  BYTE* uv = frame.data() + static_cast<size_t>(w) * h;
  for (UINT32 y = 0; y < h; y++) {
    BYTE* row = frame.data() + static_cast<size_t>(y) * w;
    memset(row, kLeftY, w / 2);
    memset(row + w / 2, kRightY, w - w / 2);
  }
  for (UINT32 y = 0; y < h / 2; y++) {
    BYTE* row = uv + static_cast<size_t>(y) * w;
    for (UINT32 x = 0; x + 1 < w; x += 2) {
      const bool left = x < w / 2;
      row[x] = left ? kLeftU : kRightU;
      row[x + 1] = left ? kLeftV : kRightV;
    }
  }

  // Kare aralığı: 1 ms zamanlayıcı çözünürlüğü ve son tarihe göre bekleme (Sleep(33) 15,6 ms adımla ~20 fps veriyordu).
  timeBeginPeriod(1);
  const auto period = std::chrono::microseconds(1'000'000 / fps);
  auto next = std::chrono::steady_clock::now();
  const ULONGLONG end = GetTickCount64() + static_cast<ULONGLONG>(seconds) * 1000;
  HANDLE mapping = nullptr;
  ClvcHeader* shm = nullptr;
  int written = 0;
  while (GetTickCount64() < end) {
    if (!shm) {
      shm = OpenShm(mapping);
      if (!shm) {
        Sleep(200);
        next = std::chrono::steady_clock::now();
        continue;
      }
      printf("Paylaşılan bellek açıldı (sürüm %u); kare %ux%u@%u%s\n", shm->version, w, h, fps,
             noWriter ? " (yazılmayacak)" : "");
      fflush(stdout);
      // Önceki yazar kare ortasında çöktüyse seq tek kalmıştır: ilk kareden önce eşliği düzelt.
      if (!noWriter && (shm->seq & 1)) InterlockedIncrement64(&shm->seq);
    }
    if (!noWriter) {
      InterlockedIncrement64(&shm->seq);  // tek: yazılıyor
      shm->width = w;                      // boyut tek penceresinde: eski kareyle eşleşmez
      shm->height = h;
      shm->fps = fps;
      memcpy(reinterpret_cast<BYTE*>(shm) + CLVC_HEADER, frame.data(), frame.size());
      InterlockedIncrement64(&shm->seq);  // çift: hazır
      shm->writerTick = static_cast<int64_t>(GetTickCount64());
      written++;
    }
    next += period;
    while (std::chrono::steady_clock::now() < next) Sleep(1);
  }
  timeEndPeriod(1);
  printf("Bitti: %d kare yazıldı\n", written);
  if (shm) UnmapViewOfFile(shm);
  if (mapping) CloseHandle(mapping);
  stop();
  FreeLibrary(dll);
  return 0;
}

static int Usage() {
  printf("Kullanım:\n"
         "  vcam-probe --write <saniye> [--size WxH] [--fps N] [--no-writer] [--dll <yol>]\n"
         "  vcam-probe --read [--mode WxH@fps] [--expect-split|--expect-black]\n");
  return 2;
}

int wmain(int argc, wchar_t** argv) {
  SetConsoleOutputCP(CP_UTF8);
  if (argc < 2) return Usage();
  CoInitializeEx(nullptr, COINIT_MULTITHREADED);
  MFStartup(MF_VERSION);
  int rc = 2;
  if (wcscmp(argv[1], L"--read") == 0) {
    Expect expect = Expect::Any;
    Mode mode;
    for (int i = 2; i < argc; i++) {
      if (wcscmp(argv[i], L"--expect-split") == 0) {
        expect = Expect::Split;
      } else if (wcscmp(argv[i], L"--expect-black") == 0) {
        expect = Expect::Black;
      } else if (wcscmp(argv[i], L"--mode") == 0 && i + 1 < argc) {
        if (swscanf_s(argv[++i], L"%ux%u@%u", &mode.width, &mode.height, &mode.fps) != 3 || !mode.width ||
            !mode.height || !mode.fps)
          return Usage();
      } else {
        return Usage();
      }
    }
    rc = Read(expect, mode);
  } else if (wcscmp(argv[1], L"--write") == 0 && argc > 2 && _wtoi(argv[2]) > 0) {
    const wchar_t* dll = kInstalledDll;
    bool noWriter = false;
    UINT32 w = 1920, h = 1080, fps = 30;
    for (int i = 3; i < argc; i++) {
      if (wcscmp(argv[i], L"--no-writer") == 0) {
        noWriter = true;
      } else if (wcscmp(argv[i], L"--dll") == 0 && i + 1 < argc) {
        dll = argv[++i];
      } else if (wcscmp(argv[i], L"--size") == 0 && i + 1 < argc) {
        if (swscanf_s(argv[++i], L"%ux%u", &w, &h) != 2) return Usage();
      } else if (wcscmp(argv[i], L"--fps") == 0 && i + 1 < argc) {
        fps = static_cast<UINT32>(_wtoi(argv[++i]));
        if (fps < 1 || fps > 120) return Usage();
      } else {
        return Usage();
      }
    }
    if (w < 4 || h < 2 || w % 2 || h % 2 || static_cast<ULONGLONG>(w) * h * 3 / 2 > CLVC_CAPACITY) {
      printf("HATA: --size çift, en az 4x2 ve en çok %u bayt NV12 olmalı\n", CLVC_CAPACITY);
      return 2;
    }
    rc = Write(dll, _wtoi(argv[2]), noWriter, w, h, fps);
  } else {
    rc = Usage();
  }
  fflush(stdout);
  MFShutdown();
  return rc;
}
