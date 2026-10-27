#include "media_stream.h"

#include <ksmedia.h>
#include <sddl.h>

#include <algorithm>
#include <cstring>
#include <iterator>

#include "nv12_scale.h"

using Microsoft::WRL::ComPtr;

namespace {

// Sunulan türler, hepsi NV12. Sıra sabittir: ilki varsayılandır (tür seçmeyen uygulamalar onu alır).
struct Mode {
  UINT32 width, height, fps;
};
const Mode kModes[] = {
    {1920, 1080, 30}, {1920, 1080, 60}, {3840, 2160, 30}, {3840, 2160, 60}, {2560, 1440, 30},
    {2560, 1440, 60}, {1280, 720, 30},  {1280, 720, 60},  {960, 540, 30},   {960, 540, 60},
    {640, 360, 30},   {640, 360, 60},   {640, 480, 30},
};

// SYSTEM, LocalService (Frame Server), Administrators: tam erişim; Interactive Users: okuma/yazma.
const wchar_t kShmSddl[] = L"D:(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;IU)";
const DWORD kShmAccess = FILE_MAP_READ | FILE_MAP_WRITE;
const ULONGLONG kStaleMs = 2000;
const DWORD kAllocatorSamples = 10;
// ponytail: ayırıcı boşken 5 ms'de bir yoklanır. IMFVideoSampleAllocatorNotify kullanılmadı: ayırıcı
// Frame Server'ın ve tek geri çağrı yuvası var (SetCallback onunkini ezerdi). Yoklama yalnız tüketici
// örnekleri tutarken çalışır; maliyet ölçülürse kendi ayırıcımıza (UsesCustomAllocator) geçilir.
const INT64 kRetryMs = 5;
// Bu kadar art arda boş havuzdan sonra (tüketici örnekleri bırakmıyor) yoklama bir kare süresine yavaşlar.
const uint32_t kBackoffMisses = 20;

// Yazarın karesi: çift, > 0 ve belleğe sığar (çarpım 64 bitte; taşmaz).
bool ValidSource(UINT32 w, UINT32 h) {
  return w > 0 && h > 0 && w % 2 == 0 && h % 2 == 0 && static_cast<ULONGLONG>(w) * h * 3 / 2 <= CLVC_CAPACITY;
}

// Başlığın anlık kopyası. Bellek güvenilmeyen bir süreçle paylaşılır: her alan volatile erişimle
// tam bir kez okunur, doğrulama ve atama yalnız bu kopyadan yapılır (memcpy + yerel kopya,
// derleyicinin as-if kuralıyla yeniden yüklemeye çevirebileceği için yeterli değil).
struct HeaderSnapshot {
  uint32_t width, height;
  uint64_t writerTick;
};

HeaderSnapshot Snapshot(const ClvcHeader* shm) {
  const volatile ClvcHeader* v = shm;
  return {v->width, v->height, static_cast<uint64_t>(v->writerTick)};
}

// Tüketicinin türü (0'lar: akış yok). Masaüstü okur; alanlar birbirinden bağımsız 32 bit yazılır.
void WriteRequest(ClvcHeader* shm, UINT32 w, UINT32 h, UINT32 fps) {
  volatile ClvcHeader* v = shm;
  v->reqW = w;
  v->reqH = h;
  v->reqFps = fps;
}

// Frame Server'ın akışı tanıması için gereken öznitelikler (tanımlayıcı ve akış özniteliklerine).
HRESULT SetStreamAttributes(IMFAttributes* a) {
  HRESULT hr = a->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE);
  if (SUCCEEDED(hr)) hr = a->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
  if (SUCCEEDED(hr)) hr = a->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
  if (SUCCEEDED(hr)) hr = a->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);
  return hr;
}

HRESULT CreateType(const Mode& m, IMFMediaType** type) {
  ComPtr<IMFMediaType> t;
  HRESULT hr = MFCreateMediaType(&t);
  if (FAILED(hr)) return hr;
  t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
  t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
  MFSetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, m.width, m.height);
  MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, m.fps, 1);
  MFSetAttributeRatio(t.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
  t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
  t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
  t->SetUINT32(MF_MT_DEFAULT_STRIDE, m.width);
  const UINT64 bits = static_cast<UINT64>(m.width) * m.height * 3 / 2 * 8 * m.fps;  // 4K60 32 biti aşar
  t->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(std::min<UINT64>(bits, UINT32_MAX)));
  *type = t.Detach();
  return S_OK;
}

}  // namespace

