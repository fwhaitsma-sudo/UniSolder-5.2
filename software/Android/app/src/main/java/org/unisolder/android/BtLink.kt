package org.unisolder.android

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import java.io.InputStream
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Bluetooth Classic (SPP) link to the ESP32 bridge in the station. The bridge passes bytes through, the station
 * speaks frames: 0x7E, 64 payload bytes (same layout as the USB reports), CRC-8 (polynomial 0x07) over the payload.
 */
class BtLink {
    @Volatile
    private var socket: BluetoothSocket? = null
    private var out: OutputStream? = null
    private val frames = LinkedBlockingQueue<ByteArray>()

    @Volatile
    private var alive = false

    @Volatile
    private var lastRx = 0L

    val connected: Boolean get() = alive && System.currentTimeMillis() - lastRx < SILENCE_MS

    /** Returns null when connected, otherwise the reason. Blocks for a few seconds. */
    @SuppressLint("MissingPermission")
    fun open(address: String): String? {
        val adapter = BluetoothAdapter.getDefaultAdapter() ?: return "this device has no Bluetooth"
        if (!adapter.isEnabled) return "Bluetooth is off"
        val dev: BluetoothDevice = try {
            adapter.getRemoteDevice(address)
        } catch (e: IllegalArgumentException) {
            return "invalid Bluetooth address"
        }
        close()
        val s = try {
            dev.createRfcommSocketToServiceRecord(SPP)
        } catch (e: Exception) {
            return "cannot create the Bluetooth socket: " + e.message
        }
        try {
            adapter.cancelDiscovery()
            s.connect()
        } catch (e: Exception) {
            try {
                s.close()
            } catch (x: Exception) {
            }
            return "no connection (" + (e.message ?: "failed") + ")"
        }
        socket = s
        out = s.outputStream
        frames.clear()
        lastRx = System.currentTimeMillis()
        alive = true
        val input = s.inputStream
        val t = Thread { readLoop(input) }
        t.isDaemon = true
        t.start()
        return null
    }

    private fun readLoop(input: InputStream) {
        val acc = ByteArray(1024)
        var n = 0
        val tmp = ByteArray(256)
        try {
            while (alive) {
                val r = input.read(tmp)
                if (r < 0) break
                if (n + r > acc.size) n = 0     // garbage, start over
                System.arraycopy(tmp, 0, acc, n, r)
                n += r
                lastRx = System.currentTimeMillis()
                var i = 0
                while (true) {
                    while (i < n && acc[i] != START) i++
                    if (n - i < FRAME) break
                    val p = acc.copyOfRange(i + 1, i + 65)
                    if (crc(p) == acc[i + 65]) {
                        frames.offer(p)
                        i += FRAME
                    } else {
                        i++     // a 0x7E inside data or noise: look for the next start byte
                    }
                }
                if (i > 0) {
                    System.arraycopy(acc, i, acc, 0, n - i)
                    n -= i
                }
            }
        } catch (e: Exception) {
        }
        alive = false
    }

    fun close() {
        alive = false
        try {
            socket?.close()
        } catch (e: Exception) {
        }
        socket = null
        out = null
        frames.clear()
    }

    @Synchronized
    fun write(data: ByteArray): Boolean {
        val o = out ?: return false
        if (!alive) return false
        val f = ByteArray(FRAME)
        f[0] = START
        System.arraycopy(data, 0, f, 1, 64)
        f[65] = crc(data)
        return try {
            o.write(f)
            o.flush()
            true
        } catch (e: Exception) {
            alive = false
            false
        }
    }

    /** One 64-byte packet, or -1 on timeout or when the link is gone. */
    fun read(buf: ByteArray, timeoutMs: Int): Int {
        val p = frames.poll(timeoutMs.toLong(), TimeUnit.MILLISECONDS) ?: return -1
        System.arraycopy(p, 0, buf, 0, 64)
        return 64
    }

    private fun crc(d: ByteArray): Byte {
        var c = 0
        for (i in 0 until 64) {
            c = c xor (d[i].toInt() and 0xFF)
            for (k in 0 until 8) c = if (c and 0x80 != 0) ((c shl 1) xor 0x07) and 0xFF else (c shl 1) and 0xFF
        }
        return c.toByte()
    }

    companion object {
        private const val START: Byte = 0x7E
        private const val FRAME = 66
        /** the station sends live data about 10 times a second while the link is kept alive */
        private const val SILENCE_MS = 6000L
        private val SPP: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }
}
