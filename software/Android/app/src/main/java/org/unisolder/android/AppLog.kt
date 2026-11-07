package org.unisolder.android

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Small in-memory log, shown in the Firmware tab and mirrored to logcat. */
object AppLog {
    private val lines = ArrayList<String>()
    private val format = SimpleDateFormat("HH:mm:ss", Locale.US)

    @Volatile
    var listener: ((String) -> Unit)? = null

    @Synchronized
    private fun add(level: String, msg: String) {
        val line = format.format(Date()) + " " + level + " " + msg
        lines.add(line)
        while (lines.size > 400) lines.removeAt(0)
        android.util.Log.i("UniSolder", line)
        listener?.invoke(line)
    }

    fun info(msg: String) = add("INFO ", msg)
    fun warn(msg: String) = add("WARN ", msg)
    fun error(msg: String) = add("ERROR", msg)

    @Synchronized
    fun text(): String = lines.joinToString("\n")
}
