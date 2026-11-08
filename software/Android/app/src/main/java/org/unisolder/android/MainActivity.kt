package org.unisolder.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Typeface
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.GridLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import java.util.Locale
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

private const val MATCH = ViewGroup.LayoutParams.MATCH_PARENT
private const val WRAP = ViewGroup.LayoutParams.WRAP_CONTENT
private const val REQ_HEX = 1

class MainActivity : Activity(), DeviceLink.Listener {
    private lateinit var link: DeviceLink
    private val ui = Handler(Looper.getMainLooper())
    private val io: ExecutorService = Executors.newSingleThreadExecutor()

    private lateinit var statusText: TextView
    private lateinit var connectButton: Button
    private val pages = ArrayList<View>()
    private val tabs = ArrayList<Button>()

    // live page
    private lateinit var readout: TextView
    private lateinit var chart: ChartView
    private lateinit var pauseButton: Button
    private var paused = false
    private var sampleCount = 0

    // PID page
    private class PidField(val max: Int, val format: (Int) -> String) {
        lateinit var bar: SeekBar
        lateinit var label: TextView
    }

    private val pidFields = listOf(
        PidField(32767) { "Kp = " + String.format(Locale.US, "%.2f", it / 32767.0) },
        PidField(3277) { "Ki = " + String.format(Locale.US, "%.3f", it / 32767.0) },
        PidField(32) { "DGain = $it" },
        PidField(100) { "OVFGain = $it" },
        PidField(256) { "Gain = $it" }
    )
    private var pid: Pid? = null
    private var loadingPid = false
    private var pidSendPending = false
    private val pidSendRunnable = Runnable { sendPidNow() }
    private lateinit var pidReload: Button

    // firmware page
    private lateinit var fwInfo: TextView
    private lateinit var fwPick: Button
    private lateinit var fwStart: Button
    private lateinit var fwProgress: ProgressBar
    private lateinit var logView: TextView
    private lateinit var logScroll: ScrollView
    private var hex: HexFile? = null
    private var hexName = ""
    private var updating = false

