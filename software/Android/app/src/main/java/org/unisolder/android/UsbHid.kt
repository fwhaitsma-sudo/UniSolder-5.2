package org.unisolder.android

import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbDeviceConnection
import android.hardware.usb.UsbEndpoint
import android.hardware.usb.UsbInterface
import android.hardware.usb.UsbManager

/** Raw 64-byte HID reports over the USB host API (interrupt endpoints, SET_REPORT when there is no OUT endpoint). */
class UsbHid(private val usb: UsbManager) {
    private var conn: UsbDeviceConnection? = null
    private var intf: UsbInterface? = null
    private var epIn: UsbEndpoint? = null
    private var epOut: UsbEndpoint? = null

    var device: UsbDevice? = null
        private set

    val connected: Boolean get() = conn != null

    /** Returns null when the device is open, otherwise the reason it is not. */
    fun open(dev: UsbDevice): String? {
        for (i in 0 until dev.interfaceCount) {
            val itf = dev.getInterface(i)
            AppLog.info("USB interface " + itf.id + ": class " + itf.interfaceClass + ", " + itf.endpointCount + " endpoints")
        }
        for (i in 0 until dev.interfaceCount) {
            val itf = dev.getInterface(i)
            if (itf.interfaceClass != UsbConstants.USB_CLASS_HID) continue
            var ein: UsbEndpoint? = null
            var eout: UsbEndpoint? = null
            for (e in 0 until itf.endpointCount) {
                val ep = itf.getEndpoint(e)
                if (ep.type != UsbConstants.USB_ENDPOINT_XFER_INT) continue
                if (ep.direction == UsbConstants.USB_DIR_IN) {
                    if (ein == null) ein = ep
                } else {
                    if (eout == null) eout = ep
                }
            }
            if (ein == null) continue
            val c = usb.openDevice(dev) ?: return "cannot open the USB device (no permission?)"
            if (!c.claimInterface(itf, true)) {
                c.close()
                return "cannot claim the HID interface"
            }
            conn = c
            intf = itf
            epIn = ein
            epOut = eout
            device = dev
            AppLog.info("USB HID opened: IN endpoint " + ein.address + (if (eout != null) ", OUT endpoint " + eout.address else ", no OUT endpoint (using SET_REPORT)"))
            return null
        }
        return "no HID interface found on the device"
    }

    fun close() {
        val c = conn
        val itf = intf
        conn = null
        intf = null
        epIn = null
        epOut = null
        device = null
        if (c != null) {
            try {
                if (itf != null) c.releaseInterface(itf)
            } catch (e: Exception) {
            }
            c.close()
        }
    }

    /** Sends one 64-byte report. */
    fun write(data: ByteArray): Boolean {
        val c = conn ?: return false
        val itf = intf ?: return false
        val out = epOut
        val n = if (out != null) {
            c.bulkTransfer(out, data, data.size, 1000)
        } else {
            // HID class request SET_REPORT, output report, report ID 0
            c.controlTransfer(0x21, 0x09, 0x0200, itf.id, data, data.size, 1000)
        }
        return n >= 0
    }

    /** Reads one report; returns the number of bytes, or -1 on timeout or error. */
    fun read(buf: ByteArray, timeoutMs: Int): Int {
        val c = conn ?: return -1
        val ep = epIn ?: return -1
        return c.bulkTransfer(ep, buf, buf.size, timeoutMs)
    }
}
