import Ionicons from '@expo/vector-icons/Ionicons';
// Yalnızca kullanılan ağırlıklar alınır; paket kökünden almak tüm ağırlıkları uygulamaya ekler.
import { Barlow_400Regular } from '@expo-google-fonts/barlow/400Regular';
import { Barlow_500Medium } from '@expo-google-fonts/barlow/500Medium';
import { Barlow_600SemiBold } from '@expo-google-fonts/barlow/600SemiBold';
import { Barlow_700Bold } from '@expo-google-fonts/barlow/700Bold';
import { BarlowSemiCondensed_600SemiBold } from '@expo-google-fonts/barlow-semi-condensed/600SemiBold';
import { BarlowSemiCondensed_700Bold } from '@expo-google-fonts/barlow-semi-condensed/700Bold';
import { useFonts } from 'expo-font';
import * as Haptics from 'expo-haptics';
import { useKeepAwake } from 'expo-keep-awake';
import { LinearGradient } from 'expo-linear-gradient';
import { StatusBar } from 'expo-status-bar';
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  AccessibilityInfo,
  Alert,
  ActivityIndicator,
  AppState,
  Animated,
  Linking,
  PanResponder,
  PermissionsAndroid,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import { SafeAreaProvider, useSafeAreaInsets } from 'react-native-safe-area-context';
import { CamlinkEngine, CamlinkPreview } from './modules/camlink-engine';
import { movedHost } from './src/beacon';
import { QrScanner, SettingsSheet, SetupSheet } from './src/components/Sheets';
import { Btn, IconBtn, Keycap, ToastHost } from './src/components/ui';
import { pairDesktop, setLive, startDesktopLink, stopDesktopLink, useDesktopLink } from './src/desktopLink';
import { startDiscovery, stopDiscovery, useDiscovery } from './src/discovery';
import { applyCameraPrefs, control, useEngineBridge } from './src/engine';
import { lensButtonLabels } from './src/lenses';
import type { CamlinkPair } from './src/pair';
import { useApp } from './src/store';
import { font, navy, withAlpha } from './src/theme';

export default function App() {
  const [fontsLoaded, fontError] = useFonts({
    Barlow_400Regular,
    Barlow_500Medium,
    Barlow_600SemiBold,
    Barlow_700Bold,
    BarlowSemiCondensed_600SemiBold,
    BarlowSemiCondensed_700Bold,
  });
  return (
    <SafeAreaProvider>
      <StatusBar style="light" hidden />
      {fontsLoaded || fontError ? <Root /> : <View style={styles.root} />}
      <ToastHost />
    </SafeAreaProvider>
  );
}

type Perm = 'checking' | 'granted' | 'denied';

async function requestPermissions(): Promise<Perm> {
  if (Platform.OS !== 'android') return 'granted';
  const r = await PermissionsAndroid.requestMultiple([
    PermissionsAndroid.PERMISSIONS.CAMERA,
    PermissionsAndroid.PERMISSIONS.RECORD_AUDIO,
    // Android 13+: arka planda yayın bildirimi (reddedilse de yayın sürer, yalnız bildirim görünmez).
    ...(Number(Platform.Version) >= 33 ? [PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS] : []),
  ]);
  useApp.setState({ micOk: r[PermissionsAndroid.PERMISSIONS.RECORD_AUDIO] === PermissionsAndroid.RESULTS.GRANTED });
  return r[PermissionsAndroid.PERMISSIONS.CAMERA] === PermissionsAndroid.RESULTS.GRANTED ? 'granted' : 'denied';
}

/** QR okuyucu (expo-camera) kapandıktan sonra kameranın serbest kalması için beklenen süre; önizleme ve yayın ancak sonra açılır. */
const CAM_HANDOFF_MS = 500;

