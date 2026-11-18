package org.unisolder.android

import android.app.Activity
import android.app.AlertDialog
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import java.util.Locale
import java.util.concurrent.ExecutorService

/**
 * Station settings: the same options as the menu on the station (firmware protocol 3, commands 0x40/0x41).
 * Changes are applied in the station at once.
 * Not here: INPUT, CALIBRATE, INST.INFO, VERSION, PINCODE, ROTATION, BUTTONS, MENU DOWN.
 */
class SettingsPage(private val act: Activity, private val link: DeviceLink, private val io: ExecutorService) {
    val view: View
    var onBack: () -> Unit = {}

    private val ui = Handler(Looper.getMainLooper())
    private val statusText: TextView
    private val level1: LinearLayout
    private var values = IntArray(29)
    private var loaded = false
    private val rows = ArrayList<Row>()

    private val dark: Boolean
        get() = (act.resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES

    /** Fixed colours: a very dark page with clearly lighter buttons (light theme: the reverse). */
    private fun style(b: Button) {
        b.backgroundTintList = android.content.res.ColorStateList.valueOf(if (dark) 0xFF3E3E46.toInt() else 0xFFD2D2DA.toInt())
    }

    private fun dp(v: Int): Int = (v * act.resources.displayMetrics.density).toInt()

    private enum class Kind { PRESET, TEMP, NUMBER, CHOICE, DEGREES }

    /** One setting. [lo]..[hi] are in menu units (degrees for temperatures). */
    private class Spec(
        val id: Int, val name: String, val kind: Kind, val lo: Int, val hi: Int,
        val choices: List<String> = emptyList(), val zeroLabel: String? = null, val unit: String = "", val note: String = ""
    )

    private inner class Row(val spec: Spec) {
        val box = LinearLayout(act)
        val valueText = TextView(act)
        var bar: SeekBar? = null
        var minus: Button? = null
        var plus: Button? = null
        var choiceButton: Button? = null
        val send = Runnable { commit(this, current) }
        var current = 0

        /** seekbar position <-> value; presets have OFF at position 0 and start at 100 degrees */
        fun toValue(p: Int): Int = when (spec.kind) {
            Kind.PRESET -> if (p == 0) 0 else spec.lo + p - 1
            else -> spec.lo + p
        }

        fun toProgress(v: Int): Int = when (spec.kind) {
            Kind.PRESET -> if (v == 0) 0 else (v - spec.lo + 1).coerceAtLeast(1)
            else -> (v - spec.lo).coerceAtLeast(0)
        }

        fun maxProgress(): Int = if (spec.kind == Kind.PRESET) spec.hi - spec.lo + 1 else spec.hi - spec.lo

        fun text(v: Int): String = when {
            spec.kind == Kind.CHOICE || spec.kind == Kind.DEGREES -> spec.choices.getOrElse(v) { v.toString() }
            spec.kind == Kind.PRESET && v == 0 -> "OFF"
            spec.zeroLabel != null && v == 0 -> spec.zeroLabel
            spec.kind == Kind.PRESET || spec.kind == Kind.TEMP -> temp(v)
            else -> v.toString() + spec.unit
        }

        fun show(v: Int) {
            current = v
            valueText.text = text(v)
            val b = bar
            if (b != null) b.progress = toProgress(v)
            choiceButton?.text = text(v)
        }

        fun setEnabled(on: Boolean) {
            bar?.isEnabled = on
            minus?.isEnabled = on
            plus?.isEnabled = on
            choiceButton?.isEnabled = on
        }
    }

    private fun fahrenheit(): Boolean = values[8] == 1

    private fun temp(c: Int): String =
        if (fahrenheit()) String.format(Locale.US, "%d°F", Math.round(c * 1.8f + 32f)) else "$c°C"

    private val level1Specs = listOf(
        Spec(0, "T1", Kind.PRESET, 100, 450),
        Spec(1, "T2", Kind.PRESET, 100, 450),
        Spec(2, "T3", Kind.PRESET, 100, 450),
        Spec(22, "T4", Kind.PRESET, 100, 450),
        Spec(3, "Holder temperature", Kind.TEMP, 100, 450, note = "Temperature while the iron rests in the stand."),
        Spec(4, "Sleep temperature", Kind.TEMP, 100, 450, note = "Temperature after the sleep time has passed."),
        Spec(5, "Sleep time", Kind.NUMBER, 0, 255, zeroLabel = "OFF", note = "Seconds when the station senses the iron in the stand, otherwise minutes."),
        Spec(6, "Stand-by time", Kind.NUMBER, 0, 255, zeroLabel = "OFF", unit = " min", note = "Heater off after this time."),
        Spec(11, "Wake up", Kind.CHOICE, 0, 3, choices = listOf("OFF", "KEY", "HOLD", "BOTH"), note = "What wakes the station from sleep: a key press, lifting the iron, both."),
        Spec(8, "Degrees", Kind.DEGREES, 0, 1, choices = listOf("Celsius", "Fahrenheit")),
        Spec(9, "Brightness", Kind.NUMBER, 1, 15),
        Spec(28, "Sound", Kind.CHOICE, 0, 1, choices = listOf("OFF", "ON")),
        Spec(18, "Temperature step", Kind.NUMBER, 1, 50, unit = "°", note = "Step of the knob when setting the temperature."),
        Spec(19, "Display step", Kind.NUMBER, 1, 5, unit = "°", note = "Resolution of the shown temperature."),
        Spec(7, "Holder sensor", Kind.CHOICE, 0, 2, choices = listOf("OFF", "ON", "AUTO")),
        Spec(13, "Sensor filter", Kind.NUMBER, 0, 255, zeroLabel = "OFF"),
        Spec(27, "Sound pitch", Kind.NUMBER, 1, 9)
    )

    init {
        val scroll = ScrollView(act)
        val col = LinearLayout(act)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(8), dp(16), dp(16))

        val top = LinearLayout(act)
        top.orientation = LinearLayout.HORIZONTAL
        top.gravity = Gravity.CENTER_VERTICAL
        val back = Button(act)
        back.text = "‹ Back"
        style(back)
        back.setOnClickListener { onBack() }
        top.addView(back)
        val title = TextView(act)
        title.text = "Station settings"
        title.textSize = 20f
        title.setPadding(dp(12), 0, 0, 0)
        top.addView(title)
        col.addView(top)

        statusText = TextView(act)
        statusText.setPadding(0, dp(8), 0, dp(8))
        col.addView(statusText)

        level1 = LinearLayout(act)
        level1.orientation = LinearLayout.VERTICAL
        col.addView(level1)

        val foot = TextView(act)
        foot.text = "INPUT, calibration, instrument info and the pincode can only be changed on the station itself."
        foot.textSize = 12f
        foot.setPadding(0, dp(16), 0, 0)
        col.addView(foot)

        for (s in level1Specs) addRow(level1, s)

        scroll.addView(col, ViewGroup.LayoutParams(MATCH_P, WRAP_P))
        scroll.setBackgroundColor(if (dark) 0xFF101012.toInt() else 0xFFF6F6F8.toInt())
        view = scroll
        enableAll(false)
    }