MediaStream::~MediaStream() { CloseSharedMemory(); }

HRESULT MediaStream::RuntimeClassInitialize(IMFMediaSource* source) {
  return Guard([&]() -> HRESULT {
    source_ = source;
    std::vector<IMFMediaType*> raw;
    for (const Mode& m : kModes) {
      ComPtr<IMFMediaType> t;
      HRESULT hr = CreateType(m, &t);
      if (FAILED(hr)) return hr;
      types_.push_back(t);
      raw.push_back(t.Get());
    }
    HRESULT hr = MFCreateStreamDescriptor(0, static_cast<DWORD>(raw.size()), raw.data(), &descriptor_);
    if (FAILED(hr)) return hr;
    ComPtr<IMFMediaTypeHandler> handler;
    hr = descriptor_->GetMediaTypeHandler(&handler);
    if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(types_[0].Get());
    if (SUCCEEDED(hr)) hr = SetStreamAttributes(descriptor_.Get());
    if (SUCCEEDED(hr)) hr = MFCreateAttributes(&attributes_, 4);
    if (SUCCEEDED(hr)) hr = SetStreamAttributes(attributes_.Get());
    if (SUCCEEDED(hr)) hr = MFCreateEventQueue(&queue_);
    return hr;
  });
}

// Tanımlayıcının geçerli türünü (Frame Server, tüketicinin seçtiğini işleyiciye yazar) kModes'ta bulur.
HRESULT MediaStream::FindMode(IMFStreamDescriptor* descriptor, size_t* mode) {
  ComPtr<IMFMediaTypeHandler> handler;
  ComPtr<IMFMediaType> type;
  HRESULT hr = descriptor->GetMediaTypeHandler(&handler);
  if (SUCCEEDED(hr)) hr = handler->GetCurrentMediaType(&type);
  UINT32 w = 0, h = 0, num = 0, den = 0;
  if (SUCCEEDED(hr)) hr = MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
  if (SUCCEEDED(hr)) hr = MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
  if (FAILED(hr)) return hr;
  for (size_t i = 0; i < std::size(kModes); i++) {
    if (kModes[i].width == w && kModes[i].height == h && den && static_cast<UINT64>(kModes[i].fps) * den == num) {
      *mode = i;
      return S_OK;
    }
  }
  return MF_E_INVALIDMEDIATYPE;
}

HRESULT MediaStream::GetAttributes(IMFAttributes** attributes) {
  return Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    return attributes_.CopyTo(attributes);
  });
}

// Hazırlanmış ayırıcıyı (mode türüyle) kilit altında yayınlar. replace: Frame Server'ın ayırıcısı mevcut
// olanın yerine geçer; değilse (kendi ayırıcımız) zaten bir ayırıcı varsa yenisi bırakılır.
// Bırakılan ayırıcının Uninitialize'ı kilit dışında yapılır.
HRESULT MediaStream::PublishAllocator(ComPtr<IMFVideoSampleAllocator> next, bool replace, size_t mode) {
  ComPtr<IMFVideoSampleAllocator> drop;
  HRESULT hr = S_OK;
  {
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) {
      drop = next;
      hr = MF_E_SHUTDOWN;
    } else if (allocator_ && !replace) {
      drop = next;
    } else {
      drop = allocator_;
      allocator_ = next;
      allocMode_ = mode;
    }
  }
  if (drop == next) drop = nullptr;  // yeni verilen ayırıcı hiç Uninitialize edilmez (aynısıysa yayındadır)
  if (drop) drop->UninitializeSampleAllocator();
  return hr;
}