function Root() {
  useKeepAwake('camlink');
  useEngineBridge();
  const loaded = useApp((s) => s.loaded);
  const pcHost = useApp((s) => s.pcHost);
  const ctlPort = useApp((s) => s.ctlPort);
  const desktopHost = useApp((s) => (s.targetKind === 'webcam' ? s.pcHost : ''));
  const [perm, setPerm] = useState<Perm>('checking');
  const [sheet, setSheet] = useState<'setup' | 'settings' | null>(null);
  const [scanning, setScanning] = useState(false);
  const [pairing, setPairing] = useState(false);
  const targetKind = useApp((s) => s.targetKind);
  const streamActive = useApp((s) => s.state !== 'idle' && s.state !== 'failed');
  // Android'de Modal görünmez olunca hemen kaldırılır; önizleme aynı karede açılırsa kamera henüz QR okuyucudadır.
  // Okuyucu kapandıktan sonra CAM_HANDOFF_MS boyunca önizleme açılmaz, okunan eşleşme de o zaman uygulanır.
  const scannerOpen = scanning || pairing;
  const [camHold, setCamHold] = useState(false);
  const [pendingPair, setPendingPair] = useState<CamlinkPair | null>(null);
  useEffect(() => {
    if (scannerOpen) {
      setCamHold(true);
      return;
    }
    const t = setTimeout(() => setCamHold(false), CAM_HANDOFF_MS);
    return () => clearTimeout(t);
  }, [scannerOpen]);
  const cameraFree = !scannerOpen && !camHold;
  useEffect(() => {
    if (!cameraFree || !pendingPair) return;
    // Kart, eşleşme kaydedilene dek gizli kalsın diye bekleyen eşleşme ancak bittikten sonra silinir.
    void pairDesktop(pendingPair).finally(() => setPendingPair(null));
  }, [cameraFree, pendingPair]);
  // Ekrana çiziliyorsa önündeyiz; ilk değişim olayı dinleyiciden önce gelirse kaçırılmasın (arama da açılışta koşulsuz başlar).
  const [active, setActive] = useState(true);

  useEffect(() => {
    void useApp.getState().load();
    void requestPermissions().then(setPerm);
    // Bilgisayarlar UDP duyurusuyla ve USB yoklamasıyla bulunur; uygulama önündeyken aranır.
    startDiscovery();
    const sub = AppState.addEventListener('change', (st) => {
      if (st === 'active') {
        setActive(true);
        startDiscovery();
        return;
      }
      // Yayın sürüyorsa (ekran kapandı, başka uygulamaya geçildi) arama ve masaüstü kanalı açık kalır: ön plan hizmeti
      // kamerayı ve JS zamanlayıcılarını canlı tutar. Yayın yoksa ikisi de kapanır, masaüstü yuvayı hemen bırakır.
      const s = useApp.getState().state;
      if (s !== 'idle' && s !== 'failed') return;
      setActive(false);
      stopDiscovery();
    });
    return () => {
      sub.remove();
      stopDiscovery();
    };
  }, []);

  // Kayıtlı CamLink masaüstüne kendiliğinden bağlan; yayın masaüstü "hoş geldin" deyince başlar.
  // Uygulama yayın yokken arkaya geçerse kanal kapanır, öne gelince yeniden kurulur.
  useEffect(() => {
    if (!loaded || perm !== 'granted' || !active || !desktopHost) return;
    startDesktopLink(desktopHost, ctlPort);
    return stopDesktopLink;
  }, [loaded, perm, active, desktopHost, ctlPort]);

  // Bilgisayarın IP adresi değişirse (ör. modem yeniden başladı) aynı hedef yeni adresiyle güncellenir.
  useEffect(
    () =>
      useDiscovery.subscribe(({ targets }) => {
        const s = useApp.getState();
        if (s.targetKind === 'manual' || !s.targetPcName) return;
        const host = movedHost(targets, { kind: s.targetKind, host: s.pcHost, pcName: s.targetPcName, port: s.srtPort });
        if (host) s.save({ pcHost: host });
      }),
    [],
  );

  if (!loaded || perm === 'checking') {
    return (
      <View style={[styles.root, styles.center]}>
        <ActivityIndicator color={navy.accent} size="large" />
      </View>
    );
  }

  if (perm === 'denied') {
    return (
      <LinearGradient colors={[navy.chassisTop, navy.chassisBottom]} style={[styles.root, styles.center, { padding: 32, gap: 16 }]}>
        <Ionicons name="videocam-off" size={48} color={navy.textMuted} />
        <Text style={styles.gateTitle}>Kamera izni gerekiyor</Text>
        <Text style={styles.gateText}>CamLink, kamerayı bilgisayara göndermek için bu izne ihtiyaç duyar.</Text>
        <Btn title="Tekrar iste" onPress={() => void requestPermissions().then(setPerm)} />
        <Btn title="Ayarları aç" variant="ghost" onPress={() => void Linking.openSettings()} />
      </LinearGradient>
    );
  }

  const pair = () => setPairing(true);

  return (
    <View style={styles.root}>
      {/* QR okunurken kamerayı tarayıcıya bırakmak için önizlemeyi kaldır. */}
      {cameraFree ? <CameraStage /> : <View style={StyleSheet.absoluteFill} />}
      <TallyFrame />
      {/* Hedef yoksa büyük kart; eski OBS/elle hedefte (güncelleme sonrası) yayın yokken destenin üstünde küçük kart:
          "QR okut" her durumda tek dokunuş. */}
      <Overlay
        onSettings={() => setSheet('settings')}
        onPair={pair}
        pairHint={!pairing && !pendingPair && !!pcHost && targetKind !== 'webcam' && !streamActive}
      />
      {!pairing && !pendingPair && !pcHost ? <PairCard onPress={pair} /> : null}
      <SetupSheet visible={sheet === 'setup'} onClose={() => setSheet(null)} onScanningChange={setScanning} onDesktopQr={setPendingPair} />
      <SettingsSheet visible={sheet === 'settings'} onClose={() => setSheet(null)} onOpenSetup={() => setSheet('setup')} />
      <QrScanner
        desktopOnly
        visible={pairing}
        onClose={() => setPairing(false)}
        onResult={(r) => {
          setPairing(false);
          if ('ctl' in r) setPendingPair(r);
        }}
      />
    </View>
  );
}

