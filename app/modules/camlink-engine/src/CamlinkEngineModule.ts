import { NativeModule, requireNativeModule } from 'expo';
import type { CameraInfo, CamlinkEngineEvents, PipCorner, RawLens, StreamConfig, WbMode } from './CamlinkEngine.types';

declare class CamlinkEngineModule extends NativeModule<CamlinkEngineEvents> {
  configure(config: StreamConfig): Promise<void>;
  /** label: bildirimde gösterilen hedef (ör. bilgisayar adı); yoksa null. */
  startStream(url: string, label: string | null): Promise<void>;
  stopStream(): Promise<void>;
  switchCamera(): Promise<'front' | 'back'>;
  setZoom(level: number, animate: boolean): Promise<void>;
  setTorch(on: boolean): Promise<boolean>;
  setExposure(level: number): Promise<void>;
  setAutoFocus(enabled: boolean): Promise<void>;
  setMirror(on: boolean): Promise<void>;
  setFilter(id: string, value: number): Promise<void>;
  setPip(enabled: boolean, corner: PipCorner, size: number): Promise<void>;
  rotatePip(): Promise<number>;
  relayout(): Promise<void>;
  setBitrate(bps: number): Promise<void>;
  /** Mikrofonu yayını durdurmadan susturur/açar (ses izi kalır, sessizdir). */
  setMicMuted(muted: boolean): Promise<void>;
  getLenses(): Promise<RawLens[]>;
  /** Her [genişlik, yükseklik, fps] kipi için bu cihazda yayınlanabilir mi (arka kamera boyutu + fps aralığı + donanım H.264); aynı sırada. */
  supportedModes(modes: [number, number, number][]): Promise<boolean[]>;
  /** Etkin lens kimliğini döndürür; bilinmeyen kimlikte mevcut lens kalır. */
  setLens(id: string): Promise<string>;
  /** true: istek kameraya uygulandı. Kamera kapalıysa ya da mod desteklenmiyorsa false. */
  setWhiteBalance(mode: WbMode): Promise<boolean>;
  setStabilization(on: boolean): Promise<boolean>;
  startDiscovery(): boolean;
  stopDiscovery(): void;
}

export default requireNativeModule<CamlinkEngineModule>('CamlinkEngine');