// Ayırıcı mode türüyle hazırsa hiçbir şey yapmaz. Başka türle hazırlanmışsa yeni türle yeniden hazırlar
// (Microsoft'un VirtualCamera örneği her Start'ta yapar; önceki istemci örnekleri tutarken de sınandı);
// akış çalışırken tür değişmez (uçuştaki istek eski boyutta kareyi yeni tampona yazardı): eşzamanlı
// MF_E_INVALIDREQUEST, olay kuyruğa girmez. Hiç yoksa (Frame Server vermediyse) kendi (sistem belleği)
// ayırıcımızı hazırlar. Hazırlık hiçbir kilit tutulmadan yapılır.
HRESULT MediaStream::EnsureAllocator(size_t mode) {
  return Guard([&]() -> HRESULT {
    ComPtr<IMFVideoSampleAllocator> current;
    {
      std::lock_guard<std::mutex> g(lock_);
      if (!queue_) return MF_E_SHUTDOWN;
      if (allocator_ && allocMode_ == mode) return S_OK;
      if (allocator_ && state_ == MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;
      current = allocator_;
    }
    if (current) {
      HRESULT hr = current->InitializeSampleAllocator(kAllocatorSamples, types_[mode].Get());
      if (FAILED(hr)) return hr;
      std::lock_guard<std::mutex> g(lock_);
      if (!queue_) return MF_E_SHUTDOWN;
      if (allocator_ == current) allocMode_ = mode;  // arada değiştirildiyse Activate türü denetler
      return S_OK;
    }
    ComPtr<IMFVideoSampleAllocatorEx> own;
    HRESULT hr = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&own));
    if (SUCCEEDED(hr)) hr = own->InitializeSampleAllocator(kAllocatorSamples, types_[mode].Get());
    if (FAILED(hr)) return hr;
    return PublishAllocator(own, false, mode);
  });
}

// lock_ altında RUNNING'e geçiş: tür kaydedilir, bellek (gerekirse) açılır ve türü başlığa yazılır.
HRESULT MediaStream::EnterRunningLocked(size_t mode) {
  if (!allocator_ || allocMode_ != mode) return MF_E_NOT_INITIALIZED;
  mode_ = mode;
  state_ = MF_STREAM_STATE_RUNNING;
  const Mode& m = kModes[mode];
  if (ClvcHeader* shm = SharedMemoryLocked()) WriteRequest(shm, m.width, m.height, m.fps);
  return S_OK;
}

HRESULT MediaStream::Activate(size_t mode) {
  return Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    return EnterRunningLocked(mode);
  });
}

// lock_ altında RUNNING'den çıkış: istek alanları sıfırlanır (yalnız çalışan akış; başka bir kaynak
// nesnesinin isteğini silmesin), nesil artar (durdurmadan önce alınmış bir istek yeniden başlatmadan
// sonra teslim edilmez) ve bekleyen istekler dropped'a alınır; çağıran onları kilidi bıraktıktan sonra
// serbest bırakır (belirteç Frame Server'ındır; RequestSample belgesi: karşılanamayan istekte belirteç
// bırakılır).
void MediaStream::LeaveRunningLocked(MF_STREAM_STATE next, std::deque<ComPtr<IUnknown>>& dropped) {
  // Tür burada sıfırlanmaz: yeni kaynak örneğinde 1080p30, kaynak yaşadıkça son tür kalır (spec §1, Ruling M5).
  if (state_ == MF_STREAM_STATE_RUNNING && shm_) WriteRequest(shm_, 0, 0, 0);
  state_ = next;
  gen_++;
  misses_ = 0;
  nextDue_ = 0;  // yeniden başlayınca ilk örnek beklemeden
  dropped.swap(pending_);  // hem boş havuz hem kare hızı nedeniyle bekleyen istekler
}

HRESULT MediaStream::Stop() {
  return Guard([&]() -> HRESULT {
    std::deque<ComPtr<IUnknown>> dropped;  // kilit dışında bırakılır
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    LeaveRunningLocked(MF_STREAM_STATE_STOPPED, dropped);
    return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
  });
}

// Frame Server'ın ayırıcısı: burada son başlatılan türle bir kez hazırlanır (kilit dışında); Start türü
// farklıysa EnsureAllocator yeniden hazırlar. Aynı ayırıcı yeniden verilirse hiçbir şey yapılmaz; farklıysa
// eskisi bırakılır.
HRESULT MediaStream::SetAllocator(IUnknown* allocator) {
  return Guard([&]() -> HRESULT {
    if (!allocator) return E_POINTER;
    ComPtr<IMFVideoSampleAllocator> next;
    HRESULT hr = allocator->QueryInterface(IID_PPV_ARGS(&next));
    if (FAILED(hr)) return hr;
    size_t mode = 0;
    {
      std::lock_guard<std::mutex> g(lock_);
      if (!queue_) return MF_E_SHUTDOWN;
      ComPtr<IUnknown> a, b;
      if (allocator_ && SUCCEEDED(allocator_.As(&a)) && SUCCEEDED(next.As(&b)) && a == b) return S_OK;
      mode = mode_;
    }
    hr = next->InitializeSampleAllocator(kAllocatorSamples, types_[mode].Get());
    if (FAILED(hr)) return hr;
    return PublishAllocator(next, true, mode);
  });
}