/** İlk açılış (ya da "Bağlantıyı kes"ten sonra): tek büyük eylem. */
function PairCard({ onPress, compact }: { onPress: () => void; compact?: boolean }) {
  if (compact) {
    return (
      <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel="Bilgisayara bağlan. Bilgisayardaki CamLink QR kodunu okut">
        {({ pressed }) => (
          <Keycap color={navy.primary} pressed={pressed} height={3} radius={14} ring={navy.accent} faceStyle={styles.pairCompactFace}>
            <Ionicons name="qr-code" size={20} color="#fff" />
            <Text style={styles.pairCompactText}>Bilgisayara bağlan</Text>
          </Keycap>
        )}
      </Pressable>
    );
  }
  return (
    <View style={[StyleSheet.absoluteFill, styles.center, { padding: 24 }]} pointerEvents="box-none">
      <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel="Bilgisayara bağlan. Bilgisayardaki CamLink QR kodunu okut">
        {({ pressed }) => (
          <Keycap color={navy.primary} pressed={pressed} height={5} radius={22} ring={navy.accent} faceStyle={styles.pairFace}>
            <Ionicons name="qr-code" size={44} color="#fff" />
            <Text style={styles.pairTitle}>Bilgisayara bağlan</Text>
            <Text style={styles.pairSub}>Bilgisayardaki CamLink QR kodunu okut</Text>
          </Keycap>
        )}
      </Pressable>
    </View>
  );
}

// ---------- Kamera ve parmakla yakınlaştırma ----------

