// Tek video akışı: paylaşılan bellekteki son NV12 kareyi örnek olarak verir.
#pragma once
#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <ks.h>
#include <ksproxy.h>  // kullanıcı kipi IKsControl
#include <wrl.h>

#include <atomic>
#include <cstdint>
#include <deque>
#include <mutex>
#include <new>
#include <vector>

#include "frame_shm.h"
#include "nv12_scale.h"

// COM sınırı: istisna Frame Server'a sızmaz, HRESULT'a çevrilir.
template <class F>
HRESULT Guard(F&& body) noexcept {
  try {
    return body();
  } catch (const std::bad_alloc&) {
    return E_OUTOFMEMORY;
  } catch (...) {
    return E_UNEXPECTED;
  }
}

class MediaStream
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>,
          IKsControl, IMFAsyncCallback> {
 public:
  ~MediaStream() override;
  HRESULT RuntimeClassInitialize(IMFMediaSource* source);

  // Kaynağın çağırdığı iç yöntemler. mode: tür sırası (media_stream.cpp kModes; 0 = 1920x1080@30).
  // Ayırıcı SetAllocator ya da EnsureAllocator'da hazırlanır; yalnız başlatılan tür hazırlandığı türden
  // farklıysa (Start) yeniden hazırlanır, Shutdown'da ya da yenisiyle değiştirilirken bırakılır; Stop
  // ayırıcıya dokunmaz. Ayırıcı (D3D) çağrıları hiçbir kilit tutulmadan yapılır.
  static HRESULT FindMode(IMFStreamDescriptor* descriptor, size_t* mode);  // geçerli türün sırası
  IMFStreamDescriptor* Descriptor() const { return descriptor_.Get(); }
  HRESULT GetAttributes(IMFAttributes** attributes);
  HRESULT EnsureAllocator(size_t mode);  // ayırıcı bu türle hazır değilse hazırlar (yoksa kendi ayırıcımız)
  HRESULT Activate(size_t mode);         // yalnız durum: RUNNING (olay kuyruğa girmez; kaynak sıralar)
  HRESULT Stop();  // durum STOPPED + MEStreamStopped; bekleyen örnek istekleri bırakılır
  HRESULT SetAllocator(IUnknown* allocator);
  void Shutdown();

  // IMFMediaEventGenerator
  IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
  IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
  IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
  IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status,
                            const PROPVARIANT* var) override;
  // IMFMediaStream
  IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
  IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
  IFACEMETHODIMP RequestSample(IUnknown* token) override;
  // IMFMediaStream2
  IFACEMETHODIMP SetStreamState(MF_STREAM_STATE next) override;
  IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* current) override;
  // IKsControl: özellik sunulmaz
  IFACEMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override;
  IFACEMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override;
  IFACEMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override;
  // IMFAsyncCallback: boş ayırıcıda bekleyen örnek isteklerinin zamanlanmış yeniden denemesi
  IFACEMETHODIMP GetParameters(DWORD*, DWORD*) override { return E_NOTIMPL; }
  IFACEMETHODIMP Invoke(IMFAsyncResult*) override;

 private:
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> Queue();
  HRESULT PublishAllocator(Microsoft::WRL::ComPtr<IMFVideoSampleAllocator> next, bool replace, size_t mode);
  HRESULT Deliver(IMFVideoSampleAllocator* allocator, IUnknown* token, uint64_t gen, size_t mode, ClvcHeader* shm,
                  LONGLONG time);
  LONGLONG SlotLocked(LONGLONG now) const;
  HRESULT ScheduleRetryLocked();
  HRESULT EnterRunningLocked(size_t mode);
  void LeaveRunningLocked(MF_STREAM_STATE next, std::deque<Microsoft::WRL::ComPtr<IUnknown>>& dropped);
  ClvcHeader* SharedMemoryLocked();
  void CloseSharedMemory();
  void LogRequestError(HRESULT hr);  // kilit tutulmadan çağrılır
  void FlushLog();                   // kilit tutulmadan çağrılır
  void FillBlack();
  void ReadFrame(ClvcHeader* shm, UINT32 width, UINT32 height);
  HRESULT CopyFrame(IMFMediaBuffer* buffer, UINT32 width, UINT32 height);

  // lock_: durum (queue_, allocator_, allocMode_, mode_, state_, source_, pending_, gen_, retryScheduled_,
  // misses_, nextDue_, mapping_/shm_); kısa tutulur, içinde ayırıcı/D3D çağrısı yok (bellek açma yalnız çekirdek çağrısı).
  // sampleLock_: yalnız kare yolu (last_, scratch_); RequestSample'ı ve yeniden denemeyi (Invoke)
  // sıralar, böylece bekleyen istekler sırayla teslim edilir. Durum yolları
  // (Start/Stop/Shutdown) sampleLock_'ı hiç beklemez, böylece D3D'de bekleyen bir örnek isteği
  // kaynağın ya da akışın durum kilidini dolaylı olarak da tutamaz. Kare yolu türü ve bellek işaretçisini
  // lock_ altında alır.
  std::mutex lock_;
  std::mutex sampleLock_;
  Microsoft::WRL::ComPtr<IMFMediaSource> source_;  // Shutdown'da bırakılır (döngüyü kırar)
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue_;
  Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor_;
  Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
  std::vector<Microsoft::WRL::ComPtr<IMFMediaType>> types_;   // kModes sırasıyla; başlatmadan sonra değişmez
  Microsoft::WRL::ComPtr<IMFVideoSampleAllocator> allocator_;  // yalnız hazırlanmış ayırıcı yayınlanır
  size_t allocMode_ = 0;  // allocator_'ın hazırlandığı tür
  size_t mode_ = 0;       // son başlatılan tür; kare yolu bu boyutta örnek üretir
  MF_STREAM_STATE state_ = MF_STREAM_STATE_STOPPED;
  // Ayırıcı boşken ya da kare süresi dolmadan alınan istek belirteçleri (FIFO; null olabilir). Boş değilken
  // tek bir zamanlanmış teslim (Invoke) bekler.
  std::deque<Microsoft::WRL::ComPtr<IUnknown>> pending_;
  LONGLONG nextDue_ = 0;  // sonraki örneğin en erken zamanı (MFGetSystemTime, 100 ns); 0 = hemen
  uint64_t gen_ = 0;             // RUNNING'den her çıkışta artar: eski istek yeniden başlatmadan sonra teslim edilmez
  bool retryScheduled_ = false;  // MFScheduleWorkItem bekliyor
  uint32_t misses_ = 0;          // art arda boş havuz; çoğalınca yoklama bir kare süresine yavaşlar

  // Bellek lock_ altında bir kez açılır, yıkıcıda kapanır (işaretçi akış boyunca geçerli kalır).
  HANDLE mapping_ = nullptr;
  ClvcHeader* shm_ = nullptr;
  bool shmErrorLogged_ = false;
  wchar_t shmLog_[160] = {};                 // lock_ altında bir kez yazılır, FlushLog kilitsiz yazdırır
  std::atomic<bool> shmLogPending_{false};
  std::atomic<bool> requestErrorLogged_{false};
  std::vector<BYTE> last_;     // kare yolu: son iyi kare (NV12, sıkı paketli, geçerli tür boyutunda)
  std::vector<BYTE> scratch_;  // kare yolu: seqlock kopyası (kaynak boyutunda); aynı boyutta last_ ile yer değiştirir
  Nv12ScaleScratch scaleScratch_;  // kare yolu: ölçekleme ara tamponları (karede yeniden ayrılmaz)
  int64_t shownSeq_ = -1;      // kare yolu: last_'taki karenin seq'i (-1: siyah ya da yok)
};
