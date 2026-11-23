package org.unisolder.android

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.view.View

/**
 * Scrolling chart of the live data, the newest sample at the right edge. Vertical axis 0..500 degrees C; the power line uses
 * the same axis with 100% at the top. The heater resistance has its own axis in ohm on the right side, scaled automatically
 * so that its trace stays in the quiet band between a quarter and half of the height, away from the other traces.
 */
class ChartView(ctx: Context) : View(ctx) {
    class Series(val name: String, val color: Int, var visible: Boolean, val rightAxis: Boolean = false) {
        val data = FloatArray(CAPACITY)
    }

    val series = listOf(
        Series("Set", Color.rgb(255, 82, 82), true),
        Series("Temp", Color.rgb(105, 240, 174), true),
        Series("Power", Color.rgb(255, 202, 40), true),
        // the temperature the PID loop reacts to (smoothed temperature plus the derivative term); off until switched on
        Series("PID input", Color.rgb(64, 196, 255), false),
        // diagnostics (off by default): the temperature after the station's averaging filter and the change of the raw
        // ADC reading, drawn around the middle of the chart (250)
        Series("Filtered", Color.rgb(224, 224, 224), false),
        Series("ADC \u0394", Color.rgb(255, 128, 171), false),
        // heater resistance in ohm, on the right axis
        Series("\u03A9", Color.rgb(179, 136, 255), true, true),
        Series("Wave sh.", Color.rgb(255, 171, 64), false),
        Series("Flag", Color.rgb(128, 222, 234), false)
    )

    /** Text in the middle of the chart, for example while no tip is in the iron; null = none. */
    var notice: String? = null
        set(v) { if (field != v) { field = v; invalidate() } }

    private var count = 0
    private var head = 0
    private val density = resources.displayMetrics.density
    private val path = Path()
    private val ohm: Series get() = series.first { it.rightAxis }
    private var axLo = 0f
    private var axHi = 10f
    private var axInit = false

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

    /**
     * Fits the ohm axis to the recent samples: the data sits in the band from 25% to 50% of the height. The fit uses the 10th to
     * 90th percentile of the last [FIT_SAMPLES] samples, so a few wild readings (open heater, contact bounce, tip change) do not
     * stretch the axis; samples far outside the band are left out of the trace (see [onDraw]).
     */
    private fun updateOhmAxis(oldest: Int) {
        val d = ohm.data
        val from = maxOf(0, count - FIT_SAMPLES)
        val v = FloatArray(count - from)
        var n = 0
        for (i in from until count) {
            val x = d[(oldest + i) % CAPACITY]
            if (!x.isNaN() && x > MIN_OHM) v[n++] = x
        }
        if (n < 3) return
        java.util.Arrays.sort(v, 0, n)
        val mn = v[(n - 1) / 10]
        val mx = v[(n - 1) * 9 / 10]
        val span = maxOf(mx - mn, 0.4f) * 4f   // the data band is a quarter of the height
        val lo = (mn + mx) / 2f - 0.375f * span
        if (!axInit) {
            axLo = lo
            axHi = lo + span
            axInit = true
        } else {
            axLo += (lo - axLo) * 0.1f          // glide, so the axis does not jump
            axHi += (lo + span - axHi) * 0.1f
        }
    }

    override fun onDraw(canvas: Canvas) {
        val showOhm = ohm.visible && count >= 2
        val oldest = (head - count + CAPACITY) % CAPACITY
        if (showOhm) updateOhmAxis(oldest)
        val left = 36f * density
        val right = width - (if (ohm.visible) 36f else 6f) * density
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

        if (showOhm && axInit && axHi > axLo) {
            // axis labels in ohm, in the colour of the trace, at nice round steps
            val rawStep = (axHi - axLo) / 5f
            val step = NICE_STEPS.firstOrNull { it >= rawStep } ?: 10f
            textPaint.color = ohm.color
            textPaint.textAlign = Paint.Align.LEFT
            var t = Math.ceil((axLo / step).toDouble()).toFloat() * step
            while (t <= axHi) {
                val y = bottom - (t - axLo) / (axHi - axLo) * h
                canvas.drawText(String.format(java.util.Locale.US, if (step < 1f) "%.1f" else "%.0f", t), right + 3f * density, y + 4f * density, textPaint)
                t += step
            }
            canvas.drawText("\u03A9", right + 3f * density, top + 10f * density, textPaint)
            textPaint.color = Color.argb(170, 255, 255, 255)
        }

        canvas.save()
        canvas.clipRect(left, top, right, bottom)
        for (s in series) {
            if (!s.visible || count < 2) continue
            path.reset()
            var pen = false
            for (i in 0 until count) {
                val raw = s.data[(oldest + i) % CAPACITY]
                if (raw.isNaN()) { pen = false; continue }      // no valid reading (tip removed): gap in the trace
                val x = left + (CAPACITY - count + i) / (CAPACITY - 1f) * w
                val y: Float
                if (s.rightAxis) {
                    // no reading (heater off) leaves a gap in the trace
                    if (raw <= MIN_OHM || !axInit || axHi <= axLo) { pen = false; continue }
                    y = bottom - (raw - axLo) / (axHi - axLo) * h
                    if (y < top - h * 0.1f || y > bottom + h * 0.1f) { pen = false; continue }   // wild reading: no line to nowhere
                } else {
                    y = bottom - raw.coerceIn(0f, 520f) / 500f * h
                }
                if (!pen) { path.moveTo(x, y); pen = true } else path.lineTo(x, y)
            }
            linePaint.color = s.color
            canvas.drawPath(path, linePaint)
        }
        canvas.restore()
        val note = notice
        if (note != null) {
            textPaint.textAlign = Paint.Align.CENTER
            textPaint.color = Color.argb(230, 255, 255, 255)
            val big = textPaint.textSize
            textPaint.textSize = big * 1.6f
            canvas.drawText(note, (left + right) / 2f, top + h * 0.5f, textPaint)
            textPaint.textSize = big
            textPaint.color = Color.argb(170, 255, 255, 255)
            textPaint.textAlign = Paint.Align.LEFT
        }
    }

    companion object {
        const val CAPACITY = 500
        private const val MIN_OHM = 0.05f
        private const val FIT_SAMPLES = 100
        private val NICE_STEPS = floatArrayOf(0.1f, 0.2f, 0.5f, 1f, 2f, 5f)
    }
}
