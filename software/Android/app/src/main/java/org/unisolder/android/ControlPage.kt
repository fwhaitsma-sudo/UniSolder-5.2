package org.unisolder.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Typeface
import android.text.Spannable
import android.text.SpannableString
import android.text.style.RelativeSizeSpan
import android.text.style.StyleSpan
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.GridLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.ExecutorService

/** The Control tab: set temperature, presets, sleep, switch iron, station info, settings backup and restore. */
class ControlPage(private val act: Activity, private val link: DeviceLink, private val io: ExecutorService) {
    val view: View
    private val settings = SettingsPage(act, link, io)
    private val ramp = RampPage(act, link, io)
    private val settingsButton: Button
    private val ui = Handler(Looper.getMainLooper())

    private var info: AppInfo? = null
    private var state: DeviceState? = null
    private var active = false
    private var dragging = false
    private var holdUntil = 0L      // do not overwrite the slider with the polled state for a moment after a change
    private var pendingTemp = -1
    private var sendPending = false

    private val infoText: TextView
    private val stateText: TextView
    private val tempText: TextView
    private val tempBar: SeekBar
    private val minusButton: Button
    private val plusButton: Button
    private val presetButtons = ArrayList<Button>()
    private val sleepButton: Button
    private val ironButton: Button
    private val saveButton: Button
    private val restoreButton: Button

    private var backupText: String? = null

    private val sendRunnable = Runnable {
        sendPending = false
        val t = pendingTemp
        if (t >= 0) sendTemp(t)
    }

    private val pollTick = object : Runnable {
        override fun run() {
            if (active && link.connected && !link.busy && info != null) {
                io.execute {
                    val s = link.getState()
                    ui.post { if (s != null) applyState(s) }
                }
            }
            ui.postDelayed(this, 1000)
        }
    }

    private fun dp(v: Int): Int = (v * act.resources.displayMetrics.density).toInt()

    init {
        val scroll = ScrollView(act)
        val col = LinearLayout(act)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(8), dp(16), dp(16))

        infoText = TextView(act)
        infoText.textSize = 14f
        col.addView(infoText)

        stateText = TextView(act)
        stateText.textSize = 16f
        stateText.setPadding(0, dp(8), 0, 0)
        col.addView(stateText)

        tempText = TextView(act)
        tempText.textSize = 44f
        tempText.gravity = Gravity.CENTER
        tempText.text = "—"
        col.addView(tempText, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        val row = LinearLayout(act)
        row.orientation = LinearLayout.HORIZONTAL
        row.gravity = Gravity.CENTER_VERTICAL
        minusButton = Button(act)
        minusButton.text = "−"
        minusButton.setOnClickListener { stepTemp(-1) }
        row.addView(minusButton)
        tempBar = SeekBar(act)
        tempBar.thumbTintList = android.content.res.ColorStateList.valueOf(0xFFFFC107.toInt())
        tempBar.progressTintList = android.content.res.ColorStateList.valueOf(0xFFFFC107.toInt())
        tempBar.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                val s = state ?: return
                if (fromUser) {
                    showTemp(s.min + progress, s)
                    queueTemp(s.min + progress)
                }
            }

            override fun onStartTrackingTouch(sb: SeekBar?) {
                dragging = true
            }

