using System;

namespace UniSolder
{
    /// <summary>
    /// One live data packet (type 3) of the station, decoded the same way as the Android app (DeviceLink.Sample).
    /// </summary>
    public class LiveSample
    {
        public float Set;           // temperature the heater regulates to, degrees C
        public float Temp;          // actual temperature, degrees C
        public float Adc;           // raw sensor temperature
        public float Avg;           // filtered temperature
        public float Duty;          // PID duty (0-512)
        public bool HeaterOn;
        public bool Reached;
        public float Resistance;    // heater resistance in ohms (0 = not measured)
        public float PidInput;      // temperature the PID loop reacts to: smoothed temperature plus the derivative term
        public float Ws0;           // first wave-shaping value (correction after the heater switched off), 0 = none

        /// <summary>Heater power in percent of full power.</summary>
        public float PowerPct => Math.Max(0f, Math.Min(100f, Duty * 100f / 512f));

        public static LiveSample Parse(byte[] b)
        {
            if (b == null || b.Length < 34 || b[0] != 3) return null;
            return new LiveSample
            {
                Set = b[3] * 2f,
                Temp = b[4] * 0.5f + b[5] * 128f,
                Adc = b[6] * 0.5f + b[7] * 128f,
                Avg = b[8] * 0.5f + b[9] * 128f,
                Duty = (b[32] + b[33] * 256) / 128f,
                HeaterOn = b[14] != 0,
                Reached = b[31] != 0,
                Resistance = (short)(b[10] | (b[11] << 8)) / 10f,
                PidInput = b[12] * 0.5f + b[13] * 128f,
                Ws0 = (b[15] | (b[16] << 8)) - 2048f
            };
        }
    }
}