    private val pollTick = object : Runnable {
        override fun run() {
            link.poll()
            ui.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        link = DeviceLink(this)
        link.listener = this
        buildUi()
        logView.text = AppLog.text()
        AppLog.listener = { line -> runOnUiThread { appendLog(line) } }
        link.start()
    }

    override fun onResume() {
        super.onResume()
        ui.removeCallbacks(pollTick)
        ui.post(pollTick)
    }

    override fun onPause() {
        ui.removeCallbacks(pollTick)
        super.onPause()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        link.poll()
    }

    override fun onDestroy() {
        AppLog.listener = null
        link.listener = null
        link.stop()
        io.shutdownNow()
        super.onDestroy()
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (updating) {
            Toast.makeText(this, "Firmware update in progress", Toast.LENGTH_SHORT).show()
        } else {
            super.onBackPressed()
        }
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()

    // ------------------------------------------------------------------ UI

    private fun buildUi() {
        val root = LinearLayout(this)
        root.orientation = LinearLayout.VERTICAL

        val statusRow = LinearLayout(this)
        statusRow.orientation = LinearLayout.HORIZONTAL
        statusRow.gravity = Gravity.CENTER_VERTICAL
        statusRow.setPadding(dp(12), dp(8), dp(12), dp(4))
        statusText = TextView(this)
        statusText.text = "Not connected"
        statusText.textSize = 16f
        statusRow.addView(statusText, LinearLayout.LayoutParams(0, WRAP, 1f))
        connectButton = Button(this)
        connectButton.text = "Connect"
        connectButton.setOnClickListener { link.retry() }
        statusRow.addView(connectButton)
        root.addView(statusRow)

        val content = FrameLayout(this)
        root.addView(content, LinearLayout.LayoutParams(MATCH, 0, 1f))
        pages.add(buildLivePage())
        pages.add(buildPidPage())
        pages.add(buildFirmwarePage())
        for (p in pages) content.addView(p, FrameLayout.LayoutParams(MATCH, MATCH))

        val tabBar = LinearLayout(this)
        tabBar.orientation = LinearLayout.HORIZONTAL
        val names = listOf("Live", "PID", "Firmware")
        for (i in names.indices) {
            val b = Button(this)
            b.text = names[i]
            b.setOnClickListener { showPage(i) }
            tabs.add(b)
            tabBar.addView(b, LinearLayout.LayoutParams(0, WRAP, 1f))
        }
        root.addView(tabBar)

        setContentView(root)
        showPage(0)
        applyPid(null)
    }

    private fun showPage(index: Int) {
        for (i in pages.indices) {
            pages[i].visibility = if (i == index) View.VISIBLE else View.GONE
            tabs[i].alpha = if (i == index) 1f else 0.55f
        }
    }

    private fun buildLivePage(): View {
        val page = LinearLayout(this)
        page.orientation = LinearLayout.VERTICAL
        page.setPadding(dp(8), 0, dp(8), dp(4))

        readout = TextView(this)
        readout.textSize = 22f
        readout.text = "No data"
        readout.setPadding(dp(4), dp(4), dp(4), dp(4))
        page.addView(readout)

        chart = ChartView(this)
        page.addView(chart, LinearLayout.LayoutParams(MATCH, 0, 1f))

        val grid = GridLayout(this)
        grid.columnCount = 4
        for (s in chart.series) {
            val chip = TextView(this)
            chip.text = "● " + s.name
            chip.setTextColor(s.color)
            chip.textSize = 15f
            chip.setPadding(dp(8), dp(8), dp(8), dp(8))
            chip.alpha = if (s.visible) 1f else 0.3f
            chip.setOnClickListener {
                s.visible = !s.visible
                chip.alpha = if (s.visible) 1f else 0.3f
                chart.invalidate()
            }
            grid.addView(chip)
        }
        page.addView(grid)

        pauseButton = Button(this)
        pauseButton.text = "Pause"
        pauseButton.setOnClickListener {
            paused = !paused
            pauseButton.text = if (paused) "Resume" else "Pause"
        }
        page.addView(pauseButton)
        return page
    }

    private fun buildPidPage(): View {
        val scroll = ScrollView(this)
        val col = LinearLayout(this)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(8), dp(16), dp(16))

        val note = TextView(this)
        note.text = "Moving a slider sends the value to the connected iron immediately, as in the Windows application."
        col.addView(note)

        for (f in pidFields) {
            val label = TextView(this)
            label.textSize = 18f
            label.setPadding(0, dp(16), 0, 0)
            label.text = f.format(0)
            f.label = label
            val bar = SeekBar(this)
            bar.max = f.max
            bar.isEnabled = false
            bar.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(sb: SeekBar?, progress: Int, fromUser: Boolean) {
                    f.label.text = f.format(progress)
                    if (fromUser && !loadingPid) schedulePidSend()
                }

                override fun onStartTrackingTouch(sb: SeekBar?) {}

                override fun onStopTrackingTouch(sb: SeekBar?) {
                    if (!loadingPid) {
                        ui.removeCallbacks(pidSendRunnable)
                        sendPidNow()
                    }
                }
            })
            f.bar = bar
            col.addView(label)
            col.addView(bar, LinearLayout.LayoutParams(MATCH, WRAP))
        }

        pidReload = Button(this)
        pidReload.text = "Read from iron"
        pidReload.setOnClickListener { loadPid() }
        col.addView(pidReload)

