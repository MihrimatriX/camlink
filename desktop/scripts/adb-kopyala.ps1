# CamLink masaüstü için adb'yi Android SDK'dan kopyalar (Windows):
#   <SDK>\platform-tools\{adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll} -> desktop\vendor\adb\
# Paketlenen adb, SDK'dakiyle aynı sürüm olmalı: iki farklı adb birbirinin sunucusunu kapatır.
# Kullanım: pwsh desktop\scripts\adb-kopyala.ps1 [-Sdk C:\yol\android-sdk]
param([string]$Sdk)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot   # desktop

# Kabuk kullanıcı ortam değişkenini görmüyorsa (yeni tanımlandıysa) kayıt defterinden okunur.
if (-not $Sdk) { $Sdk = $env:ANDROID_HOME }
if (-not $Sdk) { $Sdk = [Environment]::GetEnvironmentVariable('ANDROID_HOME', 'User') }
if (-not $Sdk) { throw "Android SDK bulunamadı: ANDROID_HOME tanımlı değil. -Sdk ile SDK klasörünü ver." }

$Src = Join-Path $Sdk "platform-tools"
$Dst = Join-Path $Root "vendor\adb"
$Files = "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll"
foreach ($f in $Files) {
  if (-not (Test-Path (Join-Path $Src $f))) { throw "$Src içinde $f yok. SDK Manager ile 'Android SDK Platform-Tools' kurulu mu?" }
}
New-Item -ItemType Directory -Force $Dst | Out-Null
foreach ($f in $Files) { Copy-Item (Join-Path $Src $f) $Dst -Force }

$Ver = (Get-Content (Join-Path $Src "source.properties") | Where-Object { $_ -like "Pkg.Revision=*" }) -replace "Pkg.Revision=", ""
& (Join-Path $Dst "adb.exe") version | Select-Object -First 2
Write-Host "adb (platform-tools $Ver) kopyalandı: $Dst"