function CameraStage() {
  const { width, height } = useWindowDimensions();
  const pinch = useRef<{ dist: number; zoom: number } | null>(null);
  const lastSent = useRef(0);

  useEffect(() => {
    // Önizleme açılınca kayıtlı efekt ve aynayı uygula.
    const t = setTimeout(() => void applyCameraPrefs(), 600);
    return () => clearTimeout(t);
  }, []);

  useEffect(() => {
    // Ekran dönünce ikinci kamera yerleşimini güncelle.
    void CamlinkEngine.relayout().catch(() => {});
  }, [width, height]);

  const responder = useMemo(
    () =>
      PanResponder.create({
        onStartShouldSetPanResponder: (e) => e.nativeEvent.touches.length === 2,
        onMoveShouldSetPanResponder: (e) => e.nativeEvent.touches.length === 2,
        onPanResponderMove: (e) => {
          const t = e.nativeEvent.touches;
          if (t.length !== 2) return;
          const d = Math.hypot(t[0].pageX - t[1].pageX, t[0].pageY - t[1].pageY);
          const cam = useApp.getState().camera;
          if (!pinch.current) {
            pinch.current = { dist: d, zoom: cam?.zoom ?? 1 };
            return;
          }
          const now = Date.now();
          if (now - lastSent.current < 33) return;
          lastSent.current = now;
          const z = Math.max(cam?.zoomMin ?? 1, Math.min(cam?.zoomMax ?? 8, pinch.current.zoom * (d / pinch.current.dist)));
          useApp.setState((s) => (s.camera ? { camera: { ...s.camera, zoom: z } } : {}));
          void CamlinkEngine.setZoom(z, false);
        },
        onPanResponderRelease: () => (pinch.current = null),
        onPanResponderTerminate: () => (pinch.current = null),
      }),
    [],
  );

  return (
    <View style={StyleSheet.absoluteFill} {...responder.panHandlers}>
      <CamlinkPreview style={StyleSheet.absoluteFill} />
    </View>
  );
}

/** Yayın kameralarındaki tally ışığı: canlıyken görüntünün çevresi kırmızı yanar. */
function TallyFrame() {
  const state = useApp((s) => s.state);
  if (state === 'idle' || state === 'failed') return null;
  const live = state === 'live';
  return (
    <View
      pointerEvents="none"
      style={[StyleSheet.absoluteFill, { borderWidth: live ? 4 : 2, borderColor: live ? navy.live : withAlpha(navy.warning, 0.8) }]}
    />
  );
}

// ---------- Ana ekran (sade): durum hapı, ayar düğmesi, lens çipleri, üç büyük düğme ----------
// Efektler, çift kamera, fener ve ayna Ayarlar'da; yakınlaştırma parmakla (masaüstü de yönetir).

function Overlay({ onSettings, onPair, pairHint }: { onSettings: () => void; onPair: () => void; pairHint: boolean }) {
  const insets = useSafeAreaInsets();
  const [dim, setDim] = useState(false);
  const { width, height } = useWindowDimensions();
  const landscape = width > height;

  if (dim) return <DimScreen onWake={() => setDim(false)} />;

  return (
    <View style={StyleSheet.absoluteFill} pointerEvents="box-none">
      <View
        style={[styles.topBar, { paddingTop: insets.top + 10, paddingLeft: insets.left + 12, paddingRight: insets.right + 12 }]}
        pointerEvents="box-none"
      >
        <View style={styles.topLeft} pointerEvents="box-none">
          <StatusPill onSettings={onSettings} onPair={onPair} />
          <LinkBanner />
          {pairHint ? <PairCard compact onPress={onPair} /> : null}
        </View>
        <IconBtn icon="settings-outline" label="Ayarlar" onPress={onSettings} />
      </View>

      {/* Dikeyde altta bir satır; yatayda sağ kenarda (çentik tarafı dahil güvenli alanın içinde) bir sütun. */}
      <View
        style={
          landscape
            ? [styles.sideColumn, { right: insets.right + 12, top: insets.top + 64, bottom: insets.bottom + 12 }]
            : [styles.bottomStack, { paddingBottom: insets.bottom + 16, paddingLeft: insets.left + 12, paddingRight: insets.right + 12 }]
        }
        pointerEvents="box-none"
      >
        <LensChips vertical={landscape} />
        <MainButtons vertical={landscape} onDim={() => setDim(true)} onPair={onPair} />
      </View>
    </View>
  );
}

