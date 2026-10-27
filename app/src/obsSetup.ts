import OBSWebSocket from 'obs-websocket-js/json';

export interface ObsSetupInput {
  host: string;
  port: number;
  password: string;
  srtPort: number;
  inputName?: string;
}

export interface ObsSetupResult {
  scene: string;
  created: boolean;
  addedToScene: boolean;
  obsVersion?: string;
}

/** OBS'in SRT dinleyicisi olarak açacağı adres. Telefon bu porta yayın yapar. */
export const listenerUrl = (srtPort: number) => `srt://0.0.0.0:${srtPort}?mode=listener`;

/** Telefonun yayın yapacağı adres. USB'de görüntü adb reverse ile 127.0.0.1'e RTMP olarak gider. */
export const streamUrl = (pcHost: string, port: number, latencyMs = 120, via: 'wifi' | 'usb' = 'wifi') =>
  via === 'usb' ? `rtmp://127.0.0.1:${port}/camlink` : `srt://${pcHost.trim()}:${port}/camlink?latency=${latencyMs}`;

export const mediaSourceSettings = (srtPort: number) => ({
  is_local_file: false,
  input: listenerUrl(srtPort),
  input_format: 'mpegts',
  reconnect_delay_sec: 1,
  restart_on_activate: true,
  close_when_inactive: false,
  buffering_mb: 0,
  hw_decode: true,
  clear_on_media_end: false,
});

/**
 * OBS WebSocket üzerinden "CamLink" adlı bir Media Source kurar:
 * yoksa aktif sahneye ekler ve tuvale sığdırır, varsa ayarlarını günceller.
 */
export async function setupObsSource(p: ObsSetupInput, obs = new OBSWebSocket()): Promise<ObsSetupResult> {
  const inputName = p.inputName ?? 'CamLink';
  const call = obs.call.bind(obs) as (t: string, d?: object) => Promise<any>;
  await withTimeout(
    obs.connect(`ws://${p.host.trim()}:${p.port}`, p.password || undefined, { rpcVersion: 1 }),
    7000,
    "OBS bulunamadı. IP adresi doğru mu, OBS'te WebSocket sunucusu açık mı, kontrol et.",
  ).catch((e: any) => {
    void obs.disconnect().catch(() => {});
    if (e?.code === 4009) throw new Error('OBS WebSocket şifresi yanlış');
    throw e;
  });
  try {
    const version = await call('GetVersion').catch(() => undefined);
    const { currentProgramSceneName: scene } = await call('GetCurrentProgramScene');
    const { inputs } = await call('GetInputList');
    const exists = (inputs as any[]).some((i) => i.inputName === inputName);
    const settings = mediaSourceSettings(p.srtPort);

    let sceneItemId: number | undefined;
    let addedToScene = false;
    if (exists) {
      await call('SetInputSettings', { inputName, inputSettings: settings, overlay: true });
      try {
        await call('GetSceneItemId', { sceneName: scene, sourceName: inputName });
      } catch {
        sceneItemId = (await call('CreateSceneItem', { sceneName: scene, sourceName: inputName, sceneItemEnabled: true })).sceneItemId;
        addedToScene = true;
      }
    } else {
      const r = await call('CreateInput', {
        sceneName: scene,
        inputName,
        inputKind: 'ffmpeg_source',
        inputSettings: settings,
        sceneItemEnabled: true,
      });
      sceneItemId = r.sceneItemId;
      addedToScene = true;
    }

    // Yeni eklenen kaynağı tuvale sığdır.
    if (sceneItemId !== undefined) {
      const v = await call('GetVideoSettings');
      await call('SetSceneItemTransform', {
        sceneName: scene,
        sceneItemId,
        sceneItemTransform: {
          boundsType: 'OBS_BOUNDS_SCALE_INNER',
          boundsWidth: v.baseWidth,
          boundsHeight: v.baseHeight,
          boundsAlignment: 0,
          alignment: 5,
          positionX: 0,
          positionY: 0,
        },
      }).catch(() => {});
    }
    return { scene, created: !exists, addedToScene, obsVersion: version?.obsVersion };
  } finally {
    await obs.disconnect().catch(() => {});
  }
}

function withTimeout<T>(p: Promise<T>, ms: number, message: string): Promise<T> {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(message)), ms);
    p.then(
      (v) => {
        clearTimeout(t);
        resolve(v);
      },
      (e) => {
        clearTimeout(t);
        reject(e);
      },
    );
  });
}
