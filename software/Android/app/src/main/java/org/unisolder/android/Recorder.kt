package org.unisolder.android

import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.File
import java.io.FileFilter
import java.io.FileReader
import java.io.FileWriter
import java.io.IOException
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Records the live data to a CSV file, five rows per second. */
class Recorder(private val dir: File) {
    private var writer: BufferedWriter? = null
    var file: File? = null
        private set
    var rows = 0
        private set
    private var startMs = 0L
    private var lastMs = 0L

    val recording: Boolean get() = writer != null
    val elapsedSeconds: Long get() = if (recording) (System.currentTimeMillis() - startMs) / 1000 else 0

    fun start(): Boolean {
        dir.mkdirs()
        val f = File(dir, "session-" + SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date()) + ".csv")
        return try {
            val w = BufferedWriter(FileWriter(f))
            w.write("time_s,set_C,actual_C,power_pct,heater_on,reached,resistance_ohm,filtered_C,adc_C\n")
            writer = w
            file = f
            rows = 0
            startMs = System.currentTimeMillis()
            lastMs = 0
            AppLog.info("Recording to " + f.name)
            true
        } catch (e: IOException) {
            AppLog.error("Cannot start recording: " + e.message)
            false
        }
    }

    fun add(s: Sample, power: Float) {
        val w = writer ?: return
        val now = System.currentTimeMillis()
        if (now - lastMs < 200) return
        lastMs = now
        try {
            w.write(
                String.format(
                    Locale.US, "%.1f,%.0f,%.1f,%.1f,%d,%d,%.2f,%.1f,%.1f\n",
                    (now - startMs) / 1000.0, s.set, s.temp, power,
                    if (s.power > 0f) 1 else 0, if (s.reached > 0f) 1 else 0, s.resistance, s.avg, s.adc
                )
            )
            rows++
            if (rows % 25 == 0) w.flush()
        } catch (e: IOException) {
            AppLog.error("Recording failed: " + e.message)
            stop()
        }
    }

    fun stop() {
        try {
            writer?.close()
        } catch (e: IOException) {
        }
        writer = null
        AppLog.info("Recording stopped, " + rows + " rows")
    }

    companion object {
        fun sessions(dir: File): List<File> {
            val list = dir.listFiles(FileFilter { it.name.endsWith(".csv") }) ?: return emptyList()
            return list.sortedByDescending { it.name }
        }
    }
}

/** Numbers for one recorded session. */
class SessionStats(
    val rows: Int,
    val durationS: Double,
    val maxTemp: Double,
    val avgPower: Double,
    val warmupS: Double?
) {
    fun text(): String {
        val sb = StringBuilder()
        sb.append(String.format(Locale.US, "%d:%02d", (durationS / 60).toInt(), (durationS % 60).toInt()))
        sb.append(String.format(Locale.US, "  max %.0f°C  avg power %.0f%%", maxTemp, avgPower))
        if (warmupS != null) sb.append(String.format(Locale.US, "  heat-up %.0f s", warmupS))
        return sb.toString()
    }

    companion object {
        fun read(f: File): SessionStats? {
            try {
                BufferedReader(FileReader(f)).use { r ->
                    r.readLine() ?: return null
                    var rows = 0
                    var last = 0.0
                    var max = 0.0
                    var power = 0.0
                    var warm: Double? = null
                    var sawNotReached = false
                    while (true) {
                        val line = r.readLine() ?: break
                        val c = line.split(',')
                        if (c.size < 9) continue
                        val t = c[0].toDoubleOrNull() ?: continue
                        rows++
                        last = t
                        max = maxOf(max, c[2].toDoubleOrNull() ?: 0.0)
                        power += c[3].toDoubleOrNull() ?: 0.0
                        val reached = c[5] == "1"
                        if (!reached) sawNotReached = true
                        if (reached && sawNotReached && warm == null) warm = t
                    }
                    if (rows == 0) return null
                    return SessionStats(rows, last, max, power / rows, warm)
                }
            } catch (e: Exception) {
                return null
            }
        }
    }
}