/** Masaüstüne ulaşılamıyorsa ya da başka telefon bağlıysa tek cümle (durum hapının altında); arkada sessizce yeniden denenir. */
function LinkBanner() {
  const desktop = useApp((s) => s.targetKind === 'webcam' && !!s.pcHost);
  const busy = useDesktopLink((l) => l.busy);
  const notFound = useDesktopLink((l) => l.notFound);
  if (!desktop || (!busy && !notFound)) return null;
  return (
    <View style={styles.banner} pointerEvents="none" accessibilityLiveRegion="polite">
      <Ionicons name={busy ? 'phone-portrait-outline' : 'desktop-outline'} size={18} color={navy.warning} />
      <Text style={styles.bannerText}>{busy ? 'Bu bilgisayarda başka bir telefon bağlı' : 'Bilgisayarda CamLink açık mı? Aranıyor…'}</Text>
    </View>
  );
}

/** Tek durum hapı. Hedef yoksa dokununca QR okuyucu açılır, varsa Ayarlar. */
function StatusPill({ onSettings, onPair }: { onSettings: () => void; onPair: () => void }) {
  const state = useApp((s) => s.state);
  const stats = useApp((s) => s.stats);
  const host = useApp((s) => s.pcHost);
  const targetKind = useApp((s) => s.targetKind);
  const targetLabel = useApp((s) => s.targetLabel);
  const targetPc = useApp((s) => s.targetPcName);
  const link = useDesktopLink();
  const live = state === 'live';
  const trying = state === 'connecting' || state === 'retrying';

  let text: string;
  let color: string = navy.warning;
  let blink = false;
  let onPress = onSettings;
  if (!host) {
    text = 'Bağlı değil, QR okut';
    color = navy.textMuted;
    onPress = onPair;
  } else if (targetKind === 'webcam') {
    if (link.busy) text = 'Başka telefon bağlı';
    else if (link.notFound) text = 'Bilgisayar bulunamadı';
    else if (live) {
      text = `Bilgisayara bağlı · ${link.pcName ?? targetPc}`;
      color = navy.success;
    } else if (link.connected && state === 'idle' && !link.wantLive) {
      // Yayın durduruldu (masaüstünden ya da bildirimden): hap yeniden başlatır. Başlatma beklerken (welcome'dan
      // hemen sonra) gösterilmez.
      text = 'Yayın durdu, başlat';
      color = navy.textMuted;
      onPress = () => void setLive(true);
    } else {
      text = 'Bağlanıyor…';
      blink = true;
    }
  } else {
    const name = targetKind === 'obs' ? `OBS: ${targetLabel}` : host;
    if (live) {
      text = `Yayında · ${name}`;
      color = navy.success;
    } else if (trying) {
      text = 'Bağlanıyor…';
      blink = true;
    } else if (state === 'failed') {
      text = 'Bağlanamadı';
      color = navy.live;
    } else {
      text = name;
      color = navy.textMuted;
    }
  }
  const hot = !!stats && stats.thermal >= 2;
  const lowBattery = !!stats && stats.battery >= 0 && stats.battery <= 20 && !stats.charging;

  return (
    <Pressable onPress={onPress} style={styles.pill} accessibilityRole="button" accessibilityLabel={text}>
      <Led color={color} blink={blink} />
      <Text style={styles.pillText} numberOfLines={1}>
        {text}
      </Text>
      {hot ? <Ionicons name="thermometer" size={15} color={stats!.thermal >= 3 ? navy.live : navy.warning} accessibilityLabel="Telefon ısınıyor" /> : null}
      {lowBattery ? <Ionicons name="battery-dead" size={16} color={navy.warning} accessibilityLabel={`Pil yüzde ${stats!.battery}`} /> : null}
    </Pressable>
  );
}

