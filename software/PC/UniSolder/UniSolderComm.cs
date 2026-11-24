
using Microsoft.VisualBasic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Windows.Forms;
using System.IO;
using System.Threading;

public class UniSolderComm
{

    #region "Enums"
    public enum Commands : byte
    {
        DEV_QUERY = 0x02,
        DEV_GET_INFO = 0x60,
        DEV_GET_OPERATING_MODE = 0x61,
        DEV_RESET = 0x62,

        APP_GET_INFO = 0x80,
        APP_JUMP_TO_BOOTLOADER = 0x81,
        APP_RESTART = 0x82,

        APP_SET_PID = 3,
        APP_GET_PID = 4,

        //remote control and settings (firmware protocol 1 and later)
        REMOTE_GET_STATE = 0x10,
        REMOTE_SET_TEMP = 0x11,
        REMOTE_PRESET = 0x12,
        REMOTE_SLEEP = 0x13,
        REMOTE_SWITCH_IRON = 0x14,
        REMOTE_PID_SAVE = 0x15,
        REMOTE_PID_CLEAR = 0x16,
        REMOTE_PID_SAVED = 0x17,
        REMOTE_READ_SETTINGS = 0x20,
        REMOTE_WRITE_SETTINGS = 0x21,
        REMOTE_RESTART = 0x22,
        REMOTE_GET_PARAMS = 0x40,
        REMOTE_SET_PARAM = 0x41,
        REMOTE_GET_NAME = 0x42,
        REMOTE_SET_NAME = 0x43,

        BL_GET_INFO = 0xE0,
        BL_ERASE_FLASH = 0xE1,
        BL_PROGRAM_FLASH = 0xE2,
        BL_READ_CRC = 0xE3,
        BL_JUMP_TO_APP = 0xE4,
        BL_PROGRAM_COMPLETE = 0xE5
    }

    #endregion

    #region "Public subclasses"

    public class T_CommandFeedback
    {
        public volatile bool IsEmpty;
        public readonly ManualResetEventSlim Received = new ManualResetEventSlim(false);
        public byte Command;
        public byte Status;
        public byte[] Data = new byte[1024];

        public UInt32 DataLength;
        public void Clear()
        {
            IsEmpty = true;
            Command = 0;
            Status = 0;
            DataLength = 0;
        }

        public bool ReadFromBuffer(ref byte[] bb, UInt32 Offset, UInt32 DLen)
        {
            UInt32 i = default(UInt32);
            Command = bb[Offset];
            //Status = bb(Offset + 1)
            DataLength = DLen - 1;
            for (i = 0; i <= DLen - 2; i++)
            {
                Data[i] = bb[Offset + 1 + i];
            }
            IsEmpty = false;
            Received.Set();
            return true;
        }

        public T_CommandFeedback()
        {
            Clear();
        }
    }

    public class PID
    {
        public UInt16 Gain;
        public UInt16 Offset;
        public UInt16 KP;
        public UInt16 KI;
        public byte DGain;
        public byte OVSGain;
    }
    /// <summary>Answer of the application info command (0x80). Protocol 1 or more means remote control is available.</summary>
    public class AppInfo
    {
        public int Minor;
        public int Major;
        public int Board;
        public int Protocol;
        public string BoardName { get { return Board == 1 ? "5.2C" : "5.2"; } }
    }

    /// <summary>State of the station: temperatures in degrees C (0 = OFF for presets, 0 = heater off for the target).</summary>
    public class DeviceState
    {
        public int Set;
        public int Target;
        public int[] Presets = new int[4];
        public int Holder;
        public int Sleep;
        public int Min;
        public int Max;
        public int Mode;
        public int Flags;
        public int ActIron;
        public int TempStep = 1;
        public bool ManualSleep { get { return (Flags & 1) != 0; } }
        public bool Standby { get { return (Flags & 2) != 0; } }
        public bool TwoIrons { get { return (Flags & 4) != 0; } }
        public bool OnStand { get { return (Flags & 8) != 0; } }
        public bool Fahrenheit { get { return (Flags & 16) != 0; } }
        /// <summary>The station is in its menu or another screen where remote changes should wait.</summary>
        public bool InMenu { get { return (Mode >= 3 && Mode <= 7) || (Mode >= 9 && Mode <= 12); } }
    }

    #endregion

