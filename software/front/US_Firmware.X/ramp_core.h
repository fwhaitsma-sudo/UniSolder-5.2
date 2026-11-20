/*
 * Ramp mode: the station runs a heating ramp by itself (idle, ramp, peak dwell, cool down, lockout).
 * Design: software/front/RAMP_MODE_DESIGN.md.
 *
 * This file is included by menu.c (after RemoteTouch) and is not compiled on its own: it uses the menu's static
 * state, and the MPLAB makefiles then need no new source file. C89, integer math only.
 *
 * Outside ramp mode (RampSt == RS_OFF) nothing in here runs and the station behaves as before.
 * The only link to the control loop is the target temperature: MenuTasks replaces its target (CTTemp) by RampTick().
 * The tick is the 20 ms MenuTasks tick (50 per second).
 */

#define RAMP_EEP_ADDR   384     //EEPROM: marker, then RAMP_NPAR x u16 (little endian); free area 384-511
#define RAMP_EEP_MARK   0xD5    //13 values; 0xD4 = 9 value format, 0xD3 = 8 value format (both read as well, missing values get their default)
#define RAMP_EEP_MARK9  0xD4
#define RAMP_EEP_MARK8  0xD3
#define RAMP_NPAR       13

enum { RS_OFF = 0, RS_IDLE, RS_RAMP, RS_PEAK, RS_COOL, RS_LOCK };

//parameters: idle C, peak C, ramp rate C/s, dwell 0.1 s, lockout s (from the end of the dwell), idle time-out min, contact duty jump % (step 2),
//contact dip 0.5 C (step 2), ready limit C (a new ramp can start when the tip is at most this hot, the ramp then starts from the tip temperature),
//contact cooling constant 0.001/s, blank after lift s, wipe ripple % (0 = no wipe check), auto start (0 off, 1 cooling detector, 2 cooling + duty detector)
static UINT16 RampP[RAMP_NPAR];
static const UINT16 RampDef[RAMP_NPAR] = {165, 275, 45, 11, 12, 10,  5, 3, 230, 16,  3,  30, 1};
static const UINT16 RampMin[RAMP_NPAR] = {100, 110,   5,   0,  0,  1,  3,  2, 110,  5,  0,   0, 0};
static const UINT16 RampMax[RAMP_NPAR] = {340, 350, 100, 100, 60, 60, 40, 20, 340, 50, 10, 200, 2};

static UINT8 RampSt;            //RS_OFF = classic station
static UINT8 RampLoaded;
static UINT8 RampPressed;       //manual trigger (knob press or app) waiting for the next tick
static UINT8 RampAbortReq;      //abort request from the app
static int RampFrom;            //temperature the ramp started from, whole degrees C
static int RampN;               //ticks in the current state
static int RampIdle;            //ticks without activity in RS_IDLE (idle time-out)
static int RampOk;              //ticks the tip has been at the idle temperature
static int RampPeakSeen;        //highest temperature seen since the last trigger, whole degrees C
static int RampActualNow;       //actual temperature at the last tick, whole degrees C

static int RampValid(const UINT16 * v){
    int i;
    for(i = 0; i < RAMP_NPAR; i++){
        if(v[i] < RampMin[i] || v[i] > RampMax[i]) return 0;
    }
    if(v[1] < v[0] + 10) return 0;      //peak at least 10 C above idle
    if(v[8] < v[0] + 10 || v[8] > v[1] - 10) return 0;  //ready limit between idle + 10 and peak - 10
    return 1;
}

static void RampLoad(void){
    UINT8 b[RAMP_NPAR * 2];
    UINT16 v[RAMP_NPAR];
    int i, n;
    if(RampLoaded) return;
    RampLoaded = 1;
    for(i = 0; i < RAMP_NPAR; i++) RampP[i] = RampDef[i];
    n = EEPRead(RAMP_EEP_ADDR, 0, 1);
    if(n != RAMP_EEP_MARK && n != RAMP_EEP_MARK9 && n != RAMP_EEP_MARK8) return;
    n = (n == RAMP_EEP_MARK) ? RAMP_NPAR : ((n == RAMP_EEP_MARK9) ? 9 : 8);
    EEPRead(RAMP_EEP_ADDR + 1, b, n * 2);
    for(i = 0; i < RAMP_NPAR; i++) v[i] = RampDef[i];
    for(i = 0; i < n; i++) v[i] = (UINT16)(b[i * 2] | (b[i * 2 + 1] << 8));
    if(n < RAMP_NPAR && v[6] == 12 && v[7] == 5){   //old format with the old contact defaults (never used): take the new defaults
        v[6] = RampDef[6];
        v[7] = RampDef[7];
    }
    if(RampValid(v)){
        for(i = 0; i < RAMP_NPAR; i++) RampP[i] = v[i];
    }
}

