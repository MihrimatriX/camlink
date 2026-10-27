// Kalite modları ve cihaza göre seçim.
// Kimlikler, boyutlar ve bit hızları masaüstüyle aynıdır (desktop/src/CamLink.Desktop.Core/Quality.cs).

/** Küçükten büyüğe: sıra, `pickSupported` için "büyüklük" sırasıdır. */
export const QUALITY_PRESETS = [
  { id: '720p30', label: '720p · 30 fps', width: 1280, height: 720, fps: 30, bitrate: 4_000_000 },
  { id: '720p60', label: '720p · 60 fps', width: 1280, height: 720, fps: 60, bitrate: 6_000_000 },
  { id: '1080p30', label: '1080p · 30 fps', width: 1920, height: 1080, fps: 30, bitrate: 8_000_000 },
  { id: '1080p60', label: '1080p · 60 fps', width: 1920, height: 1080, fps: 60, bitrate: 12_000_000 },
  { id: '1440p30', label: '1440p · 30 fps', width: 2560, height: 1440, fps: 30, bitrate: 20_000_000 },
  { id: '2160p30', label: '4K · 30 fps', width: 3840, height: 2160, fps: 30, bitrate: 35_000_000 },
] as const;
export type QualityId = (typeof QUALITY_PRESETS)[number]['id'];

/** Cihaz yoklaması çalışmazsa sunulan eski dört mod (fpsLimit ve prepare()'in 1280x720 yedeği yine geçerlidir). */
export const LEGACY_QUALITIES: readonly QualityId[] = ['720p30', '720p60', '1080p30', '1080p60'];

export const qualityOf = (id: QualityId) => QUALITY_PRESETS.find((q) => q.id === id) ?? QUALITY_PRESETS[0];

/**
 * `supportedModes` yanıtından (QUALITY_PRESETS sırasında) kimlik listesi. Yoklama çalışmadıysa (hata ya da bozuk yanıt)
 * eski dört mod; çalıştı ama hiçbir mod geçmediyse yalnız her cihazın kaldırdığı 720p30.
 */
export function supportedIds(ok: readonly boolean[] | null | undefined): QualityId[] {
  if (!Array.isArray(ok)) return [...LEGACY_QUALITIES];
  const ids = QUALITY_PRESETS.filter((_, i) => ok[i]).map((p) => p.id);
  return ids.length ? ids : ['720p30'];
}

/**
 * Kayıtlı kalite bu cihazda yoksa istenenden küçük ya da eşit olan en büyük desteklenen, o da yoksa desteklenenlerin
 * en küçüğü. Liste boşsa (henüz okunmadı) her cihazın kaldırdığı 720p30.
 */
export function pickSupported(stored: QualityId, supported: readonly QualityId[]): QualityId {
  const rank = (id: QualityId) => QUALITY_PRESETS.findIndex((q) => q.id === id);
  const sorted = QUALITY_PRESETS.map((q) => q.id).filter((id) => supported.includes(id)); // küçükten büyüğe
  const want = rank(stored); // bilinmeyen kimlik -1: hiçbiri ≤ olmaz, desteklenenlerin en küçüğü döner
  return sorted.filter((id) => rank(id) <= want).pop() ?? sorted[0] ?? QUALITY_PRESETS[0].id;
}

/** Yayına giren kalite: kayıtlı kalitenin bu cihazdaki karşılığı. Hedef türü fark etmez; fps sınırlanmaz. */
export const streamQuality = (quality: QualityId, supported: readonly QualityId[]) => qualityOf(pickSupported(quality, supported));
