#Requires -Version 7
# CamLink yayın paketleri: imzalı Android APK'sı ve Windows için Velopack kurulum paketi (Setup).
#   pwsh scripts/release.ps1 -Version 1.0.0             dist/ altına üretir (yayınlamaz)
#   pwsh scripts/release.ps1 -Version x.y.z -Publish    üretir + GitHub sürümü açar (MihrimatriX/camlink)
# Çıktılar: dist/CamLink.apk, dist/releases/ (CamLink-win-Setup.exe, *.nupkg, releases.win.json).
# Önkoşullar: vpk (dotnet tool install -g vpk, csproj'daki Velopack ile aynı sürüm), Visual Studio C++ (vcam),
# JDK 17 + Android SDK (CMake 3.31.6 dahil), imza için ~/.camlink/signing.properties (app/plugins/withReleaseSigning.js).
# -Publish için ayrıca: gh auth login ve herkese açık, en az bir commit'i olan camlink deposu.
# Sürüm: app/app.json "version" tek kaynaktır; APK'nın versionName'i bundan, versionCode'u major*10000+minor*100+patch
# formülüyle (eklenti) türetilir. Yeni sürüm için önce app.json'u değiştirin, sonra bu betiği o sürümle çalıştırın.
param(
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [switch]$Publish
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$win = Join-Path $dist 'win'
$releases = Join-Path $dist 'releases'
$apk = Join-Path $dist 'CamLink.apk'
$android = Join-Path $root 'app\android'
$repo = 'MihrimatriX/camlink'
$tag = "v$Version"

# Kabuk kullanıcı ortamını görmüyorsa bu bilgisayardaki yollar kullanılır.
if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = 'C:\Apps\DevKit\android-sdk' }
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Program Files\Java\jdk-17.0.5' }
if (-not $env:GRADLE_USER_HOME) { $env:GRADLE_USER_HOME = 'C:\Apps\DevKit\gradle' }

# Yerel komut hata kodunu hataya çevirir (PowerShell kendiliğinden çevirmez).
function Step([string]$what, [scriptblock]$run) {
  Write-Host "==> $what" -ForegroundColor Cyan
  & $run
  if ($LASTEXITCODE) { throw "$what başarısız (çıkış kodu $LASTEXITCODE)" }
}

# app/plugins/withReleaseSigning.js ile aynı formül: 1.0.0 -> 10000, 1.2.3 -> 10203.
function Get-VersionCode([string]$v) {
  $p = $v.Split('.') | ForEach-Object { [int]$_ }
  if ($p[1] -gt 99 -or $p[2] -gt 99) { throw "Sürümde minor ve patch 99'u geçemez ($v)." }
  $p[0] * 10000 + $p[1] * 100 + $p[2]
}

# Üretilmiş android/app/build.gradle'daki sürüm (APK'ya giren değer).
function Get-GradleVersion([string]$androidDir) {
  $g = Get-Content (Join-Path $androidDir 'app\build.gradle') -Raw
  [pscustomobject]@{
    Name = [regex]::Match($g, 'versionName\s+"([^"]+)"').Groups[1].Value
    Code = [int][regex]::Match($g, 'versionCode\s+(\d+)').Groups[1].Value
  }
}