// Kuyruk, ayırıcı ve kaynak kilit altında alınır; kapatma ve Uninitialize kilit dışında yapılır.
// Paylaşılan bellek yıkıcıda kapanır (kare yolu işaretçiyi kullanıyor olabilir).
void MediaStream::Shutdown() {
  Guard([&]() -> HRESULT {
    ComPtr<IMFMediaEventQueue> queue;
    ComPtr<IMFVideoSampleAllocator> allocator;
    ComPtr<IMFMediaSource> source;
    std::deque<ComPtr<IUnknown>> dropped;
    {
      std::lock_guard<std::mutex> g(lock_);
      queue.Swap(queue_);
      allocator.Swap(allocator_);
      source.Swap(source_);
      LeaveRunningLocked(MF_STREAM_STATE_STOPPED, dropped);
    }
    if (queue) queue->Shutdown();
    if (allocator) allocator->UninitializeSampleAllocator();
    return S_OK;
  });
}

ComPtr<IMFMediaEventQueue> MediaStream::Queue() {
  std::lock_guard<std::mutex> g(lock_);
  return queue_;
}

// Olay kuyruğu çağrıları kilit dışında yapılır: GetEvent bekleyebilir.
IFACEMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->BeginGetEvent(callback, state) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->EndGetEvent(result, event) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->GetEvent(flags, event) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status,
                                       const PROPVARIANT* var) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->QueueEventParamVar(type, extendedType, status, var) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source) {
  return Guard([&]() -> HRESULT {
    if (!source) return E_POINTER;
    *source = nullptr;
    std::lock_guard<std::mutex> g(lock_);
    if (!source_) return MF_E_SHUTDOWN;
    return source_.CopyTo(source);
  });
}

IFACEMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor) {
  return Guard([&]() -> HRESULT {
    if (!descriptor) return E_POINTER;
    *descriptor = nullptr;
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    return descriptor_.CopyTo(descriptor);
  });
}

// Ayırıcıdan bir örnek alır, son kareyi mode boyutunda yazar ve MEMediaSample olarak kuyruğa koyar
// (sampleLock_ altında; ayırıcı ve tampon kilidi D3D olabilir, durum kilidi yalnız kuyruğa koyarken alınır).
// Ayırıcı boşsa kare kopyalanmadan MF_E_SAMPLEALLOCATOR_EMPTY döner. gen, mode, shm ve time (kare dilimi,
// SlotLocked) istek alındığında lock_ altında okunur; arada durdurulduysa örnek düşer. Teslim edilince
// sonraki dilim bir kare süresi sonrasıdır.
HRESULT MediaStream::Deliver(IMFVideoSampleAllocator* allocator, IUnknown* token, uint64_t gen, size_t mode,
                             ClvcHeader* shm, LONGLONG time) {
  const Mode& m = kModes[mode];
  const LONGLONG period = 10'000'000LL / m.fps;
  ComPtr<IMFSample> sample;
  HRESULT hr = allocator->AllocateSample(&sample);
  if (FAILED(hr)) return hr;
  ReadFrame(shm, m.width, m.height);
  ComPtr<IMFMediaBuffer> buffer;
  hr = sample->GetBufferByIndex(0, &buffer);
  if (SUCCEEDED(hr)) hr = CopyFrame(buffer.Get(), m.width, m.height);
  if (SUCCEEDED(hr)) hr = sample->SetSampleTime(time);
  if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(period);
  if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token);
  if (FAILED(hr)) return hr;

  std::lock_guard<std::mutex> g(lock_);
  if (!queue_) return MF_E_SHUTDOWN;
  if (state_ != MF_STREAM_STATE_RUNNING || gen != gen_) return S_OK;  // bu arada durduruldu: örnek düşer
  misses_ = 0;
  nextDue_ = time + period;
  return queue_->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
}