    private SSComm.IUniComm lTransport;
    public SSComm.IUniComm Transport
    {
        get
        {
            return lTransport;
        }
        set
        {
            if (lTransport != null && lTransport != value)
            {
                lTransport.DataReceived -= Transport_DataReceived;
            }
            if ((lTransport = value) != null)
            {
                lTransport.DataReceived += Transport_DataReceived;
            }
        }
    }


    public T_CommandFeedback CommandFeedBack = new T_CommandFeedback();

    public class LiveDataReceivedEventData : EventArgs
    {
        public byte[] Data;
    }

    public event EventHandler<LiveDataReceivedEventData> LiveDataReceived;
    public event EventHandler InstrumentChange;
    /// <summary>Iron ID from the last instrument change packet (0 = none yet).</summary>
    public int IronId { get; private set; }


    private Stopwatch TOTimer = new Stopwatch();
    public void Init()
    {
        CommandFeedBack.Clear();
    }

    public int DevQuery()
    {
        byte[] bb = { (byte)Commands.DEV_QUERY };
        var result = SendBINCommand(bb, 0, 1, bb, 1000);
        return result;
    }

    public int DevGetInfo(ref UInt32 dDevID, ref byte dVerMaj, ref byte dVerMin)
    {
        byte[] bb = {
            (byte)Commands.DEV_GET_INFO,
            0,
            0,
            0,
            0,
            0,
            0
        };
        var Result = SendBINCommand(bb, 0, 1, bb, 1000);
        if (Result == 0)
        {
            dDevID = bb[3];
            dDevID <<= 8;
            dDevID += bb[2];
            dDevID <<= 8;
            dDevID += bb[1];
            dDevID <<= 8;
            dDevID += bb[0];
            dVerMin = bb[4];
            dVerMaj = bb[5];
            SSComm.Log.Info("Device info: ID=" + dDevID.ToString("X8") +" Version: " + dVerMaj + "." + dVerMin);
        }
        return Result;
    }

    public int DevGetOpMode(ref byte dOpM)
    {
        byte[] bb = { (byte)Commands.DEV_GET_OPERATING_MODE };
        var Result = SendBINCommand(bb, 0, 1, bb, 1000);
        if (Result == 0)
        {
            dOpM = bb[0];
            SSComm.Log.Info("Device operating mode: " + bb[0]);
        }
        return Result;
    }

    public void DevReset()
    {
        byte[] bb = { (byte)Commands.DEV_RESET };
        SendBINCommand(bb, 0, 1, null, 0);
        SSComm.Log.Info("Device reset.");
    }

    public int AppGetInfo(ref byte aVerMin, ref byte aVerMaj)
    {
        byte[] bb = {
            (byte)Commands.APP_GET_INFO,
            0
        };
        var Result = SendBINCommand(bb, 0, 1, bb, 1000);
        if (Result == 0)
        {
            aVerMin = bb[0];
            aVerMaj = bb[1];
            SSComm.Log.Info("Device application info: Version: " + aVerMaj + "." + aVerMin);
        }
        return Result;
    }

    public void AppJumpToBootloader()
    {
        byte[] bb = { (byte)Commands.APP_JUMP_TO_BOOTLOADER };
        SendBINCommand(bb, 0, 1, null, 0);
        SSComm.Log.Info("Jump to bootloader.");
    }

    public void AppRestart()
    {
        byte[] bb = { (byte)Commands.APP_RESTART };
        SendBINCommand(bb, 0, 1, null, 0);
        SSComm.Log.Info("Application restart.");
    }

    /// <summary>Returns null if the device did not answer (e.g. in bootloader mode).</summary>
    public PID AppGetPIDParameters()
    {
        byte[] bb = {
            (byte)Commands.APP_GET_PID,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0
        };
        SSComm.Log.Info("Get PID parameters");
        if (SendBINCommand(bb, 0, 1, bb, 1000) != 0)
        {
            SSComm.Log.Warn("Get PID parameters: no answer from device");
            return null;
        }
        var pid = new PID
        {
            Gain = (UInt16)((UInt16)bb[0] + (UInt16)bb[1] * (UInt16)256),
            Offset = (UInt16)((UInt16)bb[2] + (UInt16)bb[3] * (UInt16)256),
            KP = (UInt16)((UInt16)bb[4] + (UInt16)bb[5] * (UInt16)256),
            KI = (UInt16)((UInt16)bb[6] + (UInt16)bb[7] * (UInt16)256),
            DGain = bb[8],
            OVSGain = bb[10]
        };
        SSComm.Log.Info("PID parameters: " + FormatPID(pid));
        return pid;
    }

