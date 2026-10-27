// NV12 ölçekleme: paylaşılan bellekteki kare (yazarın boyutu) → tüketicinin seçtiği tür.
#pragma once
#include <cstdint>
#include <vector>

// Ara tamponlar: çağıran saklarsa her karede yeniden ayrılmaz (yalnız büyürken).
struct Nv12ScaleScratch {
  struct Tap {
    uint32_t i0, i1, f;  // iki komşu kaynak konumu ve ikincinin 8 bit ağırlığı
  };
  std::vector<Tap> xs;         // hedef sütun başına yatay dokunuş
  std::vector<uint8_t> rows;   // yatay ölçeklenmiş son iki kaynak satırı
};

// Sıkı paketli NV12'den (Y düzlemi sw*sh bayt, ardından sw*sh/2 bayt UV) sıkı paketli NV12'ye (dw*dh) ölçekler.
// İki doğrusal, tamsayı aritmetiği. En-boy oranı korunur; kalan kısım siyahtır (Y=16, UV=128). Aynı boyutta düz
// kopya. Bütün boyutlar çift ve > 0 olmalı (çağıran doğrular); src ve dst örtüşmez.
void ScaleNv12Fit(const uint8_t* src, uint32_t sw, uint32_t sh, uint8_t* dst, uint32_t dw, uint32_t dh,
                  Nv12ScaleScratch& scratch);

inline void ScaleNv12Fit(const uint8_t* src, uint32_t sw, uint32_t sh, uint8_t* dst, uint32_t dw, uint32_t dh) {
  Nv12ScaleScratch scratch;
  ScaleNv12Fit(src, sw, sh, dst, dw, dh, scratch);
}