# .properties değeri için ':' ve '\' kaçışı (mevcut dosyadaki biçim: C\:\\Apps\\...).
function ConvertTo-PropValue([string]$p) { $p.Replace('\', '\\').Replace(':', '\:') }

# android/local.properties: sdk.dir ve cmake.dir. cmake.dir olmadan ninja "260 karakter" sınırında düşer (kısa yoldaki
# CMake gerekli); prebuild yeni klasör açarsa dosya da yoktur. Eksik satırlar eklenir, var olanlara dokunulmaz.
function Set-LocalProperties([string]$androidDir, [string]$sdk, [string]$cmakeVersion = '3.31.6') {
  $file = Join-Path $androidDir 'local.properties'
  $lines = @(if (Test-Path $file) { Get-Content $file })
  $add = @()
  if (-not ($lines -match '^\s*sdk\.dir\s*=')) {
    if (-not (Test-Path $sdk)) { throw "Android SDK bulunamadı: $sdk (ANDROID_HOME)." }
    $add += 'sdk.dir=' + (ConvertTo-PropValue $sdk)
  }
  if (-not ($lines -match '^\s*cmake\.dir\s*=')) {
    $cmake = Join-Path $sdk "cmake\$cmakeVersion"
    if (-not (Test-Path $cmake)) {
      throw "CMake $cmakeVersion kurulu değil: $cmake. Android SDK Manager'dan 'CMake $cmakeVersion' kurun; yoksa APK derlemesi dakikalar sonra 'Filename longer than 260 characters' ile düşer."
    }
    $add += 'cmake.dir=' + (ConvertTo-PropValue $cmake)
  }
  if ($add) { Set-Content $file ($lines + $add) -Encoding utf8 }
}

# -Publish ön denetimi: derlemeden ÖNCE çalışır, eksik varsa dakikalar boşa gitmez. Token'ı döndürür.
function Test-PublishReady([string]$repo, [string]$tag) {
  gh auth status *> $null
  if ($LASTEXITCODE) { throw "GitHub oturumu yok: 'gh auth login' ile giriş yapın." }
  $token = gh auth token
  if ($LASTEXITCODE -or -not $token) { throw "gh token alınamadı: 'gh auth login' ile yeniden giriş yapın." }

  $json = gh repo view $repo --json visibility,defaultBranchRef 2>&1
  if ($LASTEXITCODE) { throw "$repo deposu yok ya da erişilemiyor. Önce depoyu oluşturun (herkese açık)." }
  $info = $json | ConvertFrom-Json
  if ($info.visibility -ne 'PUBLIC') {
    throw "$repo herkese açık değil ($($info.visibility)). Uygulamanın güncelleme denetimi ve indirme bağlantıları 404 verir; depoyu herkese açın."
  }
  if (-not $info.defaultBranchRef) { throw "$repo deposunda hiç commit yok. Sürüm açılamaz; önce bir commit gönderin (örn. README)." }

  # Taslak sürümler etiketle görünmez; liste onları da kapsar.
  gh release view $tag -R $repo *> $null
  $exists = $LASTEXITCODE -eq 0
  $tags = @(gh release list -R $repo --limit 1000 --json tagName -q '.[].tagName')
  if ($LASTEXITCODE) { throw "$repo sürümleri listelenemedi (gh release list çıkış kodu $LASTEXITCODE); taslak sürüm denetimi yapılamadan yayına geçilmez." }
  if ($exists -or $tags -contains $tag) {
    throw "$repo içinde $tag sürümü zaten var. Silmek için: gh release delete $tag --cleanup-tag -y -R $repo (sonra betiği yeniden çalıştırın) ya da yeni sürüm numarası kullanın."
  }
  $token
}

# İmza dosyası (~/.camlink/signing.properties) dört anahtarla var mı, anahtar deposu yerinde mi: dakikalar süren
# derlemeden önce bilinsin. Değerler okunur ama hiçbir yere yazdırılmaz.
function Test-SigningConfig([string]$path = (Join-Path $env:USERPROFILE '.camlink\signing.properties')) {
  if (-not (Test-Path $path)) {
    throw "İmza dosyası yok: $path. İçinde storeFile, storePassword, keyAlias, keyPassword satırları olmalı (yalnız bu kullanıcıya açık). Yoksa APK hata ayıklama anahtarıyla imzalanır ve betik onu reddeder."
  }
  $props = @{}
  foreach ($line in Get-Content $path) {
    if ($line -match '^\s*(storeFile|storePassword|keyAlias|keyPassword)\s*=\s*(.+?)\s*$') { $props[$Matches[1]] = $Matches[2] }
  }
  foreach ($k in 'storeFile', 'storePassword', 'keyAlias', 'keyPassword') {
    if (-not $props[$k]) { throw "İmza dosyasında $k eksik ya da boş: $path" }
  }
  if (-not (Test-Path $props['storeFile'])) { throw "İmza anahtar deposu bulunamadı: $($props['storeFile']) ($path içindeki storeFile)." }
}

# Etiketi ya da başlığı bu sürümle eşleşen TÜM sürümler (taslaklar dahil; GitHub etiketle aramada taslağı görmez, liste görür).
# vpk gh'nin açtığı taslağı bulamayıp ikinci bir sürüm açarsa burada iki satır görünür.
function Get-TaggedReleases([string]$repo, [string]$tag, [string]$title) {
  $lines = gh api "repos/$repo/releases?per_page=100" --paginate --jq '.[] | {id, tag: .tag_name, name, draft, assets: [.assets[].name]} | tojson'
  if ($LASTEXITCODE) { throw "$repo sürümleri okunamadı (gh api çıkış kodu $LASTEXITCODE)." }
  @($lines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.tag -eq $tag -or $_.name -eq $title })
}

# Sürüm tutarlılığı: app.json tek kaynak. -Publish'te uyuşmazlık hatadır (yanlış sürüm etiketiyle yanlış APK yayınlanmasın).
$appVersion = (Get-Content (Join-Path $root 'app\app.json') -Raw | ConvertFrom-Json).expo.version
if ($appVersion -ne $Version) {
  $msg = "app/app.json sürümü ($appVersion) -Version ($Version) ile aynı değil. Önce app.json'daki version'ı $Version yapın."
  if ($Publish) { throw $msg }
  Write-Warning "$msg Yoksa APK $appVersion olarak çıkar."
}

# 0. Önkoşullar: uzun adımlardan önce.
Test-SigningConfig
$token = if ($Publish) { Test-PublishReady $repo $tag }

function Invoke-Prebuild {
  Push-Location (Join-Path $root 'app')
  try { Step 'expo prebuild' { npx expo prebuild --platform android --no-install --no-clean } } finally { Pop-Location }
}
# app/android üretilmiş klasördür (depoda yok): yoksa ya da sürümü app.json'dan eskiyse bir kez uygulanır.
# SDK 58'de prebuild varsayılanı klasörü silip baştan kurar: "--no-clean" şart.
if (-not (Test-Path $android)) { Invoke-Prebuild }
Set-LocalProperties $android $env:ANDROID_HOME
$gv = Get-GradleVersion $android
if ($gv.Name -ne $appVersion -or $gv.Code -ne (Get-VersionCode $appVersion)) {
  Invoke-Prebuild
  Set-LocalProperties $android $env:ANDROID_HOME
  $gv = Get-GradleVersion $android
  if ($gv.Name -ne $appVersion -or $gv.Code -ne (Get-VersionCode $appVersion)) {
    throw "android/app/build.gradle sürümü ($($gv.Name) / $($gv.Code)) app.json ile ($appVersion / $(Get-VersionCode $appVersion)) uyuşmuyor; prebuild uygulanmadı ya da eklenti yüklenmedi."
  }
}
if ($Publish -and ($gv.Name -ne $Version -or $gv.Code -ne (Get-VersionCode $Version))) {
  throw "APK sürümü ($($gv.Name) / $($gv.Code)) -Version ($Version) ile uyuşmuyor."
}

# 1. Sanal kamera DLL'i (kurulum yok, yalnız derleme); csproj onu uygulamanın yanına kopyalar.
Step 'Sanal kamera DLL''i' { & (Join-Path $root 'desktop\vcam\build.ps1') }

# 2. Masaüstü uygulaması + Velopack Setup
# vpk aynı ya da daha yeni sürümün yanına paketlemez; her çalıştırma temiz başlar.
# ponytail: önceki sürümler yerelde tutulmadığı için delta paket üretilmez; gerekirse pack'ten önce 'vpk download github' eklenir.
foreach ($d in $win, $releases) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
Step 'dotnet publish' {
  dotnet publish (Join-Path $root 'desktop\src\CamLink.Desktop') -c Release -r win-x64 --self-contained -p:Version=$Version -o $win
}
# --noPortable: 125 MB'lık Portable.zip'e gerek yok (güncelleme Setup/nupkg ile yapılır).
Step 'vpk pack' {
  vpk pack -u CamLink -v $Version -p $win -e CamLink.Desktop.exe --packTitle CamLink -r win-x64 --noPortable `
    --icon (Join-Path $root 'desktop\src\CamLink.Desktop\Assets\camlink.ico') -o $releases
}

# 3. İmzalı APK
Push-Location $android
try { Step 'gradlew assembleRelease' { & .\gradlew.bat assembleRelease } } finally { Pop-Location }
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $android 'app\build\outputs\apk\release\app-release.apk') $apk -Force

# İmza gerçekten bizim anahtarımızla mı (signing.properties yoksa sessizce hata ayıklama anahtarına düşer).
# build-tools adları "36.0.0-rc1" gibi olabilir: sayısal önekle sıralanır, apksigner.bat olan en yenisi alınır.
$signer = Get-ChildItem (Join-Path $env:ANDROID_HOME 'build-tools\*\apksigner.bat') |
  Where-Object { $_.Directory.Name -match '^\d+(\.\d+)+' } |
  Sort-Object { [version][regex]::Match($_.Directory.Name, '^\d+(\.\d+)+').Value } |
  Select-Object -Last 1
if (-not $signer) { throw "apksigner.bat bulunamadı: $env:ANDROID_HOME\build-tools" }
$certs = & $signer.FullName verify --print-certs $apk
if ($LASTEXITCODE) { throw "apksigner doğrulaması başarısız" }
if (-not ($certs -match 'certificate DN: CN=CamLink\b')) { throw "APK CamLink anahtarıyla imzalanmamış (~/.camlink/signing.properties var mı?)" }

# 4. Yayın. Sıra: APK'lı TASLAK sürüm -> Velopack dosyaları (--merge, --publish YOK: vpk taslağı yayınlamasın) ->
# taslaktaki varlıkları ve sürüm sayısını denetle -> ancak ondan sonra yayınla. Denetim ya da bir adım düşerse sürüm
# taslak kalır (kullanıcılar görmez, güncelleme denetimi bulamaz); kurtarma komutları yazılır.
if ($Publish) {
  $title = "CamLink $Version"
  try {
    Step 'gh release create (taslak, APK ile)' {
      gh release create $tag $apk -R $repo --draft --title $title --notes $title
    }
    Step 'vpk upload github (taslağa birleştir)' {
      vpk upload github --repoUrl "https://github.com/$repo" --merge --releaseName $title --tag $tag --token $token -o $releases
    }

    # Tek sürüm olmalı: vpk gh'nin taslağını bulamayıp ikinci bir sürüm açtıysa burada yakalanır, hiçbiri yayınlanmaz.
    $found = @(Get-TaggedReleases $repo $tag $title)
    if ($found.Count -ne 1) {
      throw "$tag / '$title' ile eşleşen $($found.Count) sürüm var (1 olmalı; vpk gh'nin taslağına birleşmemiş olabilir). Hiçbiri yayınlanmadı."
    }
    if (-not $found[0].draft) { throw "$tag sürümü taslak değil, vpk onu denetimden önce yayınlamış." }
    # Beklenen varlıklar gerçek dosyalardan: dist/releases'taki her dosya (nupkg, releases.win.json, Setup vb.) + APK.
    # assets.win.json vpk'nın yerel kaydıdır; vpk upload onu yüklemez, güncelleyici de kullanmaz (v1.0.0'da görüldü).
    $expected = @((Get-ChildItem $releases -File | Where-Object Name -ne 'assets.win.json').Name) + 'CamLink.apk'
    $missing = @($expected | Where-Object { $found[0].assets -notcontains $_ })
    if ($missing) { throw "Taslakta eksik varlık: $($missing -join ', '). Yayınlanmadı." }

    Step 'gh release edit (yayınla)' { gh release edit $tag -R $repo --draft=false --latest }
    $now = @(Get-TaggedReleases $repo $tag $title)
    if ($now.Count -ne 1 -or $now[0].draft) { throw "Yayın sonrası doğrulama başarısız: $tag hâlâ taslak ya da birden fazla sürüm var." }
  } catch {
    Write-Host ''
    Write-Host "Yayın yarıda kaldı: $_" -ForegroundColor Red
    Write-Host 'Kurtarma (sürüm büyük olasılıkla taslak kaldı):'
    Write-Host "  Eksik dosyayı yüklemek : gh release upload $tag <dosya> -R $repo --clobber   (APK: `"$apk`")"
    Write-Host "  Sonra yayınlamak       : gh release edit $tag -R $repo --draft=false --latest"
    Write-Host "  Baştan başlamak için   : gh release delete $tag --cleanup-tag -y -R $repo   (sonra betiği yeniden çalıştırın)"
    throw
  }
}

Write-Host ''
Write-Host "Hazır: $apk"
Write-Host "Hazır: $(Join-Path $releases 'CamLink-win-Setup.exe')"
if (-not $Publish) { Write-Host 'Yayınlanmadı (-Publish verilmedi).' }
