// Yayın stüdyosu donanımı: koyu mavi eloksal gövde, arkadan aydınlatmalı tuşlar, tally ışıkları.
export const navy = {
  // Gövde ve paneller
  background: '#0B1530',
  chassisTop: '#13224A',
  chassisBottom: '#08112A',
  faceplate: '#0E1A3A',
  well: '#050C1F',
  surface: '#111F3A',
  surfaceHigh: '#1A2D52',
  outline: '#2B4374',
  bevelLight: 'rgba(160, 196, 255, 0.14)',
  bevelDark: 'rgba(0, 4, 14, 0.65)',
  // Durum gösterge camı
  glass: '#040A18',
  glassEdge: '#1C2D52',
  // Vurgu ve metin
  primary: '#2563EB',
  onPrimary: '#FFFFFF',
  accent: '#57D2FF',
  text: '#DCE7FF',
  textMuted: '#7F93BD',
  // Tally ve durumlar: kırmızı canlı, yeşil önizleme, amber duraklatıldı
  live: '#FF3B4E',
  warning: '#FFB020',
  success: '#2EE59D',
  scrim: 'rgba(3, 8, 20, 0.74)',
} as const;

export const radius = { sm: 8, md: 14, lg: 22 } as const;
export const space = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24 } as const;

// Barlow: yayın cihazlarındaki DIN çizgisine yakın endüstriyel yazı ailesi.
// Tuş etiketleri ve sayaçlar dar (Semi Condensed), metinler normal genişlikte.
export const font = {
  body: 'Barlow_400Regular',
  medium: 'Barlow_500Medium',
  semi: 'Barlow_600SemiBold',
  bold: 'Barlow_700Bold',
  key: 'BarlowSemiCondensed_600SemiBold',
  keyBold: 'BarlowSemiCondensed_700Bold',
  display: 'BarlowSemiCondensed_700Bold',
} as const;

/** Bir rengi açar (amt > 0) veya koyulaştırır (amt < 0). */
export function shade(hex: string, amt: number): string {
  const h = hex.replace('#', '');
  const n = parseInt(h.length === 3 ? h.split('').map((c) => c + c).join('') : h.slice(0, 6), 16);
  const ch = (v: number) => {
    const t = amt >= 0 ? v + (255 - v) * amt : v * (1 + amt);
    return Math.max(0, Math.min(255, Math.round(t)));
  };
  const r = ch((n >> 16) & 255);
  const g = ch((n >> 8) & 255);
  const b = ch(n & 255);
  return `#${((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1)}`;
}

export const withAlpha = (hex: string, a: number) => {
  const h = hex.replace('#', '');
  return `rgba(${parseInt(h.slice(0, 2), 16)}, ${parseInt(h.slice(2, 4), 16)}, ${parseInt(h.slice(4, 6), 16)}, ${a})`;
};