// lock_ altında, now >= nextDue_ iken: teslim edilecek örneğin zamanı. Takvimdeyse (en çok bir kare geride)
// takvim dilimi; daha gerideyse (istemci yavaş, ilk örnek ya da boş havuz beklendi) şimdi. Zamanlar böylece
// tekdüze artar ve en az bir kare süresi aralıklıdır; geride kalınca toplu teslim olmaz.
LONGLONG MediaStream::SlotLocked(LONGLONG now) const {
  return now - nextDue_ > 10'000'000LL / kModes[mode_].fps ? now : nextDue_;
}

// Tek zamanlanmış iş öğesi (retryScheduled_). Sonraki kare dilimi gelmediyse o ana kadar (kare hızı sınırı);
// geldiyse ve sıra ayırıcı boş olduğu için bekliyorsa yoklama aralığı (art arda ıskada bir kare süresi).
HRESULT MediaStream::ScheduleRetryLocked() {
  if (retryScheduled_) return S_OK;
  const LONGLONG wait = nextDue_ - MFGetSystemTime();  // 100 ns
  const INT64 delay = wait > 0 ? (wait + 9'999) / 10'000
                               : (misses_ >= kBackoffMisses ? 1000 / kModes[mode_].fps : kRetryMs);
  const HRESULT hr = MFScheduleWorkItem(static_cast<IMFAsyncCallback*>(this), nullptr, -delay, nullptr);
  if (SUCCEEDED(hr)) retryScheduled_ = true;
  return hr;
}

// Hata ayıklayıcı çıktısı hiçbir kilit tutulmadan yazılır (OutputDebugString dinleyici varken bekleyebilir).
// Frame Server'a SHUTDOWN/WRONGSTATE dışında dönen her hata akışı zehirler; ilki bir kez yazılır.
void MediaStream::LogRequestError(HRESULT hr) {
  if (requestErrorLogged_.exchange(true)) return;
  wchar_t line[96];
  swprintf_s(line, L"CamLink vcam: örnek isteği karşılanamadı (hr=0x%08lX)\n", static_cast<unsigned long>(hr));
  OutputDebugStringW(line);
}

// SharedMemoryLocked'ın kilit altında hazırladığı iletiyi (bir kez) yazar; kare yolu kilitleri bırakınca çağırır.
void MediaStream::FlushLog() {
  if (shmLogPending_.exchange(false)) OutputDebugStringW(shmLog_);
}

// Ayırıcı boşsa (tüketiciler örnekleri tutuyor) hata DÖNDÜRÜLMEZ: Frame Server RequestSample
// hatasından sonra akışın olaylarını (BeginGetEvent) bir daha okumuyor; aynı FS akış nesnesinde sonraki
// Start'ın MEStreamStarted'ı okunmadan kalıyor, Start bekçisi (20 sn) hizmeti sonlandırıyor. İstek sıraya
// alınır, zamanlanmış yeniden deneme (Invoke) örnek boşalınca teslim eder. Sıra boş değilken gelen istek
// öndekileri geçmez. Aynı nedenle diğer hatalar da (tampon kilidi, bellek, zamanlama) S_OK'ya çevrilir:
// istek düşer, Frame Server'ın olay döngüsü yaşar.
// Kare hızı: kare süresi dolmadan gelen istek de aynı sıraya alınır ve dilimi gelince teslim edilir (kare
// süresinde en çok bir örnek). Sınır olmadan Frame Server istekleri hızsız yineleyip 1-1,5 çekirdek yakıyordu.
// Hiçbir yol beklemez ya da uyumaz.
IFACEMETHODIMP MediaStream::RequestSample(IUnknown* token) {
  const HRESULT hr = Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> frameGuard(sampleLock_);
    ComPtr<IMFVideoSampleAllocator> allocator;
    uint64_t gen = 0;
    size_t mode = 0;
    ClvcHeader* shm = nullptr;
    LONGLONG time = 0;
    {
      std::lock_guard<std::mutex> g(lock_);
      if (!queue_) return MF_E_SHUTDOWN;
      if (state_ != MF_STREAM_STATE_RUNNING || !allocator_) return MF_E_MEDIA_SOURCE_WRONGSTATE;
      const LONGLONG now = MFGetSystemTime();
      if (!pending_.empty() || now < nextDue_) {
        pending_.emplace_back(token);
        return ScheduleRetryLocked();  // zaten zamanlıysa hiçbir şey yapmaz
      }
      allocator = allocator_;
      gen = gen_;
      mode = mode_;
      shm = SharedMemoryLocked();
      time = SlotLocked(now);
    }
    HRESULT result = Deliver(allocator.Get(), token, gen, mode, shm, time);
    if (result != MF_E_SAMPLEALLOCATOR_EMPTY) return result;

    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    if (state_ != MF_STREAM_STATE_RUNNING || gen != gen_) return S_OK;  // bu arada durduruldu: istek düşer
    pending_.emplace_back(token);
    misses_++;
    return ScheduleRetryLocked();
  });
  FlushLog();
  if (FAILED(hr) && hr != MF_E_SHUTDOWN && hr != MF_E_MEDIA_SOURCE_WRONGSTATE) {
    LogRequestError(hr);
    return S_OK;
  }
  return hr;
}

