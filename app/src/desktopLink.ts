// CamLink masaüstüyle kontrol kanalı (ws://PC:4468/phone) ve "QR okut, kendiliğinden yayınla" akışı.
// Karşı uç: desktop/src/CamLink.Desktop.Core/PhoneLink.cs. Kararlar saf fonksiyonlarda (control.ts, beacon.ts); burası yalnız uygular.
import { AppState, Platform } from 'react-native';
import { create } from 'zustand';
import { CamlinkEngine, type WbMode } from '../modules/camlink-engine';
import { buildState, nextRetry, retryAfterFailure, shouldIdleOut, shouldLeaveUsb, streamAction, stuckTick, validateSet, type Caps } from './control';
import { usbProbed, usbTarget } from './discovery';
import { activeStreamUrl, control, resetStartError, restartStreaming, setDesktopAudio, setUsbBlocked, startStreaming, stopStreaming, targetUrl } from './engine';
import { lensButtonLabels } from './lenses';
import type { CamlinkPair } from './pair';
import { streamQuality } from './quality';
import { FILTERS, useApp } from './store';

export const useDesktopLink = create<{
  /** Masaüstü `welcome` dedi; telefon kontrol yuvasının sahibi. */
  connected: boolean;
  /** Masaüstünün bildirdiği bilgisayar adı. */
  pcName?: string;
  /** 8 sn'den uzun süredir başka bir telefon bağlı (4009). */
  busy: boolean;
  /** 10 sn'dir masaüstüne ulaşılamıyor. */
  notFound: boolean;
  /** Görüntünün gittiği yol. */
  via: 'wifi' | 'usb';
  /** Yayın isteniyor mu (arayüz "Yayın durdu — başlat"ı yalnız istenmiyorken gösterir). */
  wantLive: boolean;
}>(() => ({ connected: false, busy: false, notFound: false, via: 'wifi', wantLive: true }));

const WB_MODES: WbMode[] = ['auto', 'daylight', 'cloudy', 'incandescent', 'fluorescent'];
const EXPOSURE = { min: -12, max: 12 };
const phoneName = () => (Platform.OS === 'android' ? Platform.constants.Model : '') || 'Android';

let cfg: { host: string; ctl: number } | null = null;
let ws: WebSocket | null = null;
let retryTimer: ReturnType<typeof setTimeout> | undefined;
let tickTimer: ReturnType<typeof setInterval> | undefined;
let unsubStore: (() => void) | undefined;
let busySince: number | undefined;
let failingSince: number | undefined;
let mismatchSince: number | undefined;
let stuck: { since?: number; url: string | null } = { url: null };
// Art arda başarısız başlatmalar ve bir sonraki denemenin en erken zamanı (2 → 5 → 10 sn).
let startFailures = 0;
let nextStartAt = 0;
let lastState = '';
let lastSentAt = 0;
/** Kullanıcı ya da masaüstü yayını istiyor mu. Açılışta ve eşleşince evet; "durdur" deyince hayır. */
let wantLive = true;
let ensuring = false;

function setWant(on: boolean) {
  wantLive = on;
  if (useDesktopLink.getState().wantLive !== on) useDesktopLink.setState({ wantLive: on });
}

// Bildirimdeki "Durdur": yayın yerelde durdu; niyet de bırakılır ki masaüstü bağlantısı yeniden başlatmasın.
// Modül düzeyinde: arayüz kapansa (uygulama kaydırılıp atılsa) da dinler.
CamlinkEngine.addListener('onRemoteStop', () => {
  setWant(false);
  void stopStreaming();
});

/** Yayın düğmesi: niyeti kaydeder ve uygular. false: hedef seçilmemiş. */
export async function setLive(on: boolean): Promise<boolean> {
  setWant(on);
  if (on) {
    resetStartError();
    startFailures = 0;
    nextStartAt = 0;
    return startStreaming();
  }
  await stopStreaming();
  return true;
}

/** QR'dan (ya da listeden) CamLink masaüstünü kaydeder; bağlantıyı App kurar, yayın `welcome`'dan sonra kendiliğinden başlar. */
export async function pairDesktop(p: CamlinkPair) {
  await stopStreaming();
  setWant(true);
  resetStartError();
  // Masaüstüne bağlanınca kalite cihazın en yüksek modu (liste küçükten büyüğe); sonra telefondan ya da masaüstünden düşürülebilir.
  const s = useApp.getState();
  s.save({ pcHost: p.host, srtPort: p.srt, ctlPort: p.ctl, targetKind: 'webcam', targetLabel: 'CamLink', targetPcName: p.name, quality: s.qualities.at(-1) ?? s.quality });
}

/**
 * "Bağlantıyı kes": kayıtlı hedefi (CamLink masaüstü ya da eski OBS/elle hedef) unutur, yayını durdurur;
 * ana ekran yeniden büyük "Bilgisayara bağlan" kartını gösterir.
 */
export async function forgetDesktop() {
  stopDesktopLink();
  await stopStreaming();
  setWant(true);
  useApp.getState().save({ pcHost: '', srtPort: 9000, ctlPort: 4468, targetKind: 'manual', targetLabel: '', targetPcName: '' });
}

