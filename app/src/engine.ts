import { useEffect } from 'react';
import { CamlinkEngine, type WbMode } from '../modules/camlink-engine';
import { toast } from './components/ui';
import { streamsAudio, type ControlKey } from './control';
import { usbTarget } from './discovery';
import { labelLenses } from './lenses';
import { streamUrl } from './obsSetup';
import { QUALITY_PRESETS, streamQuality, supportedIds, type QualityId } from './quality';
import { useApp, type Settings } from './store';

const active = () => {
  const st = useApp.getState().state;
  return st !== 'idle' && st !== 'failed';
};

let lensesAsked = false;

// Cihazın kalite modları açılışta bir kez okunur; yayın başlamadan önce bitmesi beklenir (yoksa yayın 720p30'da açılıp kalite değişirdi).
let modesProbe: Promise<void> | undefined;
function loadQualities() {
  modesProbe ??= Promise.resolve()
    .then(() => CamlinkEngine.supportedModes(QUALITY_PRESETS.map((p): [number, number, number] => [p.width, p.height, p.fps])))
    .catch((e) => {
      // Yoklama çalışamadı: eski dört mod sunulur ve bir sonraki yayın başlatmada yeniden denenir (kalıcı olmasın).
      console.warn('Kalite modları okunamadı, eski dört mod sunuluyor', e);
      modesProbe = undefined;
      return null;
    })
    .then((ok) => void useApp.setState({ qualities: supportedIds(ok) }));
  return modesProbe;
}

/** Motor olaylarını store'a bağlar ve kalite ayarlarını uygular. */
export function useEngineBridge() {
  useEffect(() => {
    void loadQualities();
    const subs = [
      CamlinkEngine.addListener('onStatus', (e) => {
        const prev = useApp.getState().state;
        useApp.setState({ state: e.state, message: e.message });
        if (e.state === 'live' && prev !== 'live') {
          const kind = useApp.getState().targetKind;
          toast(kind === 'webcam' ? 'Bağlandı, kamera bilgisayarda' : kind === 'obs' ? "OBS'e bağlandı, yayın başladı" : 'Bağlandı, yayın başladı');
        }
        if (e.state === 'failed') toast(e.message || 'Bağlantı kurulamadı', 'error');
      }),
      CamlinkEngine.addListener('onStats', (e) => useApp.setState({ stats: e })),
      CamlinkEngine.addListener('onCameraInfo', (e) => {
        useApp.setState({ camera: e, ...(e.pip === false ? { pip: false } : null) });
        // Lens listesi çalışırken değişmez; ilk kamera bilgisinde bir kez okunur.
        if (!lensesAsked) {
          lensesAsked = true;
          Promise.resolve()
            .then(() => CamlinkEngine.getLenses())
            .then((raw) => useApp.setState({ lenses: labelLenses(raw) }))
            .catch(() => (lensesAsked = false));
        }
      }),
      CamlinkEngine.addListener('onError', (e) => toast(e.message, 'error')),
    ];
    return () => subs.forEach((s) => s.remove());
  }, []);

  // Kalite, ses ve yön değişince önizlemeyi yeni ayarla kur. Yayın sürerken motor ayarı reddeder;
  // yayın her başlayışta ayarı zaten uygular (`start`).
  const quality = useApp((s) => s.quality);
  const qualities = useApp((s) => s.qualities);
  const bitrateOverride = useApp((s) => s.bitrateOverride);
  const portrait = useApp((s) => s.portrait);
  const loaded = useApp((s) => s.loaded);
  const audioTrack = useApp((s) => streamsAudio(s.targetKind, s.desktopAudio));
  const webcam = useApp((s) => s.targetKind === 'webcam');
  useEffect(() => {
    // Mod listesi gelmeden kurulmaz: kayıtlı kalite önce 720p30'a düşüp sonra yeniden kurulmasın.
    if (loaded && qualities.length && !active()) void configureEngine();
  }, [loaded, quality, qualities, bitrateOverride, portrait, audioTrack, webcam]);
}

/**
 * Gönderilecek bit hızı: elle seçilen ya da kalitenin önerdiği. CamLink masaüstünde gönderilen kalitenin önerisini
 * aşamaz (1080p30 → 8 Mb/s): fazlası yalnız ağı zorlar (cihazda 16 Mb/s seçimi görüldü).
 */
