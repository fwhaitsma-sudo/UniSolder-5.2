using System;
using System.IO;
using System.Linq;
using System.Threading;
using HidSharp;

namespace SSComm
{
    //USB HID transport built on HidSharp: device enumeration, report I/O and 32/64-bit interop are handled by the library.
    //Reports carry the report ID in byte 0 (always 0 for this device); the payload is the rest of the report.
    public class USBHID : IUniComm
    {
        public bool Connected { get; private set; } = false;

        public Int32 lBytesSent;
        public Int32 lBytesReceived;

        public const Int32 RXFIFOSIZE = 32768;
        public const Int32 TXFIFOSIZE = 16384;

        public UInt16 DevVID = UInt16.MaxValue;
        public UInt16 DevPID = UInt16.MaxValue;
        public Int32 DevRevision = Int32.MaxValue;
        public string DevSerialNumber = string.Empty;
        public Guid DevGUID = Guid.Empty;

        public event EventHandler DataReceived;

        //read timeout used only so the RX thread can notice a Disconnect(), not a protocol timeout
        private const int RXPollTimeout = 250;
        private const int TXTimeout = 2000;

        private byte[] RXFIFO = new byte[RXFIFOSIZE];
        private Int32 RXReadPos;
        private Int32 RXWritePos;

        private byte[] TXFIFO = new byte[TXFIFOSIZE];
        private Int32 TXReadPos;
        private Int32 TXWritePos;

        private Thread rxThread;
        private Thread txThread;
        private volatile bool thExit;
        private ManualResetEvent txWake = new ManualResetEvent(false);

        private HidDevice hidDevice;
        private HidStream hidStream;
        private int inputReportLength;
        private int outputReportLength;

        private object ThisLock = new Object();

        public bool Connect()
        {
            if (Connected) Disconnect();

            HidDevice device = DeviceList.Local.GetHidDevices(DevVID, DevPID)
                .FirstOrDefault(d => (DevRevision == Int32.MaxValue || d.ReleaseNumberBcd == DevRevision)
                                  && (string.IsNullOrEmpty(DevSerialNumber) || SerialMatches(d)));
            if (device == null) return false;

            HidStream stream;
            if (!device.TryOpen(out stream))
            {
                Log.Error("USB HID: device found but cannot be opened, path " + device.DevicePath);
                return false;
            }
            stream.ReadTimeout = RXPollTimeout;
            stream.WriteTimeout = TXTimeout;

            lock (ThisLock)
            {
                hidDevice = device;
                hidStream = stream;
                inputReportLength = device.GetMaxInputReportLength();
                outputReportLength = device.GetMaxOutputReportLength();
            }

            thExit = false;
            txWake.Reset();
            rxThread = new Thread(this.Th_Read) { IsBackground = true, Name = "USB HID RX" };
            txThread = new Thread(this.Th_Write) { IsBackground = true, Name = "USB HID TX" };
            Connected = true;
            rxThread.Start();
            txThread.Start();
            Log.Info("USB HID: connected VID=0x" + DevVID.ToString("X4") + " PID=0x" + DevPID.ToString("X4") + " in/out report " + inputReportLength + "/" + outputReportLength + " bytes, path " + device.DevicePath);
            return true;
        }

        private bool SerialMatches(HidDevice d)
        {
            try { return d.GetSerialNumber() == DevSerialNumber; }
            catch (IOException) { return false; }
        }

        public void Disconnect()
        {
            //cleared first so the I/O threads failing on the closed stream know the disconnect is deliberate
            bool wasConnected = Connected;
            Connected = false;
            if (wasConnected) Log.Info("USB HID: disconnecting, " + lBytesSent + " bytes sent, " + lBytesReceived + " received");
            thExit = true;
            txWake.Set();
            JoinThread(rxThread);
            JoinThread(txThread);
            rxThread = null;
            txThread = null;
            lock (ThisLock)
            {
                if (hidStream != null)
                {
                    hidStream.Dispose();
                    hidStream = null;
                }
                hidDevice = null;
            }
        }

        private static void JoinThread(Thread t)
        {
            //Disconnect() may be called from one of the I/O threads themselves after an error
            if (t != null && t.IsAlive && t != Thread.CurrentThread) t.Join(1000);
        }

        public void Init()
        {
            Disconnect();
            lBytesSent = 0;
            lBytesReceived = 0;
            RXReadPos = 0;
            RXWritePos = 0;
            TXReadPos = 0;
            TXWritePos = 0;
            DevVID = UInt16.MaxValue;
            DevPID = UInt16.MaxValue;
            DevRevision = Int32.MaxValue;
            DevGUID = Guid.Empty;
        }

        public Int32 BytesSent()
        {
            return lBytesSent;
        }