static void RampSave(void){
    int i;
    if(EEPRead(RAMP_EEP_ADDR, 0, 1) != RAMP_EEP_MARK) EEPWriteImm(RAMP_EEP_ADDR, RAMP_EEP_MARK);
    for(i = 0; i < RAMP_NPAR; i++){
        UINT8 lo = (UINT8)(RampP[i] & 0xFF);
        UINT8 hi = (UINT8)(RampP[i] >> 8);
        if(EEPRead(RAMP_EEP_ADDR + 1 + i * 2, 0, 1) != lo) EEPWriteImm(RAMP_EEP_ADDR + 1 + i * 2, lo);
        if(EEPRead(RAMP_EEP_ADDR + 2 + i * 2, 0, 1) != hi) EEPWriteImm(RAMP_EEP_ADDR + 2 + i * 2, hi);
    }
}


//---- contact detection (step 2, design note 2.4) and the 20 ms capture buffer ----
//Detector A (heater off, tip above its target): k = cooling rate / (T - T_room) in 0.001/s, contact when k >= RampP[9].
//Detector B (heater regulating at idle, only with auto start 2): duty jump >= RampP[6] % or dip >= RampP[7] x 0.5 C against
//the settled baseline.
//Contact must last 25 ticks (0.5 s). It is ignored for RampP[10] s after the iron leaves its stand (brass wool wipe).
//Wipe check (A only, RampP[11] > 0): the cooling rate in 0.1 s steps over the last second. A wipe is a series of strokes, so the
//rate jumps up and down: mean deviation / mean rate >= RampP[11] % is taken as a wipe and ignored until there has been no
//contact for 0.5 s. The thresholds are first guesses: the capture buffer is there to set them from real data.
#define CD_N        50      //1 s of ticks
#define CD_CONFIRM  25      //0.5 s
#define CD_HEATOFF  250     //5 s: right after the heater stops the sensor cools fast as the heat spreads into the tip (test 3: k 0.036 after 1 s,
                            //0.025 after 2 s, 0.020 after 3 s, 0.015 after 3.2 s), which looks like contact
#define CAP_N       500     //10 s of ticks, 8 bytes each
#define CAP_POST    100     //ticks recorded after the event (2 s)

static int CdT16[CD_N];         //DispTemp (1/16 C, RC filtered)
static INT16 CdRaw[CD_N];       //PIDVars[0].CTemp[0] (0.5 C, unfiltered)
static int CdPos, CdFill;
static int CdHeatOff;           //ticks with the heater demand at 0
static int CdLift;              //ticks since the iron left its stand
static int CdCont;              //ticks in a row with contact
static int CdQuiet;             //ticks in a row without contact
static int CdSettled;           //ticks the tip has been regulated at idle (detector B)
static INT32 CdBase;            //duty baseline, 0.1 % x 16
static INT32 CdDAvg;            //short duty average, 0.1 % x 16
static UINT8 CdWipeHold;
static int CdK;                 //last k, 0.001/s
static int CdRip;               //last wipe ripple, %
static INT32 CdRipSum;
static INT32 CdNoise = -1;      //variance of the 0.1 s rates while cooling freely (noise floor), (C/s)^2 x 64; -1 = not known yet
static INT32 CdDev;             //variance now, (C/s)^2 x 64          //sum of the ripple values since the contact began (the decision uses the mean)
static UINT8 CdFlags;           //bit 0 A armed, 1 B armed, 2 contact now, 3 blank after lift, 4 wipe hold
static UINT8 CdLast;            //last automatic event: 0 none, 1 start by A, 2 start by B, 3 wipe ignored
static UINT16 CdStarts, CdWipes;
static int CdRoom2 = 50;        //room (cold junction) temperature, 0.5 C

static UINT8 Cap[CAP_N][8];
static int CapPos;              //next record
static int CapFill;
static UINT8 CapState;          //0 recording, 1 recording the last 2 s after an event, 2 frozen
static int CapPost;
static UINT8 CapReason;         //1 manual start, 2 automatic start, 3 wipe ignored, 4 frozen by the app
static int CapSince;            //records after the event

