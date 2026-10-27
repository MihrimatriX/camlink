package expo.modules.camlinkengine

import android.Manifest
import android.animation.ValueAnimator
import android.app.ActivityManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.BatteryManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import android.view.OrientationEventListener
import android.view.Surface
import android.view.SurfaceView
import android.view.WindowManager
import android.view.animation.DecelerateInterpolator
import com.pedro.common.ConnectChecker
import com.pedro.encoder.input.gl.render.filters.AnalogTVFilterRender
import com.pedro.encoder.input.gl.render.filters.BaseFilterRender
import com.pedro.encoder.input.gl.render.filters.BeautyFilterRender
import com.pedro.encoder.input.gl.render.filters.BlurFilterRender
import com.pedro.encoder.input.gl.render.filters.CartoonFilterRender
import com.pedro.encoder.input.gl.render.filters.ContrastFilterRender
import com.pedro.encoder.input.gl.render.filters.EarlyBirdFilterRender
import com.pedro.encoder.input.gl.render.filters.GlitchFilterRender
import com.pedro.encoder.input.gl.render.filters.GreyScaleFilterRender
import com.pedro.encoder.input.gl.render.filters.Image70sFilterRender
import com.pedro.encoder.input.gl.render.filters.LamoishFilterRender
import com.pedro.encoder.input.gl.render.filters.NegativeFilterRender
import com.pedro.encoder.input.gl.render.filters.NoFilterRender
import com.pedro.encoder.input.gl.render.filters.PixelatedFilterRender
import com.pedro.encoder.input.gl.render.filters.SaturationFilterRender
import com.pedro.encoder.input.gl.render.filters.SepiaFilterRender
import com.pedro.encoder.input.gl.render.filters.SharpnessFilterRender
import com.pedro.encoder.input.gl.render.filters.TemperatureFilterRender
import com.pedro.encoder.input.gl.render.filters.`object`.SurfaceFilterRender
import com.pedro.encoder.input.sources.audio.MicrophoneSource
import com.pedro.encoder.input.sources.audio.NoAudioSource
import com.pedro.encoder.input.sources.video.Camera2Source
import com.pedro.encoder.input.video.Camera2ApiManager
import com.pedro.encoder.input.video.CameraCallbacks
import com.pedro.encoder.input.video.CameraHelper
import com.pedro.encoder.utils.gl.AspectRatioMode
import com.pedro.encoder.utils.gl.TranslateTo
import com.pedro.library.generic.GenericStream
import androidx.core.content.ContextCompat
import kotlin.math.hypot

data class EngineConfig(
  val width: Int = 1280,
  val height: Int = 720,
  val fps: Int = 30,
  val bitrate: Int = 4_000_000,
  val audio: Boolean = true,
  val portrait: Boolean = false,
  /** Yayın yönü telefonun fiziksel duruşunu izler (arayüz dönmese de). CamLink masaüstü yolunda açık. */
  val sensorOrientation: Boolean = false,
)

/**
 * Kamera → OpenGL filtre zinciri → donanım H.264 → SRT ya da RTMP boru hattının tek sahibi
 * (GenericStream, protokolü adresin önekinden seçer).
 * Bütün çağrılar ana iş parçacığından yapılır (modül fonksiyonları Queues.MAIN üzerinde çalışır).
 *
 * Filtre zinciri her zaman: [0] renk efekti (yoksa NoFilterRender), [1] resim içinde resim (isteğe bağlı).
 * RootEncoder, GL durduğunda (önizleme ve yayın yokken) filtreleri bırakır; bu yüzden zincir
 * GL her yeniden başladığında applyChain ile baştan kurulur.
 */
object CamEngine : ConnectChecker {
  // Bir lens seçimi en çok 4 ayrı adımdan oluşur: fiziksel kimliği sil, yön, mantıksal kimlik, fiziksel kimlik.
  private const val MAX_LENS_STEPS = 4

  private val main = Handler(Looper.getMainLooper())
  private var ctx: Context? = null
  private var emitter: ((String, Map<String, Any?>) -> Unit)? = null

  private var stream: GenericStream? = null
  private var config = EngineConfig()
  private var preview: SurfaceView? = null
  private var previewW = 0
  private var previewH = 0

  private var wantFront = false

  // Seçili lens ("0" ya da "mantıksal/fiziksel"); null ise yönün varsayılan kamerası. Kamera kapalıyken de saklanır.
  private var wantLens: String? = null
  // Şu anki Camera2Source'a uygulanmış fiziksel alt kamera. Kaynak yeniden kurulunca sıfırlanır.
  private var lensPhysical: String? = null
  // Bir lens seçimi için o ana dek atılan yeniden açma adımları; her yeni seçimde sıfırlanır (sonsuz döngü koruması).
  private var lensSteps = 0
  // Kamera her açıldığında artar; açılış sonrası hata bildirimlerinin eskidiğini anlamak için.
  private var openSeq = 0
  // Kamera hata ya da kopma bildirdi, henüz yeniden açılmadı. Camera2ApiManager bu durumda isRunning'i true bırakır;
  // start() tek başına bir şey yapmaz, önce stop() gerekir.
  private var cameraLost = false
  // Kayıp başına tek yeniden açma denemesi (döngü yok); başarılı açılış ya da yeni önizleme yüzeyi yeniden kurar.
  private var reopenTried = false
  private var whiteBalance = "auto"
  private var stabilization = false
  private var reapplyTries = 0
  private val reapplyTask = Runnable { reapplyCameraSettings() }

  private var filterId = "none"
  private var filterValue = 0.5f
  private var mirror = false
  // Telefon mikrofonu kapalı: ses izi kalır ama sessizdir (yayın yeniden kurulmaz; masaüstünün ses beklentisi değişmez).
  private var micMuted = false
  private var exposure = 0
  private var zoomAnimator: ValueAnimator? = null

  private var userStopped = true
  private var state = "idle"
  private var lastBitrate = 0L
  private var thermalReduced = false
  // Bit hızı denetimi (adaptBitrate): üst sınır, şu anki hedef, art arda tıkanıksız ölçüm sayısı.
  private var maxBitrate = 0
  private var targetBitrate = 0
  private var calmSamples = 0
  // Son lens/yön değişimi (SystemClock.uptimeMillis): yeni kamera açılırken gelen geçici hata yeniden açmayı tetiklemesin.
  private var lastLensChangeAt = 0L

  // Ön plan hizmeti (CamlinkService) açık mı; bildirimde gösterilecek hedef adı.
  private var serviceOn = false
  private var streamLabel: String? = null
  private val stopServiceTask = Runnable { stopService() }

  // Resim içinde resim (ikinci kamera)
  private var pipEnabled = false
  private var pipCorner = "BOTTOM_RIGHT"
  private var pipSize = 0.3f
  private var pipRotationExtra = 0
  private var pipFilter: SurfaceFilterRender? = null
  private var pipCamera: Camera2ApiManager? = null

  fun init(context: Context, emit: (String, Map<String, Any?>) -> Unit) {
    ctx = context.applicationContext
    emitter = emit
  }

  private fun emit(name: String, body: Map<String, Any?>) {
    try {
      emitter?.invoke(name, body)
    } catch (_: Throwable) {
    }
  }

  private fun setState(s: String, message: String? = null) {
    state = s
    emit("onStatus", mapOf("state" to s, "message" to message))
  }

  private fun emitError(message: String) = emit("onError", mapOf("message" to message))

