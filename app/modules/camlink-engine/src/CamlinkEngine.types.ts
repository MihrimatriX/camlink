import type { StyleProp, ViewStyle } from 'react-native';

export type StreamState = 'idle' | 'connecting' | 'live' | 'retrying' | 'failed';

export interface StreamConfig {
  width: number;
  height: number;
  fps: number;
  bitrate: number;
  audio: boolean;
  portrait: boolean;
  /** Yayın yönü telefonun fiziksel duruşunu izler (arayüz dönmese de); CamLink masaüstü yolunda açık. */
  sensorOrientation: boolean;
}

export interface CameraInfo {
  facing: 'front' | 'back';
  zoom: number;
  zoomMin: number;
  zoomMax: number;
  torch: boolean;
  dualSupported: boolean;
  width: number;
  height: number;
  streaming: boolean;
  state: StreamState;
  pip?: boolean;
  /** Etkin lens kimliği: "0" ya da fiziksel alt kamera için "mantıksal/fiziksel" ("0/2"). */
  lens?: string;
}

/** Telefondaki bir kamera. Etiketleme (Ana, Geniş açı, Tele) JS tarafında `eqMm` oranından yapılır. */
export interface RawLens {
  id: string;
  facing: 'front' | 'back' | 'external';
  focalMm: number;
  /** 35 mm eşdeğer odak uzaklığı. */
  eqMm: number;
  /** Çok kameralı telefonda mantıksal kameranın fiziksel alt kamerası. */
  physical: boolean;
}

export type WbMode = 'auto' | 'daylight' | 'cloudy' | 'incandescent' | 'fluorescent';

export interface StreamStats {
  bitrate: number;
  droppedVideo: number;
  /** Android PowerManager termal durumu: 0 normal … 3 ciddi, 4+ kritik */
  thermal: number;
  battery: number;
  charging: boolean;
  state: StreamState;
}

export type PipCorner = 'TOP_LEFT' | 'TOP_RIGHT' | 'BOTTOM_LEFT' | 'BOTTOM_RIGHT';

export type CamlinkEngineEvents = {
  onStatus: (e: { state: StreamState; message?: string | null }) => void;
  onStats: (e: StreamStats) => void;
  onCameraInfo: (e: CameraInfo) => void;
  onError: (e: { message: string }) => void;
  /** Yerel ağdaki bir duyuru: gönderen IP ve JSON metni. */
  onBeacon: (e: { ip: string; data: string }) => void;
  /** Bildirimdeki "Durdur"a basıldı; yayın yerelde durdu, JS yeniden başlatmamalı. */
  onRemoteStop: () => void;
};

export type CamlinkPreviewProps = {
  style?: StyleProp<ViewStyle>;
};
