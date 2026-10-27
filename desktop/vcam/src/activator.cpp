#include "activator.h"

using Microsoft::WRL::ComPtr;

namespace {

// WRL'nin varsayılan fabrikası RuntimeClassInitialize çağırmaz; bu fabrika çağırır.
class ActivatorFactory : public Microsoft::WRL::ClassFactory<> {
 public:
  IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override {
    return Guard([&]() -> HRESULT {
      if (!object) return E_POINTER;
      *object = nullptr;
      if (outer) return CLASS_E_NOAGGREGATION;
      ComPtr<Activator> activator;
      HRESULT hr = Microsoft::WRL::MakeAndInitialize<Activator>(&activator);
      return SUCCEEDED(hr) ? activator.CopyTo(riid, object) : hr;
    });
  }
};

}  // namespace

CoCreatableClassWithFactory(Activator, ActivatorFactory);

// Kaynak ilk istekte yaratılır; sonraki istekler aynı kaynağı alır.
IFACEMETHODIMP Activator::ActivateObject(REFIID riid, void** object) {
  return Guard([&]() -> HRESULT {
    if (!object) return E_POINTER;
    *object = nullptr;
    std::lock_guard<std::mutex> g(lock_);
    if (!source_) {
      HRESULT hr = Microsoft::WRL::MakeAndInitialize<MediaSource>(&source_, attrs_.Get());
      if (FAILED(hr)) return hr;
    }
    return source_.CopyTo(riid, object);
  });
}

// Kaynak kilit altında alınır, kapatma (ayırıcıyı bırakır) kilit dışında yapılır.
IFACEMETHODIMP Activator::ShutdownObject() {
  return Guard([&]() -> HRESULT {
    ComPtr<MediaSource> source;
    {
      std::lock_guard<std::mutex> g(lock_);
      source.Swap(source_);
    }
    if (source) source->Shutdown();
    return S_OK;
  });
}

IFACEMETHODIMP Activator::DetachObject() {
  return Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> g(lock_);
    source_.Reset();
    return S_OK;
  });
}
