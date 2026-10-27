#include "media_source.h"

using Microsoft::WRL::ComPtr;

HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activatorAttributes) {
  return Guard([&]() -> HRESULT {
    HRESULT hr = MFCreateAttributes(&attributes_, 4);
    if (SUCCEEDED(hr) && activatorAttributes) hr = activatorAttributes->CopyAllItems(attributes_.Get());
    if (FAILED(hr)) return hr;
    hr = Microsoft::WRL::MakeAndInitialize<MediaStream>(&stream_, static_cast<IMFMediaSource*>(this));
    if (FAILED(hr)) return hr;
    IMFStreamDescriptor* streams[] = {stream_->Descriptor()};
    hr = MFCreatePresentationDescriptor(1, streams, &descriptor_);
    if (SUCCEEDED(hr)) hr = descriptor_->SelectStream(0);
    if (SUCCEEDED(hr)) hr = MFCreateEventQueue(&queue_);
    if (FAILED(hr)) stream_->Shutdown();  // akış -> kaynak döngüsünü kır
    return hr;
  });
}

ComPtr<IMFMediaEventQueue> MediaSource::Queue() {
  std::lock_guard<std::mutex> g(lock_);
  return queue_;
}

// Olay kuyruğu çağrıları kilit dışında yapılır: GetEvent bekleyebilir.
IFACEMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->BeginGetEvent(callback, state) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->EndGetEvent(result, event) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->GetEvent(flags, event) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status,
                                       const PROPVARIANT* var) {
  return Guard([&]() -> HRESULT {
    auto q = Queue();
    return q ? q->QueueEventParamVar(type, extendedType, status, var) : MF_E_SHUTDOWN;
  });
}

IFACEMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics) {
  return Guard([&]() -> HRESULT {
    if (!characteristics) return E_POINTER;
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
  });
}

IFACEMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) {
  return Guard([&]() -> HRESULT {
    if (!descriptor) return E_POINTER;
    *descriptor = nullptr;
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    return descriptor_->Clone(descriptor);
  });
}

// Atomik başlatma: tüketicinin seçtiği tür akış tanımlayıcısının işleyicisinden okunur, önce (gerekirse)
// ayırıcı bu türle kaynak kilidi dışında hazırlanır, sonra akış çalışır duruma alınır; ancak bunlar
// başarılıysa olaylar sırayla kuyruğa girer:
// MENewStream (ilk seçimde) ya da MEUpdatedStream (önceki Start'ta da seçiliydi; Stop seçimi
// kaldırmaz), MEStreamStarted, MESourceStarted. Hata olursa hiçbir olay kuyruğa girmez ve hata
// eşzamanlı döner (Frame Server olay beklemeye kalmaz).
IFACEMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat,
                                  const PROPVARIANT* startPosition) {
  return Guard([&]() -> HRESULT {
    if (!descriptor || !startPosition) return E_INVALIDARG;
    if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
    DWORD count = 0;
    HRESULT hr = descriptor->GetStreamDescriptorCount(&count);
    if (FAILED(hr)) return hr;
    if (count != 1) return E_INVALIDARG;
    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> streamDescriptor;
    hr = descriptor->GetStreamDescriptorByIndex(0, &selected, &streamDescriptor);
    if (FAILED(hr)) return hr;

    size_t mode = 0;
    if (selected) {
      hr = MediaStream::FindMode(streamDescriptor.Get(), &mode);
      if (FAILED(hr)) return hr;
      ComPtr<MediaStream> stream;
      {
        std::lock_guard<std::mutex> g(lock_);
        if (!queue_) return MF_E_SHUTDOWN;
        stream = stream_;
      }
      hr = stream->EnsureAllocator(mode);  // tür değişmediyse hiçbir şey yapmaz
      if (FAILED(hr)) return hr;
    }

    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    PROPVARIANT time;
    PropVariantInit(&time);
    time.vt = VT_I8;
    time.hVal.QuadPart = MFGetSystemTime();
    if (selected) {
      hr = stream_->Activate(mode);  // yalnız durum ve bellek başlığı; ayırıcı işi yok
      if (FAILED(hr)) return hr;
      const MediaEventType event = streamActive_ ? MEUpdatedStream : MENewStream;
      hr = queue_->QueueEventParamUnk(event, GUID_NULL, S_OK, static_cast<IMFMediaStream*>(stream_.Get()));
      if (SUCCEEDED(hr)) hr = stream_->QueueEvent(MEStreamStarted, GUID_NULL, S_OK, &time);
      if (FAILED(hr)) return hr;
      streamActive_ = true;
    } else if (streamActive_) {
      streamActive_ = false;  // seçimi kaldırılan akış durur
      hr = stream_->Stop();
      if (FAILED(hr)) return hr;
    }
    return queue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
  });
}

