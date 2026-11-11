package org.unisolder.android

import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.content.res.ColorStateList
import java.util.Locale

/** Full-screen bench view: big actual temperature, set temperature and heater power. Tap to close. */
class DashboardView(ctx: Context, private val onClose: () -> Unit) : FrameLayout(ctx) {
    private val actual = TextView(ctx)
    private val set = TextView(ctx)
    private val status = TextView(ctx)
    private val powerBar = ProgressBar(ctx, null, android.R.attr.progressBarStyleHorizontal)
    private val powerText = TextView(ctx)
    private val yellow = Color.rgb(255, 193, 7)
    private val green = Color.rgb(105, 240, 174)

    init {
        setBackgroundColor(Color.BLACK)
        val d = resources.displayMetrics.density
        val col = LinearLayout(ctx)
        col.orientation = LinearLayout.VERTICAL
        col.gravity = Gravity.CENTER
        col.setPadding((24 * d).toInt(), (24 * d).toInt(), (24 * d).toInt(), (24 * d).toInt())

        actual.textSize = 110f
        actual.typeface = Typeface.MONOSPACE
        actual.gravity = Gravity.CENTER
        actual.setTextColor(yellow)
        actual.includeFontPadding = false
        actual.text = "---"
        col.addView(actual, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        set.textSize = 32f
        set.gravity = Gravity.CENTER
        set.setTextColor(Color.WHITE)
        col.addView(set, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        status.textSize = 20f
        status.gravity = Gravity.CENTER
        status.setTextColor(Color.LTGRAY)
        col.addView(status, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        powerBar.max = 100
        powerBar.progressTintList = ColorStateList.valueOf(yellow)
        val lp = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, (22 * d).toInt())
        lp.topMargin = (24 * d).toInt()
        col.addView(powerBar, lp)

        powerText.textSize = 24f
        powerText.gravity = Gravity.CENTER
        powerText.setTextColor(Color.WHITE)
        col.addView(powerText, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        addView(col, LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setOnClickListener { onClose() }
        visibility = View.GONE
    }

    fun update(s: Sample, power: Float) {
        actual.text = String.format(Locale.US, "%.0f°", s.temp)
        actual.setTextColor(if (s.reached > 0f) green else yellow)
        set.text = String.format(Locale.US, "set %.0f°C", s.set)
        status.text = if (s.reached > 0f) "at temperature" else "heating"
        powerBar.progress = Math.round(power)
        powerText.text = String.format(Locale.US, "heater %.0f%%", power)
    }

    fun showNoData() {
        actual.text = "---"
        status.text = "not connected"
        powerBar.progress = 0
        powerText.text = ""
    }
}
