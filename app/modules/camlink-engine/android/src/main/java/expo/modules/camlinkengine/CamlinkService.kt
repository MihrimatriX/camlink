package expo.modules.camlinkengine

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import com.facebook.react.HeadlessJsTaskService
import com.facebook.react.bridge.Arguments
import com.facebook.react.jstasks.HeadlessJsTaskConfig
import com.facebook.react.jstasks.HeadlessJsTaskContext

/**
 * Yayın sürerken çalışan ön plan hizmeti (kamera + mikrofon). İki işi var:
 * 1. Ekran kapanınca ya da uygulama arkaya geçince Android kamerayı ve mikrofonu kesmesin (arka plandaki uygulamanın
 *    kamerası ~1 dk sonra kapanıyordu).
 * 2. JS zamanlayıcıları dursun diye bir Headless JS görevi açık tutar: React Native uygulama duraklayınca
 *    setTimeout/setInterval'ı durdurur, etkin görev varken sürdürür. Masaüstüyle kontrol kanalının 2 sn'lik kalp
 *    atışı ve yeniden bağlanma buna bağlı (masaüstü 6 sn sessiz kalan telefonu düşürür).
 * Görev JS'te hiç bitmez (index.ts); hizmet kapanırken burada bitirilir. Üst sınıf ayrıca kısmi uyanık kalma kilidi alır.
 * Başlatan ve durduran CamEngine (yayın başlayınca / durunca).
 */
class CamlinkService : HeadlessJsTaskService() {
  private var taskStarted = false
  private var taskId: Int? = null

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    if (intent?.action == ACTION_STOP) {
      CamEngine.remoteStop()
      return START_NOT_STICKY
    }
    try {
      startForegroundCompat(intent?.getStringExtra(EXTRA_TEXT))
    } catch (e: Exception) {
      // Android 12+ arka planda ön plan hizmetine izin vermez (ForegroundServiceStartNotAllowedException), 14+ kamera/
      // mikrofon türü için uygun değilse SecurityException atar: çökme yerine hizmet kapanır, motor haberdar edilir.
      CamEngine.onServiceFailed(e)
      stopSelf()
      return START_NOT_STICKY
    }
    if (taskStarted) return START_NOT_STICKY
    taskStarted = true
    super.onStartCommand(intent, flags, startId)
    // Süreç ölürse hizmet kendiliğinden yeniden başlamasın: yayını yeniden kurmak uygulamanın işi.
    return START_NOT_STICKY
  }

  override fun getTaskConfig(intent: Intent?): HeadlessJsTaskConfig =
    HeadlessJsTaskConfig(TASK, Arguments.createMap(), 0, true)

  override fun onHeadlessJsTaskStart(taskId: Int) {
    this.taskId = taskId
  }

  /** Uygulama son kullanılanlardan kaydırılıp atıldı: yayın ve hizmet kapanır (arayüz olmadan bir şey çalışmasın). */
  override fun onTaskRemoved(rootIntent: Intent?) {
    CamEngine.onTaskRemoved()
    stopSelf()
    super.onTaskRemoved(rootIntent)
  }

  override fun onDestroy() {
    taskId?.let { id -> reactContext?.let { HeadlessJsTaskContext.getInstance(it).finishTask(id) } }
    taskId = null
    super.onDestroy()
  }

  private fun startForegroundCompat(text: String?) {
    val nm = getSystemService(NotificationManager::class.java)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
      nm.createNotificationChannel(NotificationChannel(CHANNEL, "Yayın", NotificationManager.IMPORTANCE_LOW))
    }
    val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
    val open = packageManager.getLaunchIntentForPackage(packageName)?.let { PendingIntent.getActivity(this, 0, it, flags) }
    val stop = PendingIntent.getService(this, 1, Intent(this, CamlinkService::class.java).setAction(ACTION_STOP), flags)
    val n: Notification = NotificationCompat.Builder(this, CHANNEL)
      .setSmallIcon(android.R.drawable.presence_video_online)
      .setContentTitle("CamLink yayında")
      .setContentText(text?.takeIf { it.isNotBlank() } ?: "Telefon kamerası bilgisayarda")
      .setOngoing(true)
      .setOnlyAlertOnce(true)
      .setContentIntent(open)
      .addAction(0, "Durdur", stop)
      .build()
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
      var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
      // Mikrofon türü izin yokken istenirse Android 14 hizmeti açmaz.
      if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
        type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
      }
      startForeground(ID, n, type)
    } else {
      startForeground(ID, n)
    }
  }

  companion object {
    const val EXTRA_TEXT = "text"
    const val ACTION_STOP = "expo.modules.camlinkengine.STOP"
    /** index.ts'teki AppRegistry.registerHeadlessTask adı. */
    const val TASK = "CamLinkKeepAlive"
    private const val CHANNEL = "camlink-stream"
    private const val ID = 4467
  }
}