    private fun addRow(parent: LinearLayout, s: Spec) {
        val r = Row(s)
        r.box.orientation = LinearLayout.VERTICAL
        r.box.setPadding(0, dp(10), 0, dp(4))
        val head = LinearLayout(act)
        head.orientation = LinearLayout.HORIZONTAL
        val name = TextView(act)
        name.text = s.name
        name.textSize = 16f
        head.addView(name, LinearLayout.LayoutParams(0, WRAP_P, 1f))
        r.valueText.textSize = 16f
        r.valueText.setTypeface(null, android.graphics.Typeface.BOLD)
        if (s.kind != Kind.CHOICE && s.kind != Kind.DEGREES) head.addView(r.valueText)
        r.box.addView(head, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        if (s.kind == Kind.CHOICE || s.kind == Kind.DEGREES) {
            val b = Button(act)
            style(b)
            b.setOnClickListener { chooseDialog(r) }
            r.choiceButton = b
            r.box.addView(b, LinearLayout.LayoutParams(MATCH_P, WRAP_P))
        } else {
            val line = LinearLayout(act)
            line.orientation = LinearLayout.HORIZONTAL
            line.gravity = Gravity.CENTER_VERTICAL
            val m = Button(act)
            m.text = "−"
            style(m)
            val p = Button(act)
            p.text = "+"
            style(p)
            val sb = SeekBar(act)
            sb.max = r.maxProgress()
            sb.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(b: SeekBar?, progress: Int, fromUser: Boolean) {
                    if (fromUser) {
                        r.current = r.toValue(progress)
                        r.valueText.text = r.text(r.current)
                    }
                }

                override fun onStartTrackingTouch(b: SeekBar?) {
                    ui.removeCallbacks(r.send)
                }

                override fun onStopTrackingTouch(b: SeekBar?) {
                    commit(r, r.toValue(sb.progress))
                }
            })
            m.setOnClickListener { nudge(r, -1) }
            p.setOnClickListener { nudge(r, 1) }
            line.addView(m)
            line.addView(sb, LinearLayout.LayoutParams(0, WRAP_P, 1f))
            line.addView(p)
            r.bar = sb
            r.minus = m
            r.plus = p
            r.box.addView(line, LinearLayout.LayoutParams(MATCH_P, WRAP_P))
        }
        if (s.note.isNotEmpty()) {
            val n = TextView(act)
            n.text = s.note
            n.textSize = 12f
            r.box.addView(n)
        }
        parent.addView(r.box)
        rows.add(r)
    }

    /** one step with the - and + buttons; sent when the presses stop */
    private fun nudge(r: Row, dir: Int) {
        var p = r.toProgress(r.current) + dir
        p = p.coerceIn(0, r.maxProgress())
        r.show(r.toValue(p))
        ui.removeCallbacks(r.send)
        ui.postDelayed(r.send, 400)
    }

    private fun chooseDialog(r: Row) {
        AlertDialog.Builder(act)
            .setTitle(r.spec.name)
            .setSingleChoiceItems(r.spec.choices.toTypedArray(), r.current - r.spec.lo) { d, which ->
                d.dismiss()
                commit(r, r.spec.lo + which)
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun commit(r: Row, value: Int) {
        if (!link.connected) return
        r.show(value)
        io.execute {
            val res = link.setParam(r.spec.id, value)
            ui.post {
                when {
                    res == null -> {
                        status("No answer from the station.")
                        reload()
                    }
                    res.first == 0 -> {
                        values[r.spec.id] = res.second
                        r.show(res.second)
                        status("")
                        if (r.spec.id == 8) refreshAll()   // C/F: the shown temperatures change
                    }
                    res.first == 3 -> {
                        Toast.makeText(act, "The station is in its menu. Leave the menu first.", Toast.LENGTH_LONG).show()
                        reload()
                    }
                    else -> {
                        Toast.makeText(act, "The station did not accept that value.", Toast.LENGTH_LONG).show()
                        reload()
                    }
                }
            }
        }
    }

    private fun refreshAll() {
        for (r in rows) r.show(values[r.spec.id])
    }

    private fun status(t: String) {
        statusText.text = t
    }

    private fun enableAll(on: Boolean) {
        for (r in rows) r.setEnabled(on)
    }

    /** Reads all settings from the station. */
    fun reload() {
        if (!link.connected) {
            loaded = false
            enableAll(false)
            status("Not connected.")
            return
        }
        status("Reading the settings ...")
        io.execute {
            val v = link.getParams()
            ui.post {
                if (v == null) {
                    status("No answer from the station. This needs station firmware with protocol 3 or later.")
                    enableAll(false)
                } else {
                    values = v
                    loaded = true
                    refreshAll()
                    enableAll(true)
                    status("Changes are applied in the station at once.")
                }
            }
        }
    }

    fun onDisconnected() {
        loaded = false
        enableAll(false)
        status("Not connected.")
    }

    companion object {
        private const val MATCH_P = ViewGroup.LayoutParams.MATCH_PARENT
        private const val WRAP_P = ViewGroup.LayoutParams.WRAP_CONTENT
    }
}
