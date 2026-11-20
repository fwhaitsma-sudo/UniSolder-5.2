using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace SSComm
{
    /// <summary>
    /// Bluetooth link to the ESP32 bridge in the station, through the virtual COM port Windows creates when the bridge is paired
    /// (the outgoing "Standard Serial over Bluetooth link" port). Same protocol as the Android app: first the line "PIN:&lt;pin&gt;\n"
    /// (answers OK, NO or LOCK), then frames of 0x7E, 64 payload bytes (the same layout as the USB reports) and a CRC-8
    /// (polynomial 0x07) over the payload. The receive side hands out the 64 byte payloads one after the other, like the USB transport.
    /// </summary>
    public class BtSerial : IUniComm
    {
        public const string ErrWrongPin = "wrong PIN";
        private const byte Start = 0x7E;
        private const int Frame = 66;
        private const int RXFIFOSIZE = 32768;
        /// <summary>The station sends live data about 10 times a second: no data for this long means the link is gone.</summary>
        private const long SilenceMs = 6000;

        /// <summary>COM port of the paired bridge, for example COM5.</summary>
        public string PortName = "";
        public string Pin = "";

        /// <summary>Reason of the last failed Connect(), empty after a successful one.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>The bridge refused the PIN: do not try again until the PIN is changed (repeated wrong PINs lock the bridge).</summary>
        public bool PinRejected { get; set; }

        public event EventHandler DataReceived;

        private SerialPort port;
        private Thread rxThread;
        private volatile bool thExit;
        private volatile bool alive;
        private long lastRx;
        private readonly object writeLock = new object();
        private int lBytesSent, lBytesReceived;

        private readonly byte[] RXFIFO = new byte[RXFIFOSIZE];
        private int RXReadPos, RXWritePos;
        private readonly byte[] partial = new byte[64];
        private int partialCount;

        public bool Connected
        {
            get { return alive && port != null && Environment.TickCount64 - Interlocked.Read(ref lastRx) < SilenceMs; }
        }

        public int BytesSent() { return lBytesSent; }
        public int BytesReceived() { return lBytesReceived; }
        public int TXFreeSpace { get { return 16384; } }

        public void Init()
        {
            Disconnect();
            lBytesSent = 0;
            lBytesReceived = 0;
            RXReadPos = 0;
            RXWritePos = 0;
            partialCount = 0;
        }

        /// <summary>Opens the port and sends the PIN. Blocks for up to some seconds: call it from a worker thread.</summary>
        public bool Connect()
        {
            Disconnect();
            LastError = "";
            if (string.IsNullOrEmpty(PortName))
            {
                LastError = "no COM port chosen";
                return false;
            }
            SerialPort sp = null;
            try
            {
                sp = new SerialPort(PortName, 115200, Parity.None, 8, StopBits.One);
                sp.ReadTimeout = 250;
                sp.WriteTimeout = 2000;
                sp.DtrEnable = true;
                sp.Open();      //an outgoing Bluetooth port connects to the bridge when it is opened
                sp.DiscardInBuffer();
                var pin = Encoding.ASCII.GetBytes("PIN:" + Pin + "\n");
                sp.Write(pin, 0, pin.Length);
                string reply = ReadLine(sp, 5000);
                if (reply == "NO")
                {
                    PinRejected = true;
                    return Fail(sp, ErrWrongPin);
                }
                if (reply == "LOCK") return Fail(sp, "locked after wrong PINs, try again in a minute");
                if (reply != "OK") return Fail(sp, "the bridge did not answer (wrong COM port, or the bridge is not switched on)");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException || ex is TimeoutException)
            {
                return Fail(sp, "cannot open " + PortName + ": " + ex.Message);
            }

            lock (writeLock)
            {
                port = sp;
            }
            RXReadPos = 0;
            RXWritePos = 0;
            partialCount = 0;
            Interlocked.Exchange(ref lastRx, Environment.TickCount64);
            thExit = false;
            alive = true;
            rxThread = new Thread(Th_Read) { IsBackground = true, Name = "Bluetooth RX" };
            rxThread.Start();
            Log.Info("Bluetooth: connected through " + PortName);
            return true;
        }

        private bool Fail(SerialPort sp, string reason)
        {
            LastError = reason;
            Log.Warn("Bluetooth: " + reason);
            try { if (sp != null) sp.Dispose(); } catch (Exception) { }
            return false;
        }

        /// <summary>One text line, read byte by byte so that no frame bytes are consumed; null on timeout.</summary>
        private static string ReadLine(SerialPort sp, int timeoutMs)
        {
            var end = Environment.TickCount64 + timeoutMs;
            var sb = new StringBuilder();
            while (Environment.TickCount64 < end)
            {
                int c;
                try { c = sp.ReadByte(); }
                catch (TimeoutException) { continue; }
                if (c < 0) return null;
                if (c == '\n') return sb.ToString().Trim();
                if (sb.Length < 16) sb.Append((char)c);
            }
            return null;
        }

        public void Disconnect()
        {
            bool was = alive;
            alive = false;
            thExit = true;
            var t = rxThread;
            if (t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(1500);
            rxThread = null;
            SerialPort sp;
            lock (writeLock)
            {
                sp = port;
                port = null;
            }
            if (sp != null)
            {
                try { sp.Dispose(); } catch (Exception) { }
            }
            if (was) Log.Info("Bluetooth: disconnected, " + lBytesSent + " bytes sent, " + lBytesReceived + " received");
        }

        private void Th_Read()
        {
            var sp = port;
            var acc = new byte[1024];
            var tmp = new byte[256];
            int n = 0;
            try
            {
                while (!thExit)
                {
                    int r;
                    try { r = sp.Read(tmp, 0, tmp.Length); }
                    catch (TimeoutException) { continue; }
                    if (r <= 0) continue;
                    Interlocked.Exchange(ref lastRx, Environment.TickCount64);
                    lBytesReceived += r;
                    if (n + r > acc.Length) n = 0;      //garbage: start over
                    Array.Copy(tmp, 0, acc, n, r);
                    n += r;
                    int i = 0;
                    bool got = false;
                    while (true)
                    {
                        while (i < n && acc[i] != Start) i++;
                        if (n - i < Frame) break;
                        if (Crc(acc, i + 1) == acc[i + 65])
                        {
                            Push(acc, i + 1);
                            i += Frame;
                            got = true;
                        }
                        else
                        {
                            i++;        //a 0x7E inside data or noise: look for the next start byte
                        }
                    }
                    if (i > 0)
                    {
                        Array.Copy(acc, i, acc, 0, n - i);
                        n -= i;
                    }
                    if (got) DataReceived?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ObjectDisposedException)
            {
                if (!thExit) Log.Error("Bluetooth: read failed, link lost", ex);
            }
            alive = false;
        }

        private void Push(byte[] src, int offset)
        {
            if (RXDataCount + 64 >= RXFIFOSIZE) return;      //the consumer is far behind: drop the packet
            for (int i = 0; i < 64; i++)
            {
                RXFIFO[RXWritePos] = src[offset + i];
                RXWritePos = (RXWritePos + 1) % RXFIFOSIZE;
            }
        }

        private static byte Crc(byte[] d, int offset)
        {
            int c = 0;
            for (int i = 0; i < 64; i++)
            {
                c ^= d[offset + i];
                for (int k = 0; k < 8; k++) c = ((c & 0x80) != 0) ? (((c << 1) ^ 0x07) & 0xFF) : ((c << 1) & 0xFF);
            }
            return (byte)c;
        }

        public int RXDataCount
        {
            get
            {
                int i = RXWritePos - RXReadPos;
                if (i < 0) i += RXFIFOSIZE;
                return i;
            }
        }

        public byte ReadByte()
        {
            if (RXWritePos == RXReadPos) throw new Exception("SSComm(BtSerial).ReadByte: RX FIFO empty!");
            byte rv = RXFIFO[RXReadPos];
            RXReadPos = (RXReadPos + 1) % RXFIFOSIZE;
            return rv;
        }

        public int Read(ref byte[] DBuffer, int DOffset, int MaxBytes)
        {
            if (MaxBytes < 0) MaxBytes = 0;
            int avail = RXDataCount;
            if (MaxBytes > avail) MaxBytes = avail;
            for (int i = 0; i < MaxBytes; i++)
            {
                DBuffer[DOffset + i] = RXFIFO[RXReadPos];
                RXReadPos = (RXReadPos + 1) % RXFIFOSIZE;
            }
            return MaxBytes;
        }

        public void WriteByte(ref byte wb, bool DoWake = true)
        {
            partial[partialCount++] = wb;
            if (partialCount == 64)
            {
                partialCount = 0;
                var copy = (byte[])partial.Clone();
                Write(ref copy, 0, 64);
            }
        }

        /// <summary>Sends whole packets of 64 bytes, each in its own frame.</summary>
        public bool Write(ref byte[] DBuffer, int DOffset, int NumBytes)
        {
            bool ok = true;
            while (NumBytes >= 64 && ok)
            {
                var f = new byte[Frame];
                f[0] = Start;
                Array.Copy(DBuffer, DOffset, f, 1, 64);
                f[65] = Crc(DBuffer, DOffset);
                lock (writeLock)
                {
                    try
                    {
                        if (port == null || !alive) return false;
                        port.Write(f, 0, f.Length);
                        lBytesSent += 64;
                    }
                    catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
                    {
                        Log.Error("Bluetooth: write failed, link lost", ex);
                        alive = false;
                        ok = false;
                    }
                }
                DOffset += 64;
                NumBytes -= 64;
            }
            return ok;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