            override fun onStopTrackingTouch(sb: SeekBar?) {
                dragging = false
                val s = state ?: return
                ui.removeCallbacks(sendRunnable)
                sendTemp(s.min + tempBar.progress)
            }
        })
        row.addView(tempBar, LinearLayout.LayoutParams(0, WRAP_P, 1f))
        plusButton = Button(act)
        plusButton.text = "+"
        plusButton.setOnClickListener { stepTemp(1) }
        row.addView(plusButton)
        col.addView(row, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        val grid = GridLayout(act)
        grid.columnCount = 4
        for (i in 0 until 4) {
            val b = Button(act)
            b.text = "T" + (i + 1)
            b.tag = "multiline"      // spaceButtons() leaves these two-line buttons alone
            b.maxLines = 2
            b.minHeight = dp(60)
            b.minimumHeight = dp(60)
            b.setOnClickListener { selectPreset(i) }
            presetButtons.add(b)
            val lp = GridLayout.LayoutParams()
            lp.width = 0
            lp.columnSpec = GridLayout.spec(i, 1f)
            grid.addView(b, lp)
        }
        col.addView(grid, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        sleepButton = Button(act)
        sleepButton.text = "Sleep"
        sleepButton.setOnClickListener { toggleSleep() }
        col.addView(sleepButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        ironButton = Button(act)
        ironButton.text = "Switch iron"
        ironButton.visibility = View.GONE
        ironButton.setOnClickListener { switchIron() }
        col.addView(ironButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        settingsButton = Button(act)
        settingsButton.text = "Station settings"
        settingsButton.setOnClickListener { openSettings(true) }
        col.addView(settingsButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P).also { it.topMargin = dp(16) })

        val rampButton = Button(act)
        rampButton.text = "Ramp mode"
        rampButton.setOnClickListener { ramp.view.visibility = View.VISIBLE; ramp.setVisible(true) }
        col.addView(rampButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P).also { it.topMargin = dp(8) })

        val backupTitle = TextView(act)
        backupTitle.text = "Settings backup"
        backupTitle.textSize = 18f
        backupTitle.setPadding(0, dp(24), 0, dp(4))
        col.addView(backupTitle)
        val backupNote = TextView(act)
        backupNote.text = "Backs up all station settings, saved PID values and the ramp mode values, active profile and stored profiles to a file; restoring restarts the station."
        col.addView(backupNote)
        saveButton = Button(act)
        saveButton.text = "Save settings to file"
        saveButton.setOnClickListener { saveSettings() }
        col.addView(saveButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P))
        restoreButton = Button(act)
        restoreButton.text = "Restore settings from file"
        restoreButton.setOnClickListener { pickRestoreFile() }
        col.addView(restoreButton, LinearLayout.LayoutParams(MATCH_P, WRAP_P))

        scroll.addView(col, ViewGroup.LayoutParams(MATCH_P, WRAP_P))
        val frame = android.widget.FrameLayout(act)
        frame.addView(scroll, ViewGroup.LayoutParams(MATCH_P, MATCH_P))
        frame.addView(settings.view, ViewGroup.LayoutParams(MATCH_P, MATCH_P))
        frame.addView(ramp.view, ViewGroup.LayoutParams(MATCH_P, MATCH_P))
        settings.view.visibility = View.GONE
        ramp.view.visibility = View.GONE
        settings.onBack = { openSettings(false) }
        ramp.protocol = { info?.protocol ?: 0 }
        ramp.onBack = { ramp.view.visibility = View.GONE; ramp.setVisible(false) }
        view = frame
        enableControls(false)
        showInfo()
        ui.post(pollTick)
    }

    // ------------------------------------------------------------------ connection and state

    fun setActive(a: Boolean) {
        active = a
        if (a) refreshNow()
    }

    fun onConnected() {
        info = null
        state = null
        showInfo()
        io.execute {
            val i = link.appInfo()
            val s = if (i != null && i.protocol >= 1) link.getState() else null
            ui.post {
                info = i
                showInfo()
                if (s != null) applyState(s)
            }
        }
    }

    /** Closes the settings page when it is open; returns true if it was. */
    fun closeSettings(): Boolean {
        if (ramp.view.visibility == View.VISIBLE) {
            ramp.view.visibility = View.GONE
            ramp.setVisible(false)
            return true
        }
        if (settings.view.visibility != View.VISIBLE) return false
        openSettings(false)
        return true
    }

    private fun openSettings(open: Boolean) {
        settings.view.visibility = if (open) View.VISIBLE else View.GONE
        (view as android.widget.FrameLayout).getChildAt(0).visibility = if (open) View.GONE else View.VISIBLE
        if (open) settings.reload()
    }

    fun onDisconnected() {
        ramp.onDisconnected()
        settings.onDisconnected()
        openSettings(false)
        info = null
        state = null
        showInfo()
        enableControls(false)
        tempText.text = "—"
        stateText.text = ""
    }

    fun destroy() {
        ui.removeCallbacks(pollTick)
        ui.removeCallbacks(sendRunnable)
    }

    private fun refreshNow() {
        if (!link.connected || link.busy || info == null) return
        io.execute {
            val s = link.getState()
            ui.post { if (s != null) applyState(s) }
        }
    }

    private fun showInfo() {
        val i = info
        infoText.text = when {
            !link.connected -> "Not connected."
            i == null -> "Checking the station firmware ..."
            i.protocol < 1 -> "This station firmware has no remote control. Update the firmware on the Firmware tab."
            else -> "Station firmware " + i.major + "." + i.minor + ", board " + i.boardName + ", remote control v" + i.protocol +
                    (if (i.protocol < 3) "\nStation settings need firmware with remote control v3: update it on the Firmware tab." else "")
        }
    }

    private fun enableControls(on: Boolean) {
        val s = state
        val canSet = on && s != null && !s.inMenu
        tempBar.isEnabled = canSet
        minusButton.isEnabled = canSet
        plusButton.isEnabled = canSet
        for (i in presetButtons.indices) presetButtons[i].isEnabled = canSet && s != null && s.presets[i] != 0
        sleepButton.isEnabled = on && s != null && !s.inMenu
        saveButton.isEnabled = on
        settingsButton.isEnabled = on && (info?.protocol ?: 0) >= 3
        restoreButton.isEnabled = on
    }

    /** Two centred lines: the preset name small above, the temperature (or OFF) in bold below. */
    private fun presetLabel(name: String, value: String): CharSequence {
        val sp = SpannableString(name + "\n" + value)
        sp.setSpan(RelativeSizeSpan(0.8f), 0, name.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
        sp.setSpan(StyleSpan(Typeface.BOLD), name.length + 1, sp.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
        return sp
    }

    private fun degrees(c: Int, s: DeviceState): String =
        if (s.fahrenheit) String.format(Locale.US, "%d°F", Math.round(c * 1.8f + 32f)) else "$c°C"

    private fun showTemp(c: Int, s: DeviceState) {
        tempText.text = degrees(c, s)
    }

    private fun applyState(s: DeviceState) {
        val first = state == null
        state = s
        enableControls(true)
        tempBar.max = s.max - s.min
        if (!dragging && (first || System.currentTimeMillis() > holdUntil)) {
            tempBar.progress = (s.set - s.min).coerceIn(0, s.max - s.min)
            showTemp(s.set, s)
        }
        for (i in 0 until 4) {
            presetButtons[i].text = presetLabel("T" + (i + 1), if (s.presets[i] == 0) "OFF" else degrees(s.presets[i], s))
        }
        sleepButton.text = if (s.standby) "Wake up" else "Sleep"
        ironButton.visibility = if (s.twoIrons) View.VISIBLE else View.GONE
        ironButton.text = "Switch iron (now iron " + (s.actIron + 1) + ")"
        stateText.text = when {
            s.inMenu -> "The station is in its menu: remote changes wait until it is closed."
            s.standby -> "Sleeping: heater off."
            s.target == 0 -> "Heater off."
            else -> "Regulating to " + degrees(s.target, s) + "."
        }
    }

    // ------------------------------------------------------------------ commands

    private fun stepTemp(dir: Int) {
        val s = state ?: return
        val t = (s.set + dir * s.tempStep).coerceIn(s.min, s.max)
        tempBar.progress = t - s.min
        showTemp(t, s)
        queueTemp(t)
    }

    private fun queueTemp(t: Int) {
        pendingTemp = t
        holdUntil = System.currentTimeMillis() + 2000
        if (!sendPending) {
            sendPending = true
            ui.postDelayed(sendRunnable, 250)
        }
    }

    private fun sendTemp(t: Int) {
        pendingTemp = -1
        holdUntil = System.currentTimeMillis() + 2000
        io.execute {
            val r = link.setTemp(t)
            val s = link.getState()
            ui.post {
                if (r == null) toast("The station did not answer")
                if (s != null) {
                    holdUntil = 0
                    applyState(s)
                }
            }
        }
    }

    private fun selectPreset(i: Int) {
        io.execute {
            val r = link.selectPreset(i)
            val s = link.getState()
            ui.post {
                if (r == null) toast("The station did not answer")
                if (s != null) {
                    holdUntil = 0
                    dragging = false
                    applyState(s)
                    tempBar.progress = (s.set - s.min).coerceIn(0, s.max - s.min)
                    showTemp(s.set, s)
                }
            }
        }
    }

    private fun toggleSleep() {
        val s = state ?: return
        io.execute {
            val r = link.sleep(!s.standby)
            val n = link.getState()
            ui.post {
                if (r == null) toast("The station did not answer")
                if (n != null) applyState(n)
            }
        }
    }

    private fun switchIron() {
        io.execute {
            val r = link.switchIron()
            val n = link.getState()
            ui.post {
                if (r == null) toast("The station did not answer")
                if (n != null) {
                    holdUntil = 0
                    applyState(n)
                    tempBar.progress = (n.set - n.min).coerceIn(0, n.max - n.min)
                    showTemp(n.set, n)
                }
            }
        }
    }

    private fun toast(msg: String) {
        Toast.makeText(act, msg, Toast.LENGTH_SHORT).show()
    }

    // ------------------------------------------------------------------ settings backup and restore

    private fun saveSettings() {
        saveButton.isEnabled = false
        io.execute {
            val data = link.readSettings()
            val s = link.getState()
            val i = info
            val pid = if (data != null && (i?.protocol ?: 0) >= 5) link.readPidSlots() else null
            val ramp = if (data != null) readRamp(i?.protocol ?: 0) else null
            ui.post {
                saveButton.isEnabled = link.connected
                if (data == null) {
                    toast("Could not read the settings from the station")
                } else {
                    backupText = BackupFile.format(data, i, s, pid, ramp)
                    val name = "unisolder-settings-" + SimpleDateFormat("yyyyMMdd-HHmm", Locale.US).format(Date()) + ".txt"
                    val intent = Intent(Intent.ACTION_CREATE_DOCUMENT)
                    intent.addCategory(Intent.CATEGORY_OPENABLE)
                    intent.type = "text/plain"
                    intent.putExtra(Intent.EXTRA_TITLE, name)
                    act.startActivityForResult(intent, REQ_SAVE)
                }
            }
        }
    }

    private fun pickRestoreFile() {
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT)
        intent.addCategory(Intent.CATEGORY_OPENABLE)
        intent.type = "*/*"
        act.startActivityForResult(intent, REQ_RESTORE)
    }

    /** Returns true when the result belonged to this page. */
    fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != REQ_SAVE && requestCode != REQ_RESTORE) return false
        if (resultCode != Activity.RESULT_OK) return true
        val uri = data?.data ?: return true
        if (requestCode == REQ_SAVE) {
            val text = backupText ?: return true
            try {
                act.contentResolver.openOutputStream(uri, "w")?.use { it.write(text.toByteArray(Charsets.UTF_8)) }
                toast("Settings saved")
                AppLog.info("Settings saved to a file")
            } catch (e: Exception) {
                toast("Could not save the file: " + e.message)
            }
        } else {
            val text = try {
                act.contentResolver.openInputStream(uri)?.use { String(it.readBytes(), Charsets.UTF_8) }
            } catch (e: Exception) {
                null
            }
            val parsed = if (text == null) null else BackupFile.parse(text)
            if (parsed == null) {
                AlertDialog.Builder(act).setTitle("Restore settings")
                    .setMessage("This is not a UniSolder settings file.")
                    .setPositiveButton("OK", null).show()
            } else {
                AlertDialog.Builder(act).setTitle("Restore settings")
                    .setMessage("Write the settings from this file to the station?\n\n" + parsed.summary + "\n\nThe station restarts afterwards.")
                    .setPositiveButton("Restore") { _, _ -> restore(parsed.data, parsed.pid, parsed.ramp) }
                    .setNegativeButton("Cancel", null)
                    .show()
            }
        }
        return true
    }

    /** Ramp mode values, active steps and stored profiles (call on the io thread); null when the station has no step profiles (protocol < 9). */
    private fun readRamp(protocol: Int): RampBackup? {
        if (protocol < 9) return null
        val p = link.getRampPars(DeviceLink.RAMP_PARS9) ?: return null
        val st = link.getRampSteps(protocol >= 10) ?: return null
        var slots: List<RampSlot>? = null
        if (protocol >= 10) {
            val l = ArrayList<RampSlot>()
            for (k in 0 until DeviceLink.RAMP_SLOTS) l.add(link.rampSlotRead(k) ?: return null)
            slots = l
        }
        return RampBackup(p, st.first, st.second, st.slot, slots)
    }

    /** Writes the ramp part of a backup (io thread). The values and the steps are checked against each other by the station,
     *  so when one order is refused the other is tried. Stored profiles not in the file are deleted. */
    private fun writeRamp(r: RampBackup, protocol: Int): Boolean {
        if (protocol < 9) return true
        var st = link.setRampPars(r.pars)
        if (st == 0) st = link.setRampSteps(r.count, r.steps)
        else if (st == 2) {
            st = link.setRampSteps(r.count, r.steps)
            if (st == 0) st = link.setRampPars(r.pars)
        }
        if (st != 0) return false
        if (protocol >= 10 && r.slots != null) {
            for (k in 0 until DeviceLink.RAMP_SLOTS) {
                val sl = r.slots.firstOrNull { it.slot == k && it.count > 0 }
                val res = if (sl != null) link.rampSlotWrite(k, sl.name, sl.count, sl.steps) else link.rampSlotDelete(k)
                if (res != 0) return false
            }
            if (r.active >= 0 && link.rampSlotLoad(r.active) != 0) return false
        }
        return true
    }

    private fun restore(data: ByteArray, pid: List<ByteArray>?, ramp: RampBackup?) {
        restoreButton.isEnabled = false
        io.execute {
            var ok = link.writeSettings(data)
            if (ok && pid != null && (info?.protocol ?: 0) >= 5) ok = link.writePidSlots(pid)
            if (ok && ramp != null) ok = writeRamp(ramp, info?.protocol ?: 0)
            if (ok) link.restartDevice()
            ui.post {
                restoreButton.isEnabled = link.connected
                if (ok) {
                    AppLog.info("Settings restored")
                    toast("Settings restored, the station restarts")
                } else {
                    AppLog.error("Settings restore failed")
                    AlertDialog.Builder(act).setTitle("Restore settings")
                        .setMessage("Writing the settings failed. The station was not restarted: check the settings in its menu, or retry.")
                        .setPositiveButton("OK", null).show()
                }
            }
        }
    }

    companion object {
        const val REQ_SAVE = 2
        const val REQ_RESTORE = 3
        private const val MATCH_P = ViewGroup.LayoutParams.MATCH_PARENT
        private const val WRAP_P = ViewGroup.LayoutParams.WRAP_CONTENT
    }
}

