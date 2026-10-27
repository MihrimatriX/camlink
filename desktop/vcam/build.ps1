# CamLink sanal kamera DLL'ini ve vcam-probe'u derler (Release, x64 -> build/Release).
#   pwsh desktop/vcam/build.ps1            derle
#   pwsh desktop/vcam/build.ps1 -Install   derle + yönetici olarak kur (UAC sorar)
param([switch]$Install, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'

$build = Join-Path $PSScriptRoot 'build'
$cmake = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'

if (-not $NoBuild) {
  & $cmake -S $PSScriptRoot -B $build -G 'Visual Studio 18 2026' -A x64
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  & $cmake --build $build --config Release
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
if (-not $Install) { exit 0 }

$log = Join-Path $build 'install.log'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
  [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  # Kurulum HKLM ve Program Files'a yazar: kendini yükseltilmiş olarak yeniden çalıştır.
  $p = Start-Process pwsh -Verb RunAs -Wait -PassThru `
    -ArgumentList '-NoProfile', '-File', "`"$PSCommandPath`"", '-Install', '-NoBuild'
  if (Test-Path $log) { Get-Content $log }
  exit $p.ExitCode
}

Start-Transcript -Path $log -Force | Out-Null
try {
  $dest = 'C:\Program Files\CamLink'
  New-Item -ItemType Directory -Force $dest | Out-Null
  # Frame Server eski DLL'i yüklü tutar; değiştirmeden önce durdur (ilk kamera isteğinde kendisi başlar).
  Stop-Service FrameServer -Force -ErrorAction SilentlyContinue
  Copy-Item (Join-Path $build 'Release\camlink-vcam.dll') $dest -Force
  $r = Start-Process regsvr32 -ArgumentList '/s', "`"$dest\camlink-vcam.dll`"" -Wait -PassThru
  if ($r.ExitCode) { throw "regsvr32 başarısız: $($r.ExitCode)" }
  Write-Output "Kuruldu: $dest\camlink-vcam.dll"
  $code = 0
} catch {
  Write-Output "HATA: $_"
  $code = 1
} finally {
  Stop-Transcript | Out-Null
}
exit $code
