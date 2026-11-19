package org.unisolder.android

import android.app.Activity
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.concurrent.ExecutorService

/**
 * Ramp mode of the station (firmware protocol 7, commands 0x50-0x53): the station runs idle, ramp, peak dwell, cool down and
 * lockout by itself. This page sets the values, switches the mode on and off, starts or aborts a ramp and shows the state.
 * The phone is only an editor and a viewer: nothing here has to stay connected for the station to finish a ramp.
 */
class RampPage(private val act: Activity, private val link: DeviceLink, private val io: ExecutorService) {
    val view: View
    var onBack: () -> Unit = {}
    /** firmware protocol of the connected station (0 = not connected) */
    var protocol: () -> Int = { 0 }

    private val ui = Handler(Looper.getMainLooper())
    private val statusText = TextView(act)
    private val noteText = TextView(act)
    private val modeButton = Button(act)
    private val startButton = Button(act)
    private val abortButton = Button(act)
    private val saveButton = Button(act)
    private val fields = ArrayList<EditText>()
    private val fieldPar = ArrayList<Int>()     // index in the station's parameter list for each field
    private var pars = IntArray(DeviceLink.RAMP_PARS)          // as read from the station: the two contact values are kept as they are
    private var inMode = false
    private var visible = false
    private var parsLoaded = false