static void CdReset(void){
    CdPos = 0; CdFill = 0; CdHeatOff = 0; CdCont = 0; CdQuiet = 0; CdSettled = 0;
    CdBase = 0; CdDAvg = 0; CdWipeHold = 0; CdK = 0; CdRip = 0; CdFlags = 0;
}

static void CapEvent(UINT8 reason){
    if(CapState) return;
    CapState = 1;
    CapPost = CAP_POST;
    CapReason = reason;
    CapSince = 0;
}

static void CapArm(void){
    CapState = 0;
    CapPos = 0;
    CapFill = 0;
    CapReason = 0;
    CapSince = 0;
}

static int RampISqrt(INT32 v){                  //integer square root (bit by bit, 16 steps)
    UINT32 r = 0, bit = 1UL << 30, x = (v > 0) ? (UINT32)v : 0;
    while(bit > x) bit >>= 2;
    while(bit){
        if(x >= r + bit){ x -= r + bit; r = (r >> 1) + bit; }
        else r >>= 1;
        bit >>= 2;
    }
    return (int)r;
}

static int RampDutyPm(void){                    //heater demand in 0.1 % (0x00FFFFFF = 100 %)
    UINT32 d = PIDVars[0].PIDDutyFull;
    return (int)((((d >> 8) & 0xFFFF) * 1000UL) >> 16);
}

static void CapRecord(int raw, int t16, int dutyPm){
    UINT8 * r;
    int v;
    if(CapState == 2) return;
    r = Cap[CapPos];
    r[0] = (UINT8)(raw & 0xFF); r[1] = (UINT8)((raw >> 8) & 0xFF);
    r[2] = (UINT8)(t16 & 0xFF); r[3] = (UINT8)((t16 >> 8) & 0xFF);
    v = (dutyPm * 255) / 1000; if(v > 255) v = 255; if(v < 0) v = 0;
    r[4] = (UINT8)v;
    v = CdK; if(v > 255) v = 255; if(v < 0) v = 0;
    r[5] = (UINT8)v;
    v = CdRip; if(v > 255) v = 255; if(v < 0) v = 0;
    r[6] = (UINT8)v;
    r[7] = (UINT8)((RampSt & 7) | ((CdFlags & 31) << 3));
    CapPos++; if(CapPos >= CAP_N) CapPos = 0;
    if(CapFill < CAP_N) CapFill++;
    if(CapState == 1){
        CapSince++;
        if(--CapPost <= 0) CapState = 2;
    }
}

