/** StudioHub'ın eşleştirme kodu (DeckPad ile aynı biçim). OBS WebSocket adresi ve şifresini taşır. */
export interface PairInfo {
  host: string;
  name: string;
  obsPort: number;
  obsPassword: string;
}

const num = (v: string | null, d: number) => {
  const n = Number(v);
  return Number.isInteger(n) && n > 0 && n < 65536 ? n : d;
};

export function parsePairQr(data: string): PairInfo | null {
  const text = data.trim();
  if (!/^studiohub:\/\/pair\?/i.test(text)) return null;
  const q = new URLSearchParams(text.slice(text.indexOf('?') + 1));
  const host = q.get('host');
  if (!host) return null;
  return {
    host,
    name: q.get('name') || host,
    obsPort: num(q.get('obs'), 4455),
    obsPassword: q.get('pass') ?? '',
  };
}

/** Sorgu değeri: yüzde-kodlu çözülür; bozuk dizi taramayı düşürmez, olduğu gibi kalır. */
const decode = (v: string) => {
  try {
    return decodeURIComponent(v);
  } catch {
    return v;
  }
};

/** Port parametresi: yoksa varsayılan, varsa 1–65535 arası tam sayı olmalı (yoksa null). */
const portParam = (v: string | undefined, d: number) => {
  if (v === undefined) return d;
  const n = /^\d{1,5}$/.test(v) ? Number(v) : 0;
  return n > 0 && n < 65536 ? n : null;
};

/** CamLink masaüstüyle eşleşme: Wi‑Fi adresi, bilgisayar adı, kontrol ve SRT portu. */
export type CamlinkPair = { host: string; name: string; ctl: number; srt: number };

/**
 * CamLink masaüstünün QR kodu: `camlink://pair?host=IP&name=PC&ctl=4468&srt=9100`.
 * Ad yüzde-kodludur (Uri.EscapeDataString). Sorgu elle ayrıştırılır: React Native'in URLSearchParams'ı
 * bozuk yüzde dizisinde hata fırlatır, Node'unki fırlatmaz; tarama iki yerde de aynı davranmalı.
 */
export function parseCamlinkQr(data: string): CamlinkPair | null {
  const m = /^camlink:\/\/pair\?([^#]*)/i.exec(data.trim());
  if (!m) return null;
  const q = new Map<string, string>();
  for (const part of m[1].split('&')) {
    const i = part.indexOf('=');
    if (i > 0 && !q.has(part.slice(0, i))) q.set(part.slice(0, i), decode(part.slice(i + 1)));
  }
  // Adres IPv4 ya da bilgisayar adı; URL kurarken port/yol sızdıramaz.
  const host = q.get('host')?.trim();
  if (!host || !/^[A-Za-z0-9._-]+$/.test(host)) return null;
  const ctl = portParam(q.get('ctl'), 4468);
  const srt = portParam(q.get('srt'), 9100);
  if (ctl === null || srt === null) return null;
  return { host, name: q.get('name')?.trim().slice(0, 40) || host, ctl, srt };
}
