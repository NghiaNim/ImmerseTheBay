package com.immersethebay.phone

import android.app.Activity
import android.app.Presentation
import android.content.Context
import android.graphics.Color
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.Display
import android.view.Gravity
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/**
 * While the phone UI is in front, Samsung's Mirrored mode would stretch the phone screen across both eyes
 * of the glasses. Instead, draw our own glasses screen on the presentation display: black (see-through on
 * XREAL glasses) with the same short message in each eye. Hidden whenever the phone UI leaves the front,
 * so the XR lesson owns the glasses.
 */
object TeninGlassesScreen {
    private var presentation: Presentation? = null
    private var host: Activity? = null
    private val main = Handler(Looper.getMainLooper())

    private val listener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) { host?.let(::show) }
        override fun onDisplayRemoved(displayId: Int) { if (presentation?.display?.displayId == displayId) dismiss() }
        override fun onDisplayChanged(displayId: Int) {}
    }

    /** Call from the phone activity's onResume. */
    fun show(activity: Activity) {
        val displays = activity.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager
        if (host == null) displays.registerDisplayListener(listener, main)
        host = activity
        val glasses = displays.getDisplays(DisplayManager.DISPLAY_CATEGORY_PRESENTATION)
            .firstOrNull { it.displayId != Display.DEFAULT_DISPLAY } ?: return
        if (presentation?.display?.displayId == glasses.displayId && presentation?.isShowing == true) return
        dismiss()
        presentation = GlassesPresentation(activity, glasses).also {
            try { it.show() } catch (_: WindowManager.InvalidDisplayException) { presentation = null }
        }
    }

    /** Call from the phone activity's onPause: the XR view must own the glasses. */
    fun hide(activity: Activity) {
        if (host !== activity) return
        (activity.getSystemService(Context.DISPLAY_SERVICE) as DisplayManager).unregisterDisplayListener(listener)
        host = null
        dismiss()
    }

    private fun dismiss() {
        presentation?.let { runCatching { it.dismiss() } }
        presentation = null
    }

    private class GlassesPresentation(context: Context, display: Display) : Presentation(context, display) {
        override fun onCreate(savedInstanceState: Bundle?) {
            super.onCreate(savedInstanceState)
            // Side-by-side stereo: the same content in the left and right halves reads as one image.
            val eyes = LinearLayout(context).apply { orientation = LinearLayout.HORIZONTAL; setBackgroundColor(Color.BLACK) }
            // Size before the first layout. The presentation stays fixed while the phone UI changes.
            val mode = display.mode
            val eyeHeight = minOf(mode.physicalHeight.toFloat(), mode.physicalWidth * 9f / 32f)
            repeat(2) { eyes.addView(eye(eyeHeight), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f)) }
            setContentView(eyes)
        }

        private fun eye(eyeHeight: Float): FrameLayout {
            val brandColor = 0xFFE1D6FF.toInt()
            val title = TextView(context).apply {
                text = "Tenin"
                setTextSize(TypedValue.COMPLEX_UNIT_PX, eyeHeight * 0.092f)
                setTextColor(brandColor)
                typeface = Tenin.font(context, 500)
                gravity = Gravity.CENTER
            }
            val brand = LinearLayout(context).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                val iconSize = (eyeHeight * 0.069f).toInt()
                addView(ImageView(context).apply {
                    setImageDrawable(TeninIconDrawable(TeninIcon.USER, brandColor, 1.7f))
                }, LinearLayout.LayoutParams(iconSize, iconSize).apply { marginEnd = (eyeHeight * 0.02f).toInt() })
                addView(title)
            }
            val guidance = TextView(context).apply {
                text = "Continue on your phone"
                setTextSize(TypedValue.COMPLEX_UNIT_PX, eyeHeight * 0.046f)
                setTextColor(0xFFE3E0EB.toInt())
                gravity = Gravity.CENTER
            }
            val content = LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER_HORIZONTAL
                addView(brand)
                addView(guidance)
            }
            return FrameLayout(context).apply {
                addView(content, FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
            }
        }
    }
}
