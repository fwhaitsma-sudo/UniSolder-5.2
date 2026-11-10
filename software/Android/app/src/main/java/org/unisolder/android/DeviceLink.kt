package org.unisolder.android

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** One packet of live data (message type 3). Temperatures in degrees C. */
class Sample(
    val set: Float,
    val temp: Float,
    val adc: Float,
    val avg: Float,
    val duty: Float,
    val power: Float,
    val reached: Float
)

class Pid(
    var gain: Int,
    var offset: Int,
    var kp: Int,
    var ki: Int,
    var dGain: Int,
    var ovsGain: Int
)

/** Answer of the application info command. protocol >= 1 means the firmware supports remote control and settings backup. */
class AppInfo(val minor: Int, val major: Int, val board: Int, val protocol: Int) {
    val boardName: String get() = if (board == 1) "5.2C" else "5.2"
}

/** State of the station: temperatures in degrees C (0 = OFF for presets, 0 = heater off for target). */
class DeviceState(
    val set: Int,
    val target: Int,
    val presets: IntArray,
    val holder: Int,
    val sleep: Int,
    val min: Int,
    val max: Int,
    val mode: Int,
    val flags: Int,
    val actIron: Int,
    val tempStep: Int
) {
    val manualSleep: Boolean get() = flags and 1 != 0
    val standby: Boolean get() = flags and 2 != 0
    val twoIrons: Boolean get() = flags and 4 != 0
    val onStand: Boolean get() = flags and 8 != 0
    val fahrenheit: Boolean get() = flags and 16 != 0

    /** The station is in its menu or another screen where remote changes should wait. */
    val inMenu: Boolean get() = mode in 3..7 || mode in 9..12
}

private fun ByteArray.u(i: Int): Int = this[i].toInt() and 0xFF
private fun ByteArray.u16(i: Int): Int = u(i) or (u(i + 1) shl 8)

/**
 * Connection to the UniSolder station: finds the USB device, asks for permission, reads the reports
 * and implements the commands of the Windows application (UniSolderComm.cs).
 */
class DeviceLink(private val ctx: Context) {
    interface Listener {
        fun onConnection(connected: Boolean, text: String)
        fun onLive(s: Sample)
        fun onInstrumentChange(id: Int)
    }

    @Volatile
    var listener: Listener? = null

    /** Set while a firmware update runs: it then controls the connection itself. */
    @Volatile
    var busy = false

    val connected: Boolean get() = hid.connected

    private val usb = ctx.getSystemService(Context.USB_SERVICE) as UsbManager
    private val hid = UsbHid(usb)
    private val main = Handler(Looper.getMainLooper())
    private val feedback = LinkedBlockingQueue<ByteArray>()
    private val connLock = Any()
    private val cmdLock = Any()

    @Volatile
    private var rxRunning = false
    private var rxThread: Thread? = null

