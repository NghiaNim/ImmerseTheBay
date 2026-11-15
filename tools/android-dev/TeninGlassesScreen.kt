package com.immersethebay.phone

import android.app.Activity
import android.app.Presentation
import android.content.Context
import android.graphics.Color
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Display
import android.view.Gravity
import android.view.WindowManager
import android.widget.FrameLayout
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
            repeat(2) { eyes.addView(eye(), LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f)) }
            setContentView(eyes)
        }

        private fun eye() = FrameLayout(context).apply {
            addView(LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER_HORIZONTAL
                addView(TextView(context).apply {
                    text = "Tenin"
                    textSize = 22f
                    setTextColor(0xFF9482DE.toInt())
                    typeface = Tenin.font(context, 500)
                    gravity = Gravity.CENTER
                })
                addView(TextView(context).apply {
                    text = "Continue on your phone"
                    textSize = 14f
                    setTextColor(0xFFB8B4C8.toInt())
                    gravity = Gravity.CENTER
                })
            }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
        }
    }
}
