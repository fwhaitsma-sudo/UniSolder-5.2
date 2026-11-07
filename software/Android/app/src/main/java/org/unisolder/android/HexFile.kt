package org.unisolder.android

/**
 * Intel HEX reader and checker for the UniSolder bootloader (port of HexFileManager.cs of the Windows application).
 * Data is returned as records of at most 48 bytes (the 64-byte USB packet minus a 13-byte header leaves room for 51).
 */
class HexFile {
    class Rec(var address: Long) {
        val data = ByteArray(MAX_MERGED)
        var len = 0
    }

    val records = ArrayList<Rec>()

    /** Returns null when the file was read, otherwise the reason. */
    fun load(text: String): String? {
        records.clear()
        var seg = 0L
        var lin = 0L
        var lineNo = 0
        try {
            for (raw in text.lineSequence()) {
                lineNo++
                val s = raw.trim()
                if (s.length < 11) continue
                if (s[0] != ':') return "line $lineNo: does not start with ':'"
                val count = hex(s, 1, 2)
                if (s.length != 11 + 2 * count) return "line $lineNo: length does not match its byte count"
                var sum = 0
                for (i in 0 until (s.length - 1) / 2 - 1) sum = (sum + hex(s, 1 + i * 2, 2)) and 0xFF
                if (((0x100 - sum) and 0xFF) != hex(s, s.length - 2, 2)) return "line $lineNo: bad checksum"
                when (hex(s, 7, 2)) {
                    0 -> addData(hex(s, 3, 4).toLong() + seg + lin, s, count)
                    1 -> {}
                    2 -> {
                        seg = hex(s, 9, 4).toLong() shl 4
                        lin = 0
                    }
                    4 -> {
                        lin = hex(s, 9, 4).toLong() shl 16
                        seg = 0
                    }
                    else -> {
                        lin = 0
                        seg = 0
                    }
                }
            }
        } catch (e: NumberFormatException) {
            return "line $lineNo: invalid hexadecimal digits"
        }
        return null
    }

    private fun addData(address: Long, s: String, count: Int) {
        var addr = address
        var off = 0
        var remaining = count
        val last = records.lastOrNull()
        if (last != null && addr == last.address + last.len) {
            while (remaining > 0 && last.len < MAX_MERGED) {
                last.data[last.len++] = hex(s, 9 + off * 2, 2).toByte()
                off++
                addr++
                remaining--
            }
        }
        while (remaining > 0) {
            val r = Rec(addr)
            while (remaining > 0 && r.len < MAX_MERGED) {
                r.data[r.len++] = hex(s, 9 + off * 2, 2).toByte()
                off++
                addr++
                remaining--
            }
            records.add(r)
        }
    }

    private fun hex(s: String, start: Int, n: Int): Int = s.substring(start, start + n).toInt(16)

    val totalBytes: Long get() = records.sumOf { it.len.toLong() }

    /** Last address of the application area contained in the file. */
    val applicationEnd: Long
        get() {
            var end = 0L
            for (r in records) {
                if (r.address >= APP_FLASH_START && r.address < 0x1D100000L) end = maxOf(end, r.address + r.len - 1)
            }
            return end
        }

    /** Checks the file is a firmware built for the UniSolder bootloader. Returns null if it can be used, otherwise the reason. */
    fun validate(): String? {
        if (records.isEmpty()) return "the file contains no data."
        var hasAppStart = false
        for (r in records) {
            val end = r.address + r.len // exclusive
            if (r.len > MAX_RECORD_DATA)
                return "record at 0x" + r.address.toString(16).uppercase() + " has " + r.len + " bytes, more than a USB packet can carry."
            if (r.address < APP_FLASH_START && end > BOOTLOADER_FLASH_START)
                return "the file contains code in the bootloader area (0x1D000000-0x1D002FFF). It is probably a firmware built without bootloader (PIC32_Standalone): use the PIC32_with_bootloader HEX file."
            if (r.address <= APP_FLASH_START && end > APP_FLASH_START) hasAppStart = true
        }
        if (!hasAppStart)
            return "the file contains no code at the application start address 0x1D003000: it is not a firmware built for the UniSolder bootloader (PIC32_with_bootloader)."
        return null
    }

    companion object {
        const val MAX_MERGED = 48
        const val MAX_RECORD_DATA = 51
        const val BOOTLOADER_FLASH_START = 0x1D000000L
        const val APP_FLASH_START = 0x1D003000L
    }
}
