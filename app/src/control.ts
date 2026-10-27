// CamLink masaüstünden gelen kontrol komutlarını doğrular ve telefonun durum mesajını üretir.
// Saf mantık: yerel modülü yalnız tür olarak alır. Karşı uç: desktop/src/CamLink.Desktop.Core/PhoneLink.cs.
import type { StreamState, WbMode } from '../modules/camlink-engine/src/CamlinkEngine.types';
import type { Lens } from './lenses';

export type ControlKey = 'lens' | 'zoom' | 'filter' | 'filterValue' | 'torch' | 'af' | 'exposure' | 'wb' | 'stabilization' | 'mirror' | 'quality' | 'mic';

/** Telefonun masaüstüne bildirdiği yetenekler (`caps` mesajı). Komutlar bunlara göre doğrulanır. */
export interface Caps {
  lenses: Pick<Lens, 'id' | 'label' | 'facing'>[];
  filters: { id: string; label: string; adjustable?: boolean }[];
  torch: boolean;
  exposure: { min: number; max: number };
  wb: WbMode[];
  stabilization: boolean;
  qualities: string[];
}

const KEYS: ReadonlySet<string> = new Set<ControlKey>(['lens', 'zoom', 'filter', 'filterValue', 'torch', 'af', 'exposure', 'wb', 'stabilization', 'mirror', 'quality', 'mic']);

const oneOf = (v: unknown, list: readonly string[]) => typeof v === 'string' && list.includes(v);
// NaN her karşılaştırmada false verir, ±Infinity aralığın dışında kalır.
const between = (v: unknown, min: number, max: number) => typeof v === 'number' && v >= min && v <= max;

/**
 * `{ t: 'set', key, value }` komutunu doğrular; geçerliyse `{ key, value }`, değilse null (komut yok sayılır).
 * Bilinen anahtar, doğru tür ve aralık şart: zoom `zoom` aralığında, exposure `caps.exposure` aralığında,
 * lens/filter/wb/quality `caps` listelerinde, filterValue 0–1, kalanlar boolean.
 * Anahtarlar kümeyle aranır; `__proto__` gibi kalıtılmış adlar geçmez.
 */
export function validateSet(msg: unknown, caps: Caps, zoom: { min: number; max: number }): { key: ControlKey; value: string | number | boolean } | null {
  if (typeof msg !== 'object' || msg === null) return null;
  const { t, key, value } = msg as Record<string, unknown>;
  if (t !== 'set' || typeof key !== 'string' || !KEYS.has(key)) return null;
  const k = key as ControlKey;
  let ok: boolean;
  switch (k) {
    case 'lens':
      ok = oneOf(value, caps.lenses.map((l) => l.id));
      break;
    case 'filter':
      ok = oneOf(value, caps.filters.map((f) => f.id));
      break;
    case 'wb':
      ok = oneOf(value, caps.wb);
      break;
    case 'quality':
      ok = oneOf(value, caps.qualities);
      break;
    case 'zoom':
      ok = between(value, zoom.min, zoom.max);
      break;
    case 'exposure':
      ok = between(value, caps.exposure.min, caps.exposure.max);
      break;
    case 'filterValue':
      ok = between(value, 0, 1);
      break;
    default: // torch, af, stabilization, mirror, mic
      ok = typeof value === 'boolean';
  }
  return ok ? { key: k, value: value as string | number | boolean } : null;
}

/** Telefonun güncel durumu (`state` mesajının alanları). */
export interface StateInput {
  lens: string;
  zoom: number;
  zoomMin: number;
  zoomMax: number;
  filter: string;
  filterValue: number;
  torch: boolean;
  af: boolean;
  exposure: number;
  wb: WbMode;
  stabilization: boolean;
  mirror: boolean;
  quality: string;
  mic: boolean;
  streaming: boolean;
  /** 0–100; bilinmiyorsa -1. */
  battery: number;
  charging: boolean;
  /** Android termal durumu: 0 normal … 3 ciddi, 4+ kritik. */
  thermal: number;
  /** Anlık bit hızı (bps); verilirse masaüstü Mb/s gösterir. */
  bitrate?: number;
}

/** `state` mesajı. Alanlar tek tek alınır: store'dan geniş bir nesne verilse bile kanala yalnız sözleşmedeki alanlar çıkar. */
export function buildState(s: StateInput): Record<string, unknown> {
  const { lens, zoom, zoomMin, zoomMax, filter, filterValue, torch, af, exposure, wb, stabilization, mirror, quality, mic, streaming, battery, charging, thermal, bitrate } = s;
  return { t: 'state', lens, zoom, zoomMin, zoomMax, filter, filterValue, torch, af, exposure, wb, stabilization, mirror, quality, mic, streaming, battery, charging, thermal, bitrate };
}

/** Masaüstü sessiz kalan telefonu 6 sn'de hayalet sayar; hızlı yeniden bağlanış o arada 4009 alabilir. */
const BUSY_AFTER_MS = 8000;
const NOT_FOUND_AFTER_MS = 10_000;

/**
 * Kontrol kanalı kapanınca ne zaman yeniden denenir ve kullanıcıya ne söylenir.
 * 4009 (başka telefon bağlı) 8 sn boyunca 2 sn aralıkla sessizce denenir, sonra "meşgul" ve 10 sn aralık.
 * Diğer kopuşlar 2 sn; `failingSince`'ten (son başarılı bağlantıdan ya da başlangıçtan) 10 sn geçtiyse "bulunamadı".
 */
