// vcam-scale-test: ScaleNv12Fit için assert tabanlı denetimler ve süre ölçümü.
// Başarıda "TAMAM" yazar ve 0 döner; bir denetim düşerse assert süreci sonlandırır.
#undef NDEBUG  // Release derlemesinde de assert çalışsın
#include <cassert>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <vector>

#include "../src/nv12_scale.h"

using Frame = std::vector<uint8_t>;

static const size_t kGuard = 64;  // hedefin ardındaki nöbetçi baytlar: taşma yakalanır

// vcam-probe --write ile aynı desen: sol yarı Y=200 U=128 V=128, sağ yarı Y=50 U=90 V=160.
static Frame Split(uint32_t w, uint32_t h) {
  Frame f(size_t(w) * h * 3 / 2);
  uint8_t* uv = f.data() + size_t(w) * h;
  for (uint32_t y = 0; y < h; y++)
    for (uint32_t x = 0; x < w; x++) f[size_t(y) * w + x] = x < w / 2 ? 200 : 50;
  for (uint32_t y = 0; y < h / 2; y++)
    for (uint32_t x = 0; x < w; x += 2) {
      const bool left = x < w / 2;
      uv[size_t(y) * w + x] = left ? 128 : 90;
      uv[size_t(y) * w + x + 1] = left ? 128 : 160;
    }
  return f;
}

static Frame Scale(const Frame& src, uint32_t sw, uint32_t sh, uint32_t dw, uint32_t dh) {
  const size_t bytes = size_t(dw) * dh * 3 / 2;
  Frame dst(bytes + kGuard, 0xA5);
  ScaleNv12Fit(src.data(), sw, sh, dst.data(), dw, dh);
  for (size_t i = bytes; i < dst.size(); i++) assert(dst[i] == 0xA5 && "hedefin dışına yazıldı");
  dst.resize(bytes);
  return dst;
}

struct Mean {
  double y = 0, u = 0, v = 0;
};

// [x0, x1) sütunlarının tüm satırlardaki ortalaması (x0, x1 çift).
static Mean Columns(const Frame& f, uint32_t w, uint32_t h, uint32_t x0, uint32_t x1) {
  Mean m;
  const uint8_t* uv = f.data() + size_t(w) * h;
  for (uint32_t y = 0; y < h; y++)
    for (uint32_t x = x0; x < x1; x++) m.y += f[size_t(y) * w + x];
  for (uint32_t y = 0; y < h / 2; y++)
    for (uint32_t x = x0; x < x1; x += 2) {
      m.u += uv[size_t(y) * w + x];
      m.v += uv[size_t(y) * w + x + 1];
    }
  m.y /= double(x1 - x0) * h;
  m.u /= double(x1 - x0) / 2 * (h / 2);
  m.v /= double(x1 - x0) / 2 * (h / 2);
  return m;
}

static bool Near(double value, double expected, double tolerance) { return std::fabs(value - expected) <= tolerance; }

// Bölünmüş desenin sol ve sağ çeyreği (vcam-probe --expect-split ile aynı ölçüt).
static void CheckSplit(const Frame& f, uint32_t w, uint32_t h) {
  const uint32_t quarter = (w / 4) & ~1u;
  const Mean l = Columns(f, w, h, 0, quarter), r = Columns(f, w, h, w - quarter, w);
  printf("  %ux%u | sol Y=%.1f U=%.1f V=%.1f | sağ Y=%.1f U=%.1f V=%.1f\n", w, h, l.y, l.u, l.v, r.y, r.u, r.v);
  assert(l.y > 150 && r.y < 100);
  assert(Near(l.u, 128, 4) && Near(l.v, 128, 4));
  assert(Near(r.u, 90, 4) && Near(r.v, 160, 4));
}

// Her sütunu [x0, x1) aralığında olan pikseller siyah mı (Y=16±1, UV=128±1).
static void CheckBlackColumns(const Frame& f, uint32_t w, uint32_t h, uint32_t x0, uint32_t x1) {
  const uint8_t* uv = f.data() + size_t(w) * h;
  for (uint32_t y = 0; y < h; y++)
    for (uint32_t x = x0; x < x1; x++) assert(Near(f[size_t(y) * w + x], 16, 1));
  for (uint32_t y = 0; y < h / 2; y++)
    for (uint32_t x = x0 & ~1u; x < x1; x++) assert(Near(uv[size_t(y) * w + x], 128, 1));
}

