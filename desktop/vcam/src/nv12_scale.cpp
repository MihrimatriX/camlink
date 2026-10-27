#include "nv12_scale.h"

#include <algorithm>
#include <cstring>

namespace {

using Tap = Nv12ScaleScratch::Tap;

// Merkez hizalı eşleme, 16.16 sabit nokta: kaynak = (d + 0.5) * s / n - 0.5, [0, s-1]'e kırpılır.
Tap TapAt(uint32_t d, uint32_t s, uint32_t n) {
  int64_t p = ((2 * int64_t(d) + 1) * int64_t(s) << 16) / (2 * int64_t(n)) - 32768;
  p = std::clamp<int64_t>(p, 0, int64_t(s - 1) << 16);
  const uint32_t i0 = uint32_t(p >> 16);
  return {i0, std::min(i0 + 1, s - 1), uint32_t(p >> 8) & 255};
}

// Bir kaynak satırını yatay ölçekler (yuvarlanmış 8 bit). Ch: piksel başına bayt (Y 1, UV 2).
// __restrict: satır tamponu kaynakla ve dokunuşlarla örtüşmez; olmadan her bayt yazımı dokunuşu yeniden
// okutuyor (ölçüldü: 1080p→4K ~%30 yavaş).
template <uint32_t Ch>
void ScaleRow(const uint8_t* __restrict s, const Tap* __restrict xs, uint32_t dw, uint8_t* __restrict out) {
  for (uint32_t x = 0; x < dw; x++) {
    const Tap& t = xs[x];
    const uint8_t* a = s + size_t(t.i0) * Ch;
    const uint8_t* b = s + size_t(t.i1) * Ch;
    for (uint32_t c = 0; c < Ch; c++)
      out[size_t(x) * Ch + c] = uint8_t((a[c] * (256 - t.f) + b[c] * t.f + 128) >> 8);
  }
}

// Tek düzlem: önce yatay, sonra dikey. Yatay ölçeklenmiş son iki kaynak satırı saklanır; büyütmede ardışık
// hedef satırlar aynı kaynak satırlarını kullandığından her kaynak satırı bir kez yatay ölçeklenir. Dikey
// karıştırma tüm satırda aynı ağırlıkla, 16 bit aralıkta yapılır (derleyici vektörleştirir). Genişlikler
// piksel, adımlar bayt.
template <uint32_t Ch>
void ScalePlane(const uint8_t* src, uint32_t sw, uint32_t sh, uint32_t sstride, uint8_t* dst, uint32_t dw,
                uint32_t dh, uint32_t dstride, Nv12ScaleScratch& scratch) {
  if (scratch.xs.size() < dw) scratch.xs.resize(dw);  // yalnız büyürken ayrılır
  Tap* xs = scratch.xs.data();
  for (uint32_t x = 0; x < dw; x++) xs[x] = TapAt(x, sw, dw);
  const size_t n = size_t(dw) * Ch;
  if (scratch.rows.size() < 2 * n) scratch.rows.resize(2 * n);
  uint8_t* rows[2] = {scratch.rows.data(), scratch.rows.data() + n};
  int64_t have[2] = {-1, -1};  // rows[k]'nin kaynak satırı
  for (uint32_t y = 0; y < dh; y++) {
    const Tap ty = TapAt(y, sh, dh);
    if (have[0] != ty.i0) {
      if (have[1] == ty.i0) {
        std::swap(rows[0], rows[1]);
        std::swap(have[0], have[1]);
      } else {
        ScaleRow<Ch>(src + size_t(ty.i0) * sstride, xs, dw, rows[0]);
        have[0] = ty.i0;
      }
    }
    if (have[1] != ty.i1) {
      ScaleRow<Ch>(src + size_t(ty.i1) * sstride, xs, dw, rows[1]);
      have[1] = ty.i1;
    }
    const uint8_t* __restrict a = rows[0];  // satır tamponları hedefle örtüşmez
    const uint8_t* __restrict b = rows[1];
    const uint16_t w0 = uint16_t(256 - ty.f), w1 = uint16_t(ty.f);
    uint8_t* __restrict out = dst + size_t(y) * dstride;
    for (size_t i = 0; i < n; i++) out[i] = uint8_t(uint16_t(a[i] * w0 + b[i] * w1 + 128) >> 8);
  }
}

}  // namespace

void ScaleNv12Fit(const uint8_t* src, uint32_t sw, uint32_t sh, uint8_t* dst, uint32_t dw, uint32_t dh,
                  Nv12ScaleScratch& scratch) {
  const size_t luma = size_t(dw) * dh;
  if (sw == dw && sh == dh) {
    memcpy(dst, src, luma * 3 / 2);
    return;
  }
  // Sığdırma dikdörtgeni: en-boy korunur, boyut ve konum çift sayılara hizalanır, ortalanır.
  uint32_t rw = dw, rh = dh;
  if (uint64_t(sw) * dh > uint64_t(dw) * sh) rh = uint32_t(uint64_t(sh) * dw / sw);  // kaynak daha geniş: üst/alt boş
  else rw = uint32_t(uint64_t(sw) * dh / sh);                                        // kaynak daha dar: yanlar boş
  rw = std::max(rw & ~1u, 2u);
  rh = std::max(rh & ~1u, 2u);
  const uint32_t x0 = ((dw - rw) / 2) & ~1u, y0 = ((dh - rh) / 2) & ~1u;
  if (rw != dw || rh != dh) {
    memset(dst, 16, luma);
    memset(dst + luma, 128, luma / 2);
  }
  ScalePlane<1>(src, sw, sh, sw, dst + size_t(y0) * dw + x0, rw, rh, dw, scratch);
  // UV: yarım çözünürlükte (U,V) çiftleri; x0 çift olduğundan bayt konumu x0 bir çiftin başıdır.
  ScalePlane<2>(src + size_t(sw) * sh, sw / 2, sh / 2, sw, dst + luma + size_t(y0 / 2) * dw + x0, rw / 2, rh / 2, dw,
                scratch);
}
