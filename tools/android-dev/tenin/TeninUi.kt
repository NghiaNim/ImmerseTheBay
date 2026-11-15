package com.immersethebay.phone

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PixelFormat
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import kotlin.math.roundToInt

/** Colours, type and building blocks for the Tenin phone screens (see Tenin_phone-hifi mockups). */
object Tenin {
    const val BG = 0xFFFBFAFF.toInt()
    const val INK = 0xFF1E1B2E.toInt()
    const val MUTED = 0xFF5E5A72.toInt()
    const val FAINT = 0xFF8D89A0.toInt()
    const val PRIMARY = 0xFF7A62D6.toInt()
    const val PRIMARY_DARK = 0xFF4B3A9B.toInt()
    const val PRIMARY_SOFT = 0xFF9482DE.toInt()
    const val LAVENDER = 0xFFF0ECFA.toInt()
    const val TINT = 0xFFF8F6FE.toInt()
    const val LINE = 0xFFE4E0F2.toInt()
    const val WHITE = 0xFFFFFFFF.toInt()
    const val GREEN = 0xFF1F7A54.toInt()
    const val GREEN_BG = 0xFFE2F3EA.toInt()
    const val GREEN_LINE = 0xFFBFE3CF.toInt()
    const val YELLOW = 0xFFFFE49C.toInt()
    const val YELLOW_BG = 0xFFFFF8E3.toInt()
    const val YELLOW_LINE = 0xFFF5E3A8.toInt()
    const val AMBER = 0xFFA36A00.toInt()
    const val BROWN = 0xFF4A3A00.toInt()

    /** Ratio of this screen's width to the 390-point mockups, kept within a sensible range. */
    fun scale(context: Context): Float = context.resources.displayMetrics.let { (it.widthPixels / it.density / 390f).coerceIn(0.85f, 1.15f) }

    private val fonts = HashMap<Int, Typeface>()

    /** EB Garamond (bundled under assets/fonts, SIL OFL) with the platform serif as fallback. */
    fun font(context: Context, weight: Int = 400, italic: Boolean = false): Typeface =
        fonts.getOrPut(weight * 2 + if (italic) 1 else 0) {
            try {
                val file = if (italic) "fonts/EBGaramond-Italic.ttf" else "fonts/EBGaramond.ttf"
                Typeface.Builder(context.assets, file).setFontVariationSettings("'wght' $weight").build()
                    ?: throw IllegalStateException("font")
            } catch (_: Exception) {
                Typeface.create(Typeface.SERIF, when {
                    weight >= 600 && italic -> Typeface.BOLD_ITALIC
                    weight >= 600 -> Typeface.BOLD
                    italic -> Typeface.ITALIC
                    else -> Typeface.NORMAL
                })
            }
        }
}

enum class TeninIcon {
    CAMERA, PLAY, CHEVRON, BACK, CLOSE, BOOK, HISTORY, GLASSES, MIC, SPEAKER, IMAGE, SHIELD, CHECK, CHECK_CIRCLE,
    SPARKLE, ALERT, EYE, PAUSE, STOP, SEARCH, RETRY, UTENSILS, CUP, CART, STETHOSCOPE, USER, TIMER, HOME
}

/** Density-independent pixels, scaled so the 390-wide mockups fit narrower and wider phones alike. */
fun Context.dp(value: Float): Int = (resources.displayMetrics.density * value * Tenin.scale(this)).roundToInt()
fun Context.dp(value: Int): Int = dp(value.toFloat())