// Zamanlanmış teslim: sıranın başındaki isteği, kare dilimi geldiyse teslim eder (çağrı başına en çok bir
// örnek); erken uyandıysa ya da ayırıcı hâlâ boşsa yeniden zamanlar, sırada istek kaldıysa bir sonraki dilime
// zamanlar. Boş dışı bir hatayla teslim edilemeyen istek düşer (belirteç bırakılır; dönecek bir çağıran yok).
// Teslim yalnız sampleLock_ altında ve yalnız sıranın başından yapılır (RequestSample sıra boşken doğrudan
// teslim eder); böylece bir istek iki kez teslim edilmez.
IFACEMETHODIMP MediaStream::Invoke(IMFAsyncResult*) {
  HRESULT dropped = S_OK;  // düşen isteğin hatası; kilitler bırakılınca yazılır
  Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> frameGuard(sampleLock_);
    ComPtr<IUnknown> token;  // kilit bırakıldıktan sonra serbest kalır
    ComPtr<IMFVideoSampleAllocator> allocator;
    uint64_t gen = 0;
    size_t mode = 0;
    ClvcHeader* shm = nullptr;
    LONGLONG time = 0;
    {
      std::lock_guard<std::mutex> g(lock_);
      retryScheduled_ = false;
      if (!queue_ || state_ != MF_STREAM_STATE_RUNNING || !allocator_ || pending_.empty()) return S_OK;
      const LONGLONG now = MFGetSystemTime();
      if (now < nextDue_) return ScheduleRetryLocked();  // zamanlayıcı erken uyandırdı
      token = pending_.front();
      allocator = allocator_;
      gen = gen_;
      mode = mode_;
      shm = SharedMemoryLocked();
      time = SlotLocked(now);
    }
    const HRESULT hr = Deliver(allocator.Get(), token.Get(), gen, mode, shm, time);
    std::lock_guard<std::mutex> g(lock_);
    if (gen != gen_ || pending_.empty()) return S_OK;  // arada durduruldu: sıra zaten boşaltıldı
    if (hr == MF_E_SAMPLEALLOCATOR_EMPTY) {
      misses_++;
      return ScheduleRetryLocked();
    }
    if (FAILED(hr) && hr != MF_E_SHUTDOWN) dropped = hr;
    pending_.pop_front();  // teslim edildi ya da düştü
    return pending_.empty() ? S_OK : ScheduleRetryLocked();
  });
  FlushLog();
  if (FAILED(dropped)) LogRequestError(dropped);
  return S_OK;
}

