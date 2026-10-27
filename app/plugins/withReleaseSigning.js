// Yayın (release) ayarları: kendi anahtarımızla imza ve sürümden türetilen versionCode.
//
// İmza: anahtar bilgisi depoda ve Gradle özelliklerinde durmaz; yalnız bu kullanıcının okuyabildiği
// ~/.camlink/signing.properties dosyasından (storeFile, storePassword, keyAlias, keyPassword) okunur.
// Dosya yoksa derleme bozulmaz: hata ayıklama imzası kullanılır ve Gradle uyarı yazar (scripts/release.ps1 bunu reddeder).
//
// versionCode: app.json'daki sürümden türetilir (major*10000 + minor*100 + patch; 1.0.0 -> 10000). Her sürümde
// artar, böylece yeni APK eskisinin üstüne kurulur. scripts/release.ps1 aynı formülle çapraz denetler.
const { withAppBuildGradle } = require('expo/config-plugins');

const MARK = "'.camlink/signing.properties'";

const SIGNING = `
        release {
            // CamLink yayın imzası (app/plugins/withReleaseSigning.js)
            def camlinkProps = new Properties()
            def camlinkFile = new File(System.getProperty('user.home'), '.camlink/signing.properties')
            if (camlinkFile.exists()) {
                camlinkFile.withInputStream { camlinkProps.load(it) }
                storeFile file(camlinkProps.getProperty('storeFile'))
                storePassword camlinkProps.getProperty('storePassword')
                keyAlias camlinkProps.getProperty('keyAlias')
                keyPassword camlinkProps.getProperty('keyPassword')
            } else {
                logger.warn('CamLink: ~/.camlink/signing.properties yok, yayın APK hata ayıklama anahtarıyla imzalanacak.')
            }
        }`;

function versionCodeOf(version) {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(version ?? '');
  if (!m) throw new Error(`withReleaseSigning: sürüm x.y.z olmalı (app.json: "${version}").`);
  const [major, minor, patch] = m.slice(1).map(Number);
  if (minor > 99 || patch > 99) throw new Error(`withReleaseSigning: minor ve patch 99'u geçemez (${version}).`);
  return major * 10000 + minor * 100 + patch;
}

function patch(src) {
  if (src.includes(MARK)) return src; // zaten uygulanmış
  // İlk sürümün (Gradle özelliği) yaması kalmışsa üstüne yenisini eklemek ikinci bir release {} bloğu açar ve Gradle düşer.
  if (src.includes('CAMLINK_STORE_FILE')) {
    throw new Error(
      "withReleaseSigning: android/app/build.gradle eski imza yamasını (CAMLINK_STORE_FILE) içeriyor. " +
        "app/android klasörünü silip 'npx expo prebuild --platform android --no-install --no-clean' ile yeniden üretin " +
        '(scripts/release.ps1 local.properties dosyasını kendisi yazar).',
    );
  }
  // signingConfigs içine release ekle (debug bloğundan sonra).
  const withConfig = src.replace(/signingConfigs\s*\{\s*debug\s*\{[^}]*\}/, (m) => m + SIGNING);
  // buildTypes.release: anahtar dosyası okunduysa (storeFile dolu) release imzası, yoksa hata ayıklama imzası.
  const out = withConfig.replace(
    /(buildTypes\s*\{[\s\S]*?release\s*\{[\s\S]*?)signingConfig signingConfigs\.debug/,
    '$1signingConfig signingConfigs.release.storeFile ? signingConfigs.release : signingConfigs.debug',
  );
  if (out === src || !out.includes('signingConfigs.release')) {
    throw new Error('withReleaseSigning: app/build.gradle beklenen biçimde değil, imza ayarı eklenemedi.');
  }
  return out;
}

module.exports = (config) => {
  config.android = { ...config.android, versionCode: versionCodeOf(config.version) };
  return withAppBuildGradle(config, (c) => {
    c.modResults.contents = patch(c.modResults.contents);
    return c;
  });
};
module.exports.patch = patch;
module.exports.versionCodeOf = versionCodeOf;