/** Line icon drawn on a 24-unit grid, so no drawable resources are needed. */
class TeninIconDrawable(private val icon: TeninIcon, color: Int, private val stroke: Float = 1.8f) : Drawable() {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
        this.color = color
    }
    private val rect = RectF()

    override fun draw(canvas: Canvas) {
        val size = minOf(bounds.width(), bounds.height()).toFloat()
        canvas.save()
        canvas.translate(bounds.left + (bounds.width() - size) / 2f, bounds.top + (bounds.height() - size) / 2f)
        canvas.scale(size / 24f, size / 24f)
        paint.strokeWidth = stroke
        fun line(x1: Float, y1: Float, x2: Float, y2: Float) = canvas.drawLine(x1, y1, x2, y2, paint)
        fun circle(x: Float, y: Float, r: Float) = canvas.drawCircle(x, y, r, paint)
        fun box(l: Float, t: Float, r: Float, b: Float, radius: Float) { rect.set(l, t, r, b); canvas.drawRoundRect(rect, radius, radius, paint) }
        fun arc(l: Float, t: Float, r: Float, b: Float, start: Float, sweep: Float) { rect.set(l, t, r, b); canvas.drawArc(rect, start, sweep, false, paint) }
        fun path(build: Path.() -> Unit) = canvas.drawPath(Path().apply(build), paint)
        when (icon) {
            TeninIcon.CAMERA -> {
                box(2.5f, 6.5f, 21.5f, 19.5f, 2.5f)
                path { moveTo(8.5f, 6.5f); lineTo(10f, 4f); lineTo(14f, 4f); lineTo(15.5f, 6.5f) }
                circle(12f, 13f, 3.2f)
            }
            TeninIcon.PLAY -> {
                circle(12f, 12f, 9.5f)
                path { moveTo(10f, 8.3f); lineTo(16f, 12f); lineTo(10f, 15.7f); close() }
            }
            TeninIcon.CHEVRON -> path { moveTo(9f, 6f); lineTo(15f, 12f); lineTo(9f, 18f) }
            TeninIcon.BACK -> path { moveTo(15f, 6f); lineTo(9f, 12f); lineTo(15f, 18f) }
            TeninIcon.CLOSE -> { line(6f, 6f, 18f, 18f); line(18f, 6f, 6f, 18f) }
            TeninIcon.BOOK -> {
                path {
                    moveTo(12f, 6.5f); cubicTo(10f, 5f, 7f, 4.5f, 3f, 4.5f); lineTo(3f, 18.5f)
                    cubicTo(7f, 18.5f, 10f, 19f, 12f, 20.5f); cubicTo(14f, 19f, 17f, 18.5f, 21f, 18.5f)
                    lineTo(21f, 4.5f); cubicTo(17f, 4.5f, 14f, 5f, 12f, 6.5f); close()
                }
                line(12f, 6.5f, 12f, 20.5f)
            }
            TeninIcon.HISTORY, TeninIcon.RETRY -> {
                arc(3f, 3f, 21f, 21f, -150f, 300f)
                path { moveTo(3.2f, 3.6f); lineTo(3.2f, 8.2f); lineTo(7.8f, 8.2f) }
                if (icon == TeninIcon.HISTORY) path { moveTo(12f, 7.5f); lineTo(12f, 12f); lineTo(15f, 14f) }
            }
            TeninIcon.GLASSES -> {
                circle(6.5f, 15f, 4f); circle(17.5f, 15f, 4f)
                path { moveTo(10.5f, 15f); quadTo(12f, 13.2f, 13.5f, 15f) }
                path { moveTo(2.6f, 14f); lineTo(5.4f, 5.6f); quadTo(6f, 4.1f, 7.7f, 4.6f) }
                path { moveTo(21.4f, 14f); lineTo(18.6f, 5.6f); quadTo(18f, 4.1f, 16.3f, 4.6f) }
            }
            TeninIcon.MIC -> {
                box(9f, 2.5f, 15f, 14f, 3f)
                path { moveTo(5.5f, 11f); quadTo(5.5f, 17.5f, 12f, 17.5f); quadTo(18.5f, 17.5f, 18.5f, 11f) }
                line(12f, 17.5f, 12f, 21.5f); line(9f, 21.5f, 15f, 21.5f)
            }
            TeninIcon.SPEAKER -> {
                path { moveTo(3.5f, 9.5f); lineTo(7f, 9.5f); lineTo(11.5f, 5.5f); lineTo(11.5f, 18.5f); lineTo(7f, 14.5f); lineTo(3.5f, 14.5f); close() }
                arc(10.5f, 8.5f, 16.5f, 15.5f, -55f, 110f)
                arc(8.5f, 5f, 20.5f, 19f, -50f, 100f)
            }
            TeninIcon.IMAGE -> {
                box(3f, 3f, 21f, 21f, 2.5f); circle(8.5f, 8.5f, 1.7f)
                path { moveTo(21f, 15f); lineTo(16f, 10f); lineTo(5f, 21f) }
            }
            TeninIcon.SHIELD -> {
                path { moveTo(12f, 2.5f); lineTo(20f, 5.5f); lineTo(20f, 11.5f); quadTo(20f, 18f, 12f, 21.5f); quadTo(4f, 18f, 4f, 11.5f); lineTo(4f, 5.5f); close() }
                path { moveTo(8.5f, 12f); lineTo(11f, 14.5f); lineTo(15.5f, 9.5f) }
            }
            TeninIcon.CHECK -> path { moveTo(5f, 12.5f); lineTo(10f, 17.5f); lineTo(19f, 7f) }
            TeninIcon.CHECK_CIRCLE -> {
                circle(12f, 12f, 9.5f)
                path { moveTo(8f, 12.3f); lineTo(11f, 15.2f); lineTo(16f, 9.5f) }
            }
            TeninIcon.SPARKLE -> path {
                moveTo(12f, 3f); quadTo(12.8f, 10.2f, 21f, 12f); quadTo(12.8f, 13.8f, 12f, 21f)
                quadTo(11.2f, 13.8f, 3f, 12f); quadTo(11.2f, 10.2f, 12f, 3f); close()
            }
            TeninIcon.ALERT -> {
                path { moveTo(12f, 3.5f); lineTo(21.5f, 20f); lineTo(2.5f, 20f); close() }
                line(12f, 10f, 12f, 14.5f); circle(12f, 17.2f, 0.3f)
            }
            TeninIcon.EYE -> {
                path { moveTo(2f, 12f); quadTo(12f, 2.5f, 22f, 12f); quadTo(12f, 21.5f, 2f, 12f); close() }
                circle(12f, 12f, 3f)
            }
            TeninIcon.PAUSE -> { box(6.5f, 4f, 10f, 20f, 1.2f); box(14f, 4f, 17.5f, 20f, 1.2f) }
            TeninIcon.STOP -> box(5f, 5f, 19f, 19f, 2.5f)
            TeninIcon.SEARCH -> { circle(11f, 11f, 7f); line(16.2f, 16.2f, 21f, 21f) }
            TeninIcon.UTENSILS -> {
                line(5f, 3f, 5f, 9f); line(7.5f, 3f, 7.5f, 21f); line(10f, 3f, 10f, 9f)
                path { moveTo(5f, 9f); quadTo(5f, 12f, 7.5f, 12f); quadTo(10f, 12f, 10f, 9f) }
                path { moveTo(17.5f, 21f); lineTo(17.5f, 3f); quadTo(13.5f, 4.5f, 13.5f, 10f); lineTo(13.5f, 13.5f); lineTo(17.5f, 13.5f) }
            }
            TeninIcon.CUP -> {
                path { moveTo(6f, 4f); lineTo(18f, 4f); lineTo(16.5f, 20f); lineTo(7.5f, 20f); close() }
                line(6.7f, 11f, 17.3f, 11f)
            }
            TeninIcon.CART -> {
                path { moveTo(2.5f, 3.5f); lineTo(5.5f, 3.5f); lineTo(8f, 15.5f); lineTo(19f, 15.5f); lineTo(21f, 7f); lineTo(6.3f, 7f) }
                circle(9f, 19.5f, 1.2f); circle(18f, 19.5f, 1.2f)
            }
            TeninIcon.STETHOSCOPE -> {
                path { moveTo(6f, 3f); lineTo(6f, 9f); quadTo(6f, 14f, 10f, 14f); quadTo(14f, 14f, 14f, 9f); lineTo(14f, 3f) }
                path { moveTo(10f, 14f); lineTo(10f, 16f); quadTo(10f, 21f, 14.5f, 21f); quadTo(19f, 21f, 19f, 16.5f); lineTo(19f, 15f) }
                circle(19f, 12.6f, 2.3f)
            }
            TeninIcon.USER -> {
                circle(12f, 8f, 4.2f)
                path { moveTo(4f, 17f); quadTo(12f, 23.5f, 20f, 17f) }
            }
            TeninIcon.TIMER -> { circle(12f, 13.5f, 7.5f); line(10f, 2.5f, 14f, 2.5f); line(12f, 13.5f, 14.5f, 10.5f) }
            TeninIcon.HOME -> {
                path { moveTo(3.5f, 11f); lineTo(12f, 3.5f); lineTo(20.5f, 11f) }
                path { moveTo(5.5f, 9.5f); lineTo(5.5f, 20.5f); lineTo(18.5f, 20.5f); lineTo(18.5f, 9.5f) }
            }
        }
        canvas.restore()
    }

    override fun setAlpha(alpha: Int) { paint.alpha = alpha }
    override fun setColorFilter(filter: ColorFilter?) { paint.colorFilter = filter }
    @Deprecated("Required by Drawable") override fun getOpacity() = PixelFormat.TRANSLUCENT
}