        public Int32 BytesReceived()
        {
            return lBytesReceived;
        }

        public Int32 RXDataCount
        {
            get
            {
                Int32 i;
                i = RXWritePos - RXReadPos;
                if (i < 0) i += RXFIFOSIZE;
                return i;
            }
        }

        public byte ReadByte()
        {
            byte rv;
            if (RXWritePos != RXReadPos)
            {
                rv = RXFIFO[RXReadPos];
                RXReadPos = (RXReadPos + 1) % RXFIFOSIZE;
                return rv;
            }
            else
            {
                throw new Exception("SSComm(USBHID).ReadByte: RX FIFO empty!");
            }
        }

        public Int32 Read(ref byte[] DBuffer, Int32 DOffset, Int32 MaxBytes)
        {
            Int32 i;
            if (MaxBytes < 0) MaxBytes = 0;
            if (MaxBytes > 0)
            {
                i = RXDataCount;
                if (MaxBytes > i) MaxBytes = i;
                if (MaxBytes < i) i = MaxBytes;
                while (i > 0)
                {
                    DBuffer[DOffset] = RXFIFO[RXReadPos];
                    DOffset += 1;
                    RXReadPos = (RXReadPos + 1) % RXFIFOSIZE;
                    i -= 1;
                }
            }
            return MaxBytes;
        }

        public Int32 TXFreeSpace
        {
            get
            {
                Int32 i;
                i = TXWritePos - TXReadPos;
                if (i < 0) i += TXFIFOSIZE;
                i = TXFIFOSIZE - i;
                return i;
            }
        }

        public void WriteByte(ref byte wb, bool dow = true)
        {
            if (TXFreeSpace > 0)
            {
                TXFIFO[TXWritePos] = wb;
                TXWritePos = (TXWritePos + 1) % TXFIFOSIZE;
                if (dow && (txThread != null)) txWake.Set();
            }
            else
            {
                throw new Exception("SSComm(USBHID).WriteByte: TX FIFO Full!");
            }
        }

        public bool Write(ref byte[] DBuffer, int DOffset, int NumBytes)
        {
            Int32 i;
            if (NumBytes > TXFreeSpace) throw new Exception("Not enough space in TX buffer.");
            for (i = 0; i < NumBytes; i++)
            {
                TXFIFO[TXWritePos] = DBuffer[DOffset + i];
                TXWritePos = (TXWritePos + 1) % TXFIFOSIZE;
            }
            if (txThread != null) txWake.Set();
            return true;
        }

        public void Dispose()
        {
            Disconnect();
            txWake.Dispose();
        }

        private void Th_Read()
        {
            HidStream stream = hidStream;
            byte[] report = new byte[inputReportLength];
            try
            {
                while (!thExit)
                {
                    int count;
                    try
                    {
                        count = stream.Read(report, 0, report.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    if (count <= 1) continue;
                    //skip the report ID in byte 0
                    int payload = count - 1;
                    lBytesReceived += payload;
                    if (RXDataCount + payload >= RXFIFOSIZE) throw new IOException("RX FIFO overflow");
                    for (int i = 1; i < count; i++)
                    {
                        RXFIFO[RXWritePos] = report[i];
                        RXWritePos = (RXWritePos + 1) % RXFIFOSIZE;
                    }
                    DataReceived?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                //an exception after a deliberate Disconnect() (stream closed) is expected, not an error
                if (!thExit)
                {
                    Log.Error("USB HID: read failed, disconnecting (device removed?)", ex);
                    Disconnect();
                }
            }
        }

        private void Th_Write()
        {
            HidStream stream = hidStream;
            byte[] report = new byte[outputReportLength];
            try
            {
                while (!thExit)
                {
                    Int32 lTXSize = TXWritePos - TXReadPos;
                    if (lTXSize < 0) lTXSize += TXFIFOSIZE;
                    //the protocol only sends full reports: wait until a whole payload is queued
                    if (lTXSize >= (report.Length - 1))
                    {
                        report[0] = 0;
                        for (int i = 0; i < (report.Length - 1); i++)
                        {
                            report[i + 1] = TXFIFO[(TXReadPos + i) % TXFIFOSIZE];
                        }
                        stream.Write(report, 0, report.Length);
                        TXReadPos = (TXReadPos + report.Length - 1) % TXFIFOSIZE;
                        lBytesSent += report.Length - 1;
                    }
                    else
                    {
                        txWake.WaitOne(Timeout.Infinite);
                        txWake.Reset();
                    }
                }
            }
            //device removed or stream closed while writing: end the thread instead of crashing the process
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is ObjectDisposedException)
            {
                if (!thExit) Log.Error("USB HID: write failed, TX thread stopped", ex);
            }
        }
    }
}
