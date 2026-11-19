package org.unisolder.android

import android.app.Activity
import android.app.AlertDialog
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ExecutorService

/** One step of a soldering profile: ramp to [temp] in [ramp] seconds (0 = at once), then hold it for [hold] seconds. */
class ProfileStep(val temp: Int, val ramp: Int, val hold: Int)

class Profile(
    val name: String,
    val steps: List<ProfileStep>,
    /** the hold time starts when the tip has reached the step temperature, not when the ramp ends */
    val waitForTemp: Boolean = true,
    /** at the end, set the temperature that was set when the profile started */
    val returnToStart: Boolean = true
) {
    fun totalSeconds(): Int = steps.sumOf { it.ramp + it.hold }

    fun toJson(): JSONObject {
        val o = JSONObject()
        o.put("name", name)
        o.put("wait", waitForTemp)
        o.put("return", returnToStart)
        val a = JSONArray()
        for (s in steps) a.put(JSONObject().put("t", s.temp).put("r", s.ramp).put("h", s.hold))
        o.put("steps", a)
        return o
    }

    companion object {
        fun fromJson(o: JSONObject): Profile {
            val a = o.getJSONArray("steps")
            val steps = ArrayList<ProfileStep>()
            for (i in 0 until a.length()) {
                val s = a.getJSONObject(i)
                steps.add(ProfileStep(s.getInt("t"), s.getInt("r"), s.getInt("h")))
            }
            return Profile(o.getString("name"), steps, o.optBoolean("wait", true), o.optBoolean("return", true))
        }
    }
}

/** The planned curve of a profile: ramps as sloped lines, holds as flat lines (the wait for temperature is not drawn). */
class ProfilePreview(ctx: android.content.Context) : View(ctx) {
    private var steps: List<ProfileStep> = emptyList()
    private val line = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
        color = 0xFFFFC107.toInt(); strokeWidth = 4f * ctx.resources.displayMetrics.density; style = android.graphics.Paint.Style.STROKE
    }
    private val text = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
        color = 0xFF9E9E9E.toInt(); textSize = 11f * ctx.resources.displayMetrics.scaledDensity
    }
    private val grid = android.graphics.Paint().apply { color = 0x33888888; strokeWidth = 1f }

    fun set(s: List<ProfileStep>) {
        steps = s
        invalidate()
    }

    override fun onMeasure(w: Int, h: Int) {
        setMeasuredDimension(MeasureSpec.getSize(w), (150 * resources.displayMetrics.density).toInt())
    }

    override fun onDraw(c: android.graphics.Canvas) {
        val left = text.measureText("450") + 8f
        val right = width - 6f
        val top = 8f
        val bottom = height - text.textSize - 10f
        val total = steps.sumOf { it.ramp + it.hold }
        c.drawLine(left, bottom, right, bottom, grid)
        c.drawLine(left, top, right, top, grid)
        val tMin = 100f
        val tMax = maxOf(150f, (steps.maxOfOrNull { it.temp } ?: 150).toFloat())
        c.drawText(tMax.toInt().toString(), 2f, top + text.textSize * 0.8f, text)
        c.drawText("100", 2f, bottom, text)
        if (steps.isEmpty() || total <= 0) {
            c.drawText("Add a step to see the profile", left + 8f, (top + bottom) / 2, text)
            return
        }
        fun x(sec: Float) = left + (right - left) * sec / total
        fun y(t: Float) = bottom - (bottom - top) * (t - tMin) / (tMax - tMin)
        val path = android.graphics.Path()
        var sec = 0f
        var prev = tMin    // step 1 ramps from the temperature that is set when the profile starts: drawn from the bottom
        path.moveTo(x(0f), y(prev))
        for (s in steps) {
            sec += s.ramp
            path.lineTo(x(sec), y(s.temp.toFloat()))
            sec += s.hold
            path.lineTo(x(sec), y(s.temp.toFloat()))
            prev = s.temp.toFloat()
        }
        c.drawPath(path, line)
        c.drawText("0", left, height - 4f, text)
        val label = if (total >= 120) (total / 60).toString() + " min" else "$total s"
        c.drawText(label, right - text.measureText(label), height - 4f, text)
    }
}

/**
 * Soldering profiles: a list of steps (target temperature, ramp time, hold time) that the app runs by setting the
 * station's temperature along the profile. The profile runs in the app: it stops when the connection is lost
 * (the station then stays at the last temperature), when the station is put in its menu, or with Stop.
 */
