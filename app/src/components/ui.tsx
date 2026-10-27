import Ionicons from '@expo/vector-icons/Ionicons';
import { LinearGradient } from 'expo-linear-gradient';
import { useEffect, useRef, type ReactNode } from 'react';
import {
  Animated,
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Switch,
  Text,
  TextInput,
  View,
  type StyleProp,
  type TextInputProps,
  type ViewStyle,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { create } from 'zustand';
import { font, navy, radius, shade, space } from '../theme';

// ---------- Tuş kapağı: uygulamanın temel fiziksel öğesi ----------

/**
 * Yükseklikli bir tuş kapağı çizer: alttaki "etek" tuşun yüksekliğini verir,
 * basılınca etek incelir ve yüz aşağı iner.
 */
export function Keycap({
  color,
  pressed,
  height = 3,
  radius: r = 12,
  style,
  faceStyle,
  ring,
  children,
}: {
  color: string;
  pressed?: boolean;
  height?: number;
  radius?: number;
  style?: StyleProp<ViewStyle>;
  faceStyle?: StyleProp<ViewStyle>;
  ring?: string;
  children?: ReactNode;
}) {
  const skirt = pressed ? 1 : height;
  return (
    <View style={[{ borderRadius: r, backgroundColor: shade(color, -0.5), paddingTop: height - skirt }, style]}>
      <LinearGradient
        colors={[shade(color, 0.14), color, shade(color, -0.14)]}
        locations={[0, 0.5, 1]}
        style={[
          {
            // flex: 1 yerine: yüksekliği verilen kapaklar (seçim düğmeleri) ezilmesin
            flexGrow: 1,
            flexShrink: 1,
            marginBottom: skirt,
            borderRadius: r,
            borderWidth: ring ? 1.5 : 1,
            borderColor: ring ?? 'rgba(255,255,255,0.09)',
            overflow: 'hidden',
          },
          faceStyle,
        ]}
      >
        {/* Üst kenarda ışığın vurduğu ince parlaklık */}
        <View pointerEvents="none" style={[styles.capHighlight, { borderTopLeftRadius: r, borderTopRightRadius: r }]} />
        {children}
      </LinearGradient>
    </View>
  );
}

// ---------- Alttan açılan panel ----------

export function Sheet({
  visible,
  onClose,
  title,
  children,
  footer,
  full,
}: {
  visible: boolean;
  onClose: () => void;
  title: string;
  children: ReactNode;
  footer?: ReactNode;
  full?: boolean;
}) {
  const insets = useSafeAreaInsets();
  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose} statusBarTranslucent navigationBarTranslucent>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={styles.sheetWrap}>
        <Pressable style={StyleSheet.absoluteFill} onPress={onClose} accessibilityLabel="Paneli kapat" />
        <LinearGradient
          colors={[navy.chassisTop, navy.chassisBottom]}
          style={[styles.sheet, full && { flex: 1, marginTop: insets.top + 24 }, { paddingBottom: Math.max(insets.bottom, space.md), paddingLeft: insets.left, paddingRight: insets.right }]}
        >
          <View style={styles.sheetHandle} />
          <View style={styles.sheetHeader}>
            <Text style={styles.sheetTitle} numberOfLines={1}>
              {title}
            </Text>
            <IconBtn icon="close" onPress={onClose} label="Kapat" />
          </View>
          <ScrollView
            style={full ? { flex: 1 } : { maxHeight: 560 }}
            contentContainerStyle={{ paddingHorizontal: space.lg, paddingBottom: space.lg, gap: space.md }}
            keyboardShouldPersistTaps="handled"
          >
            {children}
          </ScrollView>
          {footer ? <View style={styles.sheetFooter}>{footer}</View> : null}
        </LinearGradient>
      </KeyboardAvoidingView>
    </Modal>
  );
}

// ---------- Düğmeler ----------

const btnColors = { primary: navy.primary, secondary: '#1B2D55', danger: '#9E1F33' } as const;