    private val dark: Boolean
        get() = (act.resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES

    private fun dp(v: Int): Int = (v * act.resources.displayMetrics.density).toInt()
    private val yellow = 0xFFFFC107.toInt()

    private fun style(b: Button, strong: Boolean = false) {
        b.backgroundTintList = android.content.res.ColorStateList.valueOf(
            if (strong) yellow else if (dark) 0xFF3E3E46.toInt() else 0xFFD2D2DA.toInt()
        )
        if (strong) b.setTextColor(0xFF000000.toInt())
    }

    private val pollTick = object : Runnable {
        override fun run() {
            if (visible && link.connected && !link.busy && protocol() >= 7) {
                io.execute {
                    val s = link.rampStatus()
                    ui.post { if (s != null) showStatus(s) }
                }
            }
            if (visible) ui.postDelayed(this, 250)
        }
    }

    init {
        val scroll = object : ScrollView(act) {
            private var downX = 0f
            private var downY = 0f
            override fun dispatchTouchEvent(ev: android.view.MotionEvent): Boolean {
                if (ev.actionMasked == android.view.MotionEvent.ACTION_DOWN) {
                    downX = ev.rawX
                    downY = ev.rawY
                } else if (ev.actionMasked == android.view.MotionEvent.ACTION_UP) {
                    val dx = ev.rawX - downX
                    val dy = ev.rawY - downY
                    if (dx > dp(120) && Math.abs(dy) < dx / 2) onBack()
                }
                return super.dispatchTouchEvent(ev)
            }
        }
        scroll.setBackgroundColor(if (dark) 0xFF101012.toInt() else 0xFFF6F6F8.toInt())
        val text = if (dark) 0xFFFFFFFF.toInt() else 0xFF000000.toInt()
        val col = LinearLayout(act)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(16), dp(16), dp(24))

        val back = Button(act)
        back.text = "Back"
        style(back)
        back.setOnClickListener { onBack() }
        col.addView(back, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        val title = TextView(act)
        title.text = "Ramp mode"
        title.textSize = 22f
        title.setTextColor(text)
        title.setPadding(0, dp(8), 0, dp(4))
        col.addView(title)

        val intro = TextView(act)
        intro.text = "In ramp mode the station holds the idle temperature and runs the ramp by itself. " +
            "Start it with a short press of the knob or with Start below; a press while it heats aborts. " +
            "The knob, sleep and stand-by do not change the temperature in this mode. After a power cycle the station is a normal station again."
        intro.setTextColor(text)
        col.addView(intro)

        statusText.textSize = 18f
        statusText.setTextColor(yellow.let { if (dark) it else 0xFF9A7000.toInt() })
        statusText.setPadding(0, dp(12), 0, dp(4))
        col.addView(statusText)
        noteText.setTextColor(text)
        col.addView(noteText)

        modeButton.setOnClickListener { toggleMode() }
        style(modeButton, true)
        col.addView(modeButton, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).also { it.topMargin = dp(8) })

        val row = LinearLayout(act)
        row.orientation = LinearLayout.HORIZONTAL
        startButton.text = "Start ramp"
        abortButton.text = "Abort"
        style(startButton)
        style(abortButton)
        startButton.setOnClickListener { control(2) }
        abortButton.setOnClickListener { control(3) }
        row.addView(startButton, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(abortButton, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        col.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        val head = TextView(act)
        head.text = "Ramp values"
        head.textSize = 18f
        head.setTextColor(if (dark) yellow else 0xFF9A7000.toInt())
        head.setPadding(0, dp(20), 0, dp(4))
        col.addView(head)

        addField(col, "Idle temperature (°C)", 0, "100-340, default 165 (below the melting point of Sn62 solder, 179). Held until the ramp starts, also while the iron is in its stand. Ready: up to 10 above it.")
        addField(col, "Peak temperature (°C)", 1, "At least 10 above idle, 350 at most.")
        addField(col, "Ramp rate (°C per second)", 2, "5-100. The tip cannot always follow a high rate: check the highest temperature below.")
        addField(col, "Dwell at the peak (seconds)", 3, "0-10, one decimal.", decimal = true)
        addField(col, "Lockout after the dwell (seconds)", 4, "0-60, counted from the end of the dwell. No new ramp can start in this time. The tip cools by itself.")
        addField(col, "Ready up to (°C)", 8, "At least 10 above idle and at most 10 below the peak. A new ramp can start when the tip is not hotter than this, and then starts from the tip's temperature.")
        addField(col, "Time-out when idle (minutes)", 5, "1-60. After this time without use the station leaves ramp mode.")

        saveButton.text = "Save values in the station"
        style(saveButton)
        saveButton.setOnClickListener { save() }
        col.addView(saveButton, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).also { it.topMargin = dp(12) })

        scroll.addView(col, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        view = scroll
        refreshButtons()
    }

    private fun addField(col: LinearLayout, label: String, index: Int, hint: String, decimal: Boolean = false) {
        val text = if (dark) 0xFFFFFFFF.toInt() else 0xFF000000.toInt()
        val l = TextView(act)
        l.text = label
        l.setTextColor(text)
        l.setPadding(0, dp(10), 0, 0)
        col.addView(l)
        val e = EditText(act)
        e.inputType = if (decimal) InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL else InputType.TYPE_CLASS_NUMBER
        e.setTextColor(text)
        e.tag = if (decimal) "dec" else "int"
        fields.add(e)
        fieldPar.add(index)
        col.addView(e, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        val h = TextView(act)
        h.text = hint
        h.textSize = 12f
        h.setTextColor(if (dark) 0xFFB0B0B8.toInt() else 0xFF55555C.toInt())
        col.addView(h)
    }

    private fun supported(): Boolean = link.connected && protocol() >= 7

    private fun refreshButtons() {
        val ok = supported()
        modeButton.isEnabled = ok
        modeButton.text = if (inMode) "Leave ramp mode" else "Enter ramp mode"
        startButton.isEnabled = ok && inMode
        abortButton.isEnabled = ok && inMode
        saveButton.isEnabled = ok
        for (f in fields) f.isEnabled = ok
        if (!link.connected) noteText.text = "Not connected."
        else if (protocol() < 7) noteText.text = "This station's firmware has no ramp mode (it needs the ramp mode firmware, protocol 7)."
    }

    /** Called when the page is shown or hidden: the status is only polled while it is visible. */
    fun setVisible(v: Boolean) {
        val was = visible
        visible = v
        if (v && !was) {
            refreshButtons()
            if (supported()) loadPars()
            ui.post(pollTick)
        }
    }

    fun onDisconnected() {
        inMode = false
        parsLoaded = false
        statusText.text = ""
        refreshButtons()
    }

    private fun loadPars() {
        io.execute {
            val p = link.getRampPars()
            val s = link.rampStatus()
            ui.post {
                if (p != null) {
                    pars = p
                    parsLoaded = true
                    for ((i, f) in fields.withIndex()) {
                        val k = fieldPar[i]
                        f.setText(if (k == 3) String.format(java.util.Locale.US, "%.1f", p[3] / 10.0) else p[k].toString())
                    }
                }
                if (s != null) showStatus(s)
            }
        }
    }

    private fun save() {
        if (!parsLoaded) { Toast.makeText(act, "Values not loaded yet", Toast.LENGTH_SHORT).show(); return }
        val v = pars.copyOf()
        try {
            for ((i, f) in fields.withIndex()) {
                val t = f.text.toString().trim().replace(',', '.')
                val k = fieldPar[i]
                v[k] = if (k == 3) Math.round(t.toDouble() * 10).toInt() else t.toInt()
            }
        } catch (e: NumberFormatException) {
            Toast.makeText(act, "Check the values", Toast.LENGTH_SHORT).show()
            return
        }
        io.execute {
            val st = link.setRampPars(v)
            val back = if (st == 0) link.getRampPars() else null
            ui.post {
                when (st) {
                    0 -> { if (back != null) pars = back; Toast.makeText(act, "Saved in the station", Toast.LENGTH_SHORT).show() }
                    2 -> Toast.makeText(act, "A value is out of range (peak at least 10 above idle; ready limit between idle + 10 and peak - 10)", Toast.LENGTH_LONG).show()
                    3 -> Toast.makeText(act, "A ramp is running: wait until it is finished", Toast.LENGTH_LONG).show()
                    else -> Toast.makeText(act, "The station did not accept the values", Toast.LENGTH_LONG).show()
                }
            }
        }
    }

    private fun toggleMode() {
        control(if (inMode) 0 else 1)
    }

    private fun control(cmd: Int) {
        io.execute {
            val r = link.rampControl(cmd)
            val s = link.rampStatus()
            ui.post {
                if (r == null) Toast.makeText(act, "No answer from the station", Toast.LENGTH_SHORT).show()
                else if (r.first == 1) Toast.makeText(act, if (cmd == 1) "Not possible now (two-iron adapter, no instrument or station asleep)" else "Not possible now", Toast.LENGTH_LONG).show()
                else if (r.first == 3) Toast.makeText(act, "The station is in its menu", Toast.LENGTH_SHORT).show()
                if (s != null) showStatus(s)
            }
        }
    }

    private fun showStatus(s: RampStatus) {
        inMode = s.state != 0
        refreshButtons()
        val name = when (s.state) {
            0 -> "Off (normal station)"
            1 -> if (s.ready) "Ready" else if (s.actual > pars[8]) "Cooling: waiting for the ready limit" else "Heating to the idle temperature"
            2 -> "Ramping"
            3 -> "Peak"
            4 -> "Lockout"
            5 -> "Lockout"
            else -> "?"
        }
        statusText.text = name
        val sb = StringBuilder()
        if (s.state != 0) {
            sb.append("Target ").append(s.target).append("°C, tip ").append(s.actual).append("°C")
            if (s.msLeft > 0 && s.state >= 2) sb.append(", ").append(String.format(java.util.Locale.US, "%.1f", s.msLeft / 1000.0)).append(" s left")
            if (s.highest > 0) sb.append("\nHighest temperature since the start: ").append(s.highest).append("°C")
        }
        noteText.text = sb.toString()
    }
}