fun rounded(fill: Int, radius: Float, strokeColor: Int = 0, strokeWidth: Int = 0, dash: Float = 0f) = GradientDrawable().apply {
    setColor(fill)
    cornerRadius = radius
    if (strokeWidth > 0) setStroke(strokeWidth, strokeColor, dash, dash * 0.6f)
}

/** Builders shared by every Tenin screen. Sizes follow the mockups at half their pixel dimensions. */
class TeninUi(val context: Context) {
    private fun dp(value: Float) = context.dp(value)
    val gutter get() = dp(20f)

    fun text(value: CharSequence, size: Float, color: Int = Tenin.INK, weight: Int = 400, italic: Boolean = false) = TextView(context).apply {
        text = value
        setTextSize(TypedValue.COMPLEX_UNIT_SP, size * Tenin.scale(context))
        setTextColor(color)
        typeface = Tenin.font(context, weight, italic)
        includeFontPadding = false
        setLineSpacing(0f, 1.12f)
    }

    /** Flexible gap in a row. A bare View would claim all spare height inside a filled scroll view. */
    fun gap() = View(context).apply { layoutParams = LinearLayout.LayoutParams(0, 1, 1f) }

    fun title(value: String) = text(value, 36f).apply { setLineSpacing(0f, 0.98f) }
    fun body(value: String, color: Int = Tenin.MUTED) = text(value, 18f, color)
    fun eyebrow(value: String, color: Int = Tenin.MUTED) = text(value.uppercase(), 13f, color, 600).apply { letterSpacing = 0.09f }

