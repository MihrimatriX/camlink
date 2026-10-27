// Telefondaki ham kamera listesini kullanıcıya gösterilecek lens düğmelerine çevirir. Saf mantık: yerel modülü yalnız tür olarak alır.
import type { RawLens } from '../modules/camlink-engine/src/CamlinkEngine.types';

export interface Lens extends RawLens {
  label: string;
  /** Ana arka kameranın odak uzaklığına oran (0,5 = geniş açı, 5 = 5× tele). */
  ratio: number;
}

const FACING_ORDER = { back: 0, front: 1, external: 2 };

/**
 * Etiketler `eqMm` oranından çıkar: ana = ilk arka, fiziksel olmayan lens; < 0.8 "Geniş açı", > 1.8 "Tele N×", diğer "Ana".
 * Çok kameralı telefonda mantıksal kamera ile aynı odaktaki fiziksel alt kamera tek düğme olur (mantıksal kalır).
 * Sıra: arka (orana göre artan), ön, harici.
 */
export function labelLenses(raw: RawLens[]): Lens[] {
  const main = raw.find((l) => l.facing === 'back' && !l.physical) ?? raw.find((l) => l.facing === 'back');
  const byKey = new Map<string, Lens>();
  for (const l of raw) {
    // Odak uzaklığı bilinmiyorsa (eqMm 0) oran 1 sayılır; NaN/Infinity etikete sızmaz.
    const ratio = main && main.eqMm > 0 && l.eqMm > 0 ? Math.round((l.eqMm / main.eqMm) * 10) / 10 : 1;
    const label =
      l.facing === 'front' ? 'Ön' : l.facing === 'external' ? 'Harici' : ratio < 0.8 ? 'Geniş açı' : ratio > 1.8 ? `Tele ${ratio}×` : 'Ana';
    const key = `${l.facing}|${ratio}`;
    const cur = byKey.get(key);
    if (!cur || (cur.physical && !l.physical)) byKey.set(key, { ...l, label, ratio });
  }
  return [...byKey.values()].sort((a, b) => FACING_ORDER[a.facing] - FACING_ORDER[b.facing] || a.ratio - b.ratio);
}

/**
 * Lens düğmelerinin kısa etiketleri (telefonda ve masaüstünde aynı): arkada oran ("0,5×", "1×", "3×"), önde "Ön".
 * Aynı etiket iki kez çıkarsa (iki ön kamera) sonuna oran eklenir. Arka oranlar `labelLenses` sayesinde zaten tekildir.
 */
export function lensButtonLabels(lenses: Lens[]): string[] {
  const x = (r: number) => `${String(r).replace('.', ',')}×`;
  const base = lenses.map((l) => (l.facing === 'back' ? x(l.ratio) : l.label));
  return base.map((b, i) => (base.indexOf(b) === base.lastIndexOf(b) ? b : `${b} ${x(lenses[i].ratio)}`));
}
