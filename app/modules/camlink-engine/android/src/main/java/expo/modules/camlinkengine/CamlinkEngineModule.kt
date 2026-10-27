package expo.modules.camlinkengine

import expo.modules.kotlin.functions.Queues
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import expo.modules.kotlin.records.Field
import expo.modules.kotlin.records.Record

class StreamConfigRecord : Record {
  @Field var width: Int = 1280
  @Field var height: Int = 720
  @Field var fps: Int = 30
  @Field var bitrate: Int = 4_000_000
  @Field var audio: Boolean = true
  @Field var portrait: Boolean = false
  @Field var sensorOrientation: Boolean = false

  fun toConfig() = EngineConfig(width, height, fps, bitrate, audio, portrait, sensorOrientation)
}

class CamlinkEngineModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("CamlinkEngine")

    Events("onStatus", "onStats", "onCameraInfo", "onError", "onBeacon", "onRemoteStop")

    OnCreate {
      val context = appContext.reactContext ?: return@OnCreate
      CamEngine.init(context) { name, body -> sendEvent(name, body) }
    }

    OnDestroy {
      Discovery.stop()
      CamEngine.shutdown()
    }

    // Bilgisayar araması: ayrı iş parçacığında çalışır, sonuçlar onBeacon olayıyla gelir.
    Function("startDiscovery") {
      val context = appContext.reactContext ?: return@Function false
      Discovery.start(context) { name, body -> sendEvent(name, body) }
      true
    }

    Function("stopDiscovery") {
      Discovery.stop()
    }

    AsyncFunction("configure") { cfg: StreamConfigRecord ->
      CamEngine.configure(cfg.toConfig())
    }.runOnQueue(Queues.MAIN)

    // label: bildirimde gösterilen hedef (ör. bilgisayar adı); null olabilir.
    AsyncFunction("startStream") { url: String, label: String? ->
      CamEngine.start(url, label)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("stopStream") {
      CamEngine.stop()
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("switchCamera") {
      CamEngine.switchCamera()
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setZoom") { level: Double, animate: Boolean ->
      CamEngine.setZoom(level.toFloat(), animate)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setTorch") { on: Boolean ->
      CamEngine.setTorch(on)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setExposure") { level: Int ->
      CamEngine.setExposure(level)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setAutoFocus") { enabled: Boolean ->
      CamEngine.setAutoFocus(enabled)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setMirror") { on: Boolean ->
      CamEngine.setMirror(on)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setFilter") { id: String, value: Double ->
      CamEngine.setFilter(id, value.toFloat())
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setPip") { enabled: Boolean, corner: String, size: Double ->
      CamEngine.setPip(enabled, corner, size.toFloat())
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("rotatePip") {
      CamEngine.rotatePip()
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("relayout") {
      CamEngine.relayout()
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setBitrate") { bps: Int ->
      CamEngine.setBitrate(bps)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setMicMuted") { muted: Boolean ->
      CamEngine.setMicMuted(muted)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("getLenses") {
      CamEngine.lenses()
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("supportedModes") { modes: List<List<Int>> ->
      CamEngine.supportedModes(modes)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setLens") { id: String ->
      CamEngine.setLens(id)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setWhiteBalance") { mode: String ->
      CamEngine.setWhiteBalance(mode)
    }.runOnQueue(Queues.MAIN)

    AsyncFunction("setStabilization") { on: Boolean ->
      CamEngine.setStabilization(on)
    }.runOnQueue(Queues.MAIN)

    View(CamlinkPreviewView::class) {}
  }
}
