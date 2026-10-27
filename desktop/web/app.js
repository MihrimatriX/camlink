'use strict';

// CamLink masaüstü penceresi. Telefon yokken ortada eşleştirme QR'ı, bağlanınca önizleme; altında tek durum satırı.
// Sorunlar yalnız varken "Dikkat"te, telefon ayarları katlanır "Kamera ayarları"nda (caps'ten üretilir).
// /api/status saniyede bir yoklanır; yalnız bu bilgisayardan, aynı kökenden açılır.
// Telefondan gelen her metin (ad, lens/efekt/kalite etiketleri) textContent ile yazılır, asla innerHTML ile değil.
const $ = (id) => document.getElementById(id);

async function api(method, path, body) {
  const r = await fetch('/api' + path, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const type = r.headers.get('content-type') || '';
  const data = type.includes('json') ? await r.json() : null;
  if (!r.ok) throw new Error((data && data.message) || `İşlem yapılamadı (${r.status})`);
  return data;
}

let toastTimer;
function toast(text, kind = 'on') {
  const t = $('toast');
  t.querySelector('.led').className = 'led ' + kind;
  $('toast-text').textContent = text;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 4200);
}

function led(el, kind, blink = false) {
  el.className = 'led' + (kind ? ' ' + kind : '') + (blink ? ' blink' : '');
}

const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
const fmt = (n, d) => n.toFixed(d).replace('.', ',');
const str = (v) => (typeof v === 'string' ? v : '');
const arr = (v) => (Array.isArray(v) ? v : []);
const own = (o, k) => (Object.hasOwn(o, k) ? o[k] : undefined);

const WB = { auto: 'Otomatik', daylight: 'Gün ışığı', cloudy: 'Bulutlu', incandescent: 'Akkor', fluorescent: 'Floresan' };
const micNote = $('mic-note'); // kontroller yeniden kurulunca mikrofon anahtarının yanına taşınır

let last = null;
let receiving = false;
let showPair = false; // bağlıyken "Başka telefon eşle" küçük QR'ı açar
let capsKey = '';
let alertsKey = '';
let qrKey = '';
const busy = { cam: false, fw: false, stream: false };

// Kullanıcı bir denetimi değiştirdikten sonra telefonun eski durumu onu 1,5 sn geri almasın.
const edited = {};
const mark = (key) => (edited[key] = Date.now());
const held = (key) => Date.now() - (edited[key] || 0) < 1500;

// ---------------- Görüntüleme ----------------

function render(st) {
  last = st;
  const p = st.platform;
  const ph = st.phone;
  const { srt, rtmp } = p.receivers;
  // Sanal kameraya önce görüntü getiren yazar; aynı anda ikisi gelmez (tek telefon).
  const recv = rtmp.state === 'Receiving' ? rtmp : srt.state === 'Receiving' ? srt : null;
  const via = recv === rtmp ? 'USB' : recv === srt ? 'Wi‑Fi' : null;
  const live = ph.connected || !!recv;
  receiving = !!recv;
  previewConnect();
  if (!live) showPair = false;

  // Ana öğe: telefon yokken büyük QR, varken önizleme. Küçük QR açıkken başlık ne yapılacağını söyler.
  $('headline').hidden = live && !showPair;
  $('monitor').hidden = !live;
  $('preview').hidden = !recv;
  $('monitor-idle').hidden = !!recv;
  $('monitor').classList.toggle('live', !!recv && p.camera.inUse);
  const qrShown = !live || showPair;
  $('qr-box').hidden = !qrShown;
  $('qr-box').classList.toggle('small', live);
  $('fw-btn').hidden = !qrShown;
  $('fw-btn').disabled = busy.fw;
  $('pair-btn').hidden = !live;
  $('pair-btn').textContent = showPair ? 'Eşleştirme kodunu gizle' : 'Başka telefon eşle';
  const qk = `${p.address}|${p.pcName}`;
  if (qk !== qrKey) {
    qrKey = qk;
    $('qr').src = '/api/qr.svg?k=' + encodeURIComponent(qk);
  }

  // Tek durum satırı
  let text;
  let kind;
  if (ph.connected) [text, kind] = [`Bağlı: ${str(ph.name) || 'Telefon'} · ${via || 'görüntü bekleniyor…'}`, recv ? 'on' : 'info'];
  else if (recv) [text, kind] = [`Görüntü geliyor · ${via}`, 'on'];
  else [text, kind] = ['Telefon bekleniyor…', 'warn'];
  $('status-text').textContent = text;
  led($('status-led'), kind, !live);

  const cam = p.camera;
  $('camline').hidden = !cam.running || cam.hung;
  $('camline').textContent = cam.inUse ? '“CamLink Kamera” şu an bir uygulamada açık' : 'Zoom, Teams veya OBS\'te kamera olarak “CamLink Kamera”yı seç';

  renderAlerts(p);
  renderSettings(p, ph);
}

