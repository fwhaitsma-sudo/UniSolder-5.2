package org.unisolder.android

import android.app.Activity
import android.app.AlertDialog
import android.text.InputType
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import java.util.Locale
import java.util.concurrent.ExecutorService

/** Result of one analysed step of the set temperature. */
class StepResult(
    val stepFrom: Float,
    val target: Float,
    val riseS: Double?,
    val overshoot: Float,
    val settleS: Double?,
    val steadyError: Float,
    val pidText: String
) {
    fun text(): String {
        val sb = StringBuilder()
        sb.append(String.format(Locale.US, "%.0f → %.0f°C  (%s)\n", stepFrom, target, pidText))
        sb.append("rise 10-90%: ").append(if (riseS != null) String.format(Locale.US, "%.1f s", riseS) else "not reached")
        sb.append(String.format(Locale.US, "   overshoot: %.0f°C (%.0f%%)", overshoot, overshoot / (target - stepFrom) * 100f))
        sb.append("\nsettled within ±3°C: ").append(if (settleS != null) String.format(Locale.US, "%.1f s", settleS) else "not settled")
        sb.append(String.format(Locale.US, "   steady error: %+.1f°C", steadyError))
        return sb.toString()
    }
}

/** Watches the live data for a rise of the set temperature and measures how the iron follows it. */
class StepAnalyzer {
    private var lastSet = 0f
    private var running = false
    private var t0 = 0L
    private var from = 0f
    private var target = 0f
    private val ts = ArrayList<Double>()
    private val temps = ArrayList<Float>()
    private var reachedSince = 0L

    /** Returns a result when a step has finished, otherwise null. */
    fun add(now: Long, s: Sample, pidText: String): StepResult? {
        var result: StepResult? = null
        if (running) {
            if (s.set != target) {
                // the set temperature moved again: the measurement is not clean
                running = false
            } else {
                ts.add((now - t0) / 1000.0)
                temps.add(s.temp)
                if (s.reached > 0f) {
                    if (reachedSince == 0L) reachedSince = now
                } else {
                    reachedSince = 0L
                }
                val settledLong = reachedSince != 0L && now - reachedSince >= 15000
                if (settledLong || now - t0 >= 90000) {
                    running = false
                    result = analyse(pidText)
                }
            }
        } else if (lastSet > 0f && s.set - lastSet >= 20f && s.set > 0f) {
            running = true
            t0 = now
            from = s.temp
            target = s.set
            ts.clear()
            temps.clear()
            reachedSince = 0L
        }
        lastSet = s.set
        return result
    }

    val isRunning: Boolean get() = running

    fun reset() {
        running = false
        lastSet = 0f
    }

    private fun analyse(pidText: String): StepResult? {
        val step = target - from
        if (step < 15f || temps.size < 20) return null
        var t10: Double? = null
        var t90: Double? = null
        var max = temps[0]
        for (i in temps.indices) {
            val v = temps[i]
            if (v > max) max = v
            if (t10 == null && v >= from + 0.1f * step) t10 = ts[i]
            if (t90 == null && v >= from + 0.9f * step) t90 = ts[i]
        }
        val rise = if (t10 != null && t90 != null) t90 - t10 else null
        var lastOutside = -1
        for (i in temps.indices) if (Math.abs(temps[i] - target) > 3f) lastOutside = i
        val settle = when {
            lastOutside < 0 -> 0.0
            lastOutside >= temps.size - 1 -> null
            else -> ts[lastOutside + 1]
        }
        val end = ts[ts.size - 1]
        var sum = 0f
        var n = 0
        for (i in temps.indices) if (ts[i] >= end - 10.0) {
            sum += temps[i] - target
            n++
        }
        return StepResult(from, target, rise, Math.max(0f, max - target), settle, if (n > 0) sum / n else 0f, pidText)
    }
}