export function streamBitrate(s: Pick<Settings, 'quality' | 'targetKind' | 'bitrateOverride'> & { qualities: readonly QualityId[] }) {
  const q = streamQuality(s.quality, s.qualities);
  const b = s.bitrateOverride ?? q.bitrate;
  return s.targetKind === 'webcam' ? Math.min(b, q.bitrate) : b;
}

function configureEngine() {
  const s = useApp.getState();
  const q = streamQuality(s.quality, s.qualities);
  return CamlinkEngine.configure({
    width: q.width,
    height: q.height,
    fps: q.fps,
    bitrate: streamBitrate(s),
    // Ses izi yalnız gerekiyorsa (masaüstü sesi çalabiliyorsa); mikrofon (izin varsa) açılır, "Telefon mikrofonu" ayarı susturur.
    audio: streamsAudio(s.targetKind, s.desktopAudio),
    sensorOrientation: s.targetKind === 'webcam',
    portrait: s.portrait,
  }).catch((e) => toast(e?.message ?? 'Ayar uygulanamadı', 'error'));
}

/** Kayıtlı efekt, ayna, pozlama, lens, beyaz dengesi ve sabitleme ayarlarını kameraya uygular. */
export async function applyCameraPrefs() {
  const s = useApp.getState();
  // Beyaz dengesi ve sabitleme kamera kapalıyken false döner ama değeri saklar, açılınca uygular.
  await Promise.all([
    CamlinkEngine.setFilter(s.filterId, s.filterValue),
    CamlinkEngine.setMirror(s.mirror),
    CamlinkEngine.setExposure(Math.round(s.exposure)),
    CamlinkEngine.setWhiteBalance(s.wb),
    CamlinkEngine.setStabilization(s.stabilization),
    CamlinkEngine.setMicMuted(!s.audio),
    s.lens ? CamlinkEngine.setLens(s.lens) : null,
  ]).catch(() => {});
}

let usbBlocked = false;
/** true: USB görüntü yolu takıldı, yeni oturuma (welcome) dek Wi‑Fi kullanılır. Kontrol kanalı USB'de kalabilir. */
export const setUsbBlocked = (v: boolean) => {
  usbBlocked = v;
};

/** Seçili hedefin yayın adresi. CamLink masaüstüne USB kablosu varsa RTMP (adb reverse), yoksa Wi‑Fi'dan SRT. */
export function targetUrl(): string | null {
  const s = useApp.getState();
  if (!s.pcHost.trim()) return null;
  const usb = s.targetKind === 'webcam' && !usbBlocked ? usbTarget() : undefined;
  return usb ? streamUrl('127.0.0.1', usb.port, 120, 'usb') : streamUrl(s.pcHost, s.srtPort);
}

let activeUrl: string | null = null;
/** Sürmekte olan yayının adresi (yoksa null). */
export const activeStreamUrl = () => activeUrl;

// Başlat/durdur çağrıları sıraya girer: "durdur, sonra başlat" bölünmez, iki yayın üst üste açılmaz.
let chain: Promise<unknown> = Promise.resolve();
function serial<T>(fn: () => Promise<T>): Promise<T> {
  const p = chain.then(fn, fn);
  chain = p.catch(() => {});
  return p;
}

// Aynı başlatma hatası art arda yinelenince (masaüstü 2 sn'de bir dener) bildirim bir kez gösterilir.
let lastStartError: string | null = null;
/** Elle yeniden deneme ve yeni eşleşme: aynı hata yeniden bildirilsin. */
export const resetStartError = () => {
  lastStartError = null;
};

async function start(): Promise<boolean> {
  const url = targetUrl();
  if (!url) return false;
  if (active()) return true; // zaten yayında
  await loadQualities();
  await configureEngine();
  await CamlinkEngine.setMicMuted(!useApp.getState().audio).catch(() => {});
  useApp.setState({ state: 'connecting', message: null });
  activeUrl = url;
  try {
    // Ad, ekran kapalıyken yayını sürdüren bildirimde görünür.
    const s = useApp.getState();
    await CamlinkEngine.startStream(url, s.targetKind === 'webcam' ? s.targetPcName || null : s.targetKind === 'obs' ? `OBS: ${s.targetLabel}` : s.pcHost);
    lastStartError = null;
  } catch (e: any) {
    activeUrl = null;
    const msg: string = e?.message ?? 'Yayın başlatılamadı';
    useApp.setState({ state: 'failed', message: msg });
    if (msg !== lastStartError) toast(msg, 'error');
    lastStartError = msg;
  }
  return true;
}

