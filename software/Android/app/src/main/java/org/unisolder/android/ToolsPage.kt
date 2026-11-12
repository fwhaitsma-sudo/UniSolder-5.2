package org.unisolder.android

import android.app.Activity
import android.app.AlertDialog
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.io.File
import java.util.Locale
import java.util.concurrent.ExecutorService

/** The Tools tab: recorded sessions with CSV export, tip health (heater resistance) and alerts. */
class ToolsPage(
    private val act: Activity,
    private val io: ExecutorService,
    private val prefs: Prefs
) {
    val view: View
    val recorder = Recorder(File(act.filesDir, "sessions"))

    /** Called with the new state when a recording starts or stops. */
    var onRecordingChanged: ((Boolean) -> Unit)? = null

    var ironId = 0

    private val recordButton: Button
    private val recordText: TextView
    private val sessionsBox: LinearLayout
    private val tipText: TextView
    private val reachedCheck: CheckBox
    private val overheatCheck: CheckBox
    private var exportFile: File? = null

    // tip health
    private var resSum = 0.0
    private var resCount = 0
    private var resMin = 0f
    private var resMax = 0f
    private var resNow = 0f
    private var sampleCount = 0

    // alerts
    private var wasReached = false
    private var notReachedSince = 0L
    private var overheatSince = 0L
    private var overheatAlerted = false
    private var overheatStartTemp = 0f
    private var lastSet = -1f
    private var quietUntil = 0L

    private fun dp(v: Int): Int = (v * act.resources.displayMetrics.density).toInt()

    init {
        val scroll = ScrollView(act)
        val col = LinearLayout(act)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(8), dp(16), dp(16))

        col.addView(title("Recording"))
        recordText = TextView(act)
        recordText.text = "Records set temperature, actual temperature, power and heater resistance to a file, five times a second."
        col.addView(recordText)
        recordButton = Button(act)
        recordButton.text = "Start recording"
        recordButton.setOnClickListener { toggleRecording() }
        col.addView(recordButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        col.addView(title("Sessions"))
        sessionsBox = LinearLayout(act)
        sessionsBox.orientation = LinearLayout.VERTICAL
        col.addView(sessionsBox)

        col.addView(title("Tip health"))
        tipText = TextView(act)
        tipText.textSize = 16f
        col.addView(tipText)
        val baseNote = TextView(act)
        baseNote.text = "The heater resistance of a good tip stays about the same. A rising value points to a worn tip or a poor contact. " +
                "Press the button with a new or good tip heated up, so later readings can be compared with it."
        col.addView(baseNote)
        val baseButton = Button(act)
        baseButton.text = "Use the current average as this iron's baseline"
        baseButton.setOnClickListener { setBaseline() }
        col.addView(baseButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        col.addView(title("Alerts"))
        reachedCheck = CheckBox(act)
        reachedCheck.text = "Tell me when the temperature is reached"
        reachedCheck.isChecked = prefs.getBool("alert_reached", false)
        reachedCheck.setOnCheckedChangeListener { _, on ->
            prefs.putBool("alert_reached", on)
            if (on) askNotificationPermission()
        }
        col.addView(reachedCheck)
        overheatCheck = CheckBox(act)
        overheatCheck.text = "Warn when the tip stays 25°C above the target and is not cooling"
        overheatCheck.isChecked = prefs.getBool("alert_overheat", false)
        overheatCheck.setOnCheckedChangeListener { _, on ->
            prefs.putBool("alert_overheat", on)
            if (on) askNotificationPermission()
        }
        col.addView(overheatCheck)
        val alertNote = TextView(act)
        alertNote.text = "Alerts only work while the app is running: keep it open or in the background, and keep the cable connected. " +
                "The app can warn but it cannot switch the heater off."
        col.addView(alertNote)

        scroll.addView(col, ViewGroup.LayoutParams(MATCH_P, WRAP_P))
        view = scroll
        showTip()
        refreshSessions()
    }

    private fun title(text: String): TextView {
        val t = TextView(act)
        t.text = text
        t.textSize = 18f
        t.setPadding(0, dp(20), 0, dp(4))
        return t
    }

    // ------------------------------------------------------------------ recording

    fun toggleRecording() {
        if (recorder.recording) {
            recorder.stop()
            refreshSessions()
        } else {
            if (!recorder.start()) {
                Toast.makeText(act, "Cannot start recording", Toast.LENGTH_SHORT).show()
                return
            }
        }
        updateRecordUi()
        onRecordingChanged?.invoke(recorder.recording)
    }

    private fun updateRecordUi() {
        recordButton.text = if (recorder.recording) "Stop recording" else "Start recording"
    }

    fun setActive(a: Boolean) {
        if (a) refreshSessions()
    }

    private fun refreshSessions() {
        val dir = File(act.filesDir, "sessions")
        io.execute {
            val files = Recorder.sessions(dir)
            val stats = files.map { SessionStats.read(it) }
            act.runOnUiThread {
                sessionsBox.removeAllViews()
                if (files.isEmpty()) {
                    val none = TextView(act)
                    none.text = "No recordings yet."
                    sessionsBox.addView(none)
                }
                for (i in files.indices) {
                    val f = files[i]
                    val isCurrent = recorder.recording && recorder.file?.name == f.name
                    val label = TextView(act)
                    label.text = f.name.removePrefix("session-").removeSuffix(".csv") + "\n" +
                            (if (isCurrent) "recording ..." else stats[i]?.text() ?: "empty")
                    label.setPadding(0, dp(8), 0, 0)
                    sessionsBox.addView(label)
                    if (!isCurrent) {
                        val row = LinearLayout(act)
                        row.orientation = LinearLayout.HORIZONTAL
                        val export = Button(act)
                        export.text = "Export"
                        export.setOnClickListener { exportSession(f) }
                        val delete = Button(act)
                        delete.text = "Delete"
                        delete.setOnClickListener { confirmDelete(f) }
                        row.addView(export, LinearLayout.LayoutParams(0, WRAP_P, 1f))
                        row.addView(delete, LinearLayout.LayoutParams(0, WRAP_P, 1f))
                        sessionsBox.addView(row)
                    }
                }
            }
        }
    }

    private fun exportSession(f: File) {
        exportFile = f
        val intent = Intent(Intent.ACTION_CREATE_DOCUMENT)
        intent.addCategory(Intent.CATEGORY_OPENABLE)
        intent.type = "text/csv"
        intent.putExtra(Intent.EXTRA_TITLE, f.name)
        act.startActivityForResult(intent, REQ_EXPORT)
    }

    private fun confirmDelete(f: File) {
        AlertDialog.Builder(act).setTitle("Delete recording")
            .setMessage("Delete " + f.name + "?")
            .setPositiveButton("Delete") { _, _ ->
                f.delete()
                refreshSessions()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    /** Returns true when the result belonged to this page. */
    fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != REQ_EXPORT) return false
        if (resultCode != Activity.RESULT_OK) return true
        val uri = data?.data ?: return true
        val f = exportFile ?: return true
        try {
            act.contentResolver.openOutputStream(uri, "w")?.use { out -> f.inputStream().use { it.copyTo(out) } }
            Toast.makeText(act, "Exported", Toast.LENGTH_SHORT).show()
        } catch (e: Exception) {
            Toast.makeText(act, "Could not export: " + e.message, Toast.LENGTH_LONG).show()
        }
        return true
    }

    // ------------------------------------------------------------------ live data

    fun onSample(s: Sample, power: Float) {
        recorder.add(s, power)
        if (recorder.recording && ++sampleCount % 25 == 0) {
            recordText.text = "Recording " + recorder.elapsedSeconds + " s, " + recorder.rows + " rows"
        }
        val now = System.currentTimeMillis()

        // tip health: the heater resistance is only meaningful while it is measured (not zero)
        if (s.resistance > 0.05f) {
            resNow = s.resistance
            resSum += s.resistance
            resCount++
            if (resCount == 1 || s.resistance < resMin) resMin = s.resistance
            if (resCount == 1 || s.resistance > resMax) resMax = s.resistance
            if (resCount % 25 == 0) showTip()
        }

        // alerts
        val reached = s.reached > 0f
        if (!reached) {
            if (wasReached || notReachedSince == 0L) notReachedSince = now
        }
        if (reached && !wasReached && notReachedSince != 0L && now - notReachedSince >= 5000 && reachedCheck.isChecked) {
            alert("Temperature reached", String.format(Locale.US, "%.0f°C", s.temp))
        }
        wasReached = reached

        // Overheat: the tip stays well above the target and is not cooling down.
        // The target (the "set" in the live data) drops when the iron goes in the stand or to sleep, and the tip then cools
        // for a while above the new, lower target: that is no overheating. Any change of the target restarts the check,
        // and after a lowering the check stays quiet for two minutes.
        if (lastSet >= 0f && s.set != lastSet) {
            overheatSince = 0L
            overheatAlerted = false
            quietUntil = now + (if (s.set < lastSet) 120000L else 10000L)
        }
        lastSet = s.set
        if (s.set > 0f && s.temp > s.set + 25f && now >= quietUntil) {
            if (overheatSince == 0L) {
                overheatSince = now
                overheatStartTemp = s.temp
            }
            if (now - overheatSince >= 10000) {
                if (s.temp < overheatStartTemp - 2f) {
                    // still cooling: look again from here
                    overheatSince = now
                    overheatStartTemp = s.temp
                } else if (!overheatAlerted && overheatCheck.isChecked) {
                    overheatAlerted = true
                    alert("Tip too hot", String.format(Locale.US, "%.0f\u00B0C, set %.0f\u00B0C", s.temp, s.set))
                }
            }
        } else if (s.temp < s.set + 10f || s.set <= 0f || now < quietUntil) {
            overheatSince = 0L
            overheatAlerted = false
        }
    }

    fun onDisconnected() {
        resSum = 0.0
        resCount = 0
        wasReached = false
        notReachedSince = 0L
        overheatSince = 0L
        overheatAlerted = false
        lastSet = -1f
        quietUntil = 0L
        showTip()
    }

    // ------------------------------------------------------------------ tip health

    private fun baselineKey() = "baseline_" + ironId

    private fun setBaseline() {
        if (resCount < 25) {
            Toast.makeText(act, "Not enough readings yet: heat the iron up first", Toast.LENGTH_LONG).show()
            return
        }
        prefs.putFloat(baselineKey(), (resSum / resCount).toFloat())
        showTip()
    }

    fun showTip() {
        if (resCount == 0) {
            tipText.text = "No heater resistance readings yet (iron ID " + hex(ironId) + ")."
            return
        }
        val avg = (resSum / resCount).toFloat()
        val base = prefs.getFloat(baselineKey(), 0f)
        val sb = StringBuilder()
        sb.append(String.format(Locale.US, "Now %.1f Ω, average %.1f Ω, range %.1f - %.1f Ω", resNow, avg, resMin, resMax))
        if (base > 0f) {
            val change = (avg - base) / base * 100f
            sb.append(String.format(Locale.US, "\nBaseline %.1f Ω: %+.0f%%", base, change))
            sb.append(
                when {
                    change > 25f -> "\nMuch higher than the baseline: worn tip or poor contact."
                    change > 10f -> "\nSlightly higher than the baseline: keep an eye on it."
                    change < -25f -> "\nMuch lower than the baseline: check the tip and the connection."
                    else -> "\nClose to the baseline."
                }
            )
        } else {
            sb.append("\nNo baseline saved for this iron (ID ").append(hex(ironId)).append(").")
        }
        tipText.text = sb.toString()
    }

    private fun hex(v: Int) = "0x" + v.toString(16).uppercase()

    // ------------------------------------------------------------------ alerts

    private fun askNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33 && act.checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED) {
            act.requestPermissions(arrayOf("android.permission.POST_NOTIFICATIONS"), REQ_NOTIFY)
        }
    }

    private fun alert(title: String, text: String) {
        AppLog.info("Alert: $title $text")
        try {
            val v = act.getSystemService(Vibrator::class.java)
            v?.vibrate(VibrationEffect.createOneShot(400, VibrationEffect.DEFAULT_AMPLITUDE))
        } catch (e: Exception) {
        }
        Toast.makeText(act, "$title: $text", Toast.LENGTH_LONG).show()
        if (Build.VERSION.SDK_INT >= 33 && act.checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED) return
        val nm = act.getSystemService(NotificationManager::class.java) ?: return
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "UniSolder alerts", NotificationManager.IMPORTANCE_HIGH))
        val n = Notification.Builder(act, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setAutoCancel(true)
            .build()
        nm.notify(NOTIFY_ID, n)
    }

    companion object {
        const val REQ_EXPORT = 4
        const val REQ_NOTIFY = 10
        private const val CHANNEL = "alerts"
        private const val NOTIFY_ID = 1
        private const val MATCH_P = ViewGroup.LayoutParams.MATCH_PARENT
        private const val WRAP_P = ViewGroup.LayoutParams.WRAP_CONTENT
    }
}