function Led({ color, blink }: { color: string; blink?: boolean }) {
  const a = useRef(new Animated.Value(1)).current;
  useEffect(() => {
    let loop: Animated.CompositeAnimation | undefined;
    let cancelled = false;
    if (blink) {
      AccessibilityInfo.isReduceMotionEnabled()
        .then((reduce) => {
          if (reduce || cancelled) return;
          loop = Animated.loop(
            Animated.sequence([
              Animated.timing(a, { toValue: 0.25, duration: 600, useNativeDriver: true }),
              Animated.timing(a, { toValue: 1, duration: 600, useNativeDriver: true }),
            ]),
          );
          loop.start();
        })
        .catch(() => {});
    }
    return () => {
      cancelled = true;
      loop?.stop();
      a.setValue(1);
    };
  }, [blink, a]);
  return <Animated.View style={[styles.led, { backgroundColor: color, shadowColor: color, opacity: a }]} />;
}

/** Açık kameranın yönündeki lensler (ör. 0,5× 1× 3×); o yönde tek lens varsa hiç görünmez. */
function LensChips({ vertical }: { vertical: boolean }) {
  const lenses = useApp((s) => s.lenses);
  const facing = useApp((s) => s.camera?.facing);
  const current = useApp((s) => s.camera?.lens);
  const labels = lensButtonLabels(lenses);
  const items = lenses.map((l, i) => ({ l, label: labels[i] })).filter((x) => x.l.facing === facing);
  if (items.length < 2) return null;
  return (
    <View style={[styles.chips, vertical && styles.chipsVertical]}>
      {items.map(({ l, label }) => {
        const on = l.id === current;
        return (
          <Pressable
            key={l.id}
            onPress={() => {
              void Haptics.selectionAsync();
              void control.lens(l.id);
            }}
            accessibilityRole="button"
            accessibilityLabel={`Lens ${label}`}
            accessibilityState={{ selected: on }}
          >
            {({ pressed }) =>
              on ? (
                <Keycap color={navy.primary} pressed={pressed} height={3} radius={14} ring={navy.accent} style={styles.chip} faceStyle={styles.centerFace}>
                  <Text style={[styles.chipText, { color: '#fff' }]}>{label}</Text>
                </Keycap>
              ) : (
                <View style={[styles.chip, styles.chipOff, styles.centerFace, pressed && { opacity: 0.6 }]}>
                  <Text style={styles.chipText}>{label}</Text>
                </View>
              )
            }
          </Pressable>
        );
      })}
    </View>
  );
}

/** Üç büyük düğme: Çevir, Mikrofon, Karart. Yayın düğmesi yalnız eski OBS/elle hedefte (masaüstüyle yayın kendiliğinden). */
function MainButtons({ vertical, onDim, onPair }: { vertical: boolean; onDim: () => void; onPair: () => void }) {
  const state = useApp((s) => s.state);
  const host = useApp((s) => s.pcHost);
  const targetKind = useApp((s) => s.targetKind);
  const audio = useApp((s) => s.audio);
  const micOk = useApp((s) => s.micOk);
  const active = state === 'live' || state === 'connecting' || state === 'retrying';
  const showRecord = !!host && targetKind !== 'webcam';
  const micOn = audio && micOk;
  // Yatayda sütun Ayarlar düğmesinin altından alt kenara sığmalı (~285 dp): 3 × (46 + etiket) + boşluklar ≈ 220 dp.
  const size = vertical ? 46 : 62;

  const flip = async () => {
    void Haptics.selectionAsync();
    const facing = await CamlinkEngine.switchCamera().catch(() => null);
    // Yön değişince yönün varsayılan lensi açılır; kayıtlı lens seçimi geçersiz olur.
    if (facing) {
      useApp.getState().save({ lens: '' });
      useApp.setState((s) => (s.camera ? { camera: { ...s.camera, facing, torch: false } } : {}));
    }
  };

  const toggleMic = () => {
    void Haptics.selectionAsync();
    if (!micOk) {
      Alert.alert('Mikrofon izni yok', "Ses göndermek için CamLink'e mikrofon izni ver. Sonra uygulamayı yeniden aç.", [
        { text: 'Vazgeç', style: 'cancel' },
        { text: 'Ayarları aç', onPress: () => void Linking.openSettings() },
      ]);
      return;
    }
    void control.mic(!audio);
  };

  const toggleStream = async () => {
    void Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
    if (active) await setLive(false);
    else if (!(await setLive(true))) onPair();
  };

  return (
    <View style={vertical ? styles.btnColumn : styles.btnRow}>
      <RoundKey size={size} icon="camera-reverse" label="Çevir" a11y="Ön ya da arka kameraya geç" onPress={() => void flip()} />
      {showRecord ? <RecordButton compact={vertical} active={active} busy={state === 'connecting' || state === 'retrying'} onPress={() => void toggleStream()} /> : null}
      <RoundKey
        size={size}
        icon={micOn ? 'mic' : 'mic-off'}
        label="Mikrofon"
        active={micOn}
        a11y={!micOk ? 'Mikrofon izni yok' : micOn ? 'Mikrofon açık, kapatmak için dokun' : 'Mikrofon kapalı, açmak için dokun'}
        onPress={toggleMic}
      />
      <RoundKey size={size} icon="moon" label="Karart" a11y="Ekranı karart" onPress={onDim} />
    </View>
  );
}