//One tick of the detectors. Returns 1 when a ramp may start now (contact confirmed, not a wipe, station ready).
static int RampDetect(int inStand, int ready){
    int raw = PIDVars[0].CTemp[0];
    int t16 = DispTemp;
    UINT32 duty = PIDVars[0].PIDDutyFull;
    int dutyPm = RampDutyPm();
    int idle16 = (int)RampP[0] << 4;
    int i, j, a, b, condA = 0, condB = 0, contact, room16, above;
    INT32 sOld, sNew, m, dev, S[10];
    int start = 0, need;

    CdRoom2 = (CJTemp > -100) ? CJTemp : CRTemp;
    if(CdRoom2 < 0 || CdRoom2 > 120) CdRoom2 = 50;      //no usable room sensor: 25 C
    room16 = CdRoom2 << 3;

    CdT16[CdPos] = t16;
    CdRaw[CdPos] = (INT16)raw;
    CdPos++; if(CdPos >= CD_N) CdPos = 0;
    if(CdFill < CD_N) CdFill++;

    if(duty == 0){ if(CdHeatOff < 30000) CdHeatOff++; } else CdHeatOff = 0;
    if(inStand) CdLift = 0; else if(CdLift < 30000) CdLift++;

    CdK = 0;
    CdRip = 0;
    if(CdFill >= CD_N){
        //rate from the oldest and the newest half second (CdPos is the oldest sample now)
        sOld = 0; sNew = 0;
        for(i = 0; i < CD_N; i++){
            a = CdT16[(CdPos + i) % CD_N];
            if(i < CD_N / 2) sOld += a; else sNew += a;
        }
        m = ((sOld - sNew) * 2) / (CD_N / 2);       //1/16 C per s
        above = t16 - room16;
        if(m > 0 && above > (20 << 4)){
            CdK = (int)((m * 1000) / above);
            if(CdK > 999) CdK = 999;
        }
        //wipe ripple: 10 sums of 5 raw samples; the difference of two neighbouring sums is the rate in C/s (0.5 C x 5 per 0.1 s)
        for(j = 0; j < 10; j++){
            S[j] = 0;
            for(i = 0; i < 5; i++) S[j] += CdRaw[(CdPos + j * 5 + i) % CD_N];
        }
        //ripple = standard deviation of the 0.1 s rates above the noise floor (variance measured while the tip cooled freely),
        //relative to the mean rate. Noise and strokes add as variances, so the floor is subtracted as a variance.
        m = S[1] - S[9];                            //8 steps (the first 0.1 s is left out: it may be before the contact)
        dev = 0;
        for(j = 2; j < 10; j++){
            b = (int)(8 * (S[j - 1] - S[j]) - m);   //8 x deviation, C/s
            dev += (INT32)b * b;
        }
        CdDev = dev / 8;                            //variance x 64
        if(m > 0){
            dev = CdDev - ((CdNoise < 0) ? 0 : CdNoise);
            if(dev < 0) dev = 0;
            //ripple % = 100 x sqrt(variance) / mean rate, mean rate = m / 8, sqrt(dev) = 8 x sd
            b = RampISqrt(dev);
            CdRip = (int)(((INT32)b * 100) / m);
            if(CdRip > 999) CdRip = 999;
        }
        else CdRip = 999;
    }

    //detector B: duty against the baseline while the tip is regulated at idle
    a = t16 - idle16;
    if(duty == 0 || a > (5 << 4) || a < -(5 << 4)) CdSettled = 0;
    else if(CdSettled < 30000 && (CdSettled > 0 || (a <= 24 && a >= -24))) CdSettled++;
    if(CdSettled == 1){ CdBase = (INT32)dutyPm << 4; CdDAvg = CdBase; }
    CdDAvg += (((INT32)dutyPm << 4) - CdDAvg) >> 3;     //short average (about 0.16 s)

    CdFlags = 0;
    if(!inStand && CdLift < (int)RampP[10] * 50){
        CdFlags |= 8;
        //contact during the blank time is the wipe: it must end (0.5 s without contact) before a new contact counts
        if(RampP[12] >= 1 && CdHeatOff >= CD_HEATOFF && CdFill >= CD_N && CdK >= (int)RampP[9]){
            CdWipeHold = 1;
            CdQuiet = 0;
        }
    }
    if(RampP[12] >= 1 && !inStand && !(CdFlags & 8) && CdHeatOff >= CD_HEATOFF && CdFill >= CD_N){
        CdFlags |= 1;
        condA = (CdK >= (int)RampP[9] - ((CdCont > 0) ? 3 : 0));      //0.003/s hysteresis once a contact has begun
    }
    if(RampP[12] >= 2 && !inStand && !(CdFlags & 8) && CdSettled >= 75){
        CdFlags |= 2;
        condB = ((CdDAvg - CdBase) >= (((INT32)RampP[6] * 10) << 4)) || ((idle16 - t16) >= (int)RampP[7] * 8);
    }
    if(CdSettled >= 75 && !condB && a <= 24 && a >= -24){
        CdBase += (((INT32)dutyPm << 4) - CdBase) >> 5;     //slow baseline, only while there is no contact
    }
    contact = condA || condB;
    if((CdFlags & 1) && !condA && CdQuiet >= CD_N){     //free cooling for at least 1 s: learn the noise floor
        if(CdNoise < 0) CdNoise = CdDev; else CdNoise += (CdDev - CdNoise) >> 5;
    }
    if(contact){
        CdFlags |= 4;
        if(CdCont == 0) CdRipSum = 0;
        if(CdCont < 30000) CdCont++;
        if(CdCont > CD_CONFIRM) CdRipSum += CdRip;     //second half second: the 1 s window is then (almost) all contact
        CdQuiet = 0;
    }
    else{
        CdCont = 0;
        if(CdQuiet < 30000 && !(CdFlags & 8 && CdWipeHold && CdK >= (int)RampP[9])) CdQuiet++;
    }
    if(CdWipeHold && CdQuiet >= CD_CONFIRM) CdWipeHold = 0;
    need = (condA && RampP[11]) ? 2 * CD_CONFIRM : CD_CONFIRM;     //with the wipe check the decision takes 1 s of contact
    if(CdCont == need && need > CD_CONFIRM && !CdWipeHold && CdRipSum / CD_CONFIRM >= (int)RampP[11]){
        CdWipeHold = 1;
        CdWipes++;
        CdLast = 3;
        CapEvent(3);
    }
    if(CdWipeHold) CdFlags |= 16;
    if(CdCont >= need && !CdWipeHold && ready){
        CdStarts++;
        CdLast = condA ? 1 : 2;
        CdCont = 0;
        start = 1;
    }
    CapRecord(raw, t16, dutyPm);
    return start;
}