class ProfilesPage(private val act: Activity, private val link: DeviceLink, private val io: ExecutorService) {
    val view: View
    var onBack: () -> Unit = {}
    /** called when a run starts or ends (the Control tab shows it) */
    var onRunChanged: (String?) -> Unit = {}

    private val ui = Handler(Looper.getMainLooper())
    private val prefs = Prefs(act)
    private val col = LinearLayout(act)
    private var profiles = ArrayList<Profile>()
    private var editing = false

    // ---- run state
    private enum class Phase { RAMP, WAIT, HOLD }

    private var run: Profile? = null
    private var stepIndex = 0
    private var phase = Phase.RAMP
    private var phaseStart = 0L
    private var fromTemp = 0f
    private var startSet = 0
    private var lastSent = -1
    private var setpoint = 0f
    private var actual: Float? = null
    private var fails = 0
    private var busy = false
    private var ticks = 0
    private var runNote = ""
    private var answered = -1
    private var sentCount = 0

    val running: Boolean get() = run != null
    val runName: String? get() = run?.name

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

    init {
        val scroll = object : ScrollView(act) {
            private var downX = 0f
            private var downY = 0f
            override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
                if (ev.actionMasked == MotionEvent.ACTION_DOWN) {
                    downX = ev.rawX
                    downY = ev.rawY
                } else if (ev.actionMasked == MotionEvent.ACTION_UP && !editing) {
                    val dx = ev.rawX - downX
                    if (dx > dp(90) && Math.abs(ev.rawY - downY) < dx * 0.5f) {
                        onBack()
                        return true
                    }
                }
                return super.dispatchTouchEvent(ev)
            }
        }
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(8), dp(16), dp(16))
        scroll.addView(col, ViewGroup.LayoutParams(MATCH_P, WRAP_P))
        scroll.setBackgroundColor(if (dark) 0xFF101012.toInt() else 0xFFF6F6F8.toInt())
        view = scroll
        load()
        showList()
    }

    // ------------------------------------------------------------------ storage

    private fun load() {
        profiles = ArrayList()
        try {
            val a = JSONArray(prefs.getString("profiles", "[]"))
            for (i in 0 until a.length()) profiles.add(Profile.fromJson(a.getJSONObject(i)))
        } catch (e: Exception) {
            profiles = ArrayList()
        }
    }

    private fun save() {
        val a = JSONArray()
        for (p in profiles) a.put(p.toJson())
        prefs.putString("profiles", a.toString())
    }

    /** opens the page on the list of profiles (also refreshes the run state) */
    fun showListNow() = showList()

    // ------------------------------------------------------------------ list

    private fun card(): LinearLayout {
        val c = LinearLayout(act)
        c.orientation = LinearLayout.VERTICAL
        c.setPadding(dp(14), dp(10), dp(14), dp(10))
        val bg = android.graphics.drawable.GradientDrawable()
        bg.cornerRadius = dp(16).toFloat()
        bg.setColor(if (dark) 0xFF1D1D21.toInt() else 0xFFFFFFFF.toInt())
        c.background = bg
        val lp = LinearLayout.LayoutParams(MATCH_P, WRAP_P)
        lp.topMargin = dp(12)
        col.addView(c, lp)
        return c
    }

    private fun header(title: String) {
        val top = LinearLayout(act)
        top.orientation = LinearLayout.HORIZONTAL
        top.gravity = Gravity.CENTER_VERTICAL
        val back = Button(act)
        back.text = "‹ Back"
        style(back)
        back.setOnClickListener { if (editing) showList() else onBack() }
        top.addView(back)
        val t = TextView(act)
        t.text = title
        t.textSize = 20f
        t.setPadding(dp(12), 0, 0, 0)
        top.addView(t)
        col.addView(top)
    }

    private fun fmtTime(s: Int): String = if (s >= 60) (s / 60).toString() + " min " + (s % 60) + " s" else "$s s"

    private fun showList() {
        editing = false
        col.removeAllViews()
        header("Soldering profiles")
        val note = TextView(act)
        note.text = "A profile sets the station's temperature step by step. The app runs it, so keep the app open and connected (the screen stays on while it runs). " +
                "If the connection is lost, the station stays at the last temperature. Temperatures are in °C."
        note.textSize = 12f
        note.setPadding(0, dp(8), 0, 0)
        col.addView(note)

        if (!link.connected && run == null) {
            val off = TextView(act)
            off.text = "Not connected: you can design and edit profiles now; starting one needs a connection."
            off.textSize = 12f
            off.setPadding(0, dp(6), 0, 0)
            col.addView(off)
        }
        if (run != null) addRunCard()

        for ((i, p) in profiles.withIndex()) {
            val c = card()
            val n = TextView(act)
            n.text = p.name
            n.textSize = 16f
            n.setTypeface(null, android.graphics.Typeface.BOLD)
            c.addView(n)
            val d = TextView(act)
            d.text = p.steps.joinToString("  →  ") { it.temp.toString() + "°" } + "\n" + p.steps.size + " step(s), " + fmtTime(p.totalSeconds()) + " without waiting"
            d.textSize = 13f
            c.addView(d)
            val row = LinearLayout(act)
            row.orientation = LinearLayout.HORIZONTAL
            val start = Button(act)
            start.text = "Start"
            style(start, true)
            start.isEnabled = link.connected && run == null
            start.setOnClickListener { startRun(p) }
            val edit = Button(act)
            edit.text = "Edit"
            style(edit)
            edit.setOnClickListener { showEditor(i) }
            row.addView(start, LinearLayout.LayoutParams(0, WRAP_P, 1f).also { it.marginEnd = dp(8) })
            row.addView(edit, LinearLayout.LayoutParams(0, WRAP_P, 1f))
            c.addView(row, LinearLayout.LayoutParams(MATCH_P, WRAP_P).also { it.topMargin = dp(6) })
        }
        val add = Button(act)
        add.text = "New profile"
        style(add)
        add.setOnClickListener { showEditor(-1) }
        col.addView(add, LinearLayout.LayoutParams(MATCH_P, WRAP_P).also { it.topMargin = dp(16) })
    }

    // ------------------------------------------------------------------ editor

    private class StepRow(val temp: EditText, val ramp: EditText, val hold: EditText)

    private fun number(hint: String, value: Int?): EditText {
        val e = EditText(act)
        e.inputType = InputType.TYPE_CLASS_NUMBER
        e.hint = hint
        e.setSingleLine(true)
        e.gravity = Gravity.CENTER
        e.textSize = 15f
        if (value != null) e.setText(value.toString())
        return e
    }

    private fun showEditor(index: Int) {
        editing = true
        col.removeAllViews()
        header(if (index < 0) "New profile" else "Edit profile")
        val p = if (index >= 0) profiles[index] else null
        val c = card()
        val name = EditText(act)
        name.hint = "Name"
        name.setSingleLine(true)
        name.setText(p?.name ?: "")
        c.addView(name)
        val wait = CheckBox(act)
        wait.text = "Hold time starts when the temperature is reached"
        wait.isChecked = p?.waitForTemp ?: true
        c.addView(wait)
        val ret = CheckBox(act)
        ret.text = "At the end, go back to the temperature before the start"
        ret.isChecked = p?.returnToStart ?: true
        c.addView(ret)

        val sc = card()
        val h = TextView(act)
        h.text = "Steps: temperature °C, ramp time s, hold time s"
        h.textSize = 13f
        h.setTextColor(if (dark) yellow else 0xFF9A7000.toInt())
        sc.addView(h)
        val rows = ArrayList<StepRow>()
        val preview = ProfilePreview(act)
        fun updatePreview() {
            val st = ArrayList<ProfileStep>()
            for (r in rows) {
                val t = r.temp.text.toString().toIntOrNull() ?: continue
                st.add(ProfileStep(t.coerceIn(100, 450), (r.ramp.text.toString().toIntOrNull() ?: 0).coerceIn(0, 3600), (r.hold.text.toString().toIntOrNull() ?: 0).coerceIn(0, 3600)))
            }
            preview.set(st)
        }
        val watcher = object : android.text.TextWatcher {
            override fun afterTextChanged(s: android.text.Editable?) = updatePreview()
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
        }
        val list = LinearLayout(act)
        list.orientation = LinearLayout.VERTICAL
        sc.addView(list)

        fun addStepRow(s: ProfileStep?) {
            val r = LinearLayout(act)
            r.orientation = LinearLayout.HORIZONTAL
            r.gravity = Gravity.CENTER_VERTICAL
            val sr = StepRow(number("°C", s?.temp), number("ramp s", s?.ramp), number("hold s", s?.hold))
            for (e in listOf(sr.temp, sr.ramp, sr.hold)) {
                e.addTextChangedListener(watcher)
                r.addView(e, LinearLayout.LayoutParams(0, WRAP_P, 1f))
            }
            val del = Button(act)
            del.text = "✕"
            style(del)
            del.setOnClickListener {
                list.removeView(r)
                rows.remove(sr)
                updatePreview()
            }
            r.addView(del)
            rows.add(sr)
            list.addView(r)
        }
        for (s in (p?.steps ?: listOf(ProfileStep(150, 60, 30)))) addStepRow(s)
        updatePreview()

        val pn = TextView(act)
        pn.text = "Step 1 ramps from the temperature that is set when you press Start."
        pn.textSize = 11f
        sc.addView(preview)
        sc.addView(pn)
        val addStep = Button(act)
        addStep.text = "Add step"
        style(addStep)
        addStep.setOnClickListener { addStepRow(null) }
        sc.addView(addStep)

        val buttons = LinearLayout(act)
        buttons.orientation = LinearLayout.HORIZONTAL
        val save = Button(act)
        save.text = "Save"
        style(save, true)
        save.setOnClickListener {
            val steps = ArrayList<ProfileStep>()
            for (r in rows) {
                val t = r.temp.text.toString().toIntOrNull() ?: continue
                steps.add(
                    ProfileStep(
                        t.coerceIn(100, 450),
                        (r.ramp.text.toString().toIntOrNull() ?: 0).coerceIn(0, 3600),
                        (r.hold.text.toString().toIntOrNull() ?: 0).coerceIn(0, 3600)
                    )
                )
            }
            val nm = name.text.toString().trim()
            when {
                nm.isEmpty() -> Toast.makeText(act, "Give the profile a name.", Toast.LENGTH_SHORT).show()
                steps.isEmpty() -> Toast.makeText(act, "Add at least one step with a temperature.", Toast.LENGTH_SHORT).show()
                else -> {
                    val np = Profile(nm, steps, wait.isChecked, ret.isChecked)
                    if (index >= 0) profiles[index] = np else profiles.add(np)
                    save()
                    showList()
                }
            }
        }
        val cancel = Button(act)
        cancel.text = "Cancel"
        style(cancel)
        cancel.setOnClickListener { showList() }
        buttons.addView(save, LinearLayout.LayoutParams(0, WRAP_P, 1f).also { it.marginEnd = dp(8) })
        buttons.addView(cancel, LinearLayout.LayoutParams(0, WRAP_P, 1f))
        if (index >= 0) {
            val del = Button(act)
            del.text = "Delete"
            style(del)
            del.setOnClickListener {
                AlertDialog.Builder(act).setTitle("Delete profile").setMessage("Delete \"" + profiles[index].name + "\"?")
                    .setPositiveButton("Delete") { _, _ ->
                        profiles.removeAt(index)
                        save()
                        showList()
                    }.setNegativeButton("Cancel", null).show()
            }
            buttons.addView(del, LinearLayout.LayoutParams(0, WRAP_P, 1f).also { it.marginStart = dp(8) })
        }
        col.addView(buttons, LinearLayout.LayoutParams(MATCH_P, WRAP_P).also { it.topMargin = dp(16) })
    }

    // ------------------------------------------------------------------ run

    private val runText = TextView(act)
    private val stopButton = Button(act)

    private fun addRunCard() {
        val c = card()
        val h = TextView(act)
        h.text = "Running: " + (run?.name ?: "")
        h.textSize = 16f
        h.setTypeface(null, android.graphics.Typeface.BOLD)
        h.setTextColor(if (dark) yellow else 0xFF9A7000.toInt())
        c.addView(h)
        (runText.parent as? ViewGroup)?.removeView(runText)
        runText.textSize = 14f
        c.addView(runText)
        (stopButton.parent as? ViewGroup)?.removeView(stopButton)
        stopButton.text = "Stop"
        style(stopButton, true)
        stopButton.setOnClickListener { stopRun("Stopped.", false) }
        c.addView(stopButton)
        updateRunText()
    }

    private fun startRun(p: Profile) {
        if (run != null || !link.connected) return
        io.execute {
            val s = link.getState()
            ui.post {
                when {
                    s == null -> Toast.makeText(act, "No answer from the station.", Toast.LENGTH_SHORT).show()
                    s.inMenu -> Toast.makeText(act, "The station is in its menu. Leave the menu first.", Toast.LENGTH_LONG).show()
                    else -> begin(p, s.set)
                }
            }
        }
    }

    private fun begin(p: Profile, currentSet: Int) {
        run = p
        stepIndex = 0
        phase = Phase.RAMP
        phaseStart = System.currentTimeMillis()
        fromTemp = currentSet.toFloat()
        startSet = currentSet
        setpoint = fromTemp
        lastSent = -1
        fails = 0
        ticks = 0
        busy = false
        runNote = ""
        answered = -1
        sentCount = 0
        act.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        AppLog.info("Profile started: " + p.name)
        onRunChanged(p.name)
        showList()
        ui.post(tick)
    }

    private val tick = object : Runnable {
        override fun run() {
            val p = this@ProfilesPage.run ?: return
            step(p)
            if (this@ProfilesPage.run != null) ui.postDelayed(this, 500)
        }
    }

    private fun step(p: Profile) {
        val now = System.currentTimeMillis()
        val st = p.steps[stepIndex]
        val elapsed = (now - phaseStart) / 1000f
        ticks++
        when (phase) {
            Phase.RAMP -> {
                setpoint = if (st.ramp <= 0) st.temp.toFloat() else fromTemp + (st.temp - fromTemp) * minOf(1f, elapsed / st.ramp)
                if (elapsed >= st.ramp) {
                    phase = if (p.waitForTemp) Phase.WAIT else Phase.HOLD
                    phaseStart = now
                }
            }
            Phase.WAIT -> {
                setpoint = st.temp.toFloat()
                val a = actual
                if (a == null || Math.abs(a - st.temp) <= 5f) {
                    phase = Phase.HOLD
                    phaseStart = now
                }
            }
            Phase.HOLD -> {
                setpoint = st.temp.toFloat()
                if (elapsed >= st.hold) {
                    fromTemp = st.temp.toFloat()
                    stepIndex++
                    phaseStart = now
                    phase = Phase.RAMP
                    if (stepIndex >= p.steps.size) {
                        finish(p)
                        return
                    }
                }
            }
        }
        val target = Math.round(setpoint)
        if (target != lastSent && !busy) send(target)
        if (ticks % 6 == 0 && !busy) checkMenu()
        updateRunText()
    }

    private fun send(t: Int) {
        busy = true
        io.execute {
            val r = link.setTemp(t)
            ui.post {
                busy = false
                if (run == null) return@post
                if (r == null) {
                    if (++fails >= 3) stopRun("Lost contact with the station. It stays at the last temperature.", false)
                } else {
                    fails = 0
                    lastSent = t
                    answered = r
                    sentCount++
                }
            }
        }
    }

    private fun checkMenu() {
        busy = true
        io.execute {
            val s = link.getState()
            ui.post {
                busy = false
                if (run != null && s != null && s.inMenu) stopRun("The station went into its menu: the profile stopped.", false)
            }
        }
    }

    private fun finish(p: Profile) {
        if (p.returnToStart) {
            io.execute { link.setTemp(startSet) }
        }
        stopRun("Finished" + if (p.returnToStart) ": back to " + startSet + " °C." else ".", true)
    }

    private fun stopRun(msg: String, ok: Boolean) {
        val p = run ?: return
        run = null
        ui.removeCallbacks(tick)
        act.window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        AppLog.info("Profile ended: " + p.name + " (" + msg + ")")
        Toast.makeText(act, p.name + ": " + msg, if (ok) Toast.LENGTH_SHORT else Toast.LENGTH_LONG).show()
        onRunChanged(null)
        if (!editing) showList()
    }

    private fun updateRunText() {
        val p = run ?: return
        val st = p.steps[stepIndex]
        val ph = when (phase) {
            Phase.RAMP -> "Ramp to " + st.temp + " °C"
            Phase.WAIT -> "Waiting for " + st.temp + " °C"
            Phase.HOLD -> "Hold " + st.temp + " °C"
        }
        val a = actual
        runText.text = "Step " + (stepIndex + 1) + " of " + p.steps.size + ": " + ph + "\nSet " + Math.round(setpoint) + " °C" +
                (if (a != null) ", measured " + Math.round(a) + " °C" else "")
    }

    /** measured tip temperature from the live data */
    fun onTemp(t: Float) {
        actual = t
    }

    /** refreshes the list (the Start buttons depend on the connection) */
    fun refresh() {
        if (!editing) showList()
    }

    fun onDisconnected() {
        stopRun("Connection lost. The station stays at the last temperature.", false)
    }

    companion object {
        private const val MATCH_P = ViewGroup.LayoutParams.MATCH_PARENT
        private const val WRAP_P = ViewGroup.LayoutParams.WRAP_CONTENT
    }
}
