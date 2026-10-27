// Yerel ağ duyurularını hedeflere çevirir. Yerel modüle bağımlı değildir; testlerde doğrudan kullanılır.

/** Telefonun yayın yapabileceği bir hedef: OBS'teki bir CamLink kaynağı ya da CamLink masaüstü uygulamasının webcam alıcısı. */
export interface Target {
  key: string;
  kind: 'obs' | 'webcam';
  host: string;
  port: number;
  /** CamLink masaüstünün kontrol (HTTP+WS) portu. Yalnız webcam hedeflerinde. */
  ctl?: number;
  /** Duyuru Wi‑Fi'dan mı geldi, USB'den mi (adb reverse ile 127.0.0.1). */
  via: 'wifi' | 'usb';
  /** Bilgisayarın adı */
  pcName: string;
  /** Listede görünen ad: OBS kaynağının adı ya da "Webcam · Wi‑Fi" / "Webcam · USB" */
  label: string;
  /** Başka bir telefon şu an bu hedefe yayın yapıyor. */
  busy: boolean;
  /** Alıcı hazır değilse false (masaüstü duyurusunda hep true). */
  ready: boolean;
  seenAt: number;
}

/** Geçerli port (1–65535) ya da varsayılan. */
const port = (v: unknown, d: number) => (typeof v === 'number' && Number.isInteger(v) && v > 0 && v < 65536 ? v : d);

export function parseBeacon(ip: string, data: string, now = Date.now(), via: 'wifi' | 'usb' = 'wifi'): { targets: Target[] } {
  let o: any;
  try {
    o = JSON.parse(data);
  } catch {
    return { targets: [] };
  }
  const name = typeof o?.name === 'string' && o.name ? String(o.name).slice(0, 40) : ip;
  if (o?.app === 'camlink-desktop') {
    return {
      targets: [
        {
          key: `${ip}|webcam|${via}`,
          kind: 'webcam',
          host: ip,
          // Görüntü USB'de RTMP ile (adb reverse), Wi‑Fi'da SRT ile gelir.
          port: via === 'usb' ? port(o.rtmp, 9101) : port(o.srt, 9100),
          ctl: port(o.ctl, 4468),
          via,
          pcName: name,
          label: via === 'usb' ? 'Webcam · USB' : 'Webcam · Wi‑Fi',
          busy: !!o.busy,
          ready: true,
          seenAt: now,
        },
      ],
    };
  }
  if (o?.app === 'camlink-obs' && Array.isArray(o.cams)) {
    return {
      targets: o.cams
        .filter((c: any) => Number.isInteger(c?.port))
        .slice(0, 16)
        .map((c: any) => ({
          key: `${ip}|obs|${c.port}`,
          kind: 'obs' as const,
          host: ip,
          port: c.port,
          via: 'wifi' as const,
          pcName: name,
          label: typeof c.name === 'string' && c.name ? String(c.name).slice(0, 40) : `Kamera ${c.port}`,
          busy: !!c.live,
          ready: true,
          seenAt: now,
        })),
    };
  }
  return { targets: [] };
}

/**
 * USB yoklamasının kararı: tek yanıt USB'yi açar, art arda iki başarısızlık kapatır.
 * Tek bir zaman aşımı yayını Wi‑Fi'a taşıyıp geri getirmesin diye.
 */
export function usbVote(prev: { on: boolean; fails: number }, ok: boolean): { on: boolean; fails: number } {
  if (ok) return { on: true, fails: 0 };
  const fails = prev.fails + 1;
  return { on: prev.on && fails < 2, fails };
}

/**
 * Kayıtlı bilgisayar Wi‑Fi'da yeni bir adresten duyuru yapıyorsa o adres (DHCP değişimi), yoksa null.
 * Kayıtlı adres hâlâ görünüyorsa değişmez: iki ağ kartıyla iki adresten duyuru yapan bilgisayarda adres gidip gelmesin.
 */
export function movedHost(targets: Target[], saved: { kind: Target['kind']; host: string; pcName: string; port: number }): string | null {
  const same = targets.filter((t) => t.via === 'wifi' && t.kind === saved.kind && t.pcName === saved.pcName && t.port === saved.port);
  return same.length && !same.some((t) => t.host === saved.host) ? same[0].host : null;
}