    @Volatile
    private var permissionAsked: String? = null

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) {
            when (intent.action) {
                ACTION_PERMISSION -> {
                    val granted = intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)
                    AppLog.info("USB permission " + (if (granted) "granted" else "denied"))
                    if (granted) poll() else state(false, "USB permission denied. Tap Connect to ask again.")
                }
                UsbManager.ACTION_USB_DEVICE_DETACHED -> poll()
                UsbManager.ACTION_USB_DEVICE_ATTACHED -> poll()
            }
        }
    }

    fun start() {
        val f = IntentFilter()
        f.addAction(ACTION_PERMISSION)
        f.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED)
        f.addAction(UsbManager.ACTION_USB_DEVICE_ATTACHED)
        if (Build.VERSION.SDK_INT >= 33) {
            ctx.registerReceiver(receiver, f, Context.RECEIVER_NOT_EXPORTED)
        } else {
            ctx.registerReceiver(receiver, f)
        }
    }

    fun stop() {
        try {
            ctx.unregisterReceiver(receiver)
        } catch (e: IllegalArgumentException) {
        }
        disconnect()
    }

    /** Forget an earlier refused permission, the next poll asks again. */
    fun retry() {
        permissionAsked = null
        poll()
    }

    private fun findDevice(): UsbDevice? =
        usb.deviceList.values.firstOrNull { it.vendorId == VID && it.productId == PID }

    private fun state(connected: Boolean, text: String) {
        main.post { listener?.onConnection(connected, text) }
    }

    /** Looks at the USB bus and connects or disconnects. Called every second and on USB events. */
    fun poll() {
        if (busy) return
        synchronized(connLock) {
            if (hid.connected) {
                val d = hid.device
                if (d == null || !usb.deviceList.containsKey(d.deviceName)) disconnect()
                return
            }
            val dev = findDevice()
            if (dev == null) {
                permissionAsked = null
                return
            }
            if (!usb.hasPermission(dev)) {
                if (permissionAsked != dev.deviceName) {
                    permissionAsked = dev.deviceName
                    requestPermission(dev)
                }
                return
            }
            connect(dev)
        }
    }

    private fun requestPermission(dev: UsbDevice) {
        AppLog.info("Asking permission for the USB device")
        state(false, "Allow USB access to the UniSolder station")
        val flags = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
        val pi = PendingIntent.getBroadcast(ctx, 0, Intent(ACTION_PERMISSION).setPackage(ctx.packageName), flags)
        usb.requestPermission(dev, pi)
    }

    private fun connect(dev: UsbDevice): Boolean {
        val err = hid.open(dev)
        if (err != null) {
            AppLog.warn("Connect failed: $err")
            state(false, "Cannot connect: $err")
            return false
        }
        feedback.clear()
        startRx()
        AppLog.info("Connected")
        state(true, "Connected")
        return true
    }

    fun disconnect() {
        synchronized(connLock) {
            val was = hid.connected
            rxRunning = false
            rxThread?.let {
                try {
                    it.join(1000)
                } catch (e: InterruptedException) {
                }
            }
            rxThread = null
            hid.close()
            if (was) {
                AppLog.info("Disconnected")
                state(false, "Not connected")
            }
            Unit
        }
    }

    /** For the firmware update: waits until the device has left the USB bus (it re-enumerates in the bootloader). */
    fun waitForDetach(timeoutMs: Long): Boolean {
        val end = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < end) {
            if (findDevice() == null) return true
            Thread.sleep(50)
        }
        return false
    }

    /** For the firmware update: waits until the device shows up again and is connected. */
    fun reconnect(timeoutMs: Long): Boolean {
        val end = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < end) {
            if (synchronized(connLock) { tryConnectOnce() }) return true
            Thread.sleep(500)
        }
        return false
    }

    private fun tryConnectOnce(): Boolean {
        if (hid.connected) return true
        val dev = findDevice()
        if (dev == null) {
            permissionAsked = null
            return false
        }
        if (usb.hasPermission(dev)) return connect(dev)
        if (permissionAsked != dev.deviceName) {
            permissionAsked = dev.deviceName
            requestPermission(dev)
        }
        return false
    }

    private fun startRx() {
        rxRunning = true
        val t = Thread {
            val buf = ByteArray(64)
            var fastErrors = 0
            while (rxRunning) {
                val t0 = System.currentTimeMillis()
                val n = hid.read(buf, 200)
                if (n >= 64) {
                    fastErrors = 0
                    dispatch(buf.copyOf(64))
                } else if (n < 0 && System.currentTimeMillis() - t0 < 20) {
                    // error instead of a timeout: do not spin, and let the poll check whether the device is gone
                    Thread.sleep(20)
                    if (++fastErrors == 50) {
                        AppLog.warn("USB read keeps failing")
                        main.post { poll() }
                    }
                }
            }
        }
        t.isDaemon = true
        t.start()
        rxThread = t
    }

    private fun dispatch(p: ByteArray) {
        when (p.u(0)) {
            1 -> {
                val id = p.u(1) or (p.u(2) shl 8)
                AppLog.info("Instrument change, iron ID 0x" + id.toString(16).uppercase())
                main.post { listener?.onInstrumentChange(id) }
            }
            3 -> {
                val s = Sample(
                    set = p.u(3) * 2f,
                    temp = p.u(4) * 0.5f + p.u(5) * 128f,
                    adc = p.u(6) * 0.5f + p.u(7) * 128f,
                    avg = p.u(8) * 0.5f + p.u(9) * 128f,
                    duty = (p.u(32) + p.u(33) * 256) / 128f,
                    power = if (p.u(14) != 0) 100f else 0f,
                    reached = p.u(31) * 100f
                )
                main.post { listener?.onLive(s) }
            }
            else -> feedback.offer(p)
        }
    }

    /**
     * Sends a command and waits for the reply with the same command byte.
     * Returns the reply bytes after the command byte, an empty array when no reply is wanted (timeoutMs = 0),
     * or null when the device did not answer.
     */
    private fun send(out: ByteArray, timeoutMs: Int): ByteArray? = synchronized(cmdLock) { sendLocked(out, timeoutMs) }

    private fun sendLocked(out: ByteArray, timeoutMs: Int): ByteArray? {
        val buf = ByteArray(64)
        System.arraycopy(out, 0, buf, 0, minOf(out.size, 64))
        feedback.clear()
        if (!hid.write(buf)) {
            AppLog.warn("TX " + cmdName(out[0]) + " failed")
            return null
        }
        if (timeoutMs <= 0) return ByteArray(0)
        val end = System.currentTimeMillis() + timeoutMs
        while (true) {
            val remaining = end - System.currentTimeMillis()
            if (remaining <= 0) break
            val p = feedback.poll(remaining, TimeUnit.MILLISECONDS) ?: break
            if (p[0] == out[0]) return p.copyOfRange(1, 64)
            AppLog.warn("RX unexpected reply " + cmdName(p[0]) + " while waiting for " + cmdName(out[0]))
        }
        AppLog.warn("Timeout waiting for " + cmdName(out[0]))
        return null
    }

    private fun cmdName(b: Byte): String = "0x" + (b.toInt() and 0xFF).toString(16).uppercase().padStart(2, '0')

    // ---- commands ----

    /** Operating mode: 16 = bootloader. -1 = no answer. */
    fun devOperatingMode(): Int {
        val r = send(byteArrayOf(CMD_DEV_GET_OPERATING_MODE), 1000) ?: return -1
        return r.u(0)
    }

    fun appJumpToBootloader() {
        AppLog.info("Jump to bootloader")
        send(byteArrayOf(CMD_APP_JUMP_TO_BOOTLOADER), 0)
    }

    /** null when the device did not answer (for example when it is in bootloader mode). */
    fun appGetPid(): Pid? {
        val r = send(byteArrayOf(CMD_APP_GET_PID), 1000)
        if (r == null) {
            AppLog.warn("Get PID parameters: no answer")
            return null
        }
        val pid = Pid(
            gain = r.u(0) + r.u(1) * 256,
            offset = r.u(2) + r.u(3) * 256,
            kp = r.u(4) + r.u(5) * 256,
            ki = r.u(6) + r.u(7) * 256,
            dGain = r.u(8),
            ovsGain = r.u(10)
        )
        AppLog.info("PID: Gain=" + pid.gain + " KP=" + pid.kp + " KI=" + pid.ki + " DGain=" + pid.dGain + " OVSGain=" + pid.ovsGain)
        return pid
    }

    fun appSetPid(p: Pid) {
        val b = ByteArray(13)
        b[0] = CMD_APP_SET_PID
        b[1] = (p.gain and 255).toByte()
        b[2] = (p.gain shr 8).toByte()
        // bytes 3 and 4: offset, always 0 like the Windows application
        b[5] = (p.kp and 255).toByte()
        b[6] = (p.kp shr 8).toByte()
        b[7] = (p.ki and 255).toByte()
        b[8] = (p.ki shr 8).toByte()
        b[9] = p.dGain.toByte()
        b[11] = p.ovsGain.toByte()
        send(b, 0)
    }

    /** Erases the application. Returns the bootloader status (0 = OK), or -1 when it did not answer. */
    fun blErase(): Int {
        val r = send(byteArrayOf(CMD_BL_ERASE_FLASH, 0x34, 0x12, 0x21, 0x43), 6000) ?: return -1
        return r.u(0)
    }

    /** Programs one record. Returns the bootloader status, or -1 when it did not answer. */
    fun blProgram(address: Long, data: ByteArray, len: Int): Int {
        require(len in 1..HexFile.MAX_RECORD_DATA)
        val b = ByteArray(13 + len)
        b[0] = CMD_BL_PROGRAM_FLASH
        b[1] = 0x34
        b[2] = 0x12
        b[3] = 0x21
        b[4] = 0x43
        for (i in 0 until 4) b[5 + i] = ((address shr (8 * i)) and 0xFF).toByte()
        b[9] = len.toByte()
        System.arraycopy(data, 0, b, 13, len)
        val r = send(b, 1000) ?: return -1
        return r.u(0)
    }

    /** true when the bootloader answered. */
    fun blProgramComplete(): Boolean =
        send(byteArrayOf(CMD_BL_PROGRAM_COMPLETE, 0x34, 0x12, 0x21, 0x43), 1000) != null

    fun blJumpToApplication() {
        send(byteArrayOf(CMD_BL_JUMP_TO_APP), 0)
    }

    // ---- remote control and settings (firmware protocol 1) ----

    /** null when the firmware does not answer: it has no remote control. */
    fun appInfo(): AppInfo? {
        val r = send(byteArrayOf(CMD_APP_GET_INFO), 1000) ?: return null
        return AppInfo(minor = r.u(0), major = r.u(1), board = r.u(2), protocol = r.u(3))
    }

    fun getState(): DeviceState? {
        val r = send(byteArrayOf(CMD_GET_STATE), 1000) ?: return null
        return DeviceState(
            set = r.u16(0),
            target = r.u16(2),
            presets = intArrayOf(r.u16(4), r.u16(6), r.u16(8), r.u16(10)),
            holder = r.u16(12),
            sleep = r.u16(14),
            min = r.u16(16),
            max = r.u16(18),
            mode = r.u(20),
            flags = r.u(21),
            actIron = r.u(22),
            tempStep = r.u(23).coerceAtLeast(1)
        )
    }

    /** Sets the temperature in degrees C; the station limits it like the knob. Returns the temperature that was set. */
    fun setTemp(degC: Int): Int? {
        val r = send(byteArrayOf(CMD_SET_TEMP, (degC and 255).toByte(), ((degC shr 8) and 255).toByte()), 1000) ?: return null
        AppLog.info("Set temperature " + degC + " -> " + r.u16(0))
        return r.u16(0)
    }

    fun selectPreset(index: Int): Int? {
        val r = send(byteArrayOf(CMD_PRESET, index.toByte()), 1000) ?: return null
        return r.u16(0)
    }

    /** Returns true while the station sleeps, null when it did not answer. */
    fun sleep(on: Boolean): Boolean? {
        val r = send(byteArrayOf(CMD_SLEEP, if (on) 1 else 0), 1000) ?: return null
        return r.u(0) != 0
    }

    /** Two-iron adapter: returns the active iron. */
    fun switchIron(): Int? {
        val r = send(byteArrayOf(CMD_SWITCH_IRON), 1000) ?: return null
        return r.u(0)
    }

    /** The 64 settings bytes of the station (the pincode is left out by the firmware), or null. */
    fun readSettings(): ByteArray? {
        val out = ByteArray(SETTINGS_SIZE)
        var addr = 0
        while (addr < SETTINGS_SIZE) {
            val n = minOf(SETTINGS_CHUNK, SETTINGS_SIZE - addr)
            val r = send(byteArrayOf(CMD_READ_SETTINGS, addr.toByte(), n.toByte()), 1000) ?: return null
            if (r.u(2) != 0 || r.u(1) != n) return null
            System.arraycopy(r, 3, out, addr, n)
            addr += n
        }
        return out
    }

    /** Writes the 64 settings bytes. The station must be restarted afterwards ([restartDevice]) to use them. */
    fun writeSettings(data: ByteArray): Boolean {
        require(data.size == SETTINGS_SIZE)
        var addr = 0
        while (addr < SETTINGS_SIZE) {
            val n = minOf(SETTINGS_CHUNK, SETTINGS_SIZE - addr)
            val b = ByteArray(5 + n)
            b[0] = CMD_WRITE_SETTINGS
            b[1] = 0x5A
            b[2] = 0xA5.toByte()
            b[3] = addr.toByte()
            b[4] = n.toByte()
            System.arraycopy(data, addr, b, 5, n)
            val r = send(b, 3000) ?: return false
            if (r.u(0) != 0) return false
            addr += n
        }
        return true
    }

    fun restartDevice() {
        AppLog.info("Restart station")
        send(byteArrayOf(CMD_RESTART, 0x5A, 0xA5.toByte()), 0)
    }

    companion object {
        const val SETTINGS_SIZE = 64
        const val SETTINGS_CHUNK = 48
        const val CMD_APP_GET_INFO: Byte = 0x80.toByte()
        const val CMD_GET_STATE: Byte = 0x10
        const val CMD_SET_TEMP: Byte = 0x11
        const val CMD_PRESET: Byte = 0x12
        const val CMD_SLEEP: Byte = 0x13
        const val CMD_SWITCH_IRON: Byte = 0x14
        const val CMD_READ_SETTINGS: Byte = 0x20
        const val CMD_WRITE_SETTINGS: Byte = 0x21
        const val CMD_RESTART: Byte = 0x22
        const val VID = 0x04D8
        const val PID = 0x003C
        const val ACTION_PERMISSION = "org.unisolder.android.USB_PERMISSION"

        const val CMD_APP_SET_PID: Byte = 3
        const val CMD_APP_GET_PID: Byte = 4
        const val CMD_DEV_GET_OPERATING_MODE: Byte = 0x61
        const val CMD_APP_JUMP_TO_BOOTLOADER: Byte = 0x81.toByte()
        const val CMD_BL_ERASE_FLASH: Byte = 0xE1.toByte()
        const val CMD_BL_PROGRAM_FLASH: Byte = 0xE2.toByte()
        const val CMD_BL_JUMP_TO_APP: Byte = 0xE4.toByte()
        const val CMD_BL_PROGRAM_COMPLETE: Byte = 0xE5.toByte()
    }
}
