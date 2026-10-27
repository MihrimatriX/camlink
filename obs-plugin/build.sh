#!/bin/bash
# CamLink OBS eklentisini derler.
#   Windows (asıl hedef): MinGW ile OBS 30 başlıklarına karşı; OBS 30 ve sonrası yükler.
#   Linux: yalnızca gerçek OBS ile otomatik test için.
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build out

# OBS 30.0 libobs başlıkları (yalnız başlıklar; OBS'i derlemek gerekmez). OBS30 verilmezse build/obs-30'a indirilir.
if [ -z "${OBS30:-}" ]; then
  OBS30="$PWD/build/obs-30/libobs"
  if [ ! -f "$OBS30/obs-module.h" ]; then
    echo "OBS 30.0 başlıkları indiriliyor..."
    rm -rf build/obs-30
    git -c advice.detachedHead=false clone -q --depth 1 --branch 30.0.0 --filter=blob:none --sparse https://github.com/obsproject/obs-studio.git build/obs-30
    git -C build/obs-30 sparse-checkout set libobs
  fi
fi

# Windows
x86_64-w64-mingw32-gcc -c -O2 -Wall -Wextra -Wno-unused-parameter -std=gnu11 -I"$OBS30" -Icompat -o build/camlink.o src/camlink.c
{ echo "LIBRARY obs.dll"; echo "EXPORTS"; x86_64-w64-mingw32-nm -u build/camlink.o | awk '{print $2}' | grep -E "^(obs_|blog$|bfree$|bmalloc$|bzalloc$|text_lookup_)" | sed 's/^/    /'; } > build/obs.def
x86_64-w64-mingw32-dlltool -d build/obs.def -D obs.dll -l build/libobs.dll.a
x86_64-w64-mingw32-gcc -shared -O2 -s -o build/camlink.dll build/camlink.o build/libobs.dll.a -lws2_32 -liphlpapi -static-libgcc -Wl,--no-undefined

rm -rf out/camlink
mkdir -p out/camlink/bin/64bit out/camlink/data
cp build/camlink.dll out/camlink/bin/64bit/
cp -r data/locale out/camlink/data/

# Linux (test)
if pkg-config --exists libobs 2>/dev/null; then
  gcc -shared -fPIC -O2 -Wall -std=gnu11 $(pkg-config --cflags libobs) -o build/camlink.so src/camlink.c $(pkg-config --libs libobs) -lpthread
fi
echo "Eklenti hazır: out/camlink"
