cmake_minimum_required(VERSION 3.20)
project(camlink_vcam CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Statik CRT: Frame Server'ın yüklediği DLL, VC++ çalışma zamanının kurulu olmasına bağlı kalmasın.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
add_compile_options(/W4 /utf-8 /EHsc)

add_library(camlink-vcam SHARED
  src/dllmain.cpp src/activator.cpp src/media_source.cpp src/media_stream.cpp src/nv12_scale.cpp src/exports.def)
# runtimeobject: WRL Module'ün hata raporlaması (RoOriginateError) için.
target_link_libraries(camlink-vcam PRIVATE mfplat mfuuid mfsensorgroup advapi32 ole32 runtimeobject)

add_executable(vcam-probe probe/probe.cpp)
target_link_libraries(vcam-probe PRIVATE mfplat mf mfreadwrite mfuuid ole32 winmm)  # winmm: timeBeginPeriod

# NV12 ölçekleme denetimleri: build/Release/vcam-scale-test.exe (0 = geçti).
add_executable(vcam-scale-test tests/scale_test.cpp src/nv12_scale.cpp)