        scroll.addView(col, FrameLayout.LayoutParams(MATCH, WRAP))
        return scroll
    }

    private fun buildFirmwarePage(): View {
        val col = LinearLayout(this)
        col.orientation = LinearLayout.VERTICAL
        col.setPadding(dp(16), dp(8), dp(16), dp(8))

        val note = TextView(this)
        note.text = "Choose the .hex file built for the bootloader (PIC32_with_bootloader). " +
                "During the update the station restarts in bootloader mode and Android may ask for USB access again: allow it. " +
                "Keep the cable connected and this app open until it says the update is finished."
        col.addView(note)

        fwPick = Button(this)
        fwPick.text = "Choose HEX file"
        fwPick.setOnClickListener { pickHex() }
        col.addView(fwPick)

        fwInfo = TextView(this)
        fwInfo.text = "No file selected"
        fwInfo.setPadding(0, dp(4), 0, dp(4))
        col.addView(fwInfo)

        fwStart = Button(this)
        fwStart.text = "Update firmware"
        fwStart.isEnabled = false
        fwStart.setOnClickListener { confirmUpdate() }
        col.addView(fwStart)

        fwProgress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal)
        fwProgress.max = 1
        col.addView(fwProgress, LinearLayout.LayoutParams(MATCH, WRAP))

        logView = TextView(this)
        logView.typeface = Typeface.MONOSPACE
        logView.textSize = 11f
        logScroll = ScrollView(this)
        logScroll.addView(logView)
        col.addView(logScroll, LinearLayout.LayoutParams(MATCH, 0, 1f))
        return col
    }

    private fun appendLog(line: String) {
        if (logView.length() > 30000) logView.text = AppLog.text() else logView.append("\n" + line)
        logScroll.post { logScroll.fullScroll(View.FOCUS_DOWN) }
    }

    // ------------------------------------------------------------------ device events

    override fun onConnection(connected: Boolean, text: String) {
        statusText.text = text
        connectButton.visibility = if (connected) View.GONE else View.VISIBLE
        if (connected) {
            chart.clear()
            if (!link.busy) loadPid()
        } else {
            readout.text = "No data"
            applyPid(null)
        }
    }

    override fun onLive(s: Sample) {
        if (paused) return
        chart.add(floatArrayOf(s.set, s.temp, s.duty, s.power, s.reached, s.avg, s.adc))
        if (++sampleCount % 5 == 0) {
            readout.text = String.format(Locale.US, "Set %.0f°C    Actual %.0f°C", s.set, s.temp)
        }
    }

    override fun onInstrumentChange(id: Int) {
        if (!link.busy) loadPid()
    }

    // ------------------------------------------------------------------ PID

    private fun loadPid() {
        io.execute {
            var p: Pid? = null
            for (attempt in 0 until 3) {
                p = link.appGetPid()
                if (p != null || !link.connected) break
                Thread.sleep(700)
            }
            val result = p
            ui.post { applyPid(result) }
        }
    }

    private fun applyPid(p: Pid?) {
        loadingPid = true
        pid = p
        val values = if (p != null) listOf(p.kp, p.ki, p.dGain, p.ovsGain, p.gain) else listOf(0, 0, 0, 0, 0)
        for (i in pidFields.indices) {
            val f = pidFields[i]
            f.bar.isEnabled = p != null
            f.bar.progress = values[i].coerceIn(0, f.max)
            f.label.text = f.format(f.bar.progress)
        }
        loadingPid = false
    }

    private fun schedulePidSend() {
        if (pidSendPending) return
        pidSendPending = true
        ui.postDelayed(pidSendRunnable, 80)
    }

    private fun sendPidNow() {
        pidSendPending = false
        val old = pid ?: return
        val p = Pid(
            gain = pidFields[4].bar.progress,
            offset = old.offset,
            kp = pidFields[0].bar.progress,
            ki = pidFields[1].bar.progress,
            dGain = pidFields[2].bar.progress,
            ovsGain = pidFields[3].bar.progress
        )
        pid = p
        io.execute { link.appSetPid(p) }
    }

    // ------------------------------------------------------------------ firmware update

    private fun pickHex() {
        val i = Intent(Intent.ACTION_OPEN_DOCUMENT)
        i.addCategory(Intent.CATEGORY_OPENABLE)
        i.type = "*/*"
        startActivityForResult(i, REQ_HEX)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQ_HEX && resultCode == RESULT_OK) {
            val uri = data?.data ?: return
            var name = "firmware.hex"
            try {
                contentResolver.query(uri, null, null, null, null)?.use { c ->
                    val col = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (col >= 0 && c.moveToFirst()) name = c.getString(col)
                }
            } catch (e: Exception) {
            }
            val fileName = name
            fwInfo.text = "Reading $fileName ..."
            fwStart.isEnabled = false
            hex = null
            io.execute {
                var problem: String? = null
                val h = HexFile()
                try {
                    val bytes = contentResolver.openInputStream(uri)?.use { it.readBytes() }
                    if (bytes == null) {
                        problem = "Cannot open the file."
                    } else if (bytes.size > 4_000_000) {
                        problem = "The file is too large for a firmware."
                    } else {
                        val err = h.load(String(bytes, Charsets.UTF_8).removePrefix("﻿"))
                        if (err != null) {
                            problem = "Invalid HEX file: $err"
                        } else {
                            val v = h.validate()
                            if (v != null) problem = "This HEX file cannot be used: $v"
                        }
                    }
                } catch (e: Exception) {
                    problem = "Cannot read the file: " + e.message
                }
                val msg = problem
                ui.post {
                    if (msg != null) {
                        AppLog.error("Firmware file rejected: $msg")
                        fwInfo.text = msg
                    } else {
                        hex = h
                        hexName = fileName
                        val info = fileName + "\n" + h.totalBytes + " bytes, " + h.records.size + " records, application up to 0x" +
                                h.applicationEnd.toString(16).uppercase() + "\nChecksums OK, built for the bootloader."
                        AppLog.info("Firmware file ready: $info".replace("\n", " "))
                        fwInfo.text = info
                        fwStart.isEnabled = !updating
                    }
                }
            }
        }
    }

    private fun confirmUpdate() {
        val h = hex ?: return
        if (!link.connected) {
            Toast.makeText(this, "The station is not connected", Toast.LENGTH_SHORT).show()
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Firmware update")
            .setMessage(
                "Update the station firmware with:\n\n" + hexName + "\n" + h.totalBytes + " bytes" +
                        "\n\nDo not unplug the cable or close this app until the update has finished.\n\nContinue?"
            )
            .setPositiveButton("Update") { _, _ -> Thread { doUpdate(h) }.start() }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun updateFailed(reason: String, mayBeErased: Boolean) {
        AppLog.error("Firmware update failed: $reason" + if (mayBeErased) " (station left in bootloader mode)" else " (firmware not modified)")
        val msg = if (mayBeErased) {
            reason + "\n\nThe firmware may have been erased or partly written, so it has NOT been marked as valid: " +
                    "the station stays in bootloader mode, which is safe.\n\nRetry the update. If the station does not reconnect, power-cycle it; it restarts in bootloader mode."
        } else {
            reason + "\n\nThe station firmware has not been modified."
        }
        showMessage("Firmware update failed", msg)
    }

    private fun showMessage(title: String, msg: String) {
        runOnUiThread {
            if (!isFinishing) {
                AlertDialog.Builder(this).setTitle(title).setMessage(msg).setPositiveButton("OK", null).show()
            }
        }
    }

    private fun showProgress(done: Int) {
        runOnUiThread { fwProgress.progress = done }
    }

    /** Runs on its own thread. Same sequence as the Windows application. */
    private fun doUpdate(h: HexFile) {
        var erased = false
        link.busy = true
        runOnUiThread {
            updating = true
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            fwStart.isEnabled = false
            fwPick.isEnabled = false
            fwProgress.max = h.records.size
            fwProgress.progress = 0
        }
        try {
            AppLog.info("Firmware update: " + h.records.size + " records, " + h.totalBytes + " bytes")
            var mode = link.devOperatingMode()
            if (mode != 16) {
                AppLog.info("Firmware update: device in application mode ($mode), jumping to bootloader")
                link.appJumpToBootloader()
                Thread.sleep(500)
                link.waitForDetach(3000)
                link.disconnect()
                if (!link.reconnect(40000)) {
                    updateFailed("The station did not reconnect after the jump to bootloader mode. Power-cycle it and retry.", erased)
                    return
                }
                AppLog.info("Firmware update: reconnected, waiting for bootloader mode")
                mode = -1
                for (i in 0 until 20) {
                    mode = link.devOperatingMode()
                    if (mode == 16) break
                }
                if (mode != 16) {
                    updateFailed("The station did not enter bootloader mode (operating mode $mode). Power-cycle it and retry.", erased)
                    return
                }
            }

            // from here on the application is erased: any interruption leaves the station in bootloader mode
            erased = true
            AppLog.info("Firmware update: erasing flash...")
            val eStatus = link.blErase()
            if (eStatus < 0) {
                updateFailed("No answer from the bootloader to the erase command.", erased)
                return
            }
            AppLog.info("Firmware update: erase done, bootloader status 0x" + eStatus.toString(16))

            AppLog.info("Firmware update: programming...")
            var done = 0
            var nonZero = 0
            for (r in h.records) {
                val st = link.blProgram(r.address, r.data, r.len)
                if (st < 0) {
                    updateFailed(
                        "No answer from the bootloader while programming address 0x" + r.address.toString(16).uppercase() +
                                " (record " + (done + 1) + " of " + h.records.size + ").", erased
                    )
                    return
                }
                done++
                if (st != 0) {
                    nonZero++
                    if (nonZero <= 10) AppLog.warn("Bootloader status 0x" + st.toString(16) + " programming 0x" + r.address.toString(16).uppercase() + " (log only)")
                }
                if (done % 10 == 0 || done == h.records.size) showProgress(done)
            }
            AppLog.info("Firmware update: $done records programmed, $nonZero with non-zero bootloader status (log only)")

            // only reached when the erase and every record were acknowledged
            val completed = link.blProgramComplete()
            if (!completed) AppLog.warn("Firmware update: no answer to PROGRAM_COMPLETE, the bootloader checks the firmware CRC at restart")
            AppLog.info("Firmware update: jumping to application")
            link.blJumpToApplication()
            showMessage(
                "Firmware update",
                if (completed) "Firmware update completed. The station restarts with the new firmware."
                else "Firmware written, but the station did not confirm the final step. If it stays in bootloader mode after restarting, retry the update."
            )
        } catch (e: Exception) {
            AppLog.error("Firmware update: unexpected error: $e")
            updateFailed("Unexpected error: " + e.message, erased)
        } finally {
            link.busy = false
            runOnUiThread {
                updating = false
                window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                fwPick.isEnabled = true
                fwStart.isEnabled = hex != null
            }
        }
    }
}