    private static string FormatPID(PID p)
    {
        return "Gain=" + p.Gain + " Offset=" + p.Offset + " KP=" + p.KP + " KI=" + p.KI + " DGain=" + p.DGain + " OVSGain=" + p.OVSGain;
    }

    public void AppSetPIDParameters(ref PID PID)
    {
        byte[] BB = {
            (byte)Commands.APP_SET_PID,
            (byte)(PID.Gain & 255),
            (byte)(PID.Gain / 256),
            0,
            0,
            (byte)(PID.KP & 255),
            (byte)(PID.KP / 256),
            (byte)(PID.KI & 255),
            (byte)(PID.KI / 256),
            PID.DGain,
            0,
            PID.OVSGain,
            0
        };
        SSComm.Log.Info("Set PID parameters: " + FormatPID(PID));
        SendBINCommand(BB, 0, BB.Length, null, 0);
    }

    // ---- remote control and settings (firmware protocol 1) ----

    public const int SETTINGS_SIZE = 64;
    public const int SETTINGS_CHUNK = 48;

    private static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }

    /// <summary>Sends a command and returns the bytes after the command byte, or null when the station did not answer.</summary>
    private byte[] Request(byte[] cmd, int timeout)
    {
        byte[] r = new byte[64];
        if (SendBINCommand(cmd, 0, cmd.Length, r, timeout) != 0) return null;
        return r;
    }

    /// <summary>null when the firmware does not answer: it has no remote control.</summary>
    public AppInfo GetAppInfo()
    {
        var r = Request(new byte[] { (byte)Commands.APP_GET_INFO }, 1000);
        if (r == null) return null;
        return new AppInfo { Minor = r[0], Major = r[1], Board = r[2], Protocol = r[3] };
    }

    public DeviceState GetState()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_GET_STATE }, 1000);
        if (r == null) return null;
        var s = new DeviceState();
        s.Set = U16(r, 0);
        s.Target = U16(r, 2);
        for (int i = 0; i < 4; i++) s.Presets[i] = U16(r, 4 + 2 * i);
        s.Holder = U16(r, 12);
        s.Sleep = U16(r, 14);
        s.Min = U16(r, 16);
        s.Max = U16(r, 18);
        s.Mode = r[20];
        s.Flags = r[21];
        s.ActIron = r[22];
        s.TempStep = Math.Max(1, (int)r[23]);
        return s;
    }

    /// <summary>All 29 station settings in menu units (protocol 3), or null when the station did not answer.</summary>
    public int[] GetParams()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_GET_PARAMS }, 1000);
        if (r == null) return null;
        var v = new int[29];
        for (int i = 0; i < v.Length; i++) v[i] = U16(r, i * 2);
        return v;
    }

    /// <summary>Sets one setting. Returns { status, value now in the station } (status 0 done, 1 not settable, 2 out of range, 3 station is in its menu), or null.</summary>
    public int[] SetParam(int id, int value)
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_SET_PARAM, (byte)id, (byte)(value & 255), (byte)((value >> 8) & 255) }, 1500);
        if (r == null) return null;
        SSComm.Log.Info("Setting " + id + " = " + value + " -> status " + r[1] + ", now " + U16(r, 2));
        return new[] { (int)r[1], U16(r, 2) };
    }

    /// <summary>The power-up name (up to 10 characters), null when the firmware has no name command (protocol 4).</summary>
    public string GetName()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_GET_NAME }, 1000);
        if (r == null) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append((char)r[i]);
        return sb.ToString().TrimEnd();
    }

    /// <summary>0 done, 2 character not allowed, 3 station is in its menu, -1 no answer.</summary>
    public int SetName(string name)
    {
        var b = new byte[11];
        b[0] = (byte)Commands.REMOTE_SET_NAME;
        string padded = (name ?? "").PadRight(10, ' ').Substring(0, 10);
        for (int i = 0; i < 10; i++) b[i + 1] = (byte)padded[i];
        var r = Request(b, 1500);
        return r == null ? -1 : r[0];
    }

    /// <summary>Sets the temperature in degrees C; the station limits it like the knob. Returns the temperature that was set, or -1.</summary>
    public int SetTemperature(int degC)
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_SET_TEMP, (byte)(degC & 255), (byte)((degC >> 8) & 255) }, 1000);
        if (r == null) return -1;
        SSComm.Log.Info("Set temperature " + degC + " -> " + U16(r, 0));
        return U16(r, 0);
    }

    /// <summary>Selects preset 0..3. Returns the temperature set, or -1.</summary>
    public int SelectPreset(int index)
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_PRESET, (byte)index }, 1000);
        return r == null ? -1 : U16(r, 0);
    }

    /// <summary>Returns 1 while the station sleeps, 0 when awake, -1 when it did not answer.</summary>
    public int Sleep(bool on)
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_SLEEP, (byte)(on ? 1 : 0) }, 1000);
        return r == null ? -1 : (r[0] != 0 ? 1 : 0);
    }

    /// <summary>Two-iron adapter: returns the active iron, or -1.</summary>
    public int SwitchIron()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_SWITCH_IRON }, 1000);
        return r == null ? -1 : r[0];
    }

    /// <summary>Saves the PID values in use for the connected iron in the station. 0 = done, 1 no iron, 2 out of range, 3 memory full, -1 = no answer.</summary>
    public int PidSave()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_PID_SAVE }, 2000);
        return r == null ? -1 : r[0];
    }

    /// <summary>Forgets the saved PID values of the connected iron. 0 = done, 1 no iron, 2 none saved, -1 = no answer.</summary>
    public int PidClear()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_PID_CLEAR }, 2000);
        return r == null ? -1 : r[0];
    }

    /// <summary>1 when the station holds saved PID values for the connected iron, 0 when not, -1 = no answer.</summary>
    public int PidSaved()
    {
        var r = Request(new byte[] { (byte)Commands.REMOTE_PID_SAVED }, 1000);
        return r == null ? -1 : (r[0] != 0 ? 1 : 0);
    }

    /// <summary>The 64 settings bytes of the station (the pincode is left out by the firmware), or null.</summary>
    public byte[] ReadSettings()
    {
        var outb = new byte[SETTINGS_SIZE];
        int addr = 0;
        while (addr < SETTINGS_SIZE)
        {
            int n = Math.Min(SETTINGS_CHUNK, SETTINGS_SIZE - addr);
            var r = Request(new byte[] { (byte)Commands.REMOTE_READ_SETTINGS, (byte)addr, (byte)n }, 1000);
            if (r == null || r[2] != 0 || r[1] != n) return null;
            Array.Copy(r, 3, outb, addr, n);
            addr += n;
        }
        return outb;
    }

    /// <summary>Writes the 64 settings bytes. The station must be restarted afterwards (RestartDevice) to use them.</summary>
    public bool WriteSettings(byte[] data)
    {
        if (data == null || data.Length != SETTINGS_SIZE) return false;
        int addr = 0;
        while (addr < SETTINGS_SIZE)
        {
            int n = Math.Min(SETTINGS_CHUNK, SETTINGS_SIZE - addr);
            var b = new byte[5 + n];
            b[0] = (byte)Commands.REMOTE_WRITE_SETTINGS;
            b[1] = 0x5A;
            b[2] = 0xA5;
            b[3] = (byte)addr;
            b[4] = (byte)n;
            Array.Copy(data, addr, b, 5, n);
            var r = Request(b, 3000);
            if (r == null || r[0] != 0) return false;
            addr += n;
        }
        return true;
    }

    public void RestartDevice()
    {
        SSComm.Log.Info("Restart station");
        SendBINCommand(new byte[] { (byte)Commands.REMOTE_RESTART, 0x5A, 0xA5 }, 0, 3, null, 0);
    }

    public Int32 BlGetInfo(ref byte blVerMaj, ref byte blVerMin)
    {
        byte[] bb = {
            (byte)Commands.BL_GET_INFO,
            0
        };
        var Result = SendBINCommand(bb, 0, 1, bb, 1000);
        if (Result == 0)
        {
            blVerMin = bb[0];
            blVerMaj = bb[1];
            SSComm.Log.Info("Bootloader information: Version=" + blVerMaj + "." + blVerMin);
        }
        return Result;
    }

    //64-byte packet minus 13 header bytes. The bootloader skips its address check for records of
    //512 bytes or more (it could then overwrite itself): records must always stay small.
    public const int MAX_FLASH_RECORD_DATA = 51;

    /// <summary>
    /// Erases the application. blStatus is the bootloader result (0 = OK, 0xFF = bad key,
    /// other = NVM error, -1 = no answer). It is only logged for now, not acted upon.
    /// </summary>
    public Int32 BlEraseFlash(out int blStatus)
    {
        byte[] bb = {
            (byte)Commands.BL_ERASE_FLASH,
            0x34,
            0x12,
            0x21,
            0x43
        };
        byte[] resp = new byte[1];
        SSComm.Log.Info("BlEraseFlash");
        var result = SendBINCommand(bb, 0, 5, resp, 6000);
        blStatus = result == 0 ? resp[0] : -1;
        return result;
    }

    /// <summary>Programs one record. blStatus: see BlEraseFlash.</summary>
    public Int32 BlProgramFlash(UInt32 pfAddr, ref byte[] byteBuff, int bbOffset, int bbCount, out int blStatus)
    {
        byte[] bb = new byte[65];
        blStatus = 0;
        if (bbCount > MAX_FLASH_RECORD_DATA)
        {
            throw new ArgumentOutOfRangeException(nameof(bbCount), bbCount, "Flash record larger than " + MAX_FLASH_RECORD_DATA + " bytes, not sent to the bootloader");
        }
        if (bbCount > 0)
        {
            bb[0] = (byte)Commands.BL_PROGRAM_FLASH;
            bb[1] = 0x34;
            bb[2] = 0x12;
            bb[3] = 0x21;
            bb[4] = 0x43;
            bb[5] = (byte)(pfAddr & 0xffL);
            bb[6] = (byte)((pfAddr >> 8) & 0xffL);
            bb[7] = (byte)((pfAddr >> 16) & 0xffL);
            bb[8] = (byte)((pfAddr >> 24) & 0xffL);
            bb[9] = (byte)(bbCount & 0xffL);
            bb[10] = (byte)((bbCount >> 8) & 0xffL);
            bb[11] = (byte)((bbCount >> 16) & 0xffL);
            bb[12] = (byte)((bbCount >> 24) & 0xffL);
            for (int i = 0; i <= bbCount - 1; i++)
            {
                bb[13 + i] = byteBuff[bbOffset + i];
            }
            byte[] resp = new byte[1];
            var result = SendBINCommand(bb, 0, 13 + bbCount, resp, 1000);
            blStatus = result == 0 ? resp[0] : -1;
            return result;
        }
        else
        {
            return 0;
        }
    }

    public Int32 BlProgramComplete()
    {
        byte[] bb = {
            (byte)Commands.BL_PROGRAM_COMPLETE,
            0x34,
            0x12,
            0x21,
            0x43
        };
        SSComm.Log.Info("BlProgramComplete");
        return SendBINCommand(bb, 0, 5, null, 1000);
    }

    public Int32 BlReadCRC(UInt32 pfAddr, UInt32 pfCount, ref UInt16 pfCRC)
    {
        if (pfCount > 0)
        {
            byte[] bb = {
                (byte)Commands.BL_READ_CRC,
                (byte)(pfAddr & 0xffL),
                (byte)((pfAddr >> 8) & 0xffL),
                (byte)((pfAddr >> 16) & 0xffL),
                (byte)((pfAddr >> 24) & 0xffL),
                (byte)(pfCount & 0xffL),
                (byte)((pfCount >> 8) & 0xffL),
                (byte)((pfCount >> 16) & 0xffL),
                (byte)((pfCount >> 24) & 0xffL)
            };
            var Result = SendBINCommand(bb, 0, 9, bb, 5000);
            if (Result == 0)
            {
                pfCRC = bb[1];
                pfCRC <<= 8;
                pfCRC += bb[0];
            }
            return Result;
        }
        return 0;
    }

    public void BlJumpToApplication()
    {
        byte[] bb = { (byte)Commands.BL_JUMP_TO_APP };
        SendBINCommand(bb, 0, 1, null, 0);
    }

    private int SendBINCommand(byte[] OutBuffer, int OutOffset, int OutCount, byte[] RespBuffer = null, int TimeOut = 5000)
    {
        int rv = 0;
        byte[] OB = new byte[64];
        if (OutCount > 0)
        {
            lock (TOTimer)
            {
                Array.Copy(OutBuffer, OB, OutCount);
                //flash records are too many to trace one by one, only their failures are logged
                bool trace = OutBuffer[0] != (byte)Commands.BL_PROGRAM_FLASH;
                if (trace) SSComm.Log.Info("TX " + CommandName(OutBuffer[0]) + ": " + SSComm.Log.Hex(OB, 0, Math.Min(OutCount, 16)));
                if (!Transport.Connected) SSComm.Log.Warn("TX " + CommandName(OutBuffer[0]) + " while not connected");
                //clear feedback before sending, so a fast reply is not lost
                CommandFeedBack.Received.Reset();
                CommandFeedBack.IsEmpty = true;
                Transport.Write(ref OB, 0, 64);
                //wait for feedback
                if (TimeOut > 0)
                {
                    rv = -1;
                    TOTimer.Restart();
                    long remaining;
                    while ((remaining = TimeOut - TOTimer.ElapsedMilliseconds) > 0)
                    {
                        if (!CommandFeedBack.IsEmpty)
                        {
                            if (OutBuffer[0] == CommandFeedBack.Command)
                            {
                                rv = CommandFeedBack.Status;
                                if (RespBuffer != null)
                                {
                                    Array.Copy(CommandFeedBack.Data, RespBuffer, Math.Min(RespBuffer.Length, (int)CommandFeedBack.DataLength));
                                }
                                break;
                            }
                            else
                            {
                                SSComm.Log.Warn("RX unexpected response " + CommandName(CommandFeedBack.Command) + " while waiting for " + CommandName(OutBuffer[0]));
                                CommandFeedBack.IsEmpty = true;
                            }
                        }
                        CommandFeedBack.Received.Wait((int)remaining);
                        CommandFeedBack.Received.Reset();
                    }
                    TOTimer.Stop();
                    if (rv == -1)
                    {
                        SSComm.Log.Warn("Timeout (" + TimeOut + " ms) waiting for " + CommandName(OutBuffer[0]) + (trace ? "" : " at " + SSComm.Log.Hex(OB, 5, 4)));
                    }
                    else if (trace)
                    {
                        SSComm.Log.Info("RX " + CommandName(OutBuffer[0]) + " in " + TOTimer.ElapsedMilliseconds + " ms: " + SSComm.Log.Hex(CommandFeedBack.Data, 0, Math.Min((int)CommandFeedBack.DataLength, 16)));
                    }
                }
            }
        }
        return rv;
    }

    private static string CommandName(byte cmd)
    {
        return Enum.IsDefined(typeof(Commands), cmd) ? ((Commands)cmd).ToString() : "0x" + cmd.ToString("X2");
    }


    private byte[] RXFBuff = new byte[65];
    private void Transport_DataReceived(object sender, EventArgs e)
    {
        int rlen;
        byte[] RXB = new byte[65];
        while (Transport.RXDataCount >= 64)
        {
            rlen = Transport.Read(ref RXB, 0, 64);
            ProcessRXMessage(ref RXB);
        }
    }

    private bool ProcessRXMessage(ref byte[] RXB)
    {
        switch (RXB[0])
        {
            case 1:
                SSComm.Log.Info("RX instrument change, iron ID=0x" + (RXB[1] | (RXB[2] << 8)).ToString("X4"));
                IronId = RXB[1] | (RXB[2] << 8);
                InstrumentChange?.Invoke(this, new EventArgs());
                break;
            case 3:
                //copy: RXB is reused for the next packet while the UI thread processes this one
                LiveDataReceived?.Invoke(this, new LiveDataReceivedEventData() { Data = (byte[])RXB.Clone() });
                break;
            default:
                CommandFeedBack.ReadFromBuffer(ref RXB, 0, 64);
                break;
        }
        return true;
    }

    private UInt16 CalculateCRC(ref byte[] data, int boff, int blen)
    {
        UInt16[] crc_table = {
            0x0,
            0x1021,
            0x2042,
            0x3063,
            0x4084,
            0x50a5,
            0x60c6,
            0x70e7,
            0x8108,
            0x9129,
            0xa14a,
            0xb16b,
            0xc18c,
            0xd1ad,
            0xe1ce,
            0xf1ef
        };
        UInt32 i;
        UInt16 crc = 0;

        while (blen > 0)
        {
            i = (UInt32)((crc >> 12) ^ (data[boff] >> 4));
            crc = (UInt16)(crc_table[i & 15] ^ (crc << 4));
            i = (UInt32)((crc >> 12) ^ (UInt32)data[boff]);
            crc = (UInt16)(crc_table[i & 15] ^ (crc << 4));
            boff++;
            blen--;
        }
        return crc;
    }
}