// ---------------- Dikkat: yalnız sorun varken ----------------

// Teknik ayrıntı (port, StudioHub, Çerçeve Sunucusu) içeren iletinin önüne sade bir ilk cümle konur.
const plain = (lead, text) => (/Çerçeve Sunucusu|StudioHub|[Pp]ort \d/.test(text) ? `${lead} ${text}` : text);

function renderAlerts(p) {
  const install = 'Kur';
  const items = []; // [metin, düğme?]
  const c = p.camera;
  // Eski sürüm sürücü belleğe kare yazdırmaz; alıcının "yazılamadı" hatası aynı sorunun ikinci uyarısı olur, gizlenir.
  const updateShown = c.supported && !c.installing && !c.hung && c.installed && c.needsUpdate && c.bundled;
  if (!c.supported) items.push(['Sanal kamera için Windows 11 gerekiyor.']);
  else if (c.installing) items.push(['Sanal kamera kuruluyor… Windows izin isterse Evet\'e bas.']);
  else if (c.hung) items.push(["Sanal kamera yanıt vermiyor. CamLink'i kapatıp yeniden aç."]);
  else if (!c.installed) items.push(c.bundled ? ['Sanal kamera kurulu değil.', install] : ["Sanal kamera dosyası eksik. CamLink'i yeniden indir."]);
  else if (updateShown) items.push(['Kamera güncellemesi gerekli.', install]);
  else if (!c.running && c.error) items.push([plain('Sanal kamera başlatılamadı.', c.error)]);
  if (c.lastOpenError) items.push([plain('Sanal kamera görüntü alamıyor.', c.lastOpenError)]);
  // Port dolu, FFmpeg yok vb.: alıcının kendi Türkçe iletisi (spec §9).
  for (const e of new Set([p.receivers.srt.lastError, p.receivers.rtmp.lastError].filter(Boolean))) {
    if (updateShown && e.startsWith('Sanal kameraya yazılamadı')) continue;
    items.push([plain('Telefon görüntüsü alınamıyor.', e)]);
  }
  const u = p.usb;
  const devices = arr(u.devices);
  if (u.available && devices.some((d) => d.state === 'unauthorized')) items.push(['Telefonda USB hata ayıklamaya izin ver.']);
  // adb'nin ham iletisi gösterilmez; yalnız takılı ve izinli bir telefon varken sade bir satır.
  if (u.available && u.error && devices.some((d) => d.state === 'device')) items.push(['USB bağlantısı kurulamadı. Kabloyu çıkarıp tak.']);

  const key = JSON.stringify(items);
  if (key !== alertsKey) {
    alertsKey = key;
    $('alert-list').replaceChildren(...items.map(([text, action]) => {
      const li = document.createElement('li');
      const span = document.createElement('span');
      span.textContent = text;
      li.append(span);
      if (action) {
        const b = document.createElement('button');
        b.className = 'key key-small key-primary';
        b.textContent = action;
        b.addEventListener('click', installCamera);
        li.append(b);
      }
      return li;
    }));
    $('alerts').hidden = items.length === 0;
  }
  // Liste yeniden kurulmadan: odak düğmede kalır.
  for (const b of $('alert-list').querySelectorAll('button')) b.disabled = busy.cam;
}