/**
 * Extra tools on the PID tab: step-response analysis with an optional step test, saved PID sets per iron,
 * revert to the values read at connect, and a calibration helper that suggests the Gain.
 */
class PidTools(
    private val act: Activity,
    private val io: ExecutorService,
    private val link: DeviceLink,
    private val prefs: Prefs,
    private val getPid: () -> Pid?,
    private val applyPid: (Pid) -> Unit
) {
    val view = LinearLayout(act)
    var ironId = 0
        set(v) {
            field = v
            refreshSets()
        }
    private var original: Pid? = null
    private val analyzer = StepAnalyzer()
    private val results = ArrayList<String>()
    private val resultText = TextView(act)
    private val testButton = Button(act)
    private val setsBox = LinearLayout(act)
    private val calRef = EditText(act)
    private val calAmb = EditText(act)
    private val calText = TextView(act)
    private val storedText = TextView(act)
    private val calApply = Button(act)
    private var calSuggest = 0
    private var lastTemp = 0f
    private var lastReached = false
    private var restoreTo = -1
    private val ui = android.os.Handler(android.os.Looper.getMainLooper())
    private val restoreRunnable = Runnable { restoreTemp() }

    private fun dp(v: Int): Int = (v * act.resources.displayMetrics.density).toInt()

    private fun title(text: String): TextView {
        val t = TextView(act)
        t.text = text
        t.textSize = 18f
        t.setPadding(0, dp(24), 0, dp(4))
        return t
    }

    init {
        view.orientation = LinearLayout.VERTICAL
        val m = ViewGroup.LayoutParams.MATCH_PARENT
        val w = ViewGroup.LayoutParams.WRAP_CONTENT

        // revert
        val revert = Button(act)
        revert.text = "Revert to the values read at connect"
        revert.setOnClickListener {
            val o = original
            if (o == null) Toast.makeText(act, "No values read yet", Toast.LENGTH_SHORT).show() else applyPid(copy(o))
        }
        view.addView(revert, LinearLayout.LayoutParams(m, w))

        // permanent storage in the station
        view.addView(title("Save in the station"))
        val keepNote = TextView(act)
        keepNote.text = "Normally the iron forgets changed PID and Gain values when it restarts. Saving keeps the values now on the sliders in the station for this iron, " +
                "and they are loaded again every time this iron is detected. Needs the remote-control firmware (version 2 of the protocol)."
        view.addView(keepNote)
        storedText.setPadding(0, dp(4), 0, dp(4))
        view.addView(storedText)
        val keep = Button(act)
        keep.text = "Save the current values in the station"
        keep.setOnClickListener { saveInStation() }
        view.addView(keep, LinearLayout.LayoutParams(m, w))
        val forget = Button(act)
        forget.text = "Forget the values saved in the station"
        forget.setOnClickListener { forgetInStation() }
        view.addView(forget, LinearLayout.LayoutParams(m, w))

        // saved sets
        view.addView(title("Saved PID sets"))
        val saveNote = TextView(act)
        saveNote.text = "Sets are kept per iron ID on this phone. Loading a set sends it to the iron now; the iron forgets it when it restarts."
        view.addView(saveNote)
        val save = Button(act)
        save.text = "Save the current values as a set"
        save.setOnClickListener { askSetName() }
        view.addView(save, LinearLayout.LayoutParams(m, w))
        setsBox.orientation = LinearLayout.VERTICAL
        view.addView(setsBox)

        // step response
        view.addView(title("Step response"))
        val stepNote = TextView(act)
        stepNote.text = "Whenever the set temperature is raised by 20°C or more, the app measures how the iron follows it. " +
                "Use the test button to do that by itself: it raises the set temperature by 30°C and puts it back afterwards."
        view.addView(stepNote)
        testButton.text = "Run step test (+30°C)"
        testButton.setOnClickListener { startStepTest() }
        view.addView(testButton, LinearLayout.LayoutParams(m, w))
        resultText.text = "No step measured yet."
        resultText.setPadding(0, dp(8), 0, 0)
        view.addView(resultText)

        // calibration
        view.addView(title("Calibration helper"))
        val calNote = TextView(act)
        calNote.text = "Heat the iron to a steady temperature, measure the tip with a reference thermometer, and enter its reading. " +
                "The app suggests a new Gain. It is an estimate that assumes the room temperature below: check it by measuring again."
        view.addView(calNote)
        calRef.hint = "Reference temperature (°C)"
        calRef.inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL
        view.addView(calRef, LinearLayout.LayoutParams(m, w))
        calAmb.hint = "Room temperature (°C)"
        calAmb.setText("25")
        calAmb.inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL
        view.addView(calAmb, LinearLayout.LayoutParams(m, w))
        val calc = Button(act)
        calc.text = "Suggest Gain"
        calc.setOnClickListener { suggestGain() }
        view.addView(calc, LinearLayout.LayoutParams(m, w))
        view.addView(calText)
        calApply.text = "Apply suggested Gain"
        calApply.visibility = View.GONE
        calApply.setOnClickListener {
            val p = getPid()
            if (p != null && calSuggest > 0) {
                val n = copy(p)
                n.gain = calSuggest
                applyPid(n)
                Toast.makeText(act, "Applied. Use \"Save the current values in the station\" to keep it after a restart.", Toast.LENGTH_LONG).show()
                calApply.visibility = View.GONE
            }
        }
        view.addView(calApply, LinearLayout.LayoutParams(m, w))
        refreshSets()
    }

    private fun copy(p: Pid) = Pid(p.gain, p.offset, p.kp, p.ki, p.dGain, p.ovsGain)

    private fun pidText(): String {
        val p = getPid() ?: return "PID unknown"
        return String.format(Locale.US, "Kp %.2f Ki %.3f D %d OVS %d Gain %d", p.kp / 32767.0, p.ki / 32767.0, p.dGain, p.ovsGain, p.gain)
    }

    /** Remember the values read from the iron the first time after connecting or changing iron. */
    fun onPidLoaded(p: Pid) {
        if (original == null) original = copy(p)
        refreshStored()
    }

    // ------------------------------------------------------------------ permanent storage in the station

    private fun refreshStored() {
        io.execute {
            val info = link.appInfo()
            val text = when {
                info == null -> "Cannot tell: no answer from the station."
                info.protocol < 2 -> "This station firmware cannot store PID values. Update the firmware."
                else -> when (link.pidSaved()) {
                    true -> "Values are saved in the station for this iron."
                    false -> "No values saved in the station for this iron: it uses its built-in values."
                    null -> "Cannot tell: no answer from the station."
                }
            }
            ui.post { storedText.text = text }
        }
    }

    private fun saveInStation() {
        val shown = getPid()
        if (shown == null) {
            Toast.makeText(act, "No PID values read from the iron", Toast.LENGTH_SHORT).show()
            return
        }
        AlertDialog.Builder(act).setTitle("Save in the station")
            .setMessage("Store " + pidText() + " in the station for this iron? It uses them every time this iron is detected, until you forget them.")
            .setPositiveButton("Save") { _, _ ->
                io.execute {
                    val r = link.pidSave()
                    val msg = when (r) {
                        0 -> "Saved in the station"
                        1 -> "No iron detected"
                        2 -> "The station refused the values: out of range"
                        3 -> "The station has no free space for another iron"
                        else -> "No answer from the station: firmware too old?"
                    }
                    AppLog.info("Save PID in station: $msg")
                    ui.post { Toast.makeText(act, msg, Toast.LENGTH_LONG).show() }
                    refreshStored()
                }
            }
            .setNegativeButton("Cancel", null).show()
    }

    private fun forgetInStation() {
        io.execute {
            val r = link.pidClear()
            val msg = when (r) {
                0 -> "Forgotten: the iron uses its built-in values again"
                1 -> "No iron detected"
                2 -> "Nothing was saved for this iron"
                else -> "No answer from the station: firmware too old?"
            }
            AppLog.info("Forget saved PID: $msg")
            ui.post { Toast.makeText(act, msg, Toast.LENGTH_LONG).show() }
            refreshStored()
        }
    }

    fun forgetOriginal() {
        original = null
    }

    fun onDisconnected() {
        original = null
        analyzer.reset()
        if (restoreTo >= 0) {
            ui.removeCallbacks(restoreRunnable)
            restoreTo = -1
        }
        testButton.isEnabled = true
    }

    // ------------------------------------------------------------------ live data

    fun onSample(s: Sample) {
        lastTemp = s.temp
        lastReached = s.reached > 0f
        val r = analyzer.add(System.currentTimeMillis(), s, pidText())
        if (r != null) {
            results.add(0, r.text())
            while (results.size > 5) results.removeAt(results.size - 1)
            resultText.text = results.joinToString("\n\n")
            AppLog.info("Step measured: " + r.text().replace("\n", " "))
            if (restoreTo >= 0) restoreTemp()
        } else if (!analyzer.isRunning && restoreTo >= 0 && !testWaiting) {
            // the step was abandoned (set temperature moved again): put the temperature back anyway
            restoreTemp()
        }
    }

    // ------------------------------------------------------------------ step test

    private var testWaiting = false

    private fun startStepTest() {
        if (!link.connected) {
            Toast.makeText(act, "The station is not connected", Toast.LENGTH_SHORT).show()
            return
        }
        if (!lastReached) {
            Toast.makeText(act, "Wait until the iron has reached its set temperature", Toast.LENGTH_LONG).show()
            return
        }
        testButton.isEnabled = false
        io.execute {
            val st = link.getState()
            var msg: String? = null
            var from = 0
            if (st == null) {
                msg = "No answer from the station"
            } else if (st.inMenu || st.standby || st.manualSleep) {
                msg = "The station is in its menu or asleep"
            } else if (st.set + 30 > st.max) {
                msg = "The set temperature is too close to the maximum for a +30°C step"
            } else {
                from = st.set
                val applied = link.setTemp(st.set + 30)
                if (applied == null || applied - from < 20) msg = "The station did not accept the new set temperature"
            }
            val m = msg
            val f = from
            ui.post {
                if (m != null) {
                    testButton.isEnabled = true
                    Toast.makeText(act, m, Toast.LENGTH_LONG).show()
                } else {
                    restoreTo = f
                    testWaiting = true
                    // the analyzer starts by itself when the live data shows the rise; give up and restore after 2 minutes
                    ui.removeCallbacks(restoreRunnable)
                    ui.postDelayed(restoreRunnable, 120000)
                    ui.postDelayed({ testWaiting = false }, 3000)
                    resultText.text = "Step test running ...\n\n" + results.joinToString("\n\n")
                }
            }
        }
    }

    private fun restoreTemp() {
        val back = restoreTo
        restoreTo = -1
        ui.removeCallbacks(restoreRunnable)
        testButton.isEnabled = true
        if (back < 0) return
        io.execute {
            val ok = link.setTemp(back)
            AppLog.info("Step test: set temperature restored to $back" + if (ok == null) " (no answer)" else "")
            if (ok == null) ui.post { Toast.makeText(act, "Could not restore the set temperature: check the station", Toast.LENGTH_LONG).show() }
        }
    }

    // ------------------------------------------------------------------ saved sets

    private fun key() = "pidsets_$ironId"

    private fun loadSets(): MutableList<Pair<String, Pid>> {
        val out = ArrayList<Pair<String, Pid>>()
        for (line in prefs.getString(key(), "").split('\n')) {
            val c = line.split('|')
            if (c.size != 6) continue
            val v = c.subList(1, 6).map { it.toIntOrNull() ?: return@map -1 }
            if (v.any { it < 0 }) continue
            out.add(Pair(c[0], Pid(v[4], 0, v[0], v[1], v[2], v[3])))
        }
        return out
    }

    private fun storeSets(list: List<Pair<String, Pid>>) {
        prefs.putString(key(), list.joinToString("\n") { (n, p) -> "$n|${p.kp}|${p.ki}|${p.dGain}|${p.ovsGain}|${p.gain}" })
    }

    private fun askSetName() {
        val p = getPid()
        if (p == null) {
            Toast.makeText(act, "No PID values read from the iron", Toast.LENGTH_SHORT).show()
            return
        }
        val input = EditText(act)
        input.hint = "Name, e.g. T15 fine tip"
        AlertDialog.Builder(act).setTitle("Save PID set").setView(input)
            .setPositiveButton("Save") { _, _ ->
                val name = input.text.toString().replace('|', ' ').replace('\n', ' ').trim()
                if (name.isEmpty()) return@setPositiveButton
                val list = loadSets().filter { it.first != name }.toMutableList()
                list.add(Pair(name, copy(p)))
                storeSets(list)
                refreshSets()
            }
            .setNegativeButton("Cancel", null).show()
    }

    private fun refreshSets() {
        setsBox.removeAllViews()
        val list = loadSets()
        if (list.isEmpty()) {
            val none = TextView(act)
            none.text = "No saved sets for iron ID 0x" + ironId.toString(16).uppercase() + "."
            setsBox.addView(none)
        }
        for ((name, p) in list) {
            val row = LinearLayout(act)
            row.orientation = LinearLayout.HORIZONTAL
            row.gravity = android.view.Gravity.CENTER_VERTICAL
            val label = TextView(act)
            label.text = name + "\n" + String.format(Locale.US, "Kp %.2f Ki %.3f D %d OVS %d Gain %d", p.kp / 32767.0, p.ki / 32767.0, p.dGain, p.ovsGain, p.gain)
            label.textSize = 13f
            row.addView(label, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            val load = Button(act)
            load.text = "Load"
            load.minWidth = 0
            load.setOnClickListener {
                val cur = getPid()
                if (cur == null) {
                    Toast.makeText(act, "No PID values read from the iron", Toast.LENGTH_SHORT).show()
                } else {
                    applyPid(Pid(p.gain, cur.offset, p.kp, p.ki, p.dGain, p.ovsGain))
                }
            }
            row.addView(load)
            val del = Button(act)
            del.text = "Delete"
            del.minWidth = 0
            del.setOnClickListener {
                storeSets(loadSets().filter { it.first != name })
                refreshSets()
            }
            row.addView(del)
            setsBox.addView(row)
        }
    }

    // ------------------------------------------------------------------ calibration

    private fun suggestGain() {
        val p = getPid()
        val ref = calRef.text.toString().toFloatOrNull()
        val amb = calAmb.text.toString().toFloatOrNull()
        calApply.visibility = View.GONE
        if (p == null) {
            calText.text = "No PID values read from the iron."
            return
        }
        if (ref == null || amb == null || ref <= amb + 30f) {
            calText.text = "Enter the reference temperature (it must be well above the room temperature) and the room temperature."
            return
        }
        if (!lastReached || lastTemp <= amb + 30f) {
            calText.text = "The iron must be heated up and steady at its set temperature."
            return
        }
        val raw = p.gain * (lastTemp - amb) / (ref - amb)
        val n = Math.round(raw).coerceIn(8, 256)
        calSuggest = n
        calText.text = String.format(Locale.US, "The station shows %.0f°C, the reference %.0f°C. Gain %d → %d (%+.1f%%).", lastTemp, ref, p.gain, n, (n - p.gain) * 100f / p.gain)
        if (n != p.gain) calApply.visibility = View.VISIBLE
    }
}