static void RampLeaveMode(void){
    RampSt = RS_OFF;
    RampPressed = 0;
    RampAbortReq = 0;
    RemoteTouch();      //the classic sleep timers start again from now
}

//Returns 0 done, 1 not possible here (two-iron adapter, no instrument, sleeping), 3 station is in its menu.
static int RampEnter(void){
    if(RampSt) return 0;
    if(IronPairActive() || IronPars.Config[0].SensorConfig.Type == SENSOR_NONE) return 1;
    if(CMode == STANDBY || ManualSleep) return 1;
    if(!(CMode == DEFAULT_MENU || CMode == SET_TEMPERATURE || CMode == RESET_TEMPERATURE || CMode == MENU)) return 3;
    RampLoad();
    RampSt = RS_IDLE;
    RampN = 0;
    RampIdle = 0;
    RampOk = 0;
    RampPressed = 0;
    RampAbortReq = 0;
    RampPeakSeen = 0;
    CdReset();
    CdLift = 0;
    CMode = DEFAULT_MENU;
    BeepTicks = 2;
    return 0;
}

//End of the dwell or an abort: the lockout starts at once (the heater target is the idle temperature, the tip cools by itself).
static void RampGoCool(void){
    RampSt = RS_LOCK;
    RampN = 0;
}

//Start a ramp from the tip temperature (when it is above idle).
static void RampStart(void){
    int c = (PIDVars[0].CTemp[0] + 1) >> 1;
    RampFrom = (c > (int)RampP[0]) ? c : (int)RampP[0];
    if(RampFrom > (int)RampP[1] - 10) RampFrom = (int)RampP[1] - 10;
    RampSt = RS_RAMP;
    RampN = 0;
    RampPeakSeen = 0;
    CdCont = 0;
    BeepTicks = 2;
}