function installCamera() {
  toast('Windows izin isteyecek. Evet\'e bas.', 'info');
  run('cam', () => api('POST', '/camera/install'));
}

// ---------------- Kamera ayarları (katlanır) ----------------

function renderSettings(p, ph) {
  const s = (ph.connected && ph.state) || {};
  const caps = ph.connected && ph.caps && typeof ph.caps === 'object' ? ph.caps : null;
  $('settings').hidden = !caps;
  const key = caps ? JSON.stringify(caps) : '';
  if (key !== capsKey) {
    capsKey = key;
    buildControls(caps);
  }
  if (!caps) return;
  for (const update of controls) update(s, p);

  // Mikrofon isteğe bağlı: VB-Cable yoksa hata değil, yalnız bir not.
  const a = p.audio;
  $('mic-text').textContent = a.device
    ? 'Telefonun mikrofonunu Windows\'ta “CABLE Output” olarak seçebilirsin.'
    : 'Telefonun mikrofonunu Windows\'ta kullanmak için ücretsiz VB-Cable programı gerekir.';
  $('mic-meter').hidden = !a.device;
  $('mic-link').hidden = !!a.device;
  $('mic-level').style.width = meterPercent(a.level) + '%';

  const streaming = s.streaming === true;
  const btn = $('stream-btn');
  btn.textContent = streaming ? 'Yayını durdur' : 'Yayını başlat';
  btn.className = 'key key-small ' + (streaming ? 'key-danger' : 'key-primary');
  btn.dataset.streaming = streaming ? '1' : '';
  btn.disabled = busy.stream;
}

// RMS → yüzde: -60 dBFS boş, 0 dBFS dolu.
function meterPercent(level) {
  if (!(level > 0)) return 0;
  return Math.max(0, Math.min(100, ((20 * Math.log10(level) + 60) / 60) * 100));
}

let controls = []; // her biri (state) => void

function buildControls(caps) {
  const root = $('ctl');
  root.replaceChildren();
  controls = [];
  if (!caps) return;

  const lenses = arr(caps.lenses).filter((l) => l && typeof l.id === 'string');
  if (lenses.length) section(root, 'Lens', chipGroup('lens', lenses.map((l) => [l.id, str(l.label) || l.id])));

  section(root, 'Zoom', slider('zoom', { step: 0.1, format: (v) => fmt(v, 1) + '×', range: (s) => [s.zoomMin, s.zoomMax] }));

  const filters = arr(caps.filters).filter((f) => f && typeof f.id === 'string');
  if (filters.length) {
    const adjustable = new Set(filters.filter((f) => f.adjustable === true).map((f) => f.id));
    section(root, 'Efekt',
      chipGroup('filter', filters.map((f) => [f.id, str(f.label) || f.id]), 'effects'),
      slider('filterValue', { min: 0, max: 1, step: 0.05, format: (v) => '%' + Math.round(v * 100), visible: (s) => adjustable.has(s.filter) }));
  }

  const toggles = document.createElement('div');
  toggles.className = 'toggles';
  if (caps.torch === true) toggles.append(toggle('torch', 'Fener'));
  toggles.append(toggle('af', 'Otomatik odak'));
  if (caps.stabilization === true) toggles.append(toggle('stabilization', 'Sabitleme'));
  toggles.append(toggle('mirror', 'Ayna'), toggle('mic', 'Mikrofon'));
  root.append(toggles, micNote);

  const ex = caps.exposure;
  if (ex && isNum(ex.min) && isNum(ex.max) && ex.max > ex.min)
    section(root, 'Pozlama', slider('exposure', { min: ex.min, max: ex.max, step: 1, format: (v) => (v > 0 ? '+' : '') + v }));

  const wb = arr(caps.wb).filter((w) => typeof w === 'string');
  if (wb.length) section(root, 'Beyaz dengesi', chipGroup('wb', wb.map((w) => [w, own(WB, w) || w])));

  // "Otomatik": Windows uygulamasının istediği kaliteyi masaüstü telefondan kendisi ister. Bir kalite seçmek kipi "sabit" yapar.
  const q = arr(caps.qualities).filter((x) => typeof x === 'string');
  if (q.length)
    section(root, 'Kalite', chipGroup('quality', [['auto', 'Otomatik'], ...q.map((x) => [x, qualityLabel(x)])], 'chips', {
      onPick: pickQuality,
      current: (s, p) => (p.settings && p.settings.qualityMode === 'fixed' ? s.quality : 'auto'),
    }));
}

