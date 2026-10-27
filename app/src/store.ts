import AsyncStorage from '@react-native-async-storage/async-storage';
import * as SecureStore from 'expo-secure-store';
import { create } from 'zustand';
import type { CameraInfo, PipCorner, StreamState, StreamStats, WbMode } from '../modules/camlink-engine';
import type { Lens } from './lenses';
import type { QualityId } from './quality';

const KEY = 'camlink.settings.v1';
const PASS_KEY = 'camlink.obs.password';

export const FILTERS: { id: string; label: string; adjustable?: boolean }[] = [
  { id: 'none', label: 'Yok' },
  { id: 'beauty', label: 'Güzelleştir' },
  { id: 'warm', label: 'Sıcak', adjustable: true },
  { id: 'cool', label: 'Soğuk', adjustable: true },
  { id: 'vivid', label: 'Canlı', adjustable: true },
  { id: 'contrast', label: 'Kontrast', adjustable: true },
  { id: 'sharpen', label: 'Keskin' },
  { id: 'bw', label: 'Siyah beyaz' },
  { id: 'sepia', label: 'Sepya' },
  { id: 'retro', label: '70ler' },
  { id: 'earlybird', label: 'Sabah' },
  { id: 'lamoish', label: 'Film' },
  { id: 'blur', label: 'Bulanık' },
  { id: 'cartoon', label: 'Çizgi film' },
  { id: 'pixel', label: 'Piksel', adjustable: true },
  { id: 'analog', label: 'Eski TV' },
  { id: 'glitch', label: 'Glitch' },
  { id: 'negative', label: 'Negatif' },
];

export interface Settings {
  pcHost: string;
  srtPort: number;
  /** Seçili hedefin türü ve adı; durum şeridinde ve adres değişince bulmak için. */
  targetKind: 'obs' | 'webcam' | 'manual';
  targetLabel: string;
  targetPcName: string;
  /** CamLink masaüstünün kontrol (HTTP+WS) portu; QR'dan gelir. */
  ctlPort: number;
  obsPort: number;
  obsPassword: string;
  quality: QualityId;
  bitrateOverride?: number;
  audio: boolean;
  portrait: boolean;
  mirror: boolean;
  filterId: string;
  filterValue: number;
  pipCorner: PipCorner;
  pipSize: number;
  exposure: number;
  autoFocus: boolean;
  /** Seçili lens kimliği; boşsa yönün varsayılan kamerası. */
  lens: string;
  wb: WbMode;
  stabilization: boolean;
}

const defaults: Settings = {
  pcHost: '',
  srtPort: 9000,
  targetKind: 'manual',
  targetLabel: '',
  targetPcName: '',
  ctlPort: 4468,
  obsPort: 4455,
  obsPassword: '',
  quality: '720p30',
  audio: true,
  portrait: false,
  mirror: false,
  filterId: 'none',
  filterValue: 0.5,
  pipCorner: 'BOTTOM_RIGHT',
  pipSize: 0.3,
  exposure: 0,
  autoFocus: true,
  lens: '',
  wb: 'auto',
  stabilization: false,
};

interface AppStore extends Settings {
  loaded: boolean;
  // Anlık durum (kaydedilmez)
  state: StreamState;
  message?: string | null;
  stats?: StreamStats;
  camera?: CameraInfo;
  /** Telefondaki lensler, etiketli (ilk kamera bilgisinde okunur). */
  lenses: Lens[];
  /** Bu cihazın yayınlayabildiği kalite modları (açılışta bir kez okunur, kaydedilmez); boşsa henüz okunmadı. */
  qualities: QualityId[];
  /** CamLink masaüstünün son `welcome`/`config` iletisindeki ses isteği; yoksa undefined (ses var). */
  desktopAudio?: boolean;
  /** Mikrofon izni verildi mi; yoksa yayın sessizdir (ayar açık olsa bile). */
  micOk: boolean;
  pip: boolean;
  load: () => Promise<void>;
  save: (patch: Partial<Settings>) => void;
}

export const useApp = create<AppStore>((set, get) => ({
  ...defaults,
  loaded: false,
  state: 'idle',
  lenses: [],
  qualities: [],
  micOk: true,
  pip: false,
  load: async () => {
    try {
      const raw = await AsyncStorage.getItem(KEY);
      const stored = raw ? JSON.parse(raw) : {};
      const obsPassword = (await SecureStore.getItemAsync(PASS_KEY).catch(() => null)) ?? '';
      set({ ...defaults, ...stored, obsPassword, loaded: true });
    } catch {
      set({ loaded: true });
    }
  },
  save: (patch) => {
    set(patch);
    const { obsPassword, ...rest } = { ...pick(get()), ...patch };
    void AsyncStorage.setItem(KEY, JSON.stringify(rest)).catch(() => {});
    if ('obsPassword' in patch) {
      void (obsPassword ? SecureStore.setItemAsync(PASS_KEY, obsPassword) : SecureStore.deleteItemAsync(PASS_KEY)).catch(() => {});
    }
  },
}));

function pick(s: AppStore): Settings {
  const out: any = {};
  for (const k of Object.keys(defaults)) out[k] = (s as any)[k];
  if (s.bitrateOverride !== undefined) out.bitrateOverride = s.bitrateOverride;
  return out;
}