    fun icon(kind: TeninIcon, size: Float, color: Int, stroke: Float = 1.8f) = ImageView(context).apply {
        setImageDrawable(TeninIconDrawable(kind, color, stroke))
        layoutParams = LinearLayout.LayoutParams(dp(size), dp(size))
    }

    /** Rounded square holding an icon, as on lesson and scenario cards. */
    fun tile(kind: TeninIcon, size: Float = 48f, fill: Int = Tenin.LAVENDER, color: Int = Tenin.PRIMARY, radius: Float = 14f) = FrameLayout(context).apply {
        background = rounded(fill, dp(radius).toFloat())
        layoutParams = LinearLayout.LayoutParams(dp(size), dp(size))
        addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(kind, color)) },
            FrameLayout.LayoutParams(dp(size * 0.5f), dp(size * 0.5f), Gravity.CENTER))
    }

    fun pill(value: String, color: Int = Tenin.PRIMARY_DARK, fill: Int = 0xFFF1EEFA.toInt(), size: Float = 14f, weight: Int = 600) = text(value, size, color, weight).apply {
        background = rounded(fill, dp(40f).toFloat())
        setPadding(dp(12f), dp(6f), dp(12f), dp(7f))
        maxLines = 1
    }

    fun column(vararg children: View) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        children.forEach { addView(it) }
    }

    fun row(vararg children: View, gravity: Int = Gravity.CENTER_VERTICAL) = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        this.gravity = gravity
        isBaselineAligned = false
        children.forEach { addView(it) }
    }

    fun <T : View> T.margins(left: Float = 0f, top: Float = 0f, right: Float = 0f, bottom: Float = 0f): T {
        val params = (layoutParams as? ViewGroup.MarginLayoutParams)
            ?: LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        params.setMargins(dp(left), dp(top), dp(right), dp(bottom))
        layoutParams = params
        return this
    }

    fun <T : View> T.fillWidth(): T {
        val old = layoutParams as? ViewGroup.MarginLayoutParams
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, old?.height ?: ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            if (old != null) setMargins(old.leftMargin, old.topMargin, old.rightMargin, old.bottomMargin)
        }
        return this
    }

    fun <T : View> T.weighted(): T {
        val old = layoutParams as? ViewGroup.MarginLayoutParams
        layoutParams = LinearLayout.LayoutParams(0, old?.height ?: ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
            if (old != null) setMargins(old.leftMargin, old.topMargin, old.rightMargin, old.bottomMargin)
        }
        return this
    }

    fun <T : View> T.onTap(action: (() -> Unit)?): T {
        if (action == null) return this
        isClickable = true
        isFocusable = true
        foreground = RippleDrawable(ColorStateList.valueOf(0x227A62D6), null, background?.constantState?.newDrawable())
        setOnClickListener { action() }
        return this
    }

    /** White card with a hairline border; `selected` draws the purple outline used for the active choice. */
    fun card(vararg children: View, selected: Boolean = false, fill: Int = Tenin.WHITE, line: Int = Tenin.LINE, padding: Float = 20f, radius: Float = 24f, dash: Boolean = false) = column(*children).apply {
        background = rounded(if (selected) Tenin.TINT else fill, dp(radius).toFloat(), if (selected) Tenin.PRIMARY else line,
            dp(if (selected) 1.5f else 1f), if (dash) dp(5f).toFloat() else 0f)
        setPadding(dp(padding), dp(padding), dp(padding), dp(padding))
        if (!dash && fill == Tenin.WHITE) {
            elevation = dp(if (selected) 6f else 2f).toFloat()
            if (android.os.Build.VERSION.SDK_INT >= 28) { outlineSpotShadowColor = Tenin.PRIMARY; outlineAmbientShadowColor = Tenin.PRIMARY }
        }
    }.fillWidth()

    fun divider() = View(context).apply {
        setBackgroundColor(Tenin.LINE)
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, maxOf(1, dp(1f)))
    }

    private fun button(label: String, kind: TeninIcon?, fill: Int, color: Int, line: Int, action: (() -> Unit)?) = row(gravity = Gravity.CENTER).apply {
        background = rounded(fill, dp(18f).toFloat(), line, if (line != 0) dp(1f) else 0)
        minimumHeight = dp(54f)
        setPadding(dp(16f), dp(8f), dp(16f), dp(8f))
        if (kind != null) addView(icon(kind, 22f, color, 2f).margins(right = 10f))
        addView(text(label, 19f, color, 600).apply { maxLines = 1; ellipsize = TextUtils.TruncateAt.END })
        if (line == 0 && fill == Tenin.PRIMARY) {
            elevation = dp(6f).toFloat()
            if (android.os.Build.VERSION.SDK_INT >= 28) { outlineSpotShadowColor = Tenin.PRIMARY; outlineAmbientShadowColor = Tenin.PRIMARY }
        }
    }.fillWidth().onTap(action)

    fun primaryButton(label: String, kind: TeninIcon? = null, action: (() -> Unit)?) = button(label, kind, Tenin.PRIMARY, Tenin.WHITE, 0, action)
    fun secondaryButton(label: String, kind: TeninIcon? = null, action: (() -> Unit)?) = button(label, kind, Tenin.WHITE, Tenin.PRIMARY_DARK, Tenin.LINE, action)
    /** Non-interactive status in the primary button's slot, e.g. "Getting your lesson ready…". */
    fun busyButton(label: String) = button(label, null, Tenin.LAVENDER, Tenin.PRIMARY_DARK, 0, null)

    fun roundButton(kind: TeninIcon, size: Float = 40f, fill: Int = Tenin.LAVENDER, color: Int = Tenin.PRIMARY, action: (() -> Unit)?) = FrameLayout(context).apply {
        background = rounded(fill, dp(size).toFloat())
        layoutParams = LinearLayout.LayoutParams(dp(size), dp(size))
        addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(kind, color)) },
            FrameLayout.LayoutParams(dp(size * 0.5f), dp(size * 0.5f), Gravity.CENTER))
    }.onTap(action)

    fun connection(connected: Boolean, onDark: Boolean = false) = row().apply {
        val color = if (onDark) Tenin.WHITE else if (connected) Tenin.GREEN else Tenin.MUTED
        background = rounded(if (onDark) 0x33FFFFFF else if (connected) Tenin.GREEN_BG else Tenin.LAVENDER, dp(40f).toFloat())
        setPadding(dp(12f), dp(7f), dp(13f), dp(8f))
        addView(View(context).apply {
            background = rounded(if (!connected) Tenin.FAINT else if (onDark) 0xFF8EE6B8.toInt() else Tenin.GREEN, dp(8f).toFloat())
            layoutParams = LinearLayout.LayoutParams(dp(8f), dp(8f)).apply { rightMargin = dp(7f) }
        })
        addView(text(if (connected) "Glasses connected" else "Glasses not connected", 14f, color, 600))
    }

    fun brand(onDark: Boolean = false) = row().apply {
        if (!onDark) addView(icon(TeninIcon.USER, 26f, Tenin.PRIMARY, 1.7f).margins(right = 10f))
        addView(text("Tenin", 26f, if (onDark) Tenin.WHITE else Tenin.PRIMARY_DARK, 500))
    }

    /** Header row: brand or a back/close button on the left, glasses status on the right. */
    fun header(back: (() -> Unit)? = null, close: Boolean = false, connected: Boolean? = null, onDark: Boolean = false) = row().apply {
        addView(if (back == null) brand(onDark) else FrameLayout(context).apply {
            background = rounded(Tenin.WHITE, dp(14f).toFloat(), Tenin.LINE, dp(1f))
            layoutParams = LinearLayout.LayoutParams(dp(44f), dp(44f))
            contentDescription = if (close) "Close" else "Back"
            addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(if (close) TeninIcon.CLOSE else TeninIcon.BACK, Tenin.PRIMARY_DARK, 2f)) },
                FrameLayout.LayoutParams(dp(22f), dp(22f), Gravity.CENTER))
        }.onTap(back))
        addView(gap())
        if (connected != null) addView(connection(connected, onDark))
    }.fillWidth()

    enum class Tab(val label: String, val icon: TeninIcon) { START("Start", TeninIcon.PLAY), VOCABULARIES("Vocabularies", TeninIcon.BOOK), REVIEW("Review", TeninIcon.HISTORY) }

    fun bottomNav(active: Tab, onTab: (Tab) -> Unit) = column(divider(), row().apply {
        setPadding(0, dp(10f), 0, dp(8f))
        Tab.values().forEach { tab ->
            val on = tab == active
            addView(column(
                icon(tab.icon, 24f, if (on) Tenin.PRIMARY else Tenin.FAINT).apply { (layoutParams as LinearLayout.LayoutParams).gravity = Gravity.CENTER_HORIZONTAL },
                text(tab.label, 14f, if (on) Tenin.PRIMARY_DARK else Tenin.FAINT, if (on) 600 else 400).margins(top = 5f).apply { (layoutParams as LinearLayout.LayoutParams).gravity = Gravity.CENTER_HORIZONTAL },
                View(context).apply {
                    background = rounded(if (on) 0xFFF2C94C.toInt() else 0, dp(2f).toFloat())
                    layoutParams = LinearLayout.LayoutParams(dp(26f), dp(3f)).apply { topMargin = dp(4f); gravity = Gravity.CENTER_HORIZONTAL }
                },
            ).apply { gravity = Gravity.CENTER_HORIZONTAL }.weighted().onTap { if (!on) onTab(tab) })
        }
    }.fillWidth()).apply { setBackgroundColor(Tenin.BG) }

    /**
     * Whole screen: scrolling content, optional pinned footer buttons, optional tab bar.
     * `fill` is the page colour (the lesson-running screen is solid purple).
     */
    fun screen(content: List<View>, footer: List<View> = emptyList(), nav: View? = null, fill: Int = Tenin.BG) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setBackgroundColor(fill)
        val body = column(*content.toTypedArray()).apply { setPadding(gutter, dp(18f), gutter, dp(24f)) }
        addView(ScrollView(context).apply {
            isFillViewport = true
            isVerticalScrollBarEnabled = false
            clipToPadding = false
            addView(body, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        if (footer.isNotEmpty()) addView(column(*footer.toTypedArray()).apply {
            setPadding(gutter, dp(8f), gutter, dp(if (nav == null) 20f else 14f))
            clipToPadding = false
        }.fillWidth())
        if (nav != null) addView(nav)
    }
}