  // ---------- Kurulum ----------

  private fun hasPermission(p: String): Boolean =
    ctx?.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED

  private fun ensureStream(): GenericStream {
    stream?.let { return it }
    val c = ctx ?: throw IllegalStateException("Kamera başlatılamadı")
    if (!hasPermission(Manifest.permission.CAMERA)) throw IllegalStateException("Kamera izni yok")

    val camera = Camera2Source(c)
    lensPhysical = null
    cameraLost = false
    reopenTried = false
    val micOk = config.audio && hasPermission(Manifest.permission.RECORD_AUDIO)
    val audio = if (micOk) MicrophoneSource().also { if (micMuted) it.mute() } else NoAudioSource()
    val s = GenericStream(c, this, camera, audio)
    // RootEncoder'ın yön izleyicisi ivmeölçer yönünü yalnız arayüz de döndüyse uygular (followUI). Masaüstü yolunda telefon
    // genelde yatık bir ayakta durur ama uygulama dik açılmış (ya da otomatik döndürme kapalı) olabilir: görüntü 90° dönük ve
    // yanları siyah gidiyordu (1080p karenin ~%32'si). Orada yönü kendi ivmeölçer dinleyicimiz verir (applyOrientation).
    s.getGlInterface().autoHandleOrientation = !config.sensorOrientation
    if (config.sensorOrientation) startOrientation(c) else stopOrientation()
    s.getGlInterface().setAspectRatioMode(AspectRatioMode.Adjust)
    // OBS henüz dinlemiyorsa vazgeçmeden tekrar dener.
    s.getStreamClient().setReTries(100_000)
    s.getStreamClient().setOnlyVideo(!micOk)

    if (!prepare(s)) throw IllegalStateException("Görüntü hazırlanamadı")
    if (wantFront && camera.getCameraFacing() != CameraHelper.Facing.FRONT) camera.switchCamera()
    camera.setCameraCallback(object : CameraCallbacks {
      override fun onCameraChanged(facing: CameraHelper.Facing) {}
      override fun onCameraError(error: String) {
        emitError("Kamera hatası: $error")
        // Lens zincirindeki kapanışlar geçici "yapılandırma" hatası verebilir; yarım saniye içinde yeni bir açılış
        // gelmediyse hata gerçektir ve yönün varsayılan lensine dönülür.
        main.post {
          cameraLost = true
          val seq = openSeq
          // Lens seçimi yoksa (ör. QR okuyucu kamerayı henüz bırakmadı) kamera bir kez yeniden açılır.
          if (wantLens != null) main.postDelayed({ if (openSeq == seq) fallbackToDefaultLens(camera) }, 500)
          else main.postDelayed({ reopenLostCamera(camera, seq) }, 800)
        }
      }
      override fun onCameraOpened() {
        main.post {
          openSeq++
          cameraLost = false
          reopenTried = false
          applyOrientation()   // kamera yeniden açıldı (lens/yön değişimi): fiziksel yön korunur
          // Lens farklı açıldıysa düzeltme yeni bir açılış doğurur; bilgi o açılışta gönderilir.
          if (restoreLens(camera)) return@post
          if (exposure != 0) camera.setExposure(exposure)
          scheduleReapply()
          emitCameraInfo()
        }
      }
      override fun onCameraDisconnected() {
        // Başka bir uygulama kamerayı aldı ya da sistem kesti: hata yolundaki gibi bir kez yeniden açılır.
        main.post {
          cameraLost = true
          val seq = openSeq
          if (wantLens == null) main.postDelayed({ reopenLostCamera(camera, seq) }, 800)
        }
      }
    })

    setBitrateCeiling(config.bitrate)

    stream = s
    applyOrientation()
    applyMirror(s)
    return s
  }

  private fun glRunning(s: GenericStream): Boolean = s.getGlInterface().isRunning()

  /** Filtre zincirini sıfırdan kurar. GL kapalıysa istekler kuyruğa girer ve GL açılınca uygulanır. */
  private fun applyChain(s: GenericStream) {
    val gl = s.getGlInterface()
    closePipCamera()
    pipFilter = null
    gl.clearFilters()
    gl.addFilter(makeFilter(filterId, filterValue))
    if (pipEnabled) startPip(s)
    applyOrientation()   // GL yeniden başladı: son fiziksel yön yeniden verilir
  }

  // ---------- Fiziksel yön (masaüstü yolu) ----------

  private var orientationListener: OrientationEventListener? = null
  // Son fiziksel yön (0/90/180/270, RootEncoder'ın SensorRotationManager'ıyla aynı formül); -1: henüz okunmadı.
  private var physicalRotation = -1

  private fun startOrientation(c: Context) {
    if (orientationListener != null) return
    physicalRotation = -1
    orientationListener = object : OrientationEventListener(c) {
      override fun onOrientationChanged(deg: Int) {
        if (deg == ORIENTATION_UNKNOWN) return   // telefon düz yatıyor: son yön kalır
        val rot = (deg + 45) / 90 % 4 * 90       // 270 = öbür yana yatık: baş aşağı olmaması için ayrı ele alınır
        if (rot == physicalRotation) return
        physicalRotation = rot
        applyOrientation()
      }
    }.also { if (it.canDetectOrientation()) it.enable() }
  }

  private fun stopOrientation() {
    orientationListener?.disable()
    orientationListener = null
    physicalRotation = -1
  }

  /** Son fiziksel yönü GL'ye verir (yayın ve önizleme). Arayüz yönünden bağımsız; dinleyici kapalıysa bir şey yapmaz. */
  private fun applyOrientation() {
    val rot = physicalRotation
    if (rot < 0 || orientationListener == null) return
    val gl = stream?.getGlInterface() ?: return
    gl.setCameraOrientation(rot)
    gl.setIsPortrait(rot == 0 || rot == 180)
  }

