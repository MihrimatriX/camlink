// Frame Server'ın etkinleştirdiği Media Foundation kaynağı: tek NV12 akışlı canlı kaynak.
#pragma once
#include "media_stream.h"

class MediaSource
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
          IMFGetService, IKsControl, IMFSampleAllocatorControl> {
 public:
  // activatorAttributes: Frame Server'ın etkinleştiriciye yazdığı öznitelikler kaynağa kopyalanır.
  HRESULT RuntimeClassInitialize(IMFAttributes* activatorAttributes);

  // IMFMediaEventGenerator
  IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
  IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
  IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
  IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status,
                            const PROPVARIANT* var) override;
  // IMFMediaSource
  IFACEMETHODIMP GetCharacteristics(DWORD* characteristics) override;
  IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
  IFACEMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat,
                       const PROPVARIANT* startPosition) override;
  IFACEMETHODIMP Stop() override;
  IFACEMETHODIMP Pause() override;
  IFACEMETHODIMP Shutdown() override;
  // IMFMediaSourceEx
  IFACEMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
  IFACEMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
  IFACEMETHODIMP SetD3DManager(IUnknown* manager) override;
  // IMFGetService
  IFACEMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;
  // IKsControl: özellik sunulmaz
  IFACEMETHODIMP KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*) override;
  IFACEMETHODIMP KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*) override;
  IFACEMETHODIMP KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*) override;
  // IMFSampleAllocatorControl
  IFACEMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
  IFACEMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId,
                                   MFSampleAllocatorUsage* usage) override;

 private:
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> Queue();

  std::mutex lock_;
  Microsoft::WRL::ComPtr<IMFMediaEventQueue> queue_;  // null = kapatıldı
  Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
  Microsoft::WRL::ComPtr<IMFPresentationDescriptor> descriptor_;
  Microsoft::WRL::ComPtr<MediaStream> stream_;
  bool streamActive_ = false;  // önceki Start'ta seçiliydi: sonraki Start MEUpdatedStream gönderir
};