//One 20 ms tick. actual = measured temperature (whole C), inStand = iron in its stand, fault = heater/sensor fault or tip change,
//menuOpen = the station menu (or another screen) is open, activity = knob or button touched.
//Returns the target temperature (whole C). When it leaves the mode RampSt is RS_OFF and the caller keeps its own target.
static int RampTick(int actual, int inStand, int fault, int menuOpen, int activity){
    int tgt = RampP[0];
    int nr, c;
    RampActualNow = actual;

    if(fault){
        RampLeaveMode();
        BeepTicks = 12;
        return 0;
    }
    if(RampSt == RS_RAMP || RampSt == RS_PEAK){
        c = (PIDVars[0].CTemp[0] + 1) >> 1;
        if(c > RampPeakSeen) RampPeakSeen = c;
    }
    if(RampAbortReq){
        RampAbortReq = 0;
        if(RampSt == RS_RAMP || RampSt == RS_PEAK){
            RampGoCool();
            BeepTicks = 6;
        }
    }
    if(RampPressed){
        RampPressed = 0;
        if(RampSt == RS_RAMP || RampSt == RS_PEAK){          //a press while heating aborts
            RampGoCool();
            BeepTicks = 6;
        }
        else if(RampSt == RS_IDLE && RampOk >= 25 && !inStand && !menuOpen){
            RampStart();
            CapEvent(1);
        }
        else{
            BeepTicks = 6;                                   //not ready, in the stand or cooling
        }
    }

    //contact detection runs in IDLE and LOCKOUT (a contact may begin before the station is ready; it starts the ramp once it is)
    if(RampSt == RS_IDLE || RampSt == RS_LOCK){
        if(RampDetect(inStand, (RampSt == RS_IDLE && RampOk >= 25 && !menuOpen) ? 1 : 0)){
            RampStart();
            RampIdle = 0;
            CapEvent(2);
            return RampFrom;        //the first ramp step follows on the next tick
        }
    }
    else CapRecord(PIDVars[0].CTemp[0], DispTemp, RampDutyPm());

    switch(RampSt){
        case RS_IDLE:
            tgt = RampP[0];
            if(activity) RampIdle = 0; else RampIdle++;
            if(RampIdle >= (int)RampP[5] * 3000){            //idle time-out: back to the classic station
                RampLeaveMode();
                BeepTicks = 12;
                return 0;
            }
            //ready: not more than 5 C below idle and not hotter than the ready limit (the tip cools slowly by itself)
            c = actual - (int)RampP[0];
            if(c >= -5 && actual <= (int)RampP[8]){ if(RampOk < 1000) RampOk++; } else RampOk = 0;
            break;

        case RS_RAMP:
            if(inStand || menuOpen){                         //iron put back or menu opened: abort
                RampGoCool();
                BeepTicks = 6;
                tgt = RampP[0];
                break;
            }
            RampN++;
            nr = (((int)RampP[1] - RampFrom) * 50) / (int)RampP[2];          //ticks the ramp takes
            if(RampN < nr) tgt = RampFrom + (RampN * (int)RampP[2]) / 50;
            else tgt = RampP[1];
            //the dwell starts when the target has reached the peak and the tip (unfiltered reading) is within 5 C of it,
            //or at the latest 1.5 s after the ramp should have ended
            c = (PIDVars[0].CTemp[0] + 1) >> 1;
            if(RampN >= nr && (c >= (int)RampP[1] - 5 || RampN >= nr + 75)){
                RampSt = RS_PEAK;
                RampN = 0;
            }
            break;

        case RS_PEAK:
            tgt = RampP[1];
            if(inStand || menuOpen){
                RampGoCool();
                BeepTicks = 6;
                tgt = RampP[0];
                break;
            }
            RampN++;
            if(RampN >= (int)RampP[3] * 5){
                RampGoCool();
                BeepTicks = 2;
            }
            break;

        case RS_COOL:                                        //not used any more (the lockout follows the dwell at once)
            RampSt = RS_LOCK;
            RampN = 0;
            tgt = RampP[0];
            break;

        case RS_LOCK:
            tgt = RampP[0];
            RampN++;
            if(RampN >= (int)RampP[4] * 50){                 //the capacitor body has had time to cool: ready again
                RampSt = RS_IDLE;
                RampN = 0;
                RampIdle = 0;
                RampOk = 0;
                BeepTicks = 2;
            }
            break;

        default:
            RampSt = RS_OFF;
            break;
    }
    return tgt;
}

//Text for the first display line: 21 characters.
static void RampHeader(char * o){
    const char * s = "";
    int i, k = 0;
    char num[3];
    switch(RampSt){
        case RS_IDLE: s = CdWipeHold ? "WIPE IGNORED" : ((RampOk >= 25) ? (RampP[12] ? "READY AUTO" : "READY") : ((RampActualNow > (int)RampP[8]) ? "COOLING" : "HEATING")); break;
        case RS_RAMP: s = "RAMPING"; break;
        case RS_PEAK: s = "PEAK"; break;
        case RS_COOL: s = "COOLING"; break;
        case RS_LOCK: s = "LOCKOUT"; break;
        default: break;
    }
    o[k++] = 'R'; o[k++] = 'A'; o[k++] = 'M'; o[k++] = 'P'; o[k++] = ' '; o[k++] = ' ';
    for(i = 0; s[i] && k < 21; i++) o[k++] = s[i];
    if(RampSt == RS_LOCK){
        int left = (int)RampP[4] - RampN / 50;
        if(left < 0) left = 0;
        if(left > 99) left = 99;
        num[0] = (char)('0' + left / 10);
        num[1] = (char)('0' + left % 10);
        num[2] = 0;
        o[k++] = ' ';
        if(num[0] != '0') o[k++] = num[0];
        o[k++] = num[1];
        o[k++] = 's';
    }
    while(k < 21) o[k++] = ' ';
    o[21] = 0;
}

//---- for the phone / PC (io.c, through menu.h) ----
void RemoteRampGet(unsigned int * v){
    int i;
    RampLoad();
    for(i = 0; i < RAMP_NPAR; i++) v[i] = RampP[i];
}