async function stop() {
  await CamlinkEngine.stopStream().catch(() => {});
  activeUrl = null;
  useApp.setState({ state: 'idle', stats: undefined });
}

/** false: hedef seçilmemiş. */
export const startStreaming = () => serial(start);
export const stopStreaming = () => serial(stop);
/** Yeni adrese taşır (USB ↔ Wi‑Fi). */
export const restartStreaming = () =>
  serial(async () => {
    await stop();
    return start();
  });

/**
 * Masaüstünün ses isteği (`welcome`/`config`): ses izinin varlığı değiştiyse ve yayın sürüyorsa yayın yeniden kurulur
 * (ses izi yalnız yayın başında eklenip çıkarılabilir); değişmediyse hiçbir şey yapılmaz.
 */
export const setDesktopAudio = (on: boolean | undefined) =>
  serial(async () => {
    const s = useApp.getState();
    const changed = streamsAudio(s.targetKind, on) !== streamsAudio(s.targetKind, s.desktopAudio);
    const live = changed && active();
    if (live) await stop();
    useApp.setState({ desktopAudio: on });
    if (live) await start();
  });

/** Kalite, ses ya da yön: yayın sürüyorsa durdurur, kaydeder, yeni ayarla yeniden başlatır. */
export const changeStreamConfig = (patch: Partial<Pick<Settings, 'quality' | 'bitrateOverride' | 'portrait'>>) =>
  serial(async () => {
    const live = active();
    if (live) await stop();
    useApp.getState().save(patch);
    if (live) await start();
  });

const save = (patch: Partial<Settings>) => useApp.getState().save(patch);
const patchCamera = (patch: { zoom?: number; torch?: boolean }) => useApp.setState((s) => (s.camera ? { camera: { ...s.camera, ...patch } } : {}));

/** Telefondaki tuşların ve masaüstü komutlarının ortak eylemleri; anahtarlar kontrol protokolündeki adlar. */
export const control: { [K in ControlKey]: (v: any) => Promise<unknown> } = {
  lens: async (id: string) => {
    save({ lens: id });
    await CamlinkEngine.setLens(id);
  },
  zoom: async (z: number) => {
    patchCamera({ zoom: z });
    await CamlinkEngine.setZoom(z, true);
  },
  filter: async (id: string) => {
    save({ filterId: id });
    await CamlinkEngine.setFilter(id, useApp.getState().filterValue);
  },
  filterValue: async (v: number) => {
    save({ filterValue: v });
    await CamlinkEngine.setFilter(useApp.getState().filterId, v);
  },
  torch: async (on: boolean) => patchCamera({ torch: await CamlinkEngine.setTorch(on) }),
  af: async (on: boolean) => {
    save({ autoFocus: on });
    await CamlinkEngine.setAutoFocus(on);
  },
  exposure: async (v: number) => {
    const n = Math.round(v);
    save({ exposure: n });
    await CamlinkEngine.setExposure(n);
  },
  // false dönmesi "desteklenmiyor" demek değil: kamera kapalıyken değer saklanır, açılınca uygulanır.
  wb: async (mode: WbMode) => {
    save({ wb: mode });
    await CamlinkEngine.setWhiteBalance(mode);
  },
  stabilization: async (on: boolean) => {
    save({ stabilization: on });
    await CamlinkEngine.setStabilization(on);
  },
  mirror: async (on: boolean) => {
    save({ mirror: on });
    await CamlinkEngine.setMirror(on);
  },
  quality: (q: QualityId) => changeStreamConfig({ quality: q, bitrateOverride: undefined }),
  // Yayını yeniden kurmadan susturur: masaüstünün alıcısı ses izinin kaybolmasına yeniden başlayarak (~6 sn) tepki veriyordu.
  mic: async (on: boolean) => {
    save({ audio: on });
    await CamlinkEngine.setMicMuted(!on);
  },
};