export function startDesktopLink(host: string, ctlPort: number) {
  if (cfg && cfg.host === host && cfg.ctl === ctlPort) return;
  stopDesktopLink();
  cfg = { host, ctl: ctlPort };
  failingSince = Date.now();
  void connect();
}

/** Kanalı kapatır ve tüm zamanlayıcıları siler. Yayına dokunmaz (arka plana geçişte yayın sürebilir). */
export function stopDesktopLink() {
  cfg = null;
  clearTimeout(retryTimer);
  detach();
  const w = ws;
  ws = null;
  w?.close(1000);
  busySince = undefined;
  setUsbBlocked(false);
  // Eski bilgisayarın adı kalmasın: yeniden eşleşince şerit "Bağlanıyor: <eski PC>" demesin.
  useDesktopLink.setState({ connected: false, busy: false, notFound: false, pcName: undefined });
}

/** Bağlı oturumun zamanlayıcısını ve store aboneliğini bırakır. */
function detach() {
  clearInterval(tickTimer);
  tickTimer = undefined;
  unsubStore?.();
  unsubStore = undefined;
  mismatchSince = undefined;
  stuck = { url: null };
}

async function connect() {
  const c = cfg;
  if (!c || ws) return;
  // USB yanıtı en çok 800 ms'de gelir; önce yolu bilmek, Wi‑Fi'da başlayıp hemen USB'ye taşınmayı önler.
  await usbProbed();
  if (cfg !== c || ws) return;
  const usb = usbTarget();
  const sock = new WebSocket(usb ? `ws://127.0.0.1:${usb.ctl ?? 4468}/phone` : `ws://${c.host}:${c.ctl}/phone`);
  ws = sock;
  sock.onopen = () => {
    // RN'de bağlanırken çağrılan close() etkisizdir; bırakılmış soket sonradan açılırsa yuvayı tutmasın diye kapatılır.
    if (sock !== ws) return sock.close(1000);
    send(sock, { t: 'hello', v: 1, app: 'camlink', name: phoneName() });
  };
  sock.onmessage = (e) => onMessage(sock, e.data);
  // Bağlanamayınca da (error ardından) close gelir; yeniden deneme yalnız burada kurulur.
  sock.onclose = (e) => onClose(sock, e.code);
}

function send(sock: WebSocket | null, m: object) {
  if (sock && sock.readyState === WebSocket.OPEN) sock.send(JSON.stringify(m));
}

function onMessage(sock: WebSocket, data: unknown) {
  if (sock !== ws) return;
  let m: any;
  try {
    m = JSON.parse(String(data));
  } catch {
    return;
  }
  switch (m?.t) {
    case 'welcome':
      detach();
      busySince = failingSince = undefined;
      // Yeni oturum: USB yeniden denenir (masaüstü yeniden başlamış olabilir), başlatma beklemesi sıfırlanır.
      setUsbBlocked(false);
      startFailures = 0;
      nextStartAt = 0;
      useDesktopLink.setState({
        connected: true,
        busy: false,
        notFound: false,
        pcName: typeof m.name === 'string' && m.name ? m.name.slice(0, 40) : undefined,
      });
      // Yayın sırasına `ensure`'dan önce girer: ilk yayın doğru ses iziyle açılır.
      void setDesktopAudio(typeof m.audio === 'boolean' ? m.audio : undefined);
      sendCaps();
      sendState(true);
      // Masaüstü 6 sn sessiz kalan telefonu hayalet sayar; 2 sn'de bir durum hem kalp atışı hem yayın denetimi.
      tickTimer = setInterval(() => {
        sendState(true);
        void ensure();
      }, 2000);
      unsubStore = useApp.subscribe((s, prev) => {
        // Pozlama aralığı ve fener ön/arka kamerada farklı olabilir; lens değişince yetenekler yeniden yollanır.
        // Kalite listesi açılışta bir kez gelir; masaüstü önce boş görmüş olabilir.
        if (s.lenses !== prev.lenses || s.qualities !== prev.qualities || s.camera?.lens !== prev.camera?.lens || s.camera?.facing !== prev.camera?.facing) sendCaps();
        sendState(false);
      });
      void ensure();
      break;
    case 'config':
      // Masaüstünde ses aygıtı (VB-Cable) takıldı ya da kalktı: ses izi buna göre (yalnız değişirse yayın yeniden kurulur).
      if (typeof m.audio === 'boolean') void setDesktopAudio(m.audio);
      break;
    case 'set':
      void applySet(m);
      break;
    case 'start':
      void setLive(true);
      break;
    case 'stop':
      void setLive(false);
      break;
    case 'ping':
      send(sock, { t: 'pong' });
      break;
  }
}