function RoundKey({ icon, label, onPress, active, size, a11y }: { icon: string; label: string; onPress: () => void; active?: boolean; size: number; a11y: string }) {
  return (
    <Pressable
      onPress={onPress}
      accessibilityRole="button"
      accessibilityLabel={a11y}
      accessibilityState={active === undefined ? undefined : { selected: active }}
      hitSlop={4}
      style={styles.roundWrap}
    >
      {({ pressed }) => (
        <>
          <Keycap
            color={active ? navy.primary : '#18284C'}
            pressed={pressed}
            height={3}
            radius={size / 2}
            ring={active ? navy.accent : undefined}
            style={{ width: size, height: size }}
            faceStyle={styles.centerFace}
          >
            <Ionicons name={icon as any} size={Math.round(size * 0.42)} color={active ? '#fff' : navy.text} />
          </Keycap>
          <Text style={styles.roundLabel}>{label}</Text>
        </>
      )}
    </Pressable>
  );
}

/** Metal halkalı kayıt düğmesi (yalnız OBS/elle hedefte): kırmızı kapak basınca gömülür, yayındayken durdur işaretine döner. */
function RecordButton({ active, busy, compact, onPress }: { active: boolean; busy: boolean; compact: boolean; onPress: () => void }) {
  const outer = compact ? 50 : 70;
  const cap = outer - 16;
  return (
    <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel={active ? 'Yayını durdur' : 'Yayını başlat'}>
      {({ pressed }) => (
        <LinearGradient colors={['#5B6D92', '#1C2846', '#0A1226']} locations={[0, 0.5, 1]} style={[styles.recBezel, { width: outer, height: outer, borderRadius: outer / 2 }]}>
          <Keycap color={navy.live} pressed={pressed} height={4} radius={cap / 2} style={{ width: cap, height: cap }} faceStyle={styles.centerFace}>
            {busy ? <ActivityIndicator color="#fff" /> : active ? <View style={styles.stopIcon} /> : <View style={styles.recDot} />}
          </Keycap>
        </LinearGradient>
      )}
    </Pressable>
  );
}

function DimScreen({ onWake }: { onWake: () => void }) {
  const state = useApp((s) => s.state);
  return (
    <Pressable style={styles.dim} onPress={onWake} accessibilityLabel="Ekranı aç">
      <Led color={state === 'live' ? navy.live : navy.textMuted} blink={state === 'live'} />
      <Text style={styles.dimText}>{state === 'live' ? 'Yayın sürüyor' : 'Ekran karartıldı'}</Text>
      <Text style={styles.dimHint}>Açmak için dokun</Text>
    </Pressable>
  );
}

