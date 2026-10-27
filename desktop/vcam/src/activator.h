// CLSID'in sınıf fabrikası bu etkinleştiriciyi verir; Frame Server ActivateObject ile kaynağı ister.
#pragma once
#include "media_source.h"

class __declspec(uuid("8B6175EB-569A-4692-A61C-C449F9F5DE5F")) Activator
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>> {
 public:
  HRESULT RuntimeClassInitialize() { return MFCreateAttributes(&attrs_, 4); }

  // IMFActivate
  IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override;
  IFACEMETHODIMP ShutdownObject() override;
  IFACEMETHODIMP DetachObject() override;

  // IMFAttributes: yalnız MF öznitelik deposuna aktarır (istisna atamaz).
  IFACEMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return attrs_->GetItem(k, v); }
  IFACEMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return attrs_->GetItemType(k, t); }
  IFACEMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return attrs_->CompareItem(k, v, r); }
  IFACEMETHODIMP Compare(IMFAttributes* o, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override {
    return attrs_->Compare(o, m, r);
  }
  IFACEMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return attrs_->GetUINT32(k, v); }
  IFACEMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return attrs_->GetUINT64(k, v); }
  IFACEMETHODIMP GetDouble(REFGUID k, double* v) override { return attrs_->GetDouble(k, v); }
  IFACEMETHODIMP GetGUID(REFGUID k, GUID* v) override { return attrs_->GetGUID(k, v); }
  IFACEMETHODIMP GetStringLength(REFGUID k, UINT32* n) override { return attrs_->GetStringLength(k, n); }
  IFACEMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 size, UINT32* n) override {
    return attrs_->GetString(k, v, size, n);
  }
  IFACEMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* n) override {
    return attrs_->GetAllocatedString(k, v, n);
  }
  IFACEMETHODIMP GetBlobSize(REFGUID k, UINT32* n) override { return attrs_->GetBlobSize(k, n); }
  IFACEMETHODIMP GetBlob(REFGUID k, UINT8* v, UINT32 size, UINT32* n) override {
    return attrs_->GetBlob(k, v, size, n);
  }
  IFACEMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** v, UINT32* n) override {
    return attrs_->GetAllocatedBlob(k, v, n);
  }
  IFACEMETHODIMP GetUnknown(REFGUID k, REFIID riid, LPVOID* v) override { return attrs_->GetUnknown(k, riid, v); }
  IFACEMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return attrs_->SetItem(k, v); }
  IFACEMETHODIMP DeleteItem(REFGUID k) override { return attrs_->DeleteItem(k); }
  IFACEMETHODIMP DeleteAllItems() override { return attrs_->DeleteAllItems(); }
  IFACEMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return attrs_->SetUINT32(k, v); }
  IFACEMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return attrs_->SetUINT64(k, v); }
  IFACEMETHODIMP SetDouble(REFGUID k, double v) override { return attrs_->SetDouble(k, v); }
  IFACEMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return attrs_->SetGUID(k, v); }
  IFACEMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return attrs_->SetString(k, v); }
  IFACEMETHODIMP SetBlob(REFGUID k, const UINT8* v, UINT32 size) override { return attrs_->SetBlob(k, v, size); }
  IFACEMETHODIMP SetUnknown(REFGUID k, IUnknown* v) override { return attrs_->SetUnknown(k, v); }
  IFACEMETHODIMP LockStore() override { return attrs_->LockStore(); }
  IFACEMETHODIMP UnlockStore() override { return attrs_->UnlockStore(); }
  IFACEMETHODIMP GetCount(UINT32* n) override { return attrs_->GetCount(n); }
  IFACEMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return attrs_->GetItemByIndex(i, k, v); }
  IFACEMETHODIMP CopyAllItems(IMFAttributes* dest) override { return attrs_->CopyAllItems(dest); }

 private:
  std::mutex lock_;
  Microsoft::WRL::ComPtr<IMFAttributes> attrs_;
  Microsoft::WRL::ComPtr<MediaSource> source_;
};