// Stop seçimi değiştirmez (descriptor_ varsayılan seçimle kalır); ayırıcıya dokunmaz.
IFACEMETHODIMP MediaSource::Stop() {
  return Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    HRESULT hr = stream_->Stop();
    if (SUCCEEDED(hr)) hr = queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    return hr;
  });
}

// Canlı kaynak: duraklatma yok.
IFACEMETHODIMP MediaSource::Pause() { return MF_E_INVALID_STATE_TRANSITION; }

// Akışın kapatılması (ayırıcıyı bırakır) kaynak kilidi dışında yapılır.
IFACEMETHODIMP MediaSource::Shutdown() {
  return Guard([&]() -> HRESULT {
    ComPtr<MediaStream> stream;
    {
      std::lock_guard<std::mutex> g(lock_);
      if (!queue_) return MF_E_SHUTDOWN;
      queue_->Shutdown();
      queue_.Reset();
      stream.Swap(stream_);
      descriptor_.Reset();
      attributes_.Reset();
    }
    stream->Shutdown();
    return S_OK;
  });
}

IFACEMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes) {
  return Guard([&]() -> HRESULT {
    if (!attributes) return E_POINTER;
    *attributes = nullptr;
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    return attributes_.CopyTo(attributes);
  });
}

IFACEMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) {
  return Guard([&]() -> HRESULT {
    if (!attributes) return E_POINTER;
    *attributes = nullptr;
    if (streamId != 0) return MF_E_INVALIDSTREAMNUMBER;
    std::lock_guard<std::mutex> g(lock_);
    if (!queue_) return MF_E_SHUTDOWN;
    return stream_->GetAttributes(attributes);
  });
}

// Kareler CPU'da yazılır; GPU ayırıcısını gerekiyorsa Frame Server SetDefaultAllocator ile verir.
IFACEMETHODIMP MediaSource::SetD3DManager(IUnknown*) { return S_OK; }

IFACEMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object) {
  if (object) *object = nullptr;
  return MF_E_UNSUPPORTED_SERVICE;
}

IFACEMETHODIMP MediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) {
  return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) {
  return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) {
  return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// Ayırıcının hazırlanması (D3D) kaynak kilidi dışında yapılır.
IFACEMETHODIMP MediaSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) {
  return Guard([&]() -> HRESULT {
    if (outputStreamId != 0) return MF_E_INVALIDSTREAMNUMBER;
    ComPtr<MediaStream> stream;
    {
      std::lock_guard<std::mutex> g(lock_);
      if (!queue_) return MF_E_SHUTDOWN;
      stream = stream_;
    }
    return stream->SetAllocator(allocator);
  });
}

IFACEMETHODIMP MediaSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId,
                                              MFSampleAllocatorUsage* usage) {
  if (!inputStreamId || !usage) return E_POINTER;
  if (outputStreamId != 0) return MF_E_INVALIDSTREAMNUMBER;
  *inputStreamId = outputStreamId;
  *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
  return S_OK;
}
