// Paylaşılan bellek: Global\CamLinkVcam, sürüm 2. DLL oluşturur, masaüstü açar.
// Bayt düzeni sabittir; C# tarafı (CamLink.Desktop.Core/FrameBus.cs) aynı konumları kullanır.
// Sürüm 1'den farkı: kapasite 3840x2160 NV12 (önce 1920x1080) ve 48..59'da tüketicinin istediği tür.
//
// Yazar sözleşmesi (masaüstü ve prob buna uyar):
// - Erişim: OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, ...) ve aynı haklarla MapViewOfFile;
//   FILE_MAP_ALL_ACCESS istenmez (Interactive Users'a DACL'de yalnız GRGW verilir, reddedilir).
// - Başlık hazır sayılır ancak magic == CLVC_MAGIC iken. DLL bölümü akış başlarken oluşturur (magic en son).
// - Açılışta seq tekse (önceki yazar kare ortasında çöktü) ilk kareden önce seq bir kez artırılır.
// - Her kare: seq++ (tek) → width/height/fps → NV12'yi (Y düzlemi width*height, ardından UV) ofset 64'e
//   kopyala → seq++ (çift) → writerTick = GetTickCount64(). Başlık alanları tek penceresinde yazılır, böylece
//   DLL yeni boyutu eski kareyle eşleştirmez. Artırımlar tam bariyerli (Interlocked) yapılır.
// - Kare yazarın kendi boyutundadır (çift, > 0, en çok CLVC_CAPACITY bayt). DLL onu tüketicinin seçtiği türe
//   ölçekler: en-boy korunur, kalan kısım siyah.
// - reqW/reqH/reqFps: tüketicinin seçtiği tür. DLL akış başlarken yazar, akış durunca üçünü 0 yapar; yazar
//   yalnız okur (0 = şu an tüketici yok).
// - DLL hiç beklemez: writerTick 2000 ms'den eskiyse (ya da 2000 ms'den fazla ilerideyse) ya da boyut
//   geçersizse siyah kare verir.
#pragma once
#include <stdint.h>

#define CLVC_NAME L"Global\\CamLinkVcam"
#define CLVC_MAGIC 0x43564C43u      // "CLVC"
#define CLVC_VERSION 2u
#define CLVC_HEADER 64u
#define CLVC_CAPACITY (3840u * 2160u * 3u / 2u)   // 12 441 600 bayt NV12
#define CLVC_TOTAL (CLVC_HEADER + CLVC_CAPACITY)

typedef struct {
  uint32_t magic, version, width, height, fps, reserved; // 0..23
  volatile int64_t seq;            // 24: tek = yazılıyor, çift = hazır
  volatile int64_t writerTick;     // 32: GetTickCount64, yazar
  volatile int64_t consumerTick;   // 40: GetTickCount64, DLL (örnek istendikçe)
  uint32_t reqW, reqH, reqFps;     // 48..59: tüketicinin seçtiği tür (DLL yazar; 0 = akış yok)
  uint32_t pad;                    // 60..63
} ClvcHeader;

#ifdef __cplusplus
#include <cstddef>
static_assert(sizeof(ClvcHeader) == CLVC_HEADER, "ClvcHeader 64 bayt olmalı");
static_assert(offsetof(ClvcHeader, fps) == 16, "fps konumu");
static_assert(offsetof(ClvcHeader, seq) == 24, "seq konumu");
static_assert(offsetof(ClvcHeader, writerTick) == 32, "writerTick konumu");
static_assert(offsetof(ClvcHeader, consumerTick) == 40, "consumerTick konumu");
static_assert(offsetof(ClvcHeader, reqW) == 48 && offsetof(ClvcHeader, reqH) == 52 &&
                  offsetof(ClvcHeader, reqFps) == 56, "req konumları");
#endif