function onClose(sock: WebSocket, code: number) {
  if (sock !== ws) return; // bizim kapattığımız ya da yerine yenisi açılmış eski soket
  ws = null;
  detach();
  const now = Date.now();
  failingSince ??= now;
  const r = nextRetry(code, now, busySince, failingSince);
  busySince = r.busySince;
  // Yuva başka bir telefonda: iki telefon aynı alıcıya yayın yapmasın. Niyet korunur; yuva boşalınca yeniden başlar.
  if (r.busy && !useDesktopLink.getState().busy) void stopStreaming();
  // Ekran kapalı/arkada ve masaüstü 2 dk'dır yok: kamera, uyanık kalma kilidi ve hizmet boşuna çalışmasın.
  // Niyet korunur: uygulama öne gelince zamanlayıcılar sürer, kanal kurulunca yayın kendiliğinden başlar.
  const st = useApp.getState().state;
  if (shouldIdleOut({ background: AppState.currentState !== 'active', streaming: st !== 'idle' && st !== 'failed', lostSince: failingSince, now }))
    void stopStreaming();
  useDesktopLink.setState({ connected: false, busy: r.busy, notFound: r.notFound });
  clearTimeout(retryTimer);
  retryTimer = setTimeout(() => void connect(), r.delay);
}

function caps(): Caps {
  const s = useApp.getState();
  const labels = lensButtonLabels(s.lenses);
  return {
    lenses: s.lenses.map((l, i) => ({ id: l.id, label: labels[i], facing: l.facing })),
    filters: FILTERS,
    torch: s.camera?.facing !== 'front',
    exposure: EXPOSURE,
    wb: WB_MODES,
    stabilization: true,
    // Bu cihazın kodlayabildiği modlar (açılışta okunur); fps sınırı yok.
    qualities: s.qualities,
  };
}

function sendCaps() {
  send(ws, { t: 'caps', ...caps() });
}

/** Durum değiştiyse (ya da kalp atışında her durumda) yollar. */
function sendState(force: boolean) {
  const s = useApp.getState();
  const c = s.camera;
  const st = s.stats;
  const json = JSON.stringify(
    buildState({
      lens: c?.lens ?? s.lens,
      zoom: c?.zoom ?? 1,
      zoomMin: c?.zoomMin ?? 1,
      zoomMax: c?.zoomMax ?? 1,
      filter: s.filterId,
      filterValue: s.filterValue,
      torch: !!c?.torch,
      af: s.autoFocus,
      exposure: s.exposure,
      wb: s.wb,
      stabilization: s.stabilization,
      mirror: s.mirror,
      quality: streamQuality(s.quality, s.qualities).id,
      mic: s.audio && s.micOk,
      streaming: s.state !== 'idle' && s.state !== 'failed',
      battery: st?.battery ?? -1,
      charging: !!st?.charging,
      thermal: st?.thermal ?? 0,
      bitrate: st?.bitrate,
    }),
  );
  // Değişmese de 1,5 sn'den eskiyse yollanır: yerel istatistik olayı (2 sn'de bir, ana iş parçacığından) JS zamanlayıcıları
  // dursa bile kalp atışını sürdürür; masaüstü 6 sn sessiz kalan telefonu düşürür.
  if (!force && json === lastState && Date.now() - lastSentAt < 1500) return;
  lastState = json;
  lastSentAt = Date.now();
  if (ws?.readyState === WebSocket.OPEN) ws.send(json);
}

async function applySet(m: unknown) {
  const c = useApp.getState().camera;
  const k = caps();
  const v = validateSet(m, k, { min: c?.zoomMin ?? 1, max: c?.zoomMax ?? 1 });
  // Ön kamerada fener yok; komut yok sayılır (yoksa telefonda hata bildirimi çıkar).
  if (!v || (v.key === 'torch' && !k.torch)) return;
  await control[v.key](v.value).catch(() => {});
  sendState(true);
}

/** Bağlıyken yayını istenen durumda tutar: duruyorsa başlatır, yol değiştiyse (USB ↔ Wi‑Fi) taşır. */
async function ensure() {
  if (ensuring || !useDesktopLink.getState().connected) return;
  const now = Date.now();
  const s = useApp.getState();
  stuck = stuckTick(stuck, s.state, activeStreamUrl(), now);
  // USB'de görüntü gitmiyorsa (kanal çalışsa bile) Wi‑Fi'a kaç; aşağıdaki karar kopuk yayını hemen taşır.
  if (shouldLeaveUsb({ state: s.state, activeUrl: activeStreamUrl(), notLiveSince: stuck.since, now, wifiHost: s.pcHost })) setUsbBlocked(true);
  const url = targetUrl();
  const via = url?.startsWith('rtmp:') ? 'usb' : 'wifi';
  if (useDesktopLink.getState().via !== via) useDesktopLink.setState({ via });
  const r = streamAction({ state: s.state, wantLive, url, activeUrl: activeStreamUrl(), mismatchSince, now });
  mismatchSince = r.mismatchSince;
  if (r.action === 'none' || (r.action === 'start' && now < nextStartAt)) return;
  ensuring = true;
  try {
    await (r.action === 'start' ? startStreaming() : restartStreaming());
    if (useApp.getState().state === 'failed') nextStartAt = Date.now() + retryAfterFailure(++startFailures);
    else startFailures = 0;
  } finally {
    ensuring = false;
    // Yeni yayının takılma süresi sıfırdan sayılır (ensuring sürerken örnek alınmadı).
    stuck = { url: null };
  }
}