// Frame Server'ın akış düzeyi durum isteği. RUNNING işleyicinin geçerli türüyle sürer (okunamazsa son
// başlatılan tür); ayırıcı kilit dışında hazırlanır (zaten hazırsa hiçbir şey yapılmaz); durum değişikliği
// ve olay kilit altında.
IFACEMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE next) {
  return Guard([&]() -> HRESULT {
    size_t mode = 0;
    if (next == MF_STREAM_STATE_RUNNING) {
      if (FAILED(FindMode(descriptor_.Get(), &mode))) {  // descriptor_ başlatmadan sonra değişmez
        std::lock_guard<std::mutex> g(lock_);
        mode = mode_;
      }
      HRESULT hr = EnsureAllocator(mode);
      if (FAILED(hr)) return hr;
    }
    std::deque<ComPtr<IUnknown>> dropped;  // kilit dışında bırakılır
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    if (next == state_) return S_OK;
    switch (next) {
      case MF_STREAM_STATE_RUNNING: {
        HRESULT hr = EnterRunningLocked(mode);
        if (FAILED(hr)) return hr;
        return queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
      }
      case MF_STREAM_STATE_STOPPED:
        LeaveRunningLocked(MF_STREAM_STATE_STOPPED, dropped);
        return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
      case MF_STREAM_STATE_PAUSED:
        if (state_ != MF_STREAM_STATE_RUNNING) return MF_E_INVALID_STATE_TRANSITION;
        LeaveRunningLocked(MF_STREAM_STATE_PAUSED, dropped);
        return S_OK;
      default:
        return E_INVALIDARG;
    }
  });
}

IFACEMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* current) {
  return Guard([&]() -> HRESULT {
    if (!current) return E_POINTER;
    std::lock_guard<std::mutex> g(lock_);
    *current = state_;
    return S_OK;
  });
}

IFACEMETHODIMP MediaStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) {
  return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) {
  return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) {
  return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// lock_ altında: bellek açık değilse açar, yoksa oluşturup başlığı yazar (CLVC_TOTAL bayt). Başarısızlıkta
// null döner (kare siyah kalır, sonraki istekte yeniden denenir); ilk hatanın iletisi hazırlanır, FlushLog
// kilitler bırakılınca bir kez yazar. Akış çalışırken açılırsa tüketicinin türü de yazılır.
ClvcHeader* MediaStream::SharedMemoryLocked() {
  if (shm_) return shm_;
  HANDLE mapping = OpenFileMappingW(kShmAccess, FALSE, CLVC_NAME);
  DWORD err = mapping ? ERROR_SUCCESS : GetLastError();
  bool created = false;
  if (!mapping) {
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(kShmSddl, SDDL_REVISION_1, &sd, nullptr)) {
      err = GetLastError();
    } else {
      SECURITY_ATTRIBUTES sa{sizeof(sa), sd, FALSE};
      mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, CLVC_TOTAL, CLVC_NAME);
      err = GetLastError();
      created = mapping && err != ERROR_ALREADY_EXISTS;
      LocalFree(sd);
    }
  }
  // Boyut koruması: görünüm bölümden büyükse MapViewOfFile başarısız olur, sürüm 1'in küçük bölümüne
  // (eski masaüstü açık tutuyor) işaretçi alınmaz.
  void* view = nullptr;
  if (mapping) {
    view = MapViewOfFile(mapping, kShmAccess, 0, 0, CLVC_TOTAL);
    if (!view) {
      err = GetLastError();
      CloseHandle(mapping);
    }
  }
  if (!view) {
    if (!shmErrorLogged_) {
      shmErrorLogged_ = true;
      if (mapping && !created)  // bölüm vardı ama CLVC_TOTAL eşlenemedi (tutamaç kapandı, değer yalnız işaret)
        swprintf_s(shmLog_, L"CamLink vcam: eski (v1) bölüm açık, masaüstünü yeniden başlatın (hata %lu)\n", err);
      else
        swprintf_s(shmLog_, L"CamLink vcam: Global\\CamLinkVcam açılamadı/oluşturulamadı (hata %lu)\n", err);
      shmLogPending_ = true;
    }
    return nullptr;
  }
  mapping_ = mapping;
  shm_ = static_cast<ClvcHeader*>(view);
  const Mode& m = kModes[mode_];
  if (created) {
    shm_->version = CLVC_VERSION;
    shm_->width = m.width;
    shm_->height = m.height;
    shm_->fps = m.fps;
    MemoryBarrier();
    shm_->magic = CLVC_MAGIC;  // en son: yazar magic'i görünce başlık hazırdır
  }
  if (state_ == MF_STREAM_STATE_RUNNING) WriteRequest(shm_, m.width, m.height, m.fps);
  return shm_;
}

void MediaStream::CloseSharedMemory() {
  if (shm_) UnmapViewOfFile(shm_);
  if (mapping_) CloseHandle(mapping_);
  shm_ = nullptr;
  mapping_ = nullptr;
}