const QUALITY_NAME = { 2160: '4K', 1440: '2K' };

// 2160p30 → "4K · 30 fps", 1440p30 → "2K · 30 fps", 1080p60 → "1080p · 60 fps"
function qualityLabel(id) {
  const m = /^(\d+)p(\d+)$/.exec(id);
  return m ? `${own(QUALITY_NAME, m[1]) || m[1] + 'p'} · ${m[2]} fps` : id;
}

// Önce kip kaydedilir (sabitken otomatik döngü seçimi geri almasın), sonra telefona kalite gider.
async function pickQuality(value) {
  try {
    const r = await api('POST', '/settings', { qualityMode: value === 'auto' ? 'auto' : 'fixed' });
    if (r && r.ok === false) return toast(r.message || 'Ayar kaydedilemedi', 'warn');
  } catch (e) {
    return toast(e.message, 'live');
  }
  if (value !== 'auto') send('quality', value);
}

function section(root, title, ...nodes) {
  const label = document.createElement('span');
  label.className = 'field-label';
  label.textContent = title;
  root.append(label, ...nodes);
}

// onPick: tıklanınca yapılacak iş (varsayılan: telefona key=değer yolla); current: (durum, durumSayfası) → seçili çip.
function chipGroup(key, items, cls = 'chips', { onPick = (v) => send(key, v), current = (s) => s[key] } = {}) {
  const box = document.createElement('div');
  box.className = cls;
  const select = (value) => {
    for (const b of box.children) b.classList.toggle('sel', b.dataset.value === String(value));
  };
  for (const [value, label] of items) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'chip';
    b.textContent = label;
    b.title = label;
    b.dataset.value = value;
    b.addEventListener('click', () => {
      mark(key);
      select(value);
      onPick(value);
    });
    box.append(b);
  }
  controls.push((s, p) => {
    if (!held(key)) select(current(s, p));
  });
  return box;
}

function slider(key, { min, max, step, format, range, visible }) {
  const wrap = document.createElement('div');
  wrap.className = 'slider';
  const input = document.createElement('input');
  input.type = 'range';
  input.step = step;
  if (min !== undefined) {
    input.min = min;
    input.max = max;
  }
  const out = document.createElement('output');
  const push = throttled((v) => send(key, v), 100);
  input.addEventListener('input', () => {
    mark(key);
    out.textContent = format(+input.value);
    push(+input.value);
  });
  wrap.append(input, out);
  controls.push((s) => {
    if (visible) wrap.hidden = !visible(s);
    if (range) {
      const [lo, hi] = range(s);
      if (isNum(lo) && isNum(hi) && hi > lo) {
        input.min = lo;
        input.max = hi;
      }
    }
    if (!isNum(s[key]) || held(key)) return;
    input.value = s[key];
    out.textContent = format(s[key]);
  });
  return wrap;
}

function toggle(key, label) {
  const l = document.createElement('label');
  l.className = 'toggle';
  const input = document.createElement('input');
  input.type = 'checkbox';
  const sw = document.createElement('span');
  sw.className = 'switch';
  l.append(input, sw, label);
  input.addEventListener('change', () => {
    mark(key);
    send(key, input.checked);
  });
  controls.push((s) => {
    if (!held(key)) input.checked = s[key] === true;
  });
  return l;
}