/** Text format of the settings backup file. */
/** Ramp mode part of a backup: 16 values, active steps (count, 6 x 3), slot they came from (-1 none), stored profiles (null = not in the file). */
class RampBackup(val pars: IntArray, val count: Int, val steps: IntArray, val active: Int, val slots: List<RampSlot>?)

object BackupFile {
    class Parsed(val data: ByteArray, val summary: String, val pid: List<ByteArray>?, val ramp: RampBackup?)

    private fun ints(a: IntArray) = a.joinToString(",")

    fun format(data: ByteArray, info: AppInfo?, state: DeviceState?, pid: List<ByteArray>? = null, ramp: RampBackup? = null): String {
        val sb = StringBuilder()
        sb.append("UniSolder settings backup\n")
        sb.append("version=1\n")
        sb.append("saved=").append(SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.US).format(Date())).append('\n')
        if (info != null) sb.append("firmware=").append(info.major).append('.').append(info.minor).append(" board=").append(info.boardName).append('\n')
        if (state != null) sb.append("set=").append(state.set).append('\n')
        sb.append("data=")
        for (b in data) sb.append(String.format(Locale.US, "%02X", b.toInt() and 0xFF))
        sb.append('\n')
        if (pid != null) {
            sb.append("pid=")
            for (slot in pid) for (b in slot) sb.append(String.format(Locale.US, "%02X", b.toInt() and 0xFF))
            sb.append('\n')
        }
        if (ramp != null) {
            // ramp=16 values; steps=count;18 values;active slot (-1 none); profileN=name as hex;count;18 values
            sb.append("ramp=").append(ints(ramp.pars)).append('\n')
            sb.append("steps=").append(ramp.count).append(';').append(ints(ramp.steps)).append(';').append(ramp.active).append('\n')
            if (ramp.slots != null) {
                sb.append("profiles=").append(DeviceLink.RAMP_SLOTS).append('\n')
                for (sl in ramp.slots) {
                    if (sl.count == 0) continue
                    sb.append("profile").append(sl.slot + 1).append('=')
                    for (c in sl.name) sb.append(String.format(Locale.US, "%02X", c.code and 0xFF))
                    sb.append(';').append(sl.count).append(';').append(ints(sl.steps)).append('\n')
                }
            }
        }
        return sb.toString()
    }

    private fun parseInts(s: String, n: Int): IntArray? {
        val p = s.split(',')
        if (p.size != n) return null
        val out = IntArray(n)
        for (i in 0 until n) out[i] = p[i].trim().toIntOrNull() ?: return null
        return out
    }

    /** The ramp lines of a backup, or null when there are none (an older file) or they are damaged. */
    private fun parseRamp(lines: List<String>): RampBackup? {
        val rl = lines.firstOrNull { it.startsWith("ramp=") } ?: return null
        val sl = lines.firstOrNull { it.startsWith("steps=") } ?: return null
        val pars = parseInts(rl.substring(5), DeviceLink.RAMP_PARS9) ?: return null
        val sp = sl.substring(6).split(';')
        if (sp.size != 3) return null
        val count = sp[0].toIntOrNull() ?: return null
        val steps = parseInts(sp[1], DeviceLink.RAMP_STEPS * 3) ?: return null
        val active = sp[2].toIntOrNull() ?: return null
        var slots: List<RampSlot>? = null
        if (lines.any { it.startsWith("profiles=") }) {
            val l = ArrayList<RampSlot>()
            for (k in 0 until DeviceLink.RAMP_SLOTS) {
                val pl = lines.firstOrNull { it.startsWith("profile" + (k + 1) + "=") } ?: continue
                val f = pl.substringAfter('=').split(';')
                if (f.size != 3 || f[0].length % 2 != 0) return null
                val name = StringBuilder()
                for (i in 0 until f[0].length / 2) name.append((f[0].substring(i * 2, i * 2 + 2).toIntOrNull(16) ?: return null).toChar())
                val c = f[1].toIntOrNull() ?: return null
                val st = parseInts(f[2], DeviceLink.RAMP_STEPS * 3) ?: return null
                l.add(RampSlot(k, name.toString(), c, st))
            }
            slots = l
        }
        return RampBackup(pars, count, steps, active, slots)
    }

    fun parse(text: String): Parsed? {
        val lines = text.lines().map { it.trim() }
        if (lines.isEmpty() || lines[0] != "UniSolder settings backup") return null
        val dataLine = lines.firstOrNull { it.startsWith("data=") } ?: return null
        val hex = dataLine.substring(5)
        if (hex.length != DeviceLink.SETTINGS_SIZE * 2) return null
        val out = ByteArray(DeviceLink.SETTINGS_SIZE)
        try {
            for (i in out.indices) out[i] = hex.substring(i * 2, i * 2 + 2).toInt(16).toByte()
        } catch (e: NumberFormatException) {
            return null
        }
        var pid: List<ByteArray>? = null
        val pidLine = lines.firstOrNull { it.startsWith("pid=") }
        val n = DeviceLink.PID_SLOTS * DeviceLink.PID_SLOT_LEN
        if (pidLine != null && pidLine.length == 4 + n * 2) {
            try {
                val raw = ByteArray(n) { pidLine.substring(4 + it * 2, 6 + it * 2).toInt(16).toByte() }
                pid = List(DeviceLink.PID_SLOTS) { raw.copyOfRange(it * DeviceLink.PID_SLOT_LEN, (it + 1) * DeviceLink.PID_SLOT_LEN) }
            } catch (e: NumberFormatException) {
                return null
            }
        }
        var summary = lines.filter { it.startsWith("saved=") || it.startsWith("firmware=") }.joinToString("\n")
        if (pid != null) summary += "\nSaved PID values: " + pid.count { it[0].toInt() and 0xFF == 0xC7 } + " iron(s)"
        val ramp = parseRamp(lines)
        if (ramp != null) {
            summary += "\nRamp mode values and the active profile (" + ramp.count + " step" + (if (ramp.count == 1) "" else "s") + ")"
            if (ramp.slots != null) summary += "\nStored profiles: " + ramp.slots.size + " (profiles in the station that are not in the file are deleted)"
        }
        return Parsed(out, summary, pid, ramp)
    }
}