// DLL gibi ara tamponları kareler arasında saklar (ısınmadan sonra ayırma yok).
static double TimeMs(const Frame& src, uint32_t sw, uint32_t sh, uint32_t dw, uint32_t dh) {
  Frame dst(size_t(dw) * dh * 3 / 2);
  Nv12ScaleScratch scratch;
  ScaleNv12Fit(src.data(), sw, sh, dst.data(), dw, dh, scratch);  // ısınma
  const int runs = 20;
  const auto t0 = std::chrono::steady_clock::now();
  for (int i = 0; i < runs; i++) ScaleNv12Fit(src.data(), sw, sh, dst.data(), dw, dh, scratch);
  const std::chrono::duration<double, std::milli> ms = std::chrono::steady_clock::now() - t0;
  return ms.count() / runs;
}

int main() {
  // 1. Aynı boyut: bayt bayt kopya.
  {
    Frame src(1280u * 720 * 3 / 2);
    uint32_t seed = 12345;
    for (uint8_t& b : src) b = uint8_t((seed = seed * 1103515245u + 12345u) >> 24);
    assert(Scale(src, 1280, 720, 1280, 720) == src);
    printf("aynı boyut: bayt bayt eşit\n");
  }

  // 2. 1920x1080 bölünmüş desen → küçültme ve büyütme; renkler yerinde, U/V yer değiştirmemiş.
  {
    const Frame src = Split(1920, 1080);
    printf("bölünmüş desen 1920x1080 →\n");
    CheckSplit(Scale(src, 1920, 1080, 1280, 720), 1280, 720);
    CheckSplit(Scale(src, 1920, 1080, 3840, 2160), 3840, 2160);
    CheckSplit(Scale(src, 1920, 1080, 640, 360), 640, 360);
  }

  // 2b. Kareler arasında saklanan ara tamponlar sonucu değiştirmez (büyük → küçük → orta → dikey kaynak).
  {
    const Frame wide = Split(1920, 1080), tall = Split(1080, 1920);
    struct Case {
      const Frame* src;
      uint32_t sw, sh, dw, dh;
    } cases[] = {{&wide, 1920, 1080, 3840, 2160}, {&wide, 1920, 1080, 640, 360},
                 {&wide, 1920, 1080, 1280, 720},  {&tall, 1080, 1920, 1920, 1080}};
    Nv12ScaleScratch scratch;
    for (const Case& c : cases) {
      Frame reused(size_t(c.dw) * c.dh * 3 / 2), fresh(reused.size());
      ScaleNv12Fit(c.src->data(), c.sw, c.sh, reused.data(), c.dw, c.dh, scratch);
      ScaleNv12Fit(c.src->data(), c.sw, c.sh, fresh.data(), c.dw, c.dh);
      assert(reused == fresh);
    }
    printf("saklanan ara tamponlar: sonuç taze tamponlarla bayt bayt aynı\n");
  }

  // 3. Dikey (9:16) kaynak → 16:9: yanlar siyah, orta düz renk (gerilme yok, içerik ortada).
  {
    Frame src(1080u * 1920 * 3 / 2);
    const size_t luma = 1080u * 1920;
    for (size_t i = 0; i < luma; i++) src[i] = 200;
    for (size_t i = luma; i < src.size(); i += 2) src[i] = 90, src[i + 1] = 160;
    const Frame dst = Scale(src, 1080, 1920, 1920, 1080);
    CheckBlackColumns(dst, 1920, 1080, 0, 600);
    CheckBlackColumns(dst, 1920, 1080, 1321, 1920);
    const Mean mid = Columns(dst, 1920, 1080, 900, 1020);
    printf("dikey 1080x1920 → 1920x1080: x<600 ve x>1320 siyah; orta Y=%.1f U=%.1f V=%.1f\n", mid.y, mid.u, mid.v);
    assert(Near(mid.y, 200, 1) && Near(mid.u, 90, 1) && Near(mid.v, 160, 1));
  }

  // 4. Süreler (Frame Server'da kare başına ölçekleme maliyeti).
  {
    const Frame uhd = Split(3840, 2160), fhd = Split(1920, 1080);
    printf("süre 3840x2160 → 640x360: %.2f ms\n", TimeMs(uhd, 3840, 2160, 640, 360));
    printf("süre 1920x1080 → 3840x2160: %.2f ms\n", TimeMs(fhd, 1920, 1080, 3840, 2160));
  }

  printf("TAMAM\n");
  return 0;
}