// Kaydırıcı sürüklenirken en çok 100 ms'de bir yollar; son değer her zaman gider.
function throttled(fn, ms) {
  let pending;
  let has = false;
  let timer = 0;
  const run = () => {
    if (!has) {
      timer = 0;
      return;
    }
    has = false;
    fn(pending);
    timer = setTimeout(run, ms);
  };
  return (v) => {
    pending = v;
    has = true;
    if (!timer) run();
  };
}

async function send(key, value) {
  try {
    const r = await api('POST', '/phone/set', { key, value });
    if (!r.ok) toast('Telefona iletilemedi', 'warn');
  } catch (e) {
    toast(e.message, 'live');
  }
}

// ---------------- Eylemler ----------------

async function run(flag, work) {
  busy[flag] = true;
  if (last) render(last);
  try {
    const r = await work();
    if (r && typeof r.message === 'string') toast(r.message, r.ok ? 'on' : 'warn');
    else if (r && r.ok === false) toast('İşlem yapılamadı', 'warn');
  } catch (e) {
    toast(e.message, 'live');
  } finally {
    busy[flag] = false;
  }
}

$('fw-btn').addEventListener('click', () => {
  toast('Windows izin isteyecek. Evet\'e bas.', 'info');
  run('fw', () => api('POST', '/firewall'));
});
$('pair-btn').addEventListener('click', () => {
  showPair = !showPair;
  if (last) render(last);
});
$('stream-btn').addEventListener('click', () => {
  const stop = $('stream-btn').dataset.streaming === '1';
  run('stream', async () => {
    const r = await api('POST', stop ? '/phone/stop' : '/phone/start');
    if (!r.ok) toast('Telefona iletilemedi', 'warn');
  });
});
$('qr').addEventListener('error', () => ($('qr-error').hidden = false));
$('qr').addEventListener('load', () => ($('qr-error').hidden = true));

// ---------------- Döngüler ----------------

async function poll() {
  try {
    render(await api('GET', '/status'));
  } catch {
    $('status-text').textContent = 'CamLink yanıt vermiyor';
    led($('status-led'), 'live', true);
  }
  setTimeout(poll, 1000);
}

// Önizleme: sunucu her yeni kareyi WebSocket'ten iter (yolda en çok bir kare); çizim ekran yenilemesinde, yalnız en son kare.
// Yuva yalnız görüntü varken ve sayfa görünürken açıktır; durum yoklaması (1 sn) kopan yuvayı yeniden açar.
let previewWs = null;
let previewBlob = null;
let previewDrawing = false;

function previewConnect() {
  const want = receiving && !document.hidden;
  if (!want) {
    if (previewWs) previewWs.close();
    previewWs = null;
    return;
  }
  if (previewWs) return;
  const ws = (previewWs = new WebSocket(`ws://${location.host}/api/preview-ws`));
  ws.onmessage = (e) => {
    previewBlob = e.data;
    if (!previewDrawing) {
      previewDrawing = true;
      requestAnimationFrame(previewDraw);
    }
  };
  ws.onclose = () => {
    if (previewWs === ws) previewWs = null;
  };
}

async function previewDraw() {
  const blob = previewBlob;
  previewBlob = null;
  try {
    const bmp = await createImageBitmap(blob);
    const c = $('preview');
    if (c.width !== bmp.width || c.height !== bmp.height) {
      c.width = bmp.width;
      c.height = bmp.height;
    }
    c.getContext('2d').drawImage(bmp, 0, 0);
    bmp.close();
  } catch {
    // bozuk kare: sıradaki gelir
  }
  if (previewBlob) requestAnimationFrame(previewDraw);
  else previewDrawing = false;
}

document.addEventListener('visibilitychange', previewConnect);

poll();