void MediaStream::FillBlack() {
  shownSeq_ = -1;
  const size_t luma = last_.size() / 3 * 2;
  memset(last_.data(), 16, luma);
  memset(last_.data() + luma, 128, last_.size() - luma);
}

// Seqlock okuması: hiç beklemez. Tutarlı kare width x height'a ölçeklenip last_'a yazılır; yazar bayatsa
// ya da kare boyutu geçersizse siyah. seq, başlık alanlarından önce okunur: yazar alanları tek penceresinde
// yazar, seq sonda değişmediyse boyut ve kare aynı yazımındır.
void MediaStream::ReadFrame(ClvcHeader* shm, UINT32 width, UINT32 height) {
  const size_t outBytes = static_cast<size_t>(width) * height * 3 / 2;
  if (last_.size() != outBytes) {  // tür değişti
    last_.resize(outBytes);
    FillBlack();
  }
  if (!shm) return FillBlack();
  const ULONGLONG now = GetTickCount64();
  shm->consumerTick = static_cast<int64_t>(now);
  const int64_t s1 = shm->seq;  // volatile: tek okuma
  MemoryBarrier();
  const HeaderSnapshot h = Snapshot(shm);
  // İşaretli fark: yazarın tiki okuyucunun now'ından biraz ilerideyse sarılıp "bayat" sayılmasın;
  // çok ileride (bozuk ya da güvenilmeyen yazar) ise bayat sayılır.
  const int64_t age = static_cast<int64_t>(now - h.writerTick);
  const int64_t stale = static_cast<int64_t>(kStaleMs);
  if (age > stale || age < -stale) return FillBlack();
  if (s1 & 1) return;  // yazılıyor: son iyi kare kalır
  if (!ValidSource(h.width, h.height)) return FillBlack();
  if (s1 == shownSeq_) return;  // yeni kare yok: son kare yinelenir (yeniden ölçeklenmez)

  // scratch_ kaynak boyutuna (kapasitesi kalır; yalnız büyürken ayrılır). Aynı boyutta kopya last_ ile yer
  // değiştirir (tek kopya); değilse last_'a ölçeklenir.
  scratch_.resize(static_cast<size_t>(h.width) * h.height * 3 / 2);
  memcpy(scratch_.data(), reinterpret_cast<const BYTE*>(shm) + CLVC_HEADER, scratch_.size());
  MemoryBarrier();
  if (shm->seq != s1) return;  // kopya sırasında yazıldı: son iyi kare kalır
  if (h.width == width && h.height == height) last_.swap(scratch_);
  else ScaleNv12Fit(scratch_.data(), h.width, h.height, last_.data(), width, height, scaleScratch_);
  shownSeq_ = s1;
}

// last_'ı (sıkı paketli NV12, width x height) örnek tamponuna adım (pitch) gözeterek yazar.
HRESULT MediaStream::CopyFrame(IMFMediaBuffer* buffer, UINT32 width, UINT32 height) {
  const size_t w = width, h = height, frameBytes = last_.size();
  ComPtr<IMF2DBuffer2> buffer2d;
  if (SUCCEEDED(buffer->QueryInterface(IID_PPV_ARGS(&buffer2d)))) {
    BYTE* scan0 = nullptr;
    BYTE* start = nullptr;
    LONG pitch = 0;
    DWORD length = 0;
    HRESULT hr = buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &scan0, &pitch, &start, &length);
    if (FAILED(hr)) return hr;
    if (pitch < static_cast<LONG>(w) || length < static_cast<size_t>(pitch) * (h + h / 2)) {
      buffer2d->Unlock2D();
      return MF_E_BUFFERTOOSMALL;
    }
    for (size_t y = 0; y < h + h / 2; y++)  // Y satırları, ardından UV satırları
      memcpy(scan0 + y * pitch, last_.data() + y * w, w);
    return buffer2d->Unlock2D();
  }
  BYTE* data = nullptr;
  DWORD maxLength = 0;
  HRESULT hr = buffer->Lock(&data, &maxLength, nullptr);
  if (FAILED(hr)) return hr;
  if (maxLength < frameBytes) {
    buffer->Unlock();
    return MF_E_BUFFERTOOSMALL;
  }
  memcpy(data, last_.data(), frameBytes);
  buffer->Unlock();
  return buffer->SetCurrentLength(static_cast<DWORD>(frameBytes));
}
