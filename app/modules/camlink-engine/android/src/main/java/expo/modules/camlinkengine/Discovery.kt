package expo.modules.camlinkengine

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketTimeoutException

/**
 * Yerel ağdaki StudioHub ve CamLink OBS eklentisinin duyurularını dinler (UDP 4467).
 * Android, Wi-Fi'de yayın paketlerini pil için süzebilir; MulticastLock bu süzmeyi geçici olarak kapatır.
 */
object Discovery {
  private const val TAG = "CamLinkDiscovery"
  private const val PORT = 4467

  @Volatile private var thread: Thread? = null
  @Volatile private var socket: DatagramSocket? = null
  private var lock: WifiManager.MulticastLock? = null

  fun start(context: Context, emit: (String, Map<String, Any?>) -> Unit) {
    if (thread != null) return
    val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    lock = wifi?.createMulticastLock("camlink-discovery")?.apply {
      setReferenceCounted(false)
      try { acquire() } catch (e: Exception) { Log.w(TAG, "MulticastLock alınamadı", e) }
    }
    val t = Thread({
      try {
        val s = DatagramSocket(null).apply {
          reuseAddress = true
          broadcast = true
          soTimeout = 1000
          bind(InetSocketAddress(PORT))
        }
        socket = s
        val buf = ByteArray(8192)
        while (!Thread.currentThread().isInterrupted) {
          val packet = DatagramPacket(buf, buf.size)
          try {
            s.receive(packet)
          } catch (_: SocketTimeoutException) {
            continue
          }
          val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
          if (!text.startsWith("{")) continue
          emit("onBeacon", mapOf("ip" to packet.address.hostAddress, "data" to text))
        }
      } catch (e: Exception) {
        if (thread != null) {
          Log.w(TAG, "Dinleme durdu", e)
          emit("onError", mapOf("message" to "Bilgisayar araması başlatılamadı: ${e.message}"))
        }
      } finally {
        socket?.close()
        socket = null
      }
    }, "camlink-discovery")
    thread = t
    t.isDaemon = true
    t.start()
  }

  fun stop() {
    val t = thread ?: return
    thread = null
    t.interrupt()
    socket?.close()
    try { lock?.release() } catch (_: Exception) {}
    lock = null
  }
}