const shadowText = { textShadowColor: 'rgba(0,0,0,0.85)', textShadowRadius: 4, textShadowOffset: { width: 0, height: 1 } };

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: '#050B1A' },
  center: { alignItems: 'center', justifyContent: 'center' },
  centerFace: { alignItems: 'center', justifyContent: 'center' },
  gateTitle: { color: navy.text, fontSize: 24, fontFamily: font.display },
  gateText: { color: navy.textMuted, fontSize: 16, textAlign: 'center', lineHeight: 22, fontFamily: font.body, maxWidth: 360 },
  topBar: { position: 'absolute', top: 0, left: 0, right: 0, flexDirection: 'row', alignItems: 'flex-start', gap: 8 },
  topLeft: { flex: 1, alignItems: 'flex-start', gap: 8 },
  pill: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 9,
    height: 42,
    paddingHorizontal: 14,
    borderRadius: 21,
    backgroundColor: 'rgba(4,10,24,0.86)',
    borderWidth: 1,
    borderColor: navy.glassEdge,
    maxWidth: 360,
  },
  pillText: { color: navy.text, fontFamily: font.keyBold, fontSize: 15.5, flexShrink: 1 },
  led: { width: 9, height: 9, borderRadius: 5, shadowOpacity: 1, shadowRadius: 6, shadowOffset: { width: 0, height: 0 }, elevation: 3 },
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    maxWidth: 360,
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 12,
    backgroundColor: 'rgba(4,10,24,0.9)',
    borderWidth: 1,
    borderColor: withAlpha(navy.warning, 0.5),
  },
  bannerText: { color: navy.text, fontFamily: font.medium, fontSize: 14.5, flexShrink: 1 },
  pairFace: { alignItems: 'center', gap: 8, paddingVertical: 26, paddingHorizontal: 28, minWidth: 280 },
  pairTitle: { color: '#fff', fontFamily: font.display, fontSize: 26, letterSpacing: 0.3 },
  pairSub: { color: 'rgba(255,255,255,0.85)', fontFamily: font.medium, fontSize: 15.5, textAlign: 'center' },
  pairCompactFace: { flexDirection: 'row', alignItems: 'center', gap: 8, paddingVertical: 10, paddingHorizontal: 16 },
  pairCompactText: { color: '#fff', fontFamily: font.keyBold, fontSize: 16 },
  bottomStack: { position: 'absolute', left: 0, right: 0, bottom: 0, gap: 14, alignItems: 'center' },
  // Yatayda lens çipleri düğmelerin yanında (üstünde değil): sütun yüksekliği yalnız düğmeler kadar.
  sideColumn: { position: 'absolute', flexDirection: 'row', gap: 10, alignItems: 'center', justifyContent: 'center' },
  btnRow: { flexDirection: 'row', alignItems: 'flex-start', gap: 28 },
  btnColumn: { alignItems: 'center', gap: 8 },
  roundWrap: { alignItems: 'center', gap: 5 },
  roundLabel: { color: '#fff', fontFamily: font.keyBold, fontSize: 13.5, ...shadowText },
  chips: { flexDirection: 'row', gap: 8, padding: 5, borderRadius: 20, backgroundColor: 'rgba(4,10,24,0.7)' },
  chipsVertical: { flexDirection: 'column' },
  chip: { minWidth: 48, height: 34, paddingHorizontal: 8 },
  chipOff: { borderRadius: 14 },
  chipText: { color: navy.text, fontFamily: font.keyBold, fontSize: 14.5 },
  recBezel: { alignItems: 'center', justifyContent: 'center', padding: 3, borderWidth: 1, borderColor: 'rgba(255,255,255,0.18)' },
  recDot: { width: 18, height: 18, borderRadius: 9, backgroundColor: '#fff', opacity: 0.92 },
  stopIcon: { width: 18, height: 18, borderRadius: 3, backgroundColor: '#fff' },
  dim: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, backgroundColor: '#000', alignItems: 'center', justifyContent: 'center', gap: 10 },
  dimText: { color: '#5A6B8C', fontSize: 17, fontFamily: font.semi },
  dimHint: { color: '#2B3550', fontSize: 14, fontFamily: font.body },
});
