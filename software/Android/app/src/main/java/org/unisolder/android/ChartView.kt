package org.unisolder.android

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.view.View

/** Scrolling chart of the live data, the newest sample at the right edge. Vertical axis 0..500 degrees C; the power line uses the same axis with 100% at the top. */
class ChartView(ctx: Context) : View(ctx) {
    class Series(val name: String, val color: Int, var visible: Boolean) {
        val data = FloatArray(CAPACITY)
    }

    val series = listOf(
        Series("Set", Color.rgb(255, 82, 82), true),
        Series("Temp", Color.rgb(105, 240, 174), true),
        Series("Power", Color.rgb(255, 202, 40), true),
        Series("Heater on", Color.rgb(64, 196, 255), false),
        Series("Reached", Color.rgb(186, 104, 200), false),
        Series("Filtered", Color.rgb(255, 138, 101), false),
        Series("ADC", Color.rgb(200, 200, 200), false)
    )

    private var count = 0
    private var head = 0
    private val density = resources.displayMetrics.density
    private val path = Path()

    private val gridPaint = Paint().apply {
        color = Color.argb(70, 255, 255, 255)
        strokeWidth = 1f
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.argb(170, 255, 255, 255)
        textSize = 11f * resources.displayMetrics.scaledDensity
    }
    private val linePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2f * resources.displayMetrics.density
        strokeJoin = Paint.Join.ROUND
    }

    /** Adds one sample: values in the order of [series]. */
    fun add(values: FloatArray) {
        for (i in series.indices) series[i].data[head] = values[i]
        head = (head + 1) % CAPACITY
        if (count < CAPACITY) count++
        invalidate()
    }

    fun clear() {
        count = 0
        head = 0
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        val left = 36f * density
        val right = width - 6f * density
        val top = 6f * density
        val bottom = height - 6f * density
        val w = right - left
        val h = bottom - top
        if (w <= 0f || h <= 0f) return

        for (v in 0..500 step 100) {
            val y = bottom - v / 500f * h
            canvas.drawLine(left, y, right, y, gridPaint)
            canvas.drawText(v.toString(), 2f * density, y + 4f * density, textPaint)
        }

        canvas.save()
        canvas.clipRect(left, top, right, bottom)
        val oldest = (head - count + CAPACITY) % CAPACITY
        for (s in series) {
            if (!s.visible || count < 2) continue
            path.reset()
            for (i in 0 until count) {
                val v = s.data[(oldest + i) % CAPACITY].coerceIn(0f, 520f)
                val x = left + (CAPACITY - count + i) / (CAPACITY - 1f) * w
                val y = bottom - v / 500f * h
                if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
            }
            linePaint.color = s.color
            canvas.drawPath(path, linePaint)
        }
        canvas.restore()
    }

    companion object {
        const val CAPACITY = 500
    }
}
