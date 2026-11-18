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
    private var values = IntArray(29)
    private var loaded = false
    private val rows = ArrayList<Row>()
    private val nameCard = LinearLayout(act)
    private val nameEdit = android.widget.EditText(act)
    private val nameSave = Button(act)

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
        val segButtons = ArrayList<Button>()
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
            for ((i, b) in segButtons.withIndex()) b.backgroundTintList = android.content.res.ColorStateList.valueOf(
                if (spec.lo + i == v) (if (dark) 0xFF3B6FD4.toInt() else 0xFF1A5FC8.toInt()) else (if (dark) 0xFF3E3E46.toInt() else 0xFFD2D2DA.toInt()))
        }

        fun setEnabled(on: Boolean) {
            bar?.isEnabled = on
            minus?.isEnabled = on
            plus?.isEnabled = on
            for (b in segButtons) b.isEnabled = on
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
        val scroll = object : ScrollView(act) {
            private var downX = 0f
            private var downY = 0f
            private var armed = false

            /** A swipe to the right closes the page, unless it starts on a slider (those are dragged sideways). */
            override fun dispatchTouchEvent(ev: android.view.MotionEvent): Boolean {
                when (ev.actionMasked) {
                    android.view.MotionEvent.ACTION_DOWN -> {
                        downX = ev.rawX
                        downY = ev.rawY
                        armed = true
                        val rect = android.graphics.Rect()
                        for (r in rows) {
                            val b = r.bar ?: continue
                            if (b.getGlobalVisibleRect(rect) && rect.contains(ev.rawX.toInt(), ev.rawY.toInt())) armed = false
                        }
                    }
                    android.view.MotionEvent.ACTION_UP -> {
                        val dx = ev.rawX - downX
                        val dy = ev.rawY - downY
                        if (armed && dx > dp(90) && Math.abs(dy) < dx * 0.5f) {
                            armed = false
                            onBack()
                            return true
                        }
                    }
                    android.view.MotionEvent.ACTION_CANCEL -> armed = false
                }
                return super.dispatchTouchEvent(ev)
            }
        }
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

        nameCard.orientation = LinearLayout.VERTICAL
        nameCard.setPadding(dp(14), dp(10), dp(14), dp(10))
        val nbg = android.graphics.drawable.GradientDrawable()
        nbg.cornerRadius = dp(16).toFloat()
        nbg.setColor(if (dark) 0xFF1D1D21.toInt() else 0xFFFFFFFF.toInt())
        nameCard.background = nbg
        val nh = TextView(act)
        nh.text = "Name"
        nh.textSize = 14f
        nh.setTypeface(null, android.graphics.Typeface.BOLD)
        nh.setTextColor(if (dark) 0xFF8AB4F8.toInt() else 0xFF1A5FC8.toInt())
        nameCard.addView(nh)
        val nn = TextView(act)
        nn.text = "Shown on the station at power-up. Up to 10 characters: letters, digits, space, - and ."
        nn.textSize = 12f
        nn.setPadding(0, dp(4), 0, 0)
        nameCard.addView(nn)
        val nrow = LinearLayout(act)
        nrow.orientation = LinearLayout.HORIZONTAL
        nrow.gravity = Gravity.CENTER_VERTICAL
        nameEdit.filters = arrayOf(
            android.text.InputFilter.LengthFilter(10),
            android.text.InputFilter { src, st, en, _, _, _ ->
                val sb = StringBuilder()
                for (i in st until en) if (src[i] in 'A'..'Z' || src[i] in 'a'..'z' || src[i] in '0'..'9' || src[i] == ' ' || src[i] == '-' || src[i] == '.') sb.append(src[i])
                sb.toString()
            })
        nameEdit.setSingleLine(true)
        nrow.addView(nameEdit, LinearLayout.LayoutParams(0, WRAP_P, 1f))
        nameSave.text = "Save"
        style(nameSave)
        nameSave.setOnClickListener { saveName() }
        nrow.addView(nameSave)
        nameCard.addView(nrow)
        nameCard.visibility = View.GONE

        val foot = TextView(act)
        foot.text = "Swipe right or press Back to close. INPUT, calibration, instrument info and the pincode can only be changed on the station itself."
        foot.textSize = 12f
        foot.setPadding(0, dp(16), 0, 0)
        col.addView(foot)

        val sections = listOf(
            "Temperatures" to listOf(0, 1, 2, 22, 3, 4),
            "Sleep and stand-by" to listOf(5, 6, 11, 7),
            "Display and sound" to listOf(8, 9, 28, 27),
            "Knob and sensor" to listOf(18, 19, 13)
        )
        var at = col.indexOfChild(statusText) + 1
        for ((title, ids) in sections) {
            val card = LinearLayout(act)
            card.orientation = LinearLayout.VERTICAL
            card.setPadding(dp(14), dp(10), dp(14), dp(10))
            val bg = android.graphics.drawable.GradientDrawable()
            bg.cornerRadius = dp(16).toFloat()
            bg.setColor(if (dark) 0xFF1D1D21.toInt() else 0xFFFFFFFF.toInt())
            card.background = bg
            val h = TextView(act)
            h.text = title
            h.textSize = 14f
            h.setTypeface(null, android.graphics.Typeface.BOLD)
            h.setTextColor(if (dark) 0xFF8AB4F8.toInt() else 0xFF1A5FC8.toInt())
            card.addView(h)
            for (id in ids) addRow(card, level1Specs.first { it.id == id })
            val lp = LinearLayout.LayoutParams(MATCH_P, WRAP_P)
            lp.topMargin = dp(12)
            col.addView(card, at++, lp)
        }
        col.addView(nameCard, at, LinearLayout.LayoutParams(MATCH_P, WRAP_P).also { it.topMargin = dp(12) })

        scroll.addView(col, ViewGroup.LayoutParams(MATCH_P, WRAP_P))
        scroll.setBackgroundColor(if (dark) 0xFF101012.toInt() else 0xFFF6F6F8.toInt())
        view = scroll
        enableAll(false)
    }

    private fun addRow(parent: LinearLayout, s: Spec) {
        val r = Row(s)
        if (parent.childCount > 1) {
            val line = View(act)
            line.setBackgroundColor(if (dark) 0xFF2E2E34.toInt() else 0xFFE0E0E6.toInt())
            parent.addView(line, LinearLayout.LayoutParams(MATCH_P, dp(1)).also { it.topMargin = dp(8) })
        }
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
            val seg = LinearLayout(act)
            seg.orientation = LinearLayout.HORIZONTAL
            for ((i, label) in s.choices.withIndex()) {
                val b = Button(act)
                b.text = label
                b.tag = "multiline"
                b.isAllCaps = false
                b.minHeight = dp(44)
                b.minimumHeight = dp(44)
                b.setOnClickListener { commit(r, s.lo + i) }
                r.segButtons.add(b)
                seg.addView(b, LinearLayout.LayoutParams(0, WRAP_P, 1f).also { it.marginEnd = if (i < s.choices.size - 1) dp(6) else 0 })
            }
            r.box.addView(seg, LinearLayout.LayoutParams(MATCH_P, WRAP_P))
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

    private fun saveName() {
        val n = nameEdit.text.toString().trimEnd()
        nameSave.isEnabled = false
        io.execute {
            val st = link.setName(n)
            ui.post {
                nameSave.isEnabled = true
                Toast.makeText(act, when (st) {
                    0 -> "Name saved."
                    3 -> "The station is in its menu. Leave the menu first."
                    null -> "No answer from the station."
                    else -> "The station did not accept that name."
                }, Toast.LENGTH_SHORT).show()
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
            val name = if (v != null) link.getName() else null
            ui.post {
                nameCard.visibility = if (name != null) View.VISIBLE else View.GONE
                if (name != null) nameEdit.setText(name)
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