//0 done, 2 out of range, 3 a ramp is running
int RemoteRampSet(const unsigned int * v){
    UINT16 n[RAMP_NPAR];
    int i;
    RampLoad();
    if(RampSt == RS_RAMP || RampSt == RS_PEAK) return 3;
    for(i = 0; i < RAMP_NPAR; i++){
        if(v[i] > 65535u) return 2;
        n[i] = (UINT16)v[i];
    }
    if(!v[9] && !v[10] && !v[11] && !v[12]){       //an app that knows only 9 values (protocol 7): keep the contact values
        for(i = 9; i < RAMP_NPAR; i++) n[i] = RampP[i];
    }
    if(!RampValid(n)) return 2;
    for(i = 0; i < RAMP_NPAR; i++) RampP[i] = n[i];
    RampSave();
    return 0;
}

//cmd 0 leave, 1 enter, 2 trigger, 3 abort. Returns 0 done, 1 not possible, 3 in menu; the mode is in RemoteRampStatus.
int RemoteRampControl(int cmd){
    int st = 0;
    switch(cmd){
        case 0: if(RampSt) RampLeaveMode(); break;
        case 1: st = RampEnter(); break;
        case 2: if(RampSt) RampPressed = 1; else st = 1; break;
        case 3: if(RampSt) RampAbortReq = 1; else st = 1; break;
        default: st = 1; break;
    }
    if(!st) RemoteTouch();
    return st;
}

//o[0] state, o[1] ms left in the state (nominal), o[2] target now, o[3] highest temperature since the trigger, o[4] ready (1/0), o[5] actual temperature
void RemoteRampStatus(unsigned int * o){
    long ms = 0;
    int nr;
    RampLoad();
    switch(RampSt){
        case RS_RAMP:
            nr = (((int)RampP[1] - RampFrom) * 50) / (int)RampP[2];
            ms = (long)(nr - RampN) * 20;
            break;
        case RS_PEAK: ms = (long)((int)RampP[3] * 5 - RampN) * 20; break;
        case RS_COOL: ms = (long)(500 - RampN) * 20; break;
        case RS_LOCK: ms = (long)((int)RampP[4] * 50 - RampN) * 20; break;
        default: break;
    }
    if(ms < 0) ms = 0;
    if(ms > 65535L) ms = 65535L;
    o[0] = RampSt;
    o[1] = (unsigned int)ms;
    o[2] = (unsigned int)CTTemp;
    o[3] = (unsigned int)RampPeakSeen;
    o[4] = (RampSt == RS_IDLE && RampOk >= 25) ? 1 : 0;
    o[5] = (unsigned int)RampActualNow;
}

//contact detector: o[0] k 0.001/s, o[1] wipe ripple %, o[2] flags, o[3] last automatic event, o[4] automatic starts, o[5] wipes ignored,
//o[6] room temperature 0.5 C
void RemoteRampDetect(unsigned int * o){
    o[0] = (unsigned int)CdK;
    o[1] = (unsigned int)CdRip;
    o[2] = CdFlags;
    o[3] = CdLast;
    o[4] = CdStarts;
    o[5] = CdWipes;
    o[6] = (unsigned int)CdRoom2;
}

//capture: cmd 0 status, 1 clear and record again, 2 freeze now.
//o[0] state (0 recording, 1 event seen, 2 frozen), o[1] reason, o[2] records, o[3] index of the event record (0 = oldest), o[4] room 0.5 C
void RemoteRampCapture(int cmd, unsigned int * o){
    int e;
    if(cmd == 1) CapArm();
    else if(cmd == 2 && CapState != 2){ CapState = 2; CapReason = 4; CapSince = 0; }
    e = CapFill - 1 - CapSince;
    if(e < 0) e = 0;
    o[0] = CapState;
    o[1] = CapReason;
    o[2] = (unsigned int)CapFill;
    o[3] = (unsigned int)e;
    o[4] = (unsigned int)CdRoom2;
}

//copy up to n records from index first (0 = oldest) to d (8 bytes each); returns the number copied
int RemoteRampCaptureRead(int first, int n, unsigned char * d){
    int i, k, oldest = (CapFill < CAP_N) ? 0 : CapPos;
    for(i = 0; i < n && first + i < CapFill; i++){
        int idx = (oldest + first + i) % CAP_N;
        for(k = 0; k < 8; k++) d[i * 8 + k] = Cap[idx][k];
    }
    return i;
}