export function Btn({
  title,
  onPress,
  icon,
  variant = 'primary',
  disabled,
  style,
}: {
  title: string;
  onPress: () => void;
  icon?: string;
  variant?: 'primary' | 'secondary' | 'danger' | 'ghost';
  disabled?: boolean;
  style?: StyleProp<ViewStyle>;
}) {
  if (variant === 'ghost') {
    return (
      <Pressable onPress={onPress} disabled={disabled} style={({ pressed }) => [styles.ghost, { opacity: disabled ? 0.4 : pressed ? 0.6 : 1 }, style]}>
        {icon ? <Ionicons name={icon as any} size={18} color={navy.accent} /> : null}
        <Text style={[styles.btnText, { color: navy.accent }]}>{title}</Text>
      </Pressable>
    );
  }
  return (
    <Pressable onPress={onPress} disabled={disabled} style={[{ opacity: disabled ? 0.4 : 1 }, style]} accessibilityRole="button">
      {({ pressed }) => (
        <Keycap color={btnColors[variant]} pressed={pressed} height={4} radius={radius.md} style={{ minHeight: 50 }} faceStyle={styles.btnFace}>
          {icon ? <Ionicons name={icon as any} size={18} color="#fff" /> : null}
          <Text style={styles.btnText}>{title}</Text>
        </Keycap>
      )}
    </Pressable>
  );
}

/** Küçük donanım tuşu: üst çubuk ve paneller için. */
export function IconBtn({
  icon,
  onPress,
  label,
  color = navy.text,
  size = 20,
  active,
}: {
  icon: string;
  onPress: () => void;
  label: string;
  color?: string;
  size?: number;
  active?: boolean;
}) {
  return (
    <Pressable onPress={onPress} accessibilityLabel={label} accessibilityRole="button" hitSlop={6}>
      {({ pressed }) => (
        <Keycap color={active ? navy.primary : '#18284C'} pressed={pressed} height={3} radius={11} style={{ width: 42, height: 42 }} faceStyle={styles.center}>
          <Ionicons name={icon as any} size={size} color={active ? '#fff' : color} />
        </Keycap>
      )}
    </Pressable>
  );
}

// ---------- Form öğeleri ----------

export function Label({ children }: { children: ReactNode }) {
  return <Text style={styles.label}>{children}</Text>;
}

export function Hint({ children }: { children: ReactNode }) {
  return <Text style={styles.hint}>{children}</Text>;
}

export function Field(props: TextInputProps & { label?: string }) {
  const { label, style, ...rest } = props;
  return (
    <View style={{ gap: 6 }}>
      {label ? <Label>{label}</Label> : null}
      <TextInput placeholderTextColor={navy.textMuted} selectionColor={navy.accent} style={[styles.input, style]} autoCorrect={false} {...rest} />
    </View>
  );
}

export function ToggleRow({ label, value, onChange, hint }: { label: string; value: boolean; onChange: (v: boolean) => void; hint?: string }) {
  return (
    <View style={styles.toggleRow}>
      <View style={{ flex: 1, gap: 2 }}>
        <Text style={styles.rowText}>{label}</Text>
        {hint ? <Hint>{hint}</Hint> : null}
      </View>
      <Switch value={value} onValueChange={onChange} trackColor={{ true: navy.primary, false: navy.well }} thumbColor={value ? navy.accent : navy.textMuted} />
    </View>
  );
}

/** Seçim düğmesi: seçili olan yükselir ve arka ışığı yanar, diğerleri gövdeye gömülü durur. */
export function Chip({ label, selected, onPress }: { label: string; selected?: boolean; onPress: () => void }) {
  if (selected) {
    return (
      <Pressable onPress={onPress} accessibilityState={{ selected: true }} style={{ maxWidth: '100%' }}>
        {({ pressed }) => (
          <Keycap color={navy.primary} pressed={pressed} height={3} radius={10} ring={navy.accent} faceStyle={styles.chipFace}>
            <Text style={[styles.chipText, { color: '#fff' }]} numberOfLines={1}>
              {label}
            </Text>
          </Keycap>
        )}
      </Pressable>
    );
  }
  return (
    <Pressable onPress={onPress} style={({ pressed }) => [styles.chipWell, pressed && { opacity: 0.7 }]}>
      <Text style={styles.chipText} numberOfLines={1}>
        {label}
      </Text>
    </Pressable>
  );
}

export function ChipGroup({ options, value, onChange }: { options: { value: string; label: string }[]; value?: string; onChange: (v: string) => void }) {
  return (
    <View style={styles.chipGroup}>
      {options.map((o) => (
        <Chip key={o.value} label={o.label} selected={o.value === value} onPress={() => onChange(o.value)} />
      ))}
    </View>
  );
}

// ---------- Bildirim ----------

type Toast = { id: number; text: string; tone: 'error' | 'info' };
const useToast = create<{ items: Toast[] }>(() => ({ items: [] }));
let toastId = 0;

export function toast(text: string, tone: Toast['tone'] = 'info') {
  const id = ++toastId;
  useToast.setState((s) => ({ items: [...s.items.slice(-2), { id, text, tone }] }));
  setTimeout(() => useToast.setState((s) => ({ items: s.items.filter((t) => t.id !== id) })), tone === 'error' ? 4000 : 2200);
}