  /**
   * Bir yöndeki (ön/arka) kameraların destekleyebildiği en yüksek fps: renkli (BACKWARD_COMPATIBLE) kameraların AE
   * aralıklarındaki en yüksek üst sınırların en küçüğü (fiziksel lensler mantıksal kimlikle açılır). Bilinmiyorsa null.
   * RootEncoder (Camera2ApiManager.adaptFpsRange) istenen fps'i içeren bir aralık bulamazsa listedeki ilk aralıkta kalır;
   * SNE-LX1'de 60 istenince [12,12] açılıp görüntü takılıyordu.
   */
  private fun fpsLimit(front: Boolean): Int? {
    val cm = ctx?.getSystemService(Context.CAMERA_SERVICE) as? CameraManager ?: return null
    val want = if (front) CameraCharacteristics.LENS_FACING_FRONT else CameraCharacteristics.LENS_FACING_BACK
    val tops = try {
      cm.cameraIdList.mapNotNull { id ->
        val ch = cm.getCameraCharacteristics(id)
        if (ch.get(CameraCharacteristics.LENS_FACING) != want) return@mapNotNull null
        val caps = ch.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES) ?: return@mapNotNull null
        if (CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_BACKWARD_COMPATIBLE !in caps) return@mapNotNull null
        ch.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.maxOfOrNull { it.upper }
      }
    } catch (_: Exception) {
      emptyList()
    }
    return tops.minOrNull()
  }

  /**
   * Donanım H.264 kodlayıcısı High profili bildiriyorsa (High, bildirdiği en yüksek seviye): aynı bit hızında Baseline'dan
   * belirgin daha iyi görüntü. Yoksa (-1, -1): kodlayıcının varsayılanı (SNE-LX1'de Constrained Baseline). Profil seviyesiz
   * verilirse ACodec yapılandırmayı reddeder. B karesi istenmez (KEY_MAX_B_FRAMES yok).
   */
  private fun avcHighProfile(): Pair<Int, Int> = try {
    val high = android.media.MediaCodecInfo.CodecProfileLevel.AVCProfileHigh
    val hw = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS).codecInfos.firstOrNull {
      it.isEncoder && it.supportedTypes.any { t -> t.equals("video/avc", true) } &&
        !it.name.startsWith("OMX.google.") && !it.name.startsWith("c2.android.")
    }
    val level = hw?.getCapabilitiesForType("video/avc")?.profileLevels?.filter { it.profile == high }?.maxOfOrNull { it.level }
    android.util.Log.i("CamEngine", "H.264 kodlayıcı ${hw?.name}: High ${if (level != null) "var, seviye $level" else "yok"}")
    if (level != null) high to level else -1 to -1
  } catch (_: Exception) {
    -1 to -1
  }

  /** Kodlayıcının hazırlandığı fps (istenen, açılacak yönün sınırına sıkıştırılmış). */
  private var preparedFps = 30

  private fun prepare(s: GenericStream): Boolean {
    val rotation = if (config.portrait) 90 else 0
    val limit = fpsLimit(wantFront)
    val fps = limit?.let { minOf(config.fps, it) } ?: config.fps
    preparedFps = fps
    android.util.Log.i("CamEngine", "fps istenen=${config.fps} kullanılan=$fps (${if (wantFront) "ön" else "arka"} sınırı=$limit)")
    // Kodlayıcı High profili reddederse aynı boyut varsayılan profille; boyutu reddederse (istisna ya da false) 1280x720'ye düşülür.
    val (profile, level) = avcHighProfile()
    fun tryPrepare(p: Int, l: Int) = try {
      s.prepareVideo(config.width, config.height, config.bitrate, fps = fps, iFrameInterval = 1, rotation = rotation, profile = p, level = l)
    } catch (e: IllegalArgumentException) {
      false
    }
    val video = tryPrepare(profile, level) || (profile > 0 && tryPrepare(-1, -1)) || run {
      emitError("${config.width}x${config.height} bu kamerada desteklenmiyor, 1280x720 kullanılıyor")
      config = config.copy(width = 1280, height = 720)
      try {
        s.prepareVideo(1280, 720, config.bitrate, fps = fps, iFrameInterval = 1, rotation = rotation)
      } catch (e2: IllegalArgumentException) {
        false
      }
    }
    val audio = try {
      s.prepareAudio(44100, true, 128_000)
    } catch (e: IllegalArgumentException) {
      false
    }
    if (!audio) {
      emitError("Mikrofon hazırlanamadı, ses olmadan devam ediliyor")
      s.getStreamClient().setOnlyVideo(true)
    }
    return video
  }

  fun configure(cfg: EngineConfig) {
    if (cfg == config && stream != null) return
    if (stream?.isStreaming == true) throw IllegalStateException("Yayın sürerken ayar değiştirilemez")
    config = cfg
    rebuild()
  }

  private fun rebuild() {
    val view = preview
    closePip()
    zoomAnimator?.cancel()
    stream?.release()
    stream = null
    if (view != null) attachPreview(view, previewW, previewH)
  }

  fun shutdown() {
    stopOrientation()
    userStopped = true
    zoomAnimator?.cancel()
    closePip()
    stream?.release()
    stream = null
    preview = null
    main.removeCallbacksAndMessages(null)
    stopService()
  }

  // ---------- Önizleme ----------

  fun attachPreview(view: SurfaceView, w: Int, h: Int) {
    preview = view
    previewW = w
    previewH = h
    val s = try {
      ensureStream()
    } catch (e: Exception) {
      emitError(e.message ?: "Kamera açılamadı")
      return
    }
    val glWasRunning = glRunning(s)
    if (!s.isOnPreview) s.startPreview(view)
    if (!glWasRunning) applyChain(s)
    s.getGlInterface().setPreviewResolution(w, h)
    if (pipEnabled) pipFilter?.let { layoutPip(it) }
    // Yayın sürerken arka planda kaybedilen kamera öne dönünce kendiliğinden açılmaz (GL zaten çalışıyor); bir kez dene.
    if (cameraLost) {
      reopenTried = false
      val seq = openSeq
      main.postDelayed({ camera()?.let { reopenLostCamera(it, seq) } }, 800)
    }
  }

  fun detachPreview(view: SurfaceView) {
    if (preview !== view) return
    preview = null
    val s = stream ?: return
    if (s.isOnPreview) s.stopPreview()
    // Yayın yoksa GL ve kameralar kapanır; pil ve ısı için önemli. Filtreler GL ile birlikte bırakılır.
    if (!s.isStreaming) {
      closePipCamera()
      pipFilter = null
      cameraLost = false // kamera düzgünce kapandı; yeni önizleme onu baştan açar
    }
  }

  // ---------- Yayın ----------

  fun start(url: String, label: String? = null) {
    // Ön plan hizmeti kapalıyken arka planda başlatılan yayın kamerayı açamaz (siyah görüntü); Android 12+ hizmeti
    // de açtırmaz, 14+ çökertebilir. Uygulama öne gelince JS yeniden dener.
    if (!serviceOn && !appVisible()) throw IllegalStateException("Yayını başlatmak için CamLink'i aç")
    val s = ensureStream()
    if (s.isStreaming) return
    userStopped = false
    thermalReduced = false
    streamLabel = label
    setState("connecting", url)
    val glWasRunning = glRunning(s)
    s.startStream(url)
    if (!glWasRunning) applyChain(s)
    // stopStream kodlayıcıyı son anlık bit hızıyla yeniden hazırlar: tıkanıklıkta düşmüş (ya da ısınmada kısılmış) hız
    // yeni yayına taşınmasın, her başlangıç ayardaki bit hızından başlasın.
    setBitrateCeiling(config.bitrate)
    s.setVideoBitrateOnFly(config.bitrate)
    scheduleStats()
    startService()
  }

  /** Uygulamanın bir ekranı görünür mü (ön plan hizmeti açmak ve kamerayı kullanmak için gerekli). */
  private fun appVisible(): Boolean {
    val info = ActivityManager.RunningAppProcessInfo()
    ActivityManager.getMyMemoryState(info)
    return info.importance <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE
  }

  /** CamlinkService ön plana geçemedi (izin/arka plan kısıtı): hizmet kendini kapattı. */
  fun onServiceFailed(e: Exception) {
    serviceOn = false
    emitError("Yayın arka planda sürdürülemiyor: ${e.message}")
  }

  /**
   * Uygulama son kullanılanlardan kaydırılıp atıldı: arayüz yokken yayın ve hizmet sürmesin.
   * JS'e onRemoteStop gönderilmez: süreç önbellekte kalırsa yeniden açılışta yayın isteği silinmiş olurdu.
   */
  fun onTaskRemoved() {
    stop()
    main.removeCallbacks(stopServiceTask)
    stopService()
  }

  fun stop() {
    userStopped = true
    val s = stream
    if (s != null && s.isStreaming) s.stopStream()
    setState("idle")
    if (preview == null) {
      closePipCamera()
      pipFilter = null
    }
    scheduleServiceStop()
  }

  /** Bildirimdeki "Durdur": yayını durdurur ve JS'e bildirir (JS yeniden başlatma niyetini bırakır). */
  fun remoteStop() {
    stop()
    emit("onRemoteStop", emptyMap())
  }

  // ---------- Ön plan hizmeti ----------
  // Yayın başlayınca açılır. Durunca 5 sn sonra kapanır: aradaki yeniden başlatmalarda (USB ↔ Wi‑Fi, kalite) hizmet
  // açık kalır; Android 12+ arka plandaki uygulamanın ön plan hizmetini yeniden açmasına izin vermez.

  private fun startService() {
    main.removeCallbacks(stopServiceTask)
    if (serviceOn) return
    val c = ctx ?: return
    try {
      ContextCompat.startForegroundService(c, Intent(c, CamlinkService::class.java).putExtra(CamlinkService.EXTRA_TEXT, streamLabel))
      serviceOn = true
    } catch (e: Exception) {
      emitError("Yayın arka planda sürdürülemiyor: ${e.message}")
    }
  }

  private fun scheduleServiceStop() {
    main.removeCallbacks(stopServiceTask)
    if (serviceOn) main.postDelayed(stopServiceTask, 5000)
  }

  private fun stopService() {
    if (!serviceOn) return
    serviceOn = false
    ctx?.let { it.stopService(Intent(it, CamlinkService::class.java)) }
  }

  override fun onConnectionStarted(url: String) {
    main.post { if (!userStopped) setState("connecting", url) }
  }

  override fun onConnectionSuccess() {
    main.post { if (!userStopped) setState("live") }
  }

  override fun onConnectionFailed(reason: String) {
    main.post {
      val s = stream ?: return@post
      if (userStopped) return@post
      if (s.getStreamClient().reTry(2000, reason, null)) {
        setState("retrying", reason)
      } else {
        s.stopStream()
        userStopped = true
        setState("failed", reason)
        scheduleServiceStop()
      }
    }
  }

  override fun onNewBitrate(bitrate: Long) {
    lastBitrate = bitrate
    main.post {
      val s = stream ?: return@post
      if (s.isStreaming) adaptBitrate(s)
    }
  }

  /**
   * Saniyede bir (onNewBitrate): gönderim kuyruğu tıkanıyorsa hedef %20 düşer (en az üst sınırın dörtte biri);
   * 5 ölçüm tıkanıksız geçerse %25 artar, üst sınırı aşmaz. RootEncoder'ın BitrateAdapter'ı yeni hedefi ölçülen bit
   * hızından (×1,2) hesaplıyor: kodlayıcı hedefin altında üretince (durağan sahne) hedef her turda düşüyor ve 1080p
   * ~200 kbps'ye iniyordu. Burada yalnız tıkanıklık düşürür.
   */
  private fun adaptBitrate(s: GenericStream) {
    if (s.getStreamClient().hasCongestion()) {
      calmSamples = 0
      val next = maxOf((targetBitrate * 0.8).toInt(), maxBitrate / 4)
      if (next < targetBitrate) {
        targetBitrate = next
        s.setVideoBitrateOnFly(next)
      }
    } else if (targetBitrate < maxBitrate && ++calmSamples >= 5) {
      calmSamples = 0
      targetBitrate = minOf((targetBitrate * 1.25).toInt(), maxBitrate)
      s.setVideoBitrateOnFly(targetBitrate)
    }
  }

  private fun setBitrateCeiling(bps: Int) {
    maxBitrate = bps
    targetBitrate = bps
    calmSamples = 0
  }

  override fun onDisconnect() {
    main.post { if (userStopped) setState("idle") }
  }

  override fun onAuthError() {
    main.post { setState("failed", "Bilgisayar bağlantıyı kabul etmedi") }
  }

  override fun onAuthSuccess() {}

  private val statsTask = object : Runnable {
    override fun run() {
      val s = stream
      if (s == null || !s.isStreaming) return
      val thermal = thermalStatus()
      // Telefon çok ısınırsa bit hızını bir kez %40 düşür.
      if (thermal >= 3 && !thermalReduced) {
        thermalReduced = true
        val reduced = (config.bitrate * 0.6).toInt()
        maxBitrate = reduced
        targetBitrate = minOf(targetBitrate, reduced)
        s.setVideoBitrateOnFly(targetBitrate)
        emitError("Telefon ısındı, görüntü kalitesi biraz düşürüldü")
      }
      emit(
        "onStats",
        mapOf(
          "bitrate" to lastBitrate.toDouble(),
          "droppedVideo" to s.getStreamClient().getDroppedVideoFrames().toDouble(),
          "thermal" to thermal,
          "battery" to batteryLevel(),
          "charging" to isCharging(),
          "state" to state,
        ),
      )
      main.postDelayed(this, 2000)
    }
  }

  private fun scheduleStats() {
    main.removeCallbacks(statsTask)
    main.postDelayed(statsTask, 1000)
  }

  private fun thermalStatus(): Int {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return 0
    val pm = ctx?.getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return 0
    return pm.currentThermalStatus
  }

  private fun batteryLevel(): Int {
    val bm = ctx?.getSystemService(Context.BATTERY_SERVICE) as? BatteryManager ?: return -1
    return bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
  }

  private fun isCharging(): Boolean {
    val intent = ctx?.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return false
    val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
    return status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
  }

  /** Mikrofonu yayını durdurmadan susturur ya da açar. Mikrofon izni yoksa ses izi zaten yoktur. */
  fun setMicMuted(muted: Boolean) {
    micMuted = muted
    (stream?.audioSource as? MicrophoneSource)?.let { if (muted) it.mute() else it.unMute() }
  }

  fun setBitrate(bps: Int) {
    config = config.copy(bitrate = bps)
    setBitrateCeiling(bps)
    stream?.let { if (it.isStreaming) it.setVideoBitrateOnFly(bps) }
  }

  // ---------- Kamera kontrolleri ----------

  private fun camera(): Camera2Source? = stream?.videoSource as? Camera2Source

  fun switchCamera(): String {
    ensureStream()
    val cam = camera() ?: return facingName()
    zoomAnimator?.cancel()
    lastLensChangeAt = SystemClock.uptimeMillis()
    if (lensPhysical != null && cam.isRunning()) {
      // Fiziksel alt kamera açıkken kimliği silmek de yön değiştirmek de kamerayı yeniden açar; ikisi aynı anda
      // yapılmaz. Hedef karşı yönün varsayılan lensi: adımlar açılış bitince restoreLens ile sırayla sürer.
      val front = !wantFront
      val target = defaultLensId(front)
      wantLens = target
      lensSteps = 0
      stepLens(cam, target)
      return if (front) "front" else "back"
    }
    // Yön değişince yeni yönün varsayılan kamerası açılır; eski lens seçimi geçersiz olur.
    wantLens = null
    if (lensPhysical != null) clearPhysical(cam)
    // Yeni yönün kamerası kodlayıcının fps'ini desteklemiyorsa (ör. arka 60, ön 30) kamera o yönün sınırıyla açılır,
    // yoksa ilk AE aralığına (çoğunlukla 12–15 fps) düşerdi; geri dönünce yeniden yükselir. Tek yeniden açma:
    // durdur, yönü çevir, fps'i ayarla, aç. Kodlayıcı fps'i değişmez.
    val newFront = !wantFront
    val camFps = fpsLimit(newFront)?.let { minOf(preparedFps, it) } ?: preparedFps
    val st = cam.surfaceTexture
    if (cam.isRunning() && camFps != cam.fps && st != null) {
      cam.stop()
      cam.switchCamera()
      cam.fps = camFps
      cam.start(st)
    } else {
      cam.fps = camFps
      cam.switchCamera()
    }
    wantFront = cam.getCameraFacing() == CameraHelper.Facing.FRONT
    restartPip()
    return facingName()
  }

  /** İkinci kamera (resim içinde resim) karşı yönü açar; ana kamera yön değiştirince yeniden kurulur. */
  private fun restartPip() {
    val s = stream ?: return
    if (pipEnabled && glRunning(s)) {
      closePip()
      startPip(s)
    }
  }

  private fun facingName() = if (wantFront) "front" else "back"

  fun setZoom(target: Float, animate: Boolean) {
    val cam = camera() ?: return
    val range = cam.getZoomRange()
    val t = target.coerceIn(range.lower, range.upper)
    zoomAnimator?.cancel()
    if (!animate) {
      cam.setZoom(t)
      return
    }
    zoomAnimator = ValueAnimator.ofFloat(cam.getZoom(), t).apply {
      duration = 450
      interpolator = DecelerateInterpolator()
      addUpdateListener { cam.setZoom(it.animatedValue as Float) }
      start()
    }
  }

  fun setTorch(on: Boolean): Boolean {
    val cam = camera() ?: return false
    return try {
      if (on) cam.enableLantern() else cam.disableLantern()
      cam.isLanternEnabled()
    } catch (e: Exception) {
      emitError("Bu kamerada fener yok")
      false
    }
  }

  fun setExposure(level: Int) {
    exposure = level
    camera()?.setExposure(level)
  }

  fun setAutoFocus(enabled: Boolean) {
    val cam = camera() ?: return
    if (enabled) cam.enableAutoFocus() else cam.disableAutoFocus()
  }

  fun setMirror(on: Boolean) {
    mirror = on
    stream?.let { applyMirror(it) }
  }

  private fun applyMirror(s: GenericStream) {
    s.getGlInterface().setIsStreamHorizontalFlip(mirror)
    s.getGlInterface().setIsPreviewHorizontalFlip(mirror)
  }

  fun cameraInfo(): Map<String, Any?> {
    val cam = camera()
    val range = try {
      cam?.getZoomRange()
    } catch (_: Exception) {
      null
    }
    val zMin = range?.lower ?: 1f
    val zMax = maxOf(range?.upper ?: 1f, zMin)
    // Kamera yeni açılmışken getZoom() 0 dönebiliyor (masaüstü bir an zoom=0, zoomMin=1 gördü); aralığa sıkıştır.
    val zoom = (try { cam?.getZoom() } catch (_: Exception) { null } ?: 1f).coerceIn(zMin, zMax)
    return mapOf(
      "facing" to facingName(),
      "zoom" to zoom.toDouble(),
      "zoomMin" to zMin.toDouble(),
      "zoomMax" to zMax.toDouble(),
      "torch" to (try { cam?.isLanternEnabled() } catch (_: Exception) { null } ?: false),
      "dualSupported" to concurrentSupported(),
      "lens" to currentLens(),
      "width" to config.width,
      "height" to config.height,
      "streaming" to (stream?.isStreaming == true),
      "state" to state,
    )
  }

  private fun emitCameraInfo() = emit("onCameraInfo", cameraInfo())

  // ---------- Lens, beyaz dengesi, görüntü sabitleme ----------

  /**
   * Telefondaki kameralar. Çok kameralı (LOGICAL_MULTI_CAMERA, API 28+) telefonlarda mantıksal kameranın
   * fiziksel alt kameraları ayrıca "<mantıksal>/<fiziksel>" kimliğiyle ve physical=true ile listelenir.
   * Odak uzaklığı ya da sensör boyutu okunamayan kamera listelenmez (eşdeğer odak hesaplanamaz).
   */
  fun lenses(): List<Map<String, Any?>> {
    val cm = ctx?.getSystemService(Context.CAMERA_SERVICE) as? CameraManager ?: return emptyList()
    val out = ArrayList<Map<String, Any?>>()
    try {
      for (id in cm.cameraIdList) {
        val ch = try {
          cm.getCameraCharacteristics(id)
        } catch (_: Exception) {
          continue
        }
        lensEntry(id, ch, null, false)?.let { out.add(it) }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) continue
        val caps = ch.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES) ?: continue
        if (CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_LOGICAL_MULTI_CAMERA !in caps) continue
        for (pid in ch.physicalCameraIds) {
          val pch = try {
            cm.getCameraCharacteristics(pid)
          } catch (_: Exception) {
            continue
          }
          lensEntry("$id/$pid", pch, ch, true)?.let { out.add(it) }
        }
      }
    } catch (_: Exception) {
    }
    return out
  }

  private fun lensEntry(id: String, ch: CameraCharacteristics, parent: CameraCharacteristics?, physical: Boolean): Map<String, Any?>? {
    // Fiziksel alt kamera yönünü bildirmezse mantıksal kameranın yönü geçerlidir.
    val facing = when (ch.get(CameraCharacteristics.LENS_FACING) ?: parent?.get(CameraCharacteristics.LENS_FACING)) {
      CameraCharacteristics.LENS_FACING_FRONT -> "front"
      CameraCharacteristics.LENS_FACING_BACK -> "back"
      CameraCharacteristics.LENS_FACING_EXTERNAL -> "external"
      else -> return null
    }
    val focal = ch.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)?.firstOrNull()?.toDouble() ?: return null
    val size = ch.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE) ?: return null
    val diagonal = hypot(size.width.toDouble(), size.height.toDouble())
    if (focal <= 0.0 || diagonal <= 0.0) return null
    return mapOf(
      "id" to id,
      "facing" to facing,
      "focalMm" to focal,
      // 35 mm tam kare köşegeni 43,27 mm.
      "eqMm" to focal * 43.27 / diagonal,
      "physical" to physical,
    )
  }

  /**
   * Her (genişlik, yükseklik, fps) kipinin bu cihazda yayınlanıp yayınlanamayacağı, verilen sırayla. Koşulların hepsi:
   * arka kameranın SurfaceTexture çıkış boyutlarında var, o boyutun en küçük kare süresi fps'e izin veriyor, kameranın
   * AE aralıklarından birinin üst sınırı fps'e yetiyor, fps prepare()'in uyguladığı yön sınırını (fpsLimit) aşmıyor ve
   * bir donanım H.264 kodlayıcısı boyutu ve hızı kabul ediyor (RootEncoder önce donanım kodlayıcıyı seçer).
   * Okunamayan her şey "desteklenmiyor" sayılır. Her mod için nedeni logcat'e yazılır (etiket CamEngine).
   */
  fun supportedModes(modes: List<List<Int>>): List<Boolean> {
    val back = try {
      val cm = ctx?.getSystemService(Context.CAMERA_SERVICE) as? CameraManager
      cm?.let { m -> cameraIdFor(m, CameraCharacteristics.LENS_FACING_BACK)?.let { m.getCameraCharacteristics(it) } }
    } catch (_: Exception) {
      null
    }
    val map = back?.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
    val sizes = map?.getOutputSizes(SurfaceTexture::class.java)?.toList() ?: emptyList()
    val tops = back?.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.map { it.upper } ?: emptyList()
    // prepare() fps'i bu sınıra sıkıştırır; sunulan mod gerçekten o hızda çalışsın.
    val limit = fpsLimit(false)
    // Kodlayıcılar tek tek okunur: bozuk bir kodlayıcı listeyi boşaltmasın.
    val encoders = try {
      MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        .filter { it.isEncoder && !isSoftwareCodec(it) && it.supportedTypes.any { t -> t.equals(MediaFormat.MIMETYPE_VIDEO_AVC, true) } }
        .mapNotNull {
          try {
            it.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).videoCapabilities
          } catch (_: Exception) {
            null
          }
        }
    } catch (_: Exception) {
      emptyList()
    }
    return modes.map { m ->
      if (m.size < 3) return@map false
      val (w, h, fps) = m
      val size = sizes.firstOrNull { it.width == w && it.height == h }
      // Bu çıkış boyutunun en küçük kare süresi (ns); 0 ya da okunamadıysa boyuta özel sınır yok. 0,5 fps ölçüm payı.
      val minDuration = size?.let { try { map?.getOutputMinFrameDuration(SurfaceTexture::class.java, it) } catch (_: Exception) { null } } ?: 0L
      val why = when {
        size == null -> "arka kamera bu boyutu vermiyor"
        minDuration > 0 && fps > 1e9 / minDuration + 0.5 -> "boyutun en küçük kare süresi $minDuration ns (en çok ${(1e9 / minDuration).toInt()} fps)"
        tops.none { it >= fps } -> "AE aralığı yetmiyor (en yüksek ${tops.maxOrNull()})"
        limit != null && limit < fps -> "fpsLimit=$limit"
        encoders.none { encodes(it, w, h, fps) } -> "donanım H.264 kodlayıcısı reddetti (${encoders.size} kodlayıcı)"
        else -> null
      }
      android.util.Log.i("CamEngine", "mod ${w}x$h@$fps: ${why ?: "tamam"}")
      why == null
    }
  }

  /**
   * Kodlayıcı boyutu ve hızı kabul ediyor mu. Boyut kodlayıcının hizasına uymuyorsa (1080, 16 hizalı yükseklikte) bir üst
   * hizaya yuvarlanmış boyutla da sorulur (1088): yayın 1080'de kalır, bu yalnız yetenek denetimidir.
   */
  private fun encodes(c: MediaCodecInfo.VideoCapabilities, w: Int, h: Int, fps: Int): Boolean = try {
    val aw = maxOf(c.widthAlignment, 1)
    val ah = maxOf(c.heightAlignment, 1)
    val w2 = (w + aw - 1) / aw * aw
    val h2 = (h + ah - 1) / ah * ah
    c.areSizeAndRateSupported(w, h, fps.toDouble()) || ((w2 != w || h2 != h) && c.areSizeAndRateSupported(w2, h2, fps.toDouble()))
  } catch (_: Exception) {
    false
  }

  private fun isSoftwareCodec(i: MediaCodecInfo): Boolean =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) i.isSoftwareOnly else i.name.startsWith("OMX.google.") || i.name.startsWith("c2.android.")

  /** Yönün varsayılan kamerası (RootEncoder'ın o yön için açacağı kimlik). */
  private fun defaultLensId(front: Boolean): String {
    val cm = ctx?.getSystemService(Context.CAMERA_SERVICE) as? CameraManager
    val facing = if (front) CameraCharacteristics.LENS_FACING_FRONT else CameraCharacteristics.LENS_FACING_BACK
    return try {
      cm?.let { cameraIdFor(it, facing) }
    } catch (_: Exception) {
      null
    } ?: "0"
  }

  /** Şu an kullanılan (kamera kapalıysa açılışta kullanılacak) lens kimliği. */
  private fun currentLens(): String {
    wantLens?.let { return it }
    val cam = camera()
    if (cam != null && cam.isRunning()) return cam.getCurrentCameraId()
    return defaultLensId(wantFront)
  }

  /** Mantıksal kimliğin yönü ön mü? (external arka sayılır: RootEncoder yalnız ön/arka bilir.) */
  private fun isFrontCamera(logical: String): Boolean {
    val cm = ctx?.getSystemService(Context.CAMERA_SERVICE) as? CameraManager ?: return false
    return cm.getCameraCharacteristics(logical).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_FRONT
  }

  /**
   * Lensi seçer ve etkin lens kimliğini döndürür. Bilinmeyen kimlikte mevcut lens kalır.
   * Kamera kapalıysa seçim saklanır, kamera açılınca uygulanır. Kamera çalışıyorsa bu çağrıda EN FAZLA BİR yeniden
   * açma yapılır; kalan adımlar her açılış bitince onCameraOpened → restoreLens ile sırayla sürer
   * (RootEncoder, açılışı süren bir kamerayı kapatamaz). Dönen kimlik hedef lenstir.
   */
  fun setLens(id: String): String {
    val entry = lenses().firstOrNull { it["id"] == id } ?: return currentLens()
    val front = entry["facing"] == "front"
    wantLens = id
    lensSteps = 0
    lastLensChangeAt = SystemClock.uptimeMillis()
    val cam = camera()
    if (cam != null && cam.isRunning()) {
      stepLens(cam, id)
    } else {
      // Kapalı kaynakta switchCamera yalnız yönü çevirir, kamerayı açmaz (2.8.0); GL başlayınca doğru yön açılır.
      wantFront = front
      val facing = if (front) CameraHelper.Facing.FRONT else CameraHelper.Facing.BACK
      if (cam != null && cam.getCameraFacing() != facing) cam.switchCamera()
    }
    return currentLens()
  }

  private enum class LensStep { CLEAR_PHYSICAL, SWITCH_FACING, OPEN_LOGICAL, OPEN_PHYSICAL }

  /** Lens için sıradaki adım; null: kamera zaten hedefte. Sıra: fiziksel sil, yön, mantıksal kimlik, fiziksel kimlik. */
  private fun nextLensStep(cam: Camera2Source, lens: String): LensStep? {
    val logical = lens.substringBefore('/')
    val physical = lens.substringAfter('/', "").ifEmpty { null }
    val facing = if (isFrontCamera(logical)) CameraHelper.Facing.FRONT else CameraHelper.Facing.BACK
    val current = cam.getCurrentCameraId()
    return when {
      // Eski fiziksel kimlik yalnız mantıksal kamera aynı kalıp hedefte başka fiziksel kimlik varsa yerinde bırakılır
      // (openPhysicalCamera onu değiştirir); aksi halde yeni kamerada geçersiz olur.
      lensPhysical != null && (current != logical || physical == null) -> LensStep.CLEAR_PHYSICAL
      cam.getCameraFacing() != facing -> LensStep.SWITCH_FACING
      current != logical -> LensStep.OPEN_LOGICAL
      physical != null && physical != lensPhysical && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P -> LensStep.OPEN_PHYSICAL
      else -> null
    }
  }

  /**
   * Çalışan kamerada lensin sıradaki adımını uygular; her çağrıda en çok BİR yeniden açma başlatır.
   * true: yeniden açma başlatıldı (bitince onCameraOpened gelir). false: yapılacak adım yok ya da hata
   * (hatada seçim bırakılır).
   */
  private fun stepLens(cam: Camera2Source, lens: String): Boolean {
    return try {
      val step = nextLensStep(cam, lens) ?: return false
      lensSteps++
      zoomAnimator?.cancel()
      when (step) {
        LensStep.CLEAR_PHYSICAL -> clearPhysical(cam)
        LensStep.SWITCH_FACING -> {
          val before = cam.getCameraFacing()
          cam.switchCamera()
          wantFront = cam.getCameraFacing() == CameraHelper.Facing.FRONT
          // ikinci kamera karşı yönü açar; gerçek yön değiştiyse yeniden kurulur
          if (cam.getCameraFacing() != before) restartPip()
        }
        LensStep.OPEN_LOGICAL -> cam.openCameraId(lens.substringBefore('/'))
        LensStep.OPEN_PHYSICAL -> {
          val physical = lens.substringAfter('/')
          // kimlik önce kaydedilir: açma yarıda kalırsa geri dönüş yolu onu silebilsin
          lensPhysical = physical
          cam.openPhysicalCamera(physical)
        }
      }
      true
    } catch (e: Exception) {
      emitError("Lens açılamadı: ${e.message}")
      wantLens = null
      lensSteps = 0
      false
    }
  }

  /**
   * RootEncoder fiziksel kimliği kamera kapalıyken de hatırlar. Çalışırken kamerayı yeniden açar; kapalıyken
   * kimliği silip "önce hazırlanmalı" hatası fırlatır (2.8.0), o hata burada yutulur.
   */
  private fun clearPhysical(cam: Camera2Source) {
    lensPhysical = null
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) return
    try {
      cam.openPhysicalCamera(null)
    } catch (_: Exception) {
    }
  }

  /**
   * Kamera, seçili lensten farklı açıldıysa (adım adım geçiş sürüyor, GL durup yeniden başladı ya da kaynak yeniden
   * kuruldu) bir sonraki adımı uygular. true: yeni bir açılış başlatıldı. Adım sayısı sınırlıdır (en fazla 4 ayrı
   * adım var); sınır aşılırsa seçim bırakılır, böylece sonsuz yeniden açma olmaz.
   */
  private fun restoreLens(cam: Camera2Source): Boolean {
    val want = wantLens ?: return false
    if (camera() !== cam || !cam.isRunning()) return false
    val next = try {
      nextLensStep(cam, want)
    } catch (_: Exception) {
      null
    }
    if (next == null) {
      lensSteps = 0
      return false
    }
    if (lensSteps >= MAX_LENS_STEPS) {
      emitError("Lens ayarlanamadı")
      wantLens = null
      lensSteps = 0
      return false
    }
    return stepLens(cam, want)
  }

  /**
   * Lens değişiminden sonra kamera hata verdiyse yönün varsayılan kamerasına tek açılışla döner. wantLens
   * temizlendiğinden tekrarlayan hatada yeniden denenmez. Kamera kapalıyken yalnız seçim ve fiziksel kimlik silinir.
   */
  private fun fallbackToDefaultLens(cam: Camera2Source) {
    if (wantLens == null || camera() !== cam) return
    wantLens = null
    lensSteps = 0
    emitError("Lens açılamadı, varsayılan kameraya dönülüyor")
    val s = stream
    val restart = s != null && glRunning(s)
    try {
      if (restart) cam.stop()
      // Kayıtlı fiziksel kimlik yoksa silme çağrısı yapılmaz: kapalı kamerada eski kimliği yeniden açmayı dener.
      if (lensPhysical != null) clearPhysical(cam)
      if (restart) cam.surfaceTexture?.let { cam.start(it) }
    } catch (e: Exception) {
      emitError("Varsayılan kamera açılamadı: ${e.message}")
    }
    wantFront = cam.getCameraFacing() == CameraHelper.Facing.FRONT
  }

  /**
   * Kaybolan kamerayı (açılamadı, başka uygulama aldı, arka planda kesildi) lens seçimi yoksa BİR KEZ yeniden açar.
   * Önizleme yoksa denenmez (arka planda kamera açılamaz); önizleme yeniden bağlanınca attachPreview yeniden dener.
   * Çağıranlar openSeq'in değişmediğini denetler: arada başarılı bir açılış olduysa bu çağrı eskidir.
   */
  private fun reopenLostCamera(cam: Camera2Source, seq: Int) {
    if (openSeq != seq || !cameraLost || reopenTried || wantLens != null || preview == null || camera() !== cam) return
    // Yön ya da lens yeni değiştiyse yeni kamera hâlâ açılıyor olabilir; açılışı kesmemek için 2 sn dolana dek bekle.
    val wait = lastLensChangeAt + 2000 - SystemClock.uptimeMillis()
    if (wait > 0) {
      main.postDelayed({ reopenLostCamera(cam, seq) }, wait)
      return
    }
    val st = cam.surfaceTexture ?: return
    reopenTried = true
    try {
      cam.stop()
      cam.start(st)
    } catch (e: Exception) {
      emitError("Kamera yeniden açılamadı: ${e.message}")
    }
  }

  private val awbModes = mapOf(
    "auto" to CameraMetadata.CONTROL_AWB_MODE_AUTO,
    "daylight" to CameraMetadata.CONTROL_AWB_MODE_DAYLIGHT,
    "cloudy" to CameraMetadata.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT,
    "incandescent" to CameraMetadata.CONTROL_AWB_MODE_INCANDESCENT,
    "fluorescent" to CameraMetadata.CONTROL_AWB_MODE_FLUORESCENT,
  )

  /**
   * true: istek kameraya uygulandı. Kamera kapalıysa ya da kamera bu modu desteklemiyorsa false; mod yine de saklanır.
   * Kamera çalışıp istek reddedildiyse (yakalama oturumu henüz kurulmadı) kısa aralıkla yeniden denenir.
   */
  fun setWhiteBalance(mode: String): Boolean {
    if (mode !in awbModes) return false
    whiteBalance = mode
    val cam = camera()?.takeIf { it.isRunning() } ?: return false
    val ok = applyWhiteBalance(cam)
    if (!ok) scheduleReapply()
    return ok
  }

  private fun applyWhiteBalance(cam: Camera2Source): Boolean = try {
    cam.enableAutoWhiteBalance(awbModes.getValue(whiteBalance))
  } catch (_: Exception) {
    false
  }

  /** true: istek kameraya uygulandı. Açarken önce optik, olmazsa dijital sabitleme denenir; kapatırken ikisi de kapanır. */
  fun setStabilization(on: Boolean): Boolean {
    stabilization = on
    val cam = camera()?.takeIf { it.isRunning() } ?: return false
    val ok = applyStabilization(cam, on)
    if (!ok) scheduleReapply()
    return ok
  }

  private fun applyStabilization(cam: Camera2Source, on: Boolean): Boolean = try {
    if (on) {
      cam.enableOpticalVideoStabilization() || cam.enableVideoStabilization()
    } else {
      cam.disableOpticalVideoStabilization()
      cam.disableVideoStabilization()
      true
    }
  } catch (_: Exception) {
    false
  }

  /** Kamera yeniden açılınca Camera2 istekleri sıfırlanır; saklanan beyaz dengesi ve sabitleme geri uygulanır. */
  private fun scheduleReapply() {
    reapplyTries = 0
    main.removeCallbacks(reapplyTask)
    main.post(reapplyTask)
  }

  private fun reapplyCameraSettings() {
    val cam = camera()?.takeIf { it.isRunning() } ?: return
    val wbOk = whiteBalance == "auto" || applyWhiteBalance(cam)
    val stabOk = !stabilization || applyStabilization(cam, true)
    // Açılıştan hemen sonra yakalama oturumu henüz kurulmadığından RootEncoder isteği reddeder;
    // kısa aralıkla birkaç kez daha denenir. Kamera özelliği desteklemiyorsa da aynı sınırda biter.
    if (!(wbOk && stabOk) && ++reapplyTries < 10) main.postDelayed(reapplyTask, 200)
  }

  // ---------- Renk efektleri ----------

  fun setFilter(id: String, value: Float) {
    filterId = id
    filterValue = value.coerceIn(0f, 1f)
    val s = stream ?: return
    // SET: 0. sıradaki filtreyi değiştirir. GL kapalıysa zincir açılışta zaten güncel efekti kurar.
    if (glRunning(s)) s.getGlInterface().setFilter(makeFilter(filterId, filterValue))
  }

  private fun makeFilter(id: String, v: Float): BaseFilterRender = when (id) {
    "beauty" -> BeautyFilterRender()
    "bw" -> GreyScaleFilterRender()
    "sepia" -> SepiaFilterRender()
    "warm" -> TemperatureFilterRender().apply { setTemperature(0.5f + v * 0.5f) }
    "cool" -> TemperatureFilterRender().apply { setTemperature(0.5f - v * 0.5f) }
    "vivid" -> SaturationFilterRender().apply { setSaturation(v) }
    "contrast" -> ContrastFilterRender().apply { setContrast(1f + v) }
    "sharpen" -> SharpnessFilterRender()
    "blur" -> BlurFilterRender()
    "retro" -> Image70sFilterRender()
    "earlybird" -> EarlyBirdFilterRender()
    "lamoish" -> LamoishFilterRender()
    "cartoon" -> CartoonFilterRender()
    "pixel" -> PixelatedFilterRender().apply { setPixelated(0.005f + v * 0.045f) }
    "negative" -> NegativeFilterRender()
    "glitch" -> GlitchFilterRender()
    "analog" -> AnalogTVFilterRender()
    else -> NoFilterRender()
  }

  // ---------- Resim içinde resim: ön ve arka kamera aynı anda ----------

  fun concurrentSupported(): Boolean {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return false
    val cm = ctx?.getSystemService(Context.CAMERA_SERVICE) as? CameraManager ?: return false
    return try {
      val back = cameraIdFor(cm, CameraCharacteristics.LENS_FACING_BACK) ?: return false
      val front = cameraIdFor(cm, CameraCharacteristics.LENS_FACING_FRONT) ?: return false
      cm.concurrentCameraIds.any { it.contains(back) && it.contains(front) }
    } catch (_: Exception) {
      false
    }
  }

  private fun cameraIdFor(cm: CameraManager, lensFacing: Int): String? =
    cm.cameraIdList.firstOrNull { cm.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == lensFacing }

  fun setPip(enabled: Boolean, corner: String, size: Float) {
    pipCorner = corner
    pipSize = size.coerceIn(0.15f, 0.5f)
    if (!enabled) {
      pipEnabled = false
      closePip()
      return
    }
    val s = stream
    pipEnabled = true
    if (s == null || !glRunning(s)) return
    val f = pipFilter
    if (f != null) layoutPip(f) else startPip(s)
  }

  fun rotatePip(): Int {
    pipRotationExtra = (pipRotationExtra + 90) % 360
    pipFilter?.let { layoutPip(it) }
    return pipRotationExtra
  }

  fun relayout() {
    pipFilter?.let { layoutPip(it) }
  }

  private fun startPip(s: GenericStream) {
    val filter = SurfaceFilterRender { st -> openPipCamera(st) }
    pipFilter = filter
    layoutPip(filter)
    s.getGlInterface().addFilter(filter)
  }

  private fun openPipCamera(st: SurfaceTexture) {
    val c = ctx ?: return
    if (!pipEnabled) return
    val facing = if (wantFront) CameraHelper.Facing.BACK else CameraHelper.Facing.FRONT
    val cam = Camera2ApiManager(c)
    cam.setCameraCallbacks(object : CameraCallbacks {
      override fun onCameraChanged(facing: CameraHelper.Facing) {}
      override fun onCameraError(error: String) {
        main.post {
          emitError("İkinci kamera açılamadı. Bu telefon iki kamerayı aynı anda desteklemiyor olabilir.")
          pipEnabled = false
          closePip()
          emit("onCameraInfo", cameraInfo() + ("pip" to false))
        }
      }
      override fun onCameraOpened() {}
      override fun onCameraDisconnected() {}
    })
    try {
      st.setDefaultBufferSize(640, 480)
      cam.prepareCamera(st, 640, 480, 30, facing)
      cam.openCameraFacing(facing)
      pipCamera = cam
    } catch (e: Exception) {
      emitError("İkinci kamera açılamadı: ${e.message}")
      pipEnabled = false
      closePip()
    }
  }

  private fun closePipCamera() {
    pipCamera?.let {
      try {
        it.closeCamera()
      } catch (_: Exception) {
      }
    }
    pipCamera = null
  }

  private fun closePip() {
    closePipCamera()
    val f = pipFilter
    val s = stream
    if (f != null && s != null && glRunning(s)) s.getGlInterface().removeFilter(f)
    pipFilter = null
  }

  /** Sensör yönü ve ekran dönüşüne göre ikinci kamera görüntüsünü dik tutacak açı. */
  private fun pipBaseRotation(): Int {
    val c = ctx ?: return 0
    val cm = c.getSystemService(Context.CAMERA_SERVICE) as? CameraManager ?: return 0
    val front = !wantFront
    val id = cameraIdFor(cm, if (front) CameraCharacteristics.LENS_FACING_FRONT else CameraCharacteristics.LENS_FACING_BACK) ?: return 0
    val sensor = cm.getCameraCharacteristics(id).get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
    @Suppress("DEPRECATION")
    val rotation = (c.getSystemService(Context.WINDOW_SERVICE) as? WindowManager)?.defaultDisplay?.rotation ?: Surface.ROTATION_0
    val display = when (rotation) {
      Surface.ROTATION_90 -> 90
      Surface.ROTATION_180 -> 180
      Surface.ROTATION_270 -> 270
      else -> 0
    }
    return if (front) (sensor + display) % 360 else (sensor - display + 360) % 360
  }

  private fun layoutPip(f: SurfaceFilterRender) {
    val streamW = (if (config.portrait) config.height else config.width).toFloat()
    val streamH = (if (config.portrait) config.width else config.height).toFloat()
    val rot = (pipBaseRotation() + pipRotationExtra) % 360
    val rotated = rot % 180 != 0
    // Sensör görüntüsü 4:3 yatay; 90° dönünce 3:4 olur.
    val visual = if (rotated) 3f / 4f else 4f / 3f
    val pct = pipSize * 100f
    val w: Float
    val h: Float
    if (!rotated) {
      w = pct
      h = pct * streamW / streamH / visual
    } else {
      h = pct
      w = pct * streamW / (visual * streamH)
    }
    f.setScale(w, h)
    f.setRotation(rot)
    f.setPosition(
      when (pipCorner) {
        "TOP_LEFT" -> TranslateTo.TOP_LEFT
        "TOP_RIGHT" -> TranslateTo.TOP_RIGHT
        "BOTTOM_LEFT" -> TranslateTo.BOTTOM_LEFT
        else -> TranslateTo.BOTTOM_RIGHT
      },
    )
  }
}
