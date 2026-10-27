# CamLink masaüstü için FFmpeg'i hazırlar (Windows):
#   FFmpeg'i (gyan.dev 9.0.2 essentials, GPL) indirir, özetini doğrular -> desktop\vendor\ffmpeg\ffmpeg.exe
# Kaynak: deckpad 48923a0 scripts/hazirla.ps1 (yalnız FFmpeg kısmı).
# Kullanım: pwsh desktop\scripts\ffmpeg-indir.ps1
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest'in ilerleme çubuğu ~100 MB indirmeyi dakikalarca yavaşlatır
$Root = Split-Path -Parent $PSScriptRoot   # desktop

$FfVer = "9.0.2"
# Özet, zip'in değil, zip'ten çıkan ffmpeg.exe'nindir.
$FfExeSha256 = "3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC"
$FfUrls = @(
  "https://github.com/GyanD/codexffmpeg/releases/download/$FfVer/ffmpeg-$FfVer-essentials_build.zip",
  "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$FfVer-essentials_build.zip"
)
$FfDir = Join-Path $Root "vendor\ffmpeg"
$FfExe = Join-Path $FfDir "ffmpeg.exe"

if ((Test-Path $FfExe) -and ((Get-FileHash $FfExe -Algorithm SHA256).Hash -eq $FfExeSha256)) {
  Write-Host "FFmpeg hazır."
  return
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
try {
  $zip = Join-Path $tmp "ffmpeg.zip"
  $ok = $false
  foreach ($u in $FfUrls) {
    Write-Host "FFmpeg indiriliyor: $u"
    try { Invoke-WebRequest -Uri $u -OutFile $zip -UseBasicParsing; $ok = $true; break } catch { Write-Warning $_ }
  }
  if (-not $ok) { throw "FFmpeg indirilemedi." }
  Expand-Archive $zip -DestinationPath $tmp -Force
  $src = Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
  if (-not $src) { throw "İndirilen zip'te ffmpeg.exe bulunamadı ($zip). Adresler ya da arşiv düzeni değişmiş olabilir." }
  # Özet, dosya vendor klasörüne girmeden önce doğrulanır.
  $got = (Get-FileHash $src.FullName -Algorithm SHA256).Hash
  if ($got -ne $FfExeSha256) { throw "FFmpeg özeti beklenenden farklı ($got). Dosya kopyalanmadı." }
  New-Item -ItemType Directory -Force $FfDir | Out-Null
  # Yarım kopya ffmpeg.exe adıyla kalmasın: önce .part'a kopyala, sonra taşı.
  Copy-Item $src.FullName "$FfExe.part" -Force
  Move-Item "$FfExe.part" $FfExe -Force
  Write-Host "FFmpeg $FfVer hazır."
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