export function ToastHost() {
  const items = useToast((s) => s.items);
  const insets = useSafeAreaInsets();
  return (
    <View pointerEvents="none" style={[styles.toastHost, { top: insets.top + 64 }]}>
      {items.map((t) => (
        <ToastItem key={t.id} t={t} />
      ))}
    </View>
  );
}

function ToastItem({ t }: { t: Toast }) {
  const a = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    Animated.timing(a, { toValue: 1, duration: 160, useNativeDriver: true }).start();
  }, [a]);
  const c = t.tone === 'error' ? navy.live : navy.success;
  return (
    <Animated.View
      style={[styles.toast, { opacity: a, transform: [{ translateY: a.interpolate({ inputRange: [0, 1], outputRange: [10, 0] }) }] }]}
    >
      <View style={[styles.toastLed, { backgroundColor: c, shadowColor: c }]} />
      <Text style={styles.toastText}>{t.text}</Text>
    </Animated.View>
  );
}

export const styles = StyleSheet.create({
  center: { alignItems: 'center', justifyContent: 'center' },
  capHighlight: {
    position: 'absolute',
    top: 0,
    left: 6,
    right: 6,
    height: 1,
    backgroundColor: 'rgba(255,255,255,0.28)',
  },
  sheetWrap: { flex: 1, justifyContent: 'flex-end', backgroundColor: navy.scrim },
  sheet: {
    borderTopLeftRadius: radius.lg,
    borderTopRightRadius: radius.lg,
    borderTopWidth: 1,
    borderColor: navy.bevelLight,
  },
  sheetHandle: { alignSelf: 'center', width: 44, height: 5, borderRadius: 3, backgroundColor: navy.well, marginTop: 8, borderBottomWidth: 1, borderBottomColor: navy.bevelLight },
  sheetHeader: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: space.lg, paddingVertical: space.sm, gap: space.sm },
  sheetTitle: { flex: 1, color: navy.text, fontSize: 22, fontFamily: font.display, letterSpacing: 0.2 },
  sheetFooter: {
    flexDirection: 'row',
    gap: space.sm,
    paddingHorizontal: space.lg,
    paddingTop: space.md,
    borderTopWidth: 1,
    borderTopColor: navy.bevelDark,
  },
  btnFace: { flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 8, paddingHorizontal: space.lg },
  btnText: { fontSize: 16, fontFamily: font.keyBold, color: '#fff', letterSpacing: 0.2 },
  ghost: { flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 8, minHeight: 46, paddingHorizontal: space.md },
  label: { color: navy.textMuted, fontSize: 14, fontFamily: font.semi },
  hint: { color: navy.textMuted, fontSize: 13.5, lineHeight: 19, fontFamily: font.body },
  input: {
    backgroundColor: navy.well,
    borderWidth: 1,
    borderTopColor: navy.bevelDark,
    borderLeftColor: navy.bevelDark,
    borderRightColor: navy.glassEdge,
    borderBottomColor: navy.glassEdge,
    borderRadius: radius.md,
    color: navy.text,
    fontSize: 16,
    fontFamily: font.medium,
    paddingHorizontal: space.md,
    paddingVertical: 12,
  },
  toggleRow: { flexDirection: 'row', alignItems: 'center', gap: space.md, minHeight: 52 },
  rowText: { color: navy.text, fontSize: 16, fontFamily: font.medium },
  chipGroup: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chipFace: { paddingHorizontal: 14, height: 36, justifyContent: 'center' },
  chipWell: {
    paddingHorizontal: 14,
    height: 39,
    justifyContent: 'center',
    borderRadius: 10,
    backgroundColor: navy.well,
    borderWidth: 1,
    borderTopColor: navy.bevelDark,
    borderColor: navy.glassEdge,
    maxWidth: '100%',
  },
  chipText: { color: navy.text, fontSize: 15, fontFamily: font.key },
  toastHost: { position: 'absolute', left: 16, right: 16, alignItems: 'center', gap: 8 },
  toast: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    backgroundColor: navy.glass,
    borderColor: navy.glassEdge,
    borderWidth: 1,
    borderRadius: 12,
    paddingHorizontal: 14,
    paddingVertical: 11,
    maxWidth: 520,
  },
  toastLed: { width: 8, height: 8, borderRadius: 4, shadowOpacity: 0.9, shadowRadius: 6, shadowOffset: { width: 0, height: 0 }, elevation: 3 },
  toastText: { color: navy.text, fontSize: 15, fontFamily: font.medium, flexShrink: 1 },
});
