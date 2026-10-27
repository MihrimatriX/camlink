import Ionicons from '@expo/vector-icons/Ionicons';
import Slider from '@react-native-community/slider';
import { CameraView, useCameraPermissions } from 'expo-camera';
import * as Clipboard from 'expo-clipboard';
import { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { CamlinkEngine, type PipCorner } from '../../modules/camlink-engine';
import { forgetDesktop, pairDesktop, useDesktopLink } from '../desktopLink';
import { useDiscovery, type Target } from '../discovery';
import { changeStreamConfig, control, stopStreaming, streamBitrate } from '../engine';
import { listenerUrl, setupObsSource } from '../obsSetup';
import { parseCamlinkQr, parsePairQr, type CamlinkPair, type PairInfo } from '../pair';
import { parseObsQr } from '../qr';
import { QUALITY_PRESETS, streamQuality } from '../quality';
import { FILTERS, useApp } from '../store';
import { font, navy, radius, space } from '../theme';
import { Btn, ChipGroup, Field, Hint, Keycap, Label, Sheet, ToggleRow, toast } from './ui';

// ---------- Hedef seçimi ve OBS kurulumu ----------

type Mode = 'find' | 'auto' | 'manual';

/** `onDesktopQr`: CamLink masaüstü kodu okundu; eşleşmeyi App, QR okuyucu kamerayı bıraktıktan sonra yapar. */
export function SetupSheet({
  visible,
  onClose,
  onScanningChange,
  onDesktopQr,
}: {
  visible: boolean;
  onClose: () => void;
  onScanningChange: (v: boolean) => void;
  onDesktopQr: (r: CamlinkPair) => void;
}) {
  const app = useApp();
  const [mode, setMode] = useState<Mode>('find');
  const [host, setHost] = useState(app.pcHost);
  const [obsPort, setObsPort] = useState(String(app.obsPort));
  const [password, setPassword] = useState(app.obsPassword);
  const [srtPort, setSrtPort] = useState(String(app.srtPort));
  const [busy, setBusy] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [paired, setPaired] = useState<PairInfo | null>(null);
  const targets = useDiscovery((d) => d.targets);

  useEffect(() => {
    if (!visible) return;
    setHost(app.pcHost);
    setObsPort(String(app.obsPort));
    setPassword(app.obsPassword);
    setSrtPort(String(app.srtPort));
    setPaired(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  useEffect(() => onScanningChange(scanning), [scanning, onScanningChange]);

  const choose = async (t: Pick<Target, 'host' | 'port' | 'kind' | 'label' | 'pcName' | 'via' | 'ctl'>) => {
    onClose();
    // CamLink masaüstü: QR ile aynı eşleşme. USB satırında Wi‑Fi adresi bilinmez; duyuru gelince adres kendiliğinden düzelir.
    if (t.kind === 'webcam') return pairDesktop({ host: t.host, name: t.pcName, ctl: t.ctl ?? 4468, srt: t.via === 'usb' ? 9100 : t.port });
    await stopStreaming();
    app.save({ pcHost: t.host, srtPort: t.port, targetKind: t.kind, targetLabel: t.label, targetPcName: t.pcName });
    toast(`OBS: "${t.label}" seçildi`);
  };

  const runAuto = async (h = host, p = obsPort, pw = password) => {
    const srt = Number(srtPort) || 9000;
    if (!h.trim()) return toast('Bilgisayarın IP adresini yaz', 'error');
    setBusy(true);
    try {
      const r = await setupObsSource({ host: h, port: Number(p) || 4455, password: pw, srtPort: srt });
      app.save({ pcHost: h.trim(), obsPort: Number(p) || 4455, obsPassword: pw, srtPort: srt, targetKind: 'obs', targetLabel: 'CamLink', targetPcName: h.trim() });
      toast(r.created ? `OBS'te "CamLink" kaynağı "${r.scene}" sahnesine eklendi` : 'OBS kaynağı güncellendi');
      onClose();
    } catch (e: any) {
      toast(e?.message ?? 'OBS kurulamadı', 'error');
    } finally {
      setBusy(false);
    }
  };

  const saveManual = () => {
    if (!host.trim()) return toast('Bilgisayarın IP adresini yaz', 'error');
    app.save({ pcHost: host.trim(), srtPort: Number(srtPort) || 9000, targetKind: 'manual', targetLabel: '', targetPcName: '' });
    toast('Kaydedildi');
    onClose();
  };

  const srtUrl = listenerUrl(Number(srtPort) || 9000);

  const footer =
    mode === 'auto' ? (
      <Btn title={busy ? 'Kuruluyor…' : "OBS'te kaynağı oluştur"} icon="flash" style={{ flex: 1 }} disabled={busy} onPress={() => void runAuto()} />
    ) : mode === 'manual' ? (
      <Btn title="Kaydet" icon="checkmark" style={{ flex: 1 }} onPress={saveManual} />
    ) : (
      <Btn title="QR kodu okut" icon="qr-code" variant="secondary" style={{ flex: 1 }} onPress={() => setScanning(true)} />
    );

  return (
    <>
      <Sheet visible={visible && !scanning} onClose={onClose} title="Nereye yayın yapılsın?" full footer={footer}>
        <ChipGroup
          options={[
            { value: 'find', label: 'Bilgisayarı bul' },
            { value: 'auto', label: 'OBS WebSocket' },
            { value: 'manual', label: 'Elle' },
          ]}
          value={mode}
          onChange={(v) => setMode(v as Mode)}
        />

        {mode === 'find' ? (
          <>
            {paired ? (
              <>
                <Label>{paired.name}</Label>
                <TargetRow
                  icon="layers"
                  title="OBS'e"
                  sub="CamLink kaynağı"
                  onPress={() => void choose({ host: paired.host, port: 9000, kind: 'obs', label: 'OBS', pcName: paired.name, via: 'wifi' })}
                />
              </>
            ) : null}

            <Label>Bulunan bilgisayarlar</Label>
            {targets.length ? (
              targets.map((t) => (
                <TargetRow
                  key={t.key}
                  icon={t.kind === 'obs' ? 'layers' : t.via === 'usb' ? 'git-commit-outline' : 'videocam'}
                  title={t.label}
                  sub={`${t.pcName}  ${t.host}:${t.port}`}
                  badge={!t.ready ? 'Alıcı kapalı' : t.busy ? 'Kullanımda' : undefined}
                  selected={t.kind === app.targetKind && (t.kind === 'webcam' ? t.pcName === app.targetPcName : app.pcHost === t.host && app.srtPort === t.port)}
                  onPress={() => void choose(t)}
                />
              ))
            ) : (
              <View style={styles.searching}>
                <ActivityIndicator color={navy.accent} />
                <Text style={styles.searchingText}>Bilgisayar aranıyor…</Text>
              </View>
            )}
            <View style={styles.steps}>
              <Step n={1} text="Bilgisayarda CamLink'i aç. OBS kullanıyorsan CamLink eklentisini kur." />
              <Step n={2} text="Telefon ve bilgisayar aynı Wi‑Fi'a bağlı olsun ya da USB kablosuyla bağlansın. Bilgisayar burada kendiliğinden görünür." />
              <Step n={3} text="Görünmezse bilgisayardaki QR kodu okut." />
            </View>
          </>
        ) : mode === 'auto' ? (
          <>
            <Hint>
              Eklenti yoksa CamLink, OBS'te "CamLink" adlı bir Medya Kaynağı oluşturup açık sahneye ekler.
            </Hint>
            <View style={styles.steps}>
              <Step n={1} text="OBS'te Araçlar › WebSocket Sunucu Ayarları'nı aç, sunucuyu etkinleştir." />
              <Step n={2} text={'"Bağlantı bilgilerini göster"e bas.'} />
              <Step n={3} text="Çıkan QR kodu okut. Bilgiler kendiliğinden dolar." />
            </View>
            <Btn title="OBS'teki QR kodu okut" icon="qr-code" variant="secondary" onPress={() => setScanning(true)} disabled={busy} />
            {busy ? <ActivityIndicator color={navy.accent} /> : null}
            <Field label="Bilgisayarın IP adresi" value={host} onChangeText={setHost} placeholder="192.168.1.20" keyboardType="numbers-and-punctuation" autoCapitalize="none" />
            <View style={styles.row}>
              <View style={{ flex: 1 }}>
                <Field label="WebSocket portu" value={obsPort} onChangeText={setObsPort} keyboardType="number-pad" />
              </View>
              <View style={{ flex: 1 }}>
                <Field label="Görüntü portu" value={srtPort} onChangeText={setSrtPort} keyboardType="number-pad" />
              </View>
            </View>
            <Field label="WebSocket şifresi" value={password} onChangeText={setPassword} secureTextEntry autoCapitalize="none" />
          </>
        ) : (
          <>
            <Field label="Bilgisayarın IP adresi" value={host} onChangeText={setHost} placeholder="192.168.1.20" keyboardType="numbers-and-punctuation" autoCapitalize="none" />
            <Field label="Görüntü portu" value={srtPort} onChangeText={setSrtPort} keyboardType="number-pad" />
            <Hint>CamLink masaüstü için port 9100'dür. QR koduyla eklemek daha kolay.</Hint>
            <Label>OBS'te yapılacaklar</Label>
            <View style={styles.steps}>
              <Step n={1} text="Kaynaklar › + › Medya Kaynağı ekle." />
              <Step n={2} text={'"Yerel dosya" işaretini kaldır.'} />
              <Step n={3} text="Giriş alanına aşağıdaki adresi yapıştır." />
            </View>
            <Pressable
              onPress={() => {
                void Clipboard.setStringAsync(srtUrl);
                toast('Adres kopyalandı');
              }}
              style={styles.copyBox}
            >
              <Text style={styles.copyText} selectable>
                {srtUrl}
              </Text>
              <Ionicons name="copy-outline" size={18} color={navy.accent} />
            </Pressable>
            <View style={styles.steps}>
              <Step n={4} text={'Giriş biçimi: mpegts. Ağ arabelleğe alma: 1 MB.'} />
              <Step n={5} text={'"Kaynak etkinleştiğinde oynatmayı yeniden başlat" işaretsiz kalsın, Tamam\'a bas.'} />
            </View>
          </>
        )}

        <View style={styles.note}>
          <Ionicons name="shield-checkmark" size={18} color={navy.warning} />
          <Text style={styles.noteText}>
            Bağlanamıyorsan bilgisayarın güvenlik duvarında CamLink'e ve OBS'e izin ver. Telefon ve bilgisayar aynı Wi‑Fi'a bağlı olmalı.
          </Text>
        </View>
      </Sheet>

      <QrScanner
        visible={visible && scanning}
        onClose={() => setScanning(false)}
        onResult={(r) => {
          setScanning(false);
          if ('ctl' in r) {
            // CamLink masaüstü: kaydet, bağlan, yayın kendiliğinden başlar.
            onClose();
            onDesktopQr(r);
            return;
          }
          if ('obsPort' in r) {
            // StudioHub kodu: hedef türünü kullanıcı seçer; OBS bilgileri de saklanır.
            setPaired(r);
            setMode('find');
            app.save({ obsPort: r.obsPort, ...(r.obsPassword ? { obsPassword: r.obsPassword } : null) });
            return;
          }
          setHost(r.host);
          setObsPort(String(r.port));
          setPassword(r.password);
          void runAuto(r.host, String(r.port), r.password);
        }}
      />
    </>
  );
}

/** Bulunan hedef: basılınca seçilen tuş satırı. */
function TargetRow({
  icon,
  title,
  sub,
  badge,
  selected,
  onPress,
}: {
  icon: string;
  title: string;
  sub: string;
  badge?: string;
  selected?: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable onPress={onPress} accessibilityRole="button" accessibilityState={{ selected }}>
      {({ pressed }) => (
        <Keycap color={selected ? navy.primary : navy.surfaceHigh} pressed={pressed} height={4} radius={14} ring={selected ? navy.accent : undefined} faceStyle={styles.targetFace}>
          <View style={styles.targetIcon}>
            <Ionicons name={icon as any} size={22} color={selected ? '#fff' : navy.accent} />
          </View>
          <View style={{ flex: 1, minWidth: 0 }}>
            <Text style={styles.targetTitle} numberOfLines={1}>
              {title}
            </Text>
            <Text style={[styles.targetSub, selected && { color: 'rgba(255,255,255,0.8)' }]} numberOfLines={1}>
              {sub}
            </Text>
          </View>
          {badge ? <Text style={styles.badge}>{badge}</Text> : <Ionicons name={selected ? 'checkmark-circle' : 'chevron-forward'} size={20} color={selected ? '#fff' : navy.textMuted} />}
        </Keycap>
      )}
    </Pressable>
  );
}

function Step({ n, text }: { n: number; text: string }) {
  return (
    <View style={styles.step}>
      <View style={styles.stepNum}>
        <Text style={styles.stepNumText}>{n}</Text>
      </View>
      <Text style={styles.stepText}>{text}</Text>
    </View>
  );
}

/** QR okuyucu. `desktopOnly`: yalnız CamLink masaüstü kodu kabul edilir (ana ekrandaki "Bilgisayara bağlan"). */
export function QrScanner({
  visible,
  onClose,
  onResult,
  desktopOnly,
}: {
  visible: boolean;
  onClose: () => void;
  onResult: (r: { host: string; port: number; password: string } | PairInfo | CamlinkPair) => void;
  desktopOnly?: boolean;
}) {
  const [perm, requestPerm] = useCameraPermissions();
  const insets = useSafeAreaInsets();
  const [done, setDone] = useState(false);
  const [ready, setReady] = useState(false);
  const lastWrong = useRef(0);

  useEffect(() => {
    if (!visible) {
      setReady(false);
      return;
    }
    setDone(false);
    // Önizleme kamerayı bıraksın diye kısa bir bekleme.
    const t = setTimeout(() => setReady(true), 400);
    if (perm && !perm.granted && perm.canAskAgain) void requestPerm();
    return () => clearTimeout(t);
  }, [visible, perm, requestPerm]);

  return (
    <Modal visible={visible} animationType="fade" onRequestClose={onClose} statusBarTranslucent>
      <View style={styles.scanWrap}>
        {perm?.granted && ready ? (
          <CameraView
            style={StyleSheet.absoluteFill}
            facing="back"
            barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
            onBarcodeScanned={
              done
                ? undefined
                : ({ data }) => {
                    const r = parseCamlinkQr(data) ?? (desktopOnly ? null : (parsePairQr(data) ?? parseObsQr(data)));
                    if (r) {
                      setDone(true);
                      onResult(r);
                    } else if (Date.now() - lastWrong.current > 3000) {
                      lastWrong.current = Date.now();
                      toast(desktopOnly ? 'QR kod tanınmadı. Bilgisayardaki CamLink QR kodunu okut' : 'QR kod tanınmadı. CamLink, StudioHub ya da OBS QR kodunu okut', 'error');
                    }
                  }
            }
          />
        ) : null}
        <View style={styles.scanFrame} pointerEvents="none" />
        <View style={[styles.scanTop, { paddingTop: insets.top + 12, paddingLeft: insets.left + 16, paddingRight: insets.right + 16 }]}>
          <Text style={styles.scanTitle}>{desktopOnly ? 'Bilgisayardaki CamLink QR kodunu çerçeveye getir' : 'Bilgisayardaki ya da OBS\'teki QR kodu çerçeveye getir'}</Text>
          <Pressable onPress={onClose} style={styles.scanClose} hitSlop={8}>
            <Ionicons name="close" size={26} color="#fff" />
          </Pressable>
        </View>
      </View>
    </Modal>
  );
}

// ---------- Ayarlar ----------

const BITRATES = [3, 4, 6, 8, 12, 16, 20, 35];

export function SettingsSheet({ visible, onClose, onOpenSetup }: { visible: boolean; onClose: () => void; onOpenSetup: () => void }) {
  const app = useApp();
  const link = useDesktopLink();
  const streaming = app.state !== 'idle' && app.state !== 'failed';
  // Gönderilen kalite: kayıtlı kalitenin bu cihazdaki karşılığı ve onun önerdiği bit hızı (streamQuality/streamBitrate).
  const q = streamQuality(app.quality, app.qualities);
  const desktop = app.targetKind === 'webcam' && !!app.pcHost;

  return (
    <Sheet visible={visible} onClose={onClose} title="Ayarlar" full>
      <Section title="Görüntü" />
      {streaming ? <Hint>Kalite ya da yön değişince görüntü bir an durup yeniden başlar.</Hint> : null}
      <Label>Görüntü kalitesi</Label>
      <ChipGroup
        options={QUALITY_PRESETS.filter((p) => app.qualities.includes(p.id)).map((p) => ({ value: p.id, label: p.label }))}
        value={q.id}
        onChange={(v) => void control.quality(v)}
      />

      <Label>Bit hızı</Label>
      <ChipGroup
        options={[
          { value: 'auto', label: `Önerilen (${q.bitrate / 1_000_000} Mb/s)` },
          ...BITRATES.filter((b) => !desktop || b * 1_000_000 <= q.bitrate).map((b) => ({ value: String(b), label: `${b} Mb/s` })),
        ]}
        value={app.bitrateOverride ? String(app.bitrateOverride / 1_000_000) : 'auto'}
        onChange={(v) => {
          const bps = v === 'auto' ? undefined : Number(v) * 1_000_000;
          if (streaming) {
            useApp.setState({ bitrateOverride: bps });
            void CamlinkEngine.setBitrate(streamBitrate({ ...app, bitrateOverride: bps }));
          } else app.save({ bitrateOverride: bps });
        }}
      />
      <Hint>Wi-Fi zayıfsa bu değeri düşür. Ağ yavaşlarsa uygulama bunu kendisi de azaltır.</Hint>
      <ToggleRow label="Dikey yayın" hint="9:16 görüntü (Shorts, Reels için)" value={app.portrait} onChange={(portrait) => void changeStreamConfig({ portrait })} />

      <Section title="Ses" />
      <ToggleRow label="Telefon mikrofonu" hint="Kapalıyken ses gitmez, görüntü devam eder" value={app.audio} onChange={(on) => void control.mic(on)} />

      <Section title="Kamera" />
      <ToggleRow
        label="Fener"
        hint={app.camera?.facing === 'front' ? 'Ön kamerada fener yok' : undefined}
        value={!!app.camera?.torch}
        onChange={(on) => void control.torch(on)}
      />
      <ToggleRow label="Ayna" hint="Görüntüyü yatay çevirir" value={app.mirror} onChange={(on) => void control.mirror(on)} />
      <ToggleRow label="Otomatik odak" value={app.autoFocus} onChange={(on) => void control.af(on)} />

      <Label>Pozlama: {app.exposure > 0 ? `+${app.exposure}` : app.exposure}</Label>
      <Slider
        minimumValue={-12}
        maximumValue={12}
        step={1}
        value={app.exposure}
        onSlidingComplete={(v) => void control.exposure(v)}
        minimumTrackTintColor={navy.accent}
        maximumTrackTintColor={navy.outline}
        thumbTintColor="#fff"
      />

      <Section title="Efektler" />
      <EffectSettings />

      <Section title="Çift kamera" />
      <DualCameraSettings />

      <Section title="Bağlantı" />
      <View style={styles.infoBox}>
        <Text style={styles.infoText}>
          {desktop
            ? `Bilgisayar: ${link.pcName ?? app.targetPcName}${link.connected ? (link.via === 'usb' ? ' · USB' : ' · Wi‑Fi') : ''}`
            : app.pcHost
              ? `Hedef: ${app.pcHost} · port ${app.srtPort}`
              : 'Henüz bir bilgisayara bağlı değil'}
        </Text>
        {desktop ? <Text style={styles.infoSub}>{`${app.pcHost} · görüntü ${app.srtPort} · kontrol ${app.ctlPort}`}</Text> : null}
      </View>
      {app.pcHost ? (
        <>
          <Btn
            title="Bağlantıyı kes / başka bilgisayar"
            icon="unlink"
            variant="secondary"
            onPress={() => {
              onClose();
              void forgetDesktop();
            }}
          />
          <Hint>Sonra ana ekrandaki "Bilgisayara bağlan" ile yeni bilgisayarın QR kodunu okut.</Hint>
        </>
      ) : null}
      <Btn title="OBS ya da başka hedef (gelişmiş)" icon="layers-outline" variant="ghost" onPress={onOpenSetup} />
    </Sheet>
  );
}

/** Ayarlar'daki bölüm başlığı. */
function Section({ title }: { title: string }) {
  return (
    <Text style={styles.section} accessibilityRole="header">
      {title}
    </Text>
  );
}

/** Renk efektleri (ana ekrandan taşındı). Ayarlanabilir efektte şiddet kaydırıcısı. */
function EffectSettings() {
  const filterId = useApp((s) => s.filterId);
  const filterValue = useApp((s) => s.filterValue);
  const current = FILTERS.find((f) => f.id === filterId);
  return (
    <>
      <ChipGroup options={FILTERS.map((f) => ({ value: f.id, label: f.label }))} value={filterId} onChange={(id) => void control.filter(id)} />
      {current?.adjustable ? (
        <>
          <Label>Şiddet: %{Math.round(filterValue * 100)}</Label>
          <Slider
            minimumValue={0}
            maximumValue={1}
            value={filterValue}
            onSlidingComplete={(v) => void control.filterValue(v)}
            minimumTrackTintColor={navy.accent}
            maximumTrackTintColor={navy.outline}
            thumbTintColor="#fff"
          />
        </>
      ) : null}
    </>
  );
}

const CORNERS: { id: PipCorner; label: string }[] = [
  { id: 'TOP_LEFT', label: 'Sol üst' },
  { id: 'TOP_RIGHT', label: 'Sağ üst' },
  { id: 'BOTTOM_LEFT', label: 'Sol alt' },
  { id: 'BOTTOM_RIGHT', label: 'Sağ alt' },
];

const cornerMarkPos: Record<PipCorner, object> = {
  TOP_LEFT: { top: 4, left: 4 },
  TOP_RIGHT: { top: 4, right: 4 },
  BOTTOM_LEFT: { bottom: 4, left: 4 },
  BOTTOM_RIGHT: { bottom: 4, right: 4 },
};

/** Ön ve arka kamera birlikte, resim içinde resim (ana ekrandan taşındı). */
function DualCameraSettings() {
  const pip = useApp((s) => s.pip);
  const corner = useApp((s) => s.pipCorner);
  const size = useApp((s) => s.pipSize);
  const dual = useApp((s) => s.camera?.dualSupported);
  const save = useApp((s) => s.save);
  const apply = (enabled: boolean, c = corner, sz = size) => {
    useApp.setState({ pip: enabled });
    void CamlinkEngine.setPip(enabled, c, sz);
  };
  return (
    <>
      <ToggleRow
        label="Ön ve arka kamera birlikte"
        hint={dual === false ? 'Bu telefon bunu desteklemiyor görünüyor. Yine de deneyebilirsin.' : undefined}
        value={pip}
        onChange={apply}
      />
      {pip ? (
        <>
          <Label>Köşe</Label>
          <View style={styles.row}>
            {CORNERS.map((c) => (
              <Pressable
                key={c.id}
                accessibilityRole="button"
                accessibilityLabel={c.label}
                accessibilityState={{ selected: corner === c.id }}
                onPress={() => {
                  save({ pipCorner: c.id });
                  apply(true, c.id);
                }}
                style={[styles.cornerBtn, corner === c.id && styles.cornerBtnOn]}
              >
                <View style={[styles.cornerMark, cornerMarkPos[c.id], corner === c.id && { backgroundColor: navy.accent }]} />
              </Pressable>
            ))}
          </View>
          <Label>Boyut</Label>
          <ChipGroup
            options={[
              { value: '0.22', label: 'Küçük' },
              { value: '0.3', label: 'Orta' },
              { value: '0.4', label: 'Büyük' },
            ]}
            value={String(size)}
            onChange={(v) => {
              const n = Number(v);
              save({ pipSize: n });
              apply(true, corner, n);
            }}
          />
          <Btn title="Küçük görüntü yan duruyorsa döndür" icon="refresh" variant="ghost" onPress={() => void CamlinkEngine.rotatePip().then(() => toast('Küçük görüntü döndürüldü'))} />
        </>
      ) : null}
    </>
  );
}

const styles = StyleSheet.create({
  section: { color: navy.text, fontSize: 19, fontFamily: font.display, letterSpacing: 0.3, marginTop: space.sm },
  cornerBtn: { width: 46, height: 34, borderRadius: 7, backgroundColor: navy.well, borderWidth: 1, borderColor: navy.glassEdge },
  cornerBtnOn: { borderColor: navy.accent },
  cornerMark: { position: 'absolute', width: 15, height: 11, borderRadius: 2, backgroundColor: navy.outline },
  row: { flexDirection: 'row', gap: space.sm },
  steps: { gap: 10, backgroundColor: navy.glass, padding: space.md, borderRadius: radius.md, borderWidth: 1, borderColor: navy.glassEdge },
  step: { flexDirection: 'row', gap: 10, alignItems: 'flex-start' },
  stepNum: { width: 22, height: 22, borderRadius: 11, backgroundColor: navy.primary, alignItems: 'center', justifyContent: 'center', marginTop: 1 },
  stepNumText: { color: '#fff', fontSize: 13, fontFamily: font.keyBold },
  stepText: { color: navy.text, fontSize: 15, lineHeight: 21, flex: 1, fontFamily: font.body },
  copyBox: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    backgroundColor: navy.glass,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: navy.accent,
    padding: space.md,
  },
  copyText: { color: navy.accent, fontFamily: font.keyBold, fontSize: 16, flex: 1, letterSpacing: 0.2 },
  note: { flexDirection: 'row', gap: 8, padding: space.md, borderRadius: radius.md, backgroundColor: 'rgba(255,181,71,0.08)' },
  noteText: { color: navy.textMuted, fontSize: 13.5, lineHeight: 19, flex: 1, fontFamily: font.body },
  infoBox: { backgroundColor: navy.glass, padding: space.md, borderRadius: radius.md, borderWidth: 1, borderColor: navy.glassEdge },
  infoText: { color: navy.text, fontSize: 15, fontFamily: font.medium },
  infoSub: { color: navy.textMuted, fontSize: 13, fontFamily: font.body, marginTop: 4 },
  searching: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: space.md, borderRadius: radius.md, backgroundColor: navy.glass, borderWidth: 1, borderColor: navy.glassEdge },
  searchingText: { color: navy.textMuted, fontSize: 15, fontFamily: font.medium },
  targetFace: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 12, paddingHorizontal: 14 },
  targetIcon: { width: 40, height: 40, borderRadius: 12, backgroundColor: 'rgba(4,10,24,0.45)', alignItems: 'center', justifyContent: 'center' },
  targetTitle: { color: '#fff', fontSize: 17, fontFamily: font.keyBold },
  targetSub: { color: navy.textMuted, fontSize: 13.5, fontFamily: font.medium, fontVariant: ['tabular-nums'] },
  badge: { color: navy.warning, fontSize: 13, fontFamily: font.semi },
  scanWrap: { flex: 1, backgroundColor: '#000' },
  scanFrame: { position: 'absolute', alignSelf: 'center', top: '30%', width: 240, height: 240, borderRadius: 24, borderWidth: 3, borderColor: navy.accent },
  scanTop: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 16,
    paddingBottom: 12,
    backgroundColor: 'rgba(0,0,0,0.45)',
  },
  scanTitle: { color: '#fff', fontSize: 17, fontFamily: font.semi, flex: 1 },
  scanClose: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
});
