import { create } from 'zustand';
import { CamlinkEngine } from '../modules/camlink-engine';
import { parseBeacon, usbVote, type Target } from './beacon';

export type { Target } from './beacon';

interface DiscoveryState {
  targets: Target[];
}

export const useDiscovery = create<DiscoveryState>(() => ({ targets: [] }));

const STALE_MS = 7000;
/** USB kablosunda CamLink masaüstü `adb reverse tcp:4468` kurar; telefon kendi 127.0.0.1'inde bilgisayara ulaşır. */
const USB_INFO = 'http://127.0.0.1:4468/info';
let running = false;
let sub: { remove: () => void } | undefined;
let pruneTimer: ReturnType<typeof setInterval> | undefined;
let usbTimer: ReturnType<typeof setInterval> | undefined;
let usb = { on: false, fails: 0 };
let firstProbe: Promise<void> = Promise.resolve();

const isUsb = (t: Target) => t.via === 'usb';

function merge(ip: string, data: string, via: 'wifi' | 'usb' = 'wifi') {
  const { targets } = parseBeacon(ip, data, Date.now(), via);
  const app = (() => {
    try {
      return JSON.parse(data).app;
    } catch {
      return undefined;
    }
  })();
  useDiscovery.setState((s) => {
    // Aynı bilgisayardan aynı yoldan gelen aynı türden eski kayıtlar yenileriyle değiştirilir (kaynak silinmiş olabilir).
    const kind = app === 'camlink-desktop' ? 'webcam' : app === 'camlink-obs' ? 'obs' : null;
    const kept = s.targets.filter((t) => !(t.host === ip && t.kind === kind && t.via === via));
    return { targets: sortTargets([...kept, ...targets]) };
  });
}

const sortTargets = (t: Target[]) =>
  [...t].sort(
    (a, b) =>
      Number(isUsb(b)) - Number(isUsb(a)) ||
      a.pcName.localeCompare(b.pcName, 'tr') ||
      (a.kind === b.kind ? a.port - b.port : a.kind === 'obs' ? -1 : 1),
  );

/** Kablo bağlı ve masaüstü açıksa `/info` yanıt verir. Tek zaman aşımı USB'yi düşürmez (`usbVote`). */
async function probeUsb() {
  let text = '';
  try {
    const r = await fetch(USB_INFO, { signal: AbortSignal.timeout(800) });
    if (r.ok) text = await r.text();
  } catch {
    // Kablo yok, adb reverse kurulmamış ya da masaüstü kapalı.
  }
  if (!running) return;
  const ok = parseBeacon('127.0.0.1', text, Date.now(), 'usb').targets.some((t) => t.kind === 'webcam');
  usb = usbVote(usb, ok);
  if (ok) merge('127.0.0.1', text, 'usb');
  else if (!usb.on && useDiscovery.getState().targets.some(isUsb))
    useDiscovery.setState((s) => ({ targets: s.targets.filter((t) => !isUsb(t)) }));
}

/** USB ile bağlı CamLink masaüstü (varsa). */
export const usbTarget = () => useDiscovery.getState().targets.find((t) => t.kind === 'webcam' && isUsb(t));

/** Arama başladıktan sonraki ilk USB yoklamasının sonucu; bağlanmadan önce yol seçimi için beklenir (en çok 800 ms). */
export const usbProbed = () => firstProbe;

export function startDiscovery() {
  if (running) return;
  running = true;
  sub = CamlinkEngine.addListener('onBeacon', (e) => merge(e.ip, e.data));
  try {
    CamlinkEngine.startDiscovery();
  } catch {
    // Eski bir derlemede fonksiyon yoksa arama sessizce devre dışı kalır.
  }
  pruneTimer = setInterval(() => {
    const now = Date.now();
    const { targets } = useDiscovery.getState();
    if (targets.some((t) => now - t.seenAt > STALE_MS))
      useDiscovery.setState((s) => ({ targets: s.targets.filter((t) => now - t.seenAt <= STALE_MS) }));
  }, 2000);
  usb = { on: false, fails: 0 };
  firstProbe = probeUsb();
  usbTimer = setInterval(() => void probeUsb(), 2000);
}

export function stopDiscovery() {
  if (!running) return;
  running = false;
  sub?.remove();
  clearInterval(pruneTimer);
  clearInterval(usbTimer);
  try {
    CamlinkEngine.stopDiscovery();
  } catch {}
  useDiscovery.setState({ targets: [] });
}
