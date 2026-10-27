// camlink-vcam.dll giriş noktaları: COM sınıf fabrikası, kayıt ve VcamStart/VcamStop.
#include "activator.h"

#include <mfvirtualcamera.h>

#include <string>

using Microsoft::WRL::ComPtr;

namespace {

const wchar_t kClsid[] = L"{8B6175EB-569A-4692-A61C-C449F9F5DE5F}";
const wchar_t kClsidKey[] = L"Software\\Classes\\CLSID\\{8B6175EB-569A-4692-A61C-C449F9F5DE5F}";

HMODULE g_module = nullptr;
std::mutex g_camLock;
// VcamStart ile VcamStop arası. Ham işaretçi: global yıkıcı DLL boşaltılırken (yükleyici kilidi
// altında) Release çağırmasın diye; DLL_PROCESS_DETACH'ta bilerek sızdırılır.
IMFVirtualCamera* g_cam = nullptr;

HRESULT SetString(HKEY key, const wchar_t* name, const wchar_t* value) {
  const DWORD bytes = static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t));
  return HRESULT_FROM_WIN32(RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), bytes));
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    g_module = module;
    DisableThreadLibraryCalls(module);
  }
  return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object) {
  return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().GetClassObject(clsid, riid, object);
}

STDAPI DllCanUnloadNow() {
  return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().Terminate() ? S_OK : S_FALSE;
}

// HKLM: Frame Server LocalService olarak çalışır, HKCU'yu okuyamaz.
STDAPI DllRegisterServer() {
  return Guard([&]() -> HRESULT {
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(g_module, path, MAX_PATH);
    if (n == 0 || n == MAX_PATH) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    HKEY key = nullptr;
    const std::wstring inproc = std::wstring(kClsidKey) + L"\\InprocServer32";
    const LSTATUS st =
        RegCreateKeyExW(HKEY_LOCAL_MACHINE, inproc.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (st != ERROR_SUCCESS) return HRESULT_FROM_WIN32(st);
    HRESULT hr = SetString(key, nullptr, path);
    if (SUCCEEDED(hr)) hr = SetString(key, L"ThreadingModel", L"Both");
    RegCloseKey(key);
    return hr;
  });
}

STDAPI DllUnregisterServer() {
  const LSTATUS st = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
  return st == ERROR_SUCCESS || st == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(st);
}

// "CamLink Kamera"yı bu oturum için oluşturur ve başlatır; zaten açıksa S_OK.
extern "C" HRESULT __stdcall VcamStart(const wchar_t* friendlyName) {
  return Guard([&]() -> HRESULT {
    if (!friendlyName || !*friendlyName) return E_INVALIDARG;
    std::lock_guard<std::mutex> g(g_camLock);
    if (g_cam) return S_OK;
    // Çağıran iş parçacığında COM yoksa MTA'ya alınır; sayaç bilerek bırakılır (iş parçacığı MTA kalır).
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) return hr;
    ComPtr<IMFVirtualCamera> cam;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
                               MFVirtualCameraAccess_CurrentUser, friendlyName, kClsid, nullptr, 0, &cam);
    if (SUCCEEDED(hr)) hr = cam->Start(nullptr);
    if (FAILED(hr)) {
      if (cam) cam->Shutdown();
      MFShutdown();
      return hr;
    }
    g_cam = cam.Detach();
    return S_OK;
  });
}

extern "C" void __stdcall VcamStop() {
  Guard([&]() -> HRESULT {
    std::lock_guard<std::mutex> g(g_camLock);
    if (!g_cam) return S_OK;
    // Remove() çağrılmaz: MFVirtualCameraLifetime_Session kamerası Shutdown'da sistemden kaldırılır
    // (Microsoft Learn, IMFVirtualCamera::Shutdown: "For virtual cameras created with a lifetime value
    // of MFVirtualCameraLifeTime_Session, when Shutdown is called, the virtual camera will be removed
    // from the system"; MFCreateVirtualCamera da aynısını söyler). Remove sürücüyü kaldırmaya eşdeğer
    // kalıcı kayıt silmedir ve Frame Server çöktükten sonra uzun süre takılabiliyordu.
    g_cam->Shutdown();
    g_cam->Release();
    g_cam = nullptr;
    MFShutdown();
    return S_OK;
  });
}

extern "C" int __stdcall VcamVersion() { return 1; }
