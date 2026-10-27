package expo.modules.camlinkengine

import android.content.Context
import android.view.SurfaceHolder
import android.view.SurfaceView
import expo.modules.kotlin.AppContext
import expo.modules.kotlin.views.ExpoView

/** Kamera önizlemesi. Yüzey hazır olunca motoru bağlar, kaybolunca bırakır. */
class CamlinkPreviewView(context: Context, appContext: AppContext) : ExpoView(context, appContext), SurfaceHolder.Callback {
  override val shouldUseAndroidLayout = true

  private val surfaceView = SurfaceView(context).also {
    it.layoutParams = LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT)
    it.holder.addCallback(this)
    addView(it)
  }

  override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
    super.onSizeChanged(w, h, oldw, oldh)
    post { measureAndLayout() }
  }

  override fun surfaceCreated(holder: SurfaceHolder) {}

  override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
    CamEngine.attachPreview(surfaceView, width, height)
  }

  override fun surfaceDestroyed(holder: SurfaceHolder) {
    CamEngine.detachPreview(surfaceView)
  }
}