export function nextRetry(
  code: number,
  now: number,
  busySince: number | undefined,
  failingSince: number,
): { delay: number; busySince?: number; busy: boolean; notFound: boolean } {
  if (code === 4009) {
    const since = busySince ?? now;
    const busy = now - since >= BUSY_AFTER_MS;
    return { delay: busy ? 10_000 : 2000, busySince: since, busy, notFound: false };
  }
  return { delay: 2000, busy: false, notFound: now - failingSince >= NOT_FOUND_AFTER_MS };
}

/** USB'de bu süre canlıya geçemeyen yayın Wi‑Fi'a kaçar (ör. /info yanıt veriyor ama RTMP alıcısı yanıt vermiyor). */
const USB_STUCK_MS = 10_000;

const stuck = (s: StreamState) => s === 'connecting' || s === 'retrying';

/**
 * Yayının ne zamandan beri canlı olmadığı (bağlanıyor ya da yeniden deniyor). RootEncoder her denemede önce
 * "connecting" (yanıt vermeyen eşte 5 sn), sonra "retrying" bildirir; ikisi birlikte sayılır. Canlı, duran ya da
 * düşen yayın sayacı sıfırlar.
 */
export const notLiveSince = (state: StreamState, prev: number | undefined, now: number) => (stuck(state) ? (prev ?? now) : undefined);

/**
 * "Canlı değil" sayacı yayın adresine bağlıdır: adres değişince (USB ↔ Wi‑Fi yeniden başlatma) sıfırdan sayılır.
 * Yoksa takılı SRT'nin süresi yeni RTMP yayınına taşınır ve USB, bağlanırken hemen engellenirdi.
 */
export function stuckTick(prev: { since?: number; url: string | null }, state: StreamState, url: string | null, now: number) {
  return { since: notLiveSince(state, prev.url === url ? prev.since : undefined, now), url };
}

/** Uygulama arkadayken masaüstüne bu süre ulaşılamazsa yayın kapanır (pil); öne gelince kendiliğinden sürer. */
const IDLE_OUT_MS = 120_000;

/** Ekran kapalı ya da uygulama arkada, yayın sürüyor ve masaüstü 2 dk'dır yok: yayını ve ön plan hizmetini kapat. */
export function shouldIdleOut(p: { background: boolean; streaming: boolean; lostSince?: number; now: number }): boolean {
  return p.background && p.streaming && p.lostSince !== undefined && p.now - p.lostSince >= IDLE_OUT_MS;
}

/**
 * USB'de takılan yayın Wi‑Fi'a geçmeli mi: USB adresinde 10 sn'dir canlı değil ve Wi‑Fi adresi biliniyor.
 * USB satırından eşleşmişse kayıtlı adres 127.0.0.1'dir; o zaman kaçılacak bir Wi‑Fi adresi yoktur.
 */
export function shouldLeaveUsb(p: { state: StreamState; activeUrl: string | null; notLiveSince?: number; now: number; wifiHost: string }): boolean {
  return (
    stuck(p.state) &&
    !!p.activeUrl?.startsWith('rtmp:') &&
    !!p.wifiHost &&
    p.wifiHost !== '127.0.0.1' &&
    p.notLiveSince !== undefined &&
    p.now - p.notLiveSince >= USB_STUCK_MS
  );
}

/** Yayın başlatılamadıysa yeniden denemeden önceki bekleme: art arda 1., 2., 3.+ hatada 2, 5, 10 sn. */
export const retryAfterFailure = (failures: number) => (failures <= 1 ? 2000 : failures === 2 ? 5000 : 10_000);

/** Çalışan yayının yeni adrese (kablo takıldı) taşınması için adresin kararlı kalması gereken süre. */
const SWITCH_AFTER_MS = 4000;

/**
 * Masaüstü bağlıyken yayına ne yapılacağı. İstenmiyorsa ya da hedef yoksa hiçbir şey; duruyorsa başlat;
 * adres değiştiyse (USB ↔ Wi‑Fi) yeniden başlat. Kopuk yayın hemen taşınır, canlı yayın yeni adres 4 sn kararlı kalınca.
 * Sürmekte olan yayın aynı adrese ikinci kez başlatılmaz.
 */
export function streamAction(p: {
  state: StreamState;
  wantLive: boolean;
  url: string | null;
  activeUrl: string | null;
  mismatchSince?: number;
  now: number;
}): { action: 'start' | 'restart' | 'none'; mismatchSince?: number } {
  if (!p.wantLive || !p.url) return { action: 'none' };
  if (p.state === 'idle' || p.state === 'failed') return { action: 'start' };
  if (p.activeUrl === p.url) return { action: 'none' };
  if (p.state !== 'live') return { action: 'restart' };
  const since = p.mismatchSince ?? p.now;
  return p.now - since >= SWITCH_AFTER_MS ? { action: 'restart' } : { action: 'none', mismatchSince: since };
}

/**
 * Yayına ses izi konsun mu. CamLink masaüstü sesi çalacak aygıt (VB-Cable) yokken `audio: false` der: ses izi olan akışta
 * FFmpeg görüntüyü ~200 ms geç verir (perf-report Tur 2–3), çalınmayacak ses için bu bedel ödenmez. Eski masaüstü alanı
 * yollamaz (undefined → ses var). OBS ve elle hedef etkilenmez.
 */
export const streamsAudio = (targetKind: string, desktopAudio: boolean | undefined) => targetKind !== 'webcam' || desktopAudio !== false;
