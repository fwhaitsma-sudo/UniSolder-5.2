/*
 * Ramp mode: the station runs a soldering profile by itself (idle, profile steps, lockout), like JBC's Dynamic Soldering Profiles.
 * Design: software/front/RAMP_MODE_DESIGN.md (section 2.7 for the step profiles and the lift start).
 *
 * This file is included by menu.c (after RemoteTouch) and is not compiled on its own: it uses the menu's static
 * state, and the MPLAB makefiles then need no new source file. C89, integer math only.
 *
 * Outside ramp mode (RampSt == RS_OFF) nothing in here runs and the station behaves as before.
 * The only link to the control loop is the target temperature: MenuTasks replaces its target (CTTemp) by RampTick().
 * The tick is the 20 ms MenuTasks tick (50 per second).
 *
 * Start (parameter 12): 0 = knob or app only, 1 = lift the iron from its stand, the profile starts after the start delay
 * (JBC auto-start, default), 2 = pad contact by the cooling of the tip (experimental), 3 = 2 + heater power (experimental).
 * The knob and the app can always start a profile.
 * Profile: up to RAMP_NSTEP steps; each step ramps the target from the previous step's temperature (the start temperature for
 * step 1) to its own temperature at its rate, then holds it for its hold time. After the last step the heater target is the
 * idle temperature again and the lockout runs.
 */

#define RAMP_EEP_ADDR   384     //EEPROM: marker, then RAMP_NPAR x u16 (little endian); free area 384-511
#define RAMP_EEP_MARK   0xD6    //16 values; 0xD5 = 13, 0xD4 = 9, 0xD3 = 8 value format (all read, missing values get their default)
#define RAMP_EEP_MARK13 0xD5
#define RAMP_EEP_MARK9  0xD4
#define RAMP_EEP_MARK8  0xD3
#define RAMP_NPAR       16
#define RAMP_STEP_ADDR  420     //EEPROM: marker, then RAMP_NSTEP x (temperature C, rate 0.1 C/s, hold 0.1 s) u16
#define RAMP_STEP_MARK  0xE1
#define RAMP_NSTEP      6

enum { RS_OFF = 0, RS_IDLE, RS_RAMP, RS_PEAK, RS_COOL, RS_LOCK };

//parameters: 0 idle C, 1-3 not used any more (peak, rate, dwell of the single ramp; kept for older apps), 4 lockout s (from the end of
//the profile), 5 idle time-out min, 6 contact duty jump %, 7 contact dip 0.5 C, 8 ready limit C (a profile can start when the tip is at
//most this hot), 9 contact cooling constant 0.001/s, 10 contact blank after lift s, 11 wipe ripple %, 12 start (see above),
//13 start delay 0.1 s, 14 maximum rate 0.1 C/s (no step may be faster), 15 number of steps
static UINT16 RampP[RAMP_NPAR];
static const UINT16 RampDef[RAMP_NPAR] = {165, 275, 45, 11,  5, 10,  5, 3, 200, 16,  3,  30, 1,  30,   40, 2};
static const UINT16 RampMin[RAMP_NPAR] = {100, 110,   5,   0,  0,  1,  3,  2, 110,  5,  0,   0, 0,   0,    5, 1};
static const UINT16 RampMax[RAMP_NPAR] = {340, 350, 100, 100, 60, 60, 40, 20, 340, 50, 10, 200, 3, 100, 1000, RAMP_NSTEP};

//profile steps: temperature C, rate 0.1 C/s, hold 0.1 s. Default (tin-lead, Kyocera AVX TCJ D-case, design note 2.7):
//preheat 190 C at 2 C/s, soak 20 s; reflow 240 C at 2 C/s, peak 5 s.
static UINT16 RampS[RAMP_NSTEP][3];
static const UINT16 RampSDef[RAMP_NSTEP][3] = {{190, 20, 200}, {240, 20, 50}, {250, 20, 0}, {250, 20, 0}, {250, 20, 0}, {250, 20, 0}};

static UINT8 RampSt;            //RS_OFF = classic station
static UINT8 RampLoaded;
static UINT8 RampPressed;       //manual trigger (knob press or app) waiting for the next tick
static UINT8 RampAbortReq;      //abort request from the app
static int RampFrom;            //temperature the current step ramps from, whole degrees C
static int RampStep;            //current step (0 based) while RS_RAMP / RS_PEAK
static int RampN;               //ticks in the current state
static int RampIdle;            //ticks without activity in RS_IDLE (idle time-out)
static int RampOk;              //ticks the tip has been ready
static int RampPeakSeen;        //highest temperature seen since the last start, whole degrees C
static int RampActualNow;       //actual temperature at the last tick, whole degrees C
static UINT8 RampLiftArmed;     //the iron was lifted from its stand: start after the delay (lift start)
static int RampLiftN;           //ticks since the lift
static UINT8 RampWasInStand;

static int RampPeakOf(UINT16 s[][3], int n){
    int i, p = 0;
    for(i = 0; i < n; i++) if((int)s[i][0] > p) p = s[i][0];
    return p;
}

//parameters v with steps s (n steps): 1 = valid
static int RampValidAll(const UINT16 * v, UINT16 s[][3]){
    int i, n, peak;
    for(i = 0; i < RAMP_NPAR; i++){
        if(v[i] < RampMin[i] || v[i] > RampMax[i]) return 0;
    }
    n = v[15];
    for(i = 0; i < n; i++){
        if(s[i][0] < 100 || s[i][0] > 350) return 0;                //hard maximum 350 C
        if(s[i][1] < 1 || s[i][1] > v[14]) return 0;               //rate 0.1 C/s up to the maximum rate
        if(s[i][2] > 6000) return 0;                                //hold up to 600 s
    }
    peak = RampPeakOf(s, n);
    if(peak < (int)v[0] + 10) return 0;                             //the profile goes at least 10 C above idle
    if(v[8] < v[0] + 10 || (int)v[8] > peak - 10) return 0;         //ready limit between idle + 10 and peak - 10
    return 1;
}

static void RampLoad(void){
    UINT8 b[RAMP_NPAR * 2];
    UINT16 v[RAMP_NPAR];
    UINT16 s[RAMP_NSTEP][3];
    int i, j, n;
    if(RampLoaded) return;
    RampLoaded = 1;
    for(i = 0; i < RAMP_NPAR; i++) RampP[i] = RampDef[i];
    for(i = 0; i < RAMP_NSTEP; i++) for(j = 0; j < 3; j++) RampS[i][j] = RampSDef[i][j];
    for(i = 0; i < RAMP_NPAR; i++) v[i] = RampDef[i];
    for(i = 0; i < RAMP_NSTEP; i++) for(j = 0; j < 3; j++) s[i][j] = RampSDef[i][j];
    n = EEPRead(RAMP_EEP_ADDR, 0, 1);
    if(n == RAMP_EEP_MARK || n == RAMP_EEP_MARK13 || n == RAMP_EEP_MARK9 || n == RAMP_EEP_MARK8){
        int m = (n == RAMP_EEP_MARK) ? RAMP_NPAR : ((n == RAMP_EEP_MARK13) ? 13 : ((n == RAMP_EEP_MARK9) ? 9 : 8));
        EEPRead(RAMP_EEP_ADDR + 1, b, m * 2);
        for(i = 0; i < m; i++) v[i] = (UINT16)(b[i * 2] | (b[i * 2 + 1] << 8));
        if(m < 13 && v[6] == 12 && v[7] == 5){      //old format with the old contact defaults (never used): take the new defaults
            v[6] = RampDef[6];
            v[7] = RampDef[7];
        }
        if(m < RAMP_NPAR){                          //before the step profiles: start by lifting the iron (the contact start was not reliable)
            v[12] = RampDef[12];
            if(v[8] > RampDef[8]) v[8] = RampDef[8];
        }
    }
    if(EEPRead(RAMP_STEP_ADDR, 0, 1) == RAMP_STEP_MARK){
        UINT8 sb[RAMP_NSTEP * 6];
        EEPRead(RAMP_STEP_ADDR + 1, sb, RAMP_NSTEP * 6);
        for(i = 0; i < RAMP_NSTEP; i++) for(j = 0; j < 3; j++) s[i][j] = (UINT16)(sb[i * 6 + j * 2] | (sb[i * 6 + j * 2 + 1] << 8));
    }
    if(RampValidAll(v, s)){
        for(i = 0; i < RAMP_NPAR; i++) RampP[i] = v[i];
        for(i = 0; i < RAMP_NSTEP; i++) for(j = 0; j < 3; j++) RampS[i][j] = s[i][j];
    }
}

static void RampEepPut(int a, UINT8 b){
    if(EEPRead(a, 0, 1) != b) EEPWriteImm(a, b);
}

static void RampSave(void){
    int i, j;
    RampEepPut(RAMP_EEP_ADDR, RAMP_EEP_MARK);
    for(i = 0; i < RAMP_NPAR; i++){
        RampEepPut(RAMP_EEP_ADDR + 1 + i * 2, (UINT8)(RampP[i] & 0xFF));
        RampEepPut(RAMP_EEP_ADDR + 2 + i * 2, (UINT8)(RampP[i] >> 8));
    }
    RampEepPut(RAMP_STEP_ADDR, RAMP_STEP_MARK);
    for(i = 0; i < RAMP_NSTEP; i++){
        for(j = 0; j < 3; j++){
            RampEepPut(RAMP_STEP_ADDR + 1 + i * 6 + j * 2, (UINT8)(RampS[i][j] & 0xFF));
            RampEepPut(RAMP_STEP_ADDR + 2 + i * 6 + j * 2, (UINT8)(RampS[i][j] >> 8));
        }
    }
}

//---- contact detection (step 2, design note 2.4) and the 20 ms capture buffer ----
//Detector A (heater off, tip above its target): k = cooling rate / (T - T_room) in 0.001/s, contact when k >= RampP[9].
//Detector B (heater regulating at idle, only with start 3): duty jump >= RampP[6] % or dip >= RampP[7] x 0.5 C against
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
static INT32 CdRipSum;          //sum of the ripple values since the contact began (the decision uses the mean)
static INT32 CdNoise = -1;      //variance of the 0.1 s rates while cooling freely (noise floor), (C/s)^2 x 64; -1 = not known yet
static INT32 CdDev;             //variance now, (C/s)^2 x 64
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
        if(RampP[12] >= 2 && CdHeatOff >= CD_HEATOFF && CdFill >= CD_N && CdK >= (int)RampP[9]){
            CdWipeHold = 1;
            CdQuiet = 0;
        }
    }
    if(RampP[12] >= 2 && !inStand && !(CdFlags & 8) && CdHeatOff >= CD_HEATOFF && CdFill >= CD_N){
        CdFlags |= 1;
        condA = (CdK >= (int)RampP[9] - ((CdCont > 0) ? 3 : 0));      //0.003/s hysteresis once a contact has begun
    }
    if(RampP[12] >= 3 && !inStand && !(CdFlags & 8) && CdSettled >= 75){
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
    RampLiftArmed = 0;
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
    RampLiftArmed = 0;
    RampWasInStand = 0;
    CdReset();
    CdLift = 0;
    CMode = DEFAULT_MENU;
    BeepTicks = 2;
    return 0;
}

//End of the profile or an abort: the lockout starts at once (the heater target is the idle temperature, the tip cools by itself).
static void RampGoCool(void){
    RampSt = RS_LOCK;
    RampN = 0;
    RampLiftArmed = 0;
}

//Start the profile from the tip temperature (when it is above idle, but not above step 1's temperature).
static void RampStart(void){
    int c = (PIDVars[0].CTemp[0] + 1) >> 1;
    RampFrom = (c > (int)RampP[0]) ? c : (int)RampP[0];
    if((int)RampS[0][0] >= (int)RampP[0] && RampFrom > (int)RampS[0][0]) RampFrom = RampS[0][0];
    RampStep = 0;
    RampSt = RS_RAMP;
    RampN = 0;
    RampPeakSeen = 0;
    RampLiftArmed = 0;
    CdCont = 0;
    BeepTicks = 2;
}

//ticks the ramp of step k takes from 'from'
static int RampRampTicks(int k, int from){
    int d = (int)RampS[k][0] - from;
    if(d < 0) d = -d;
    return (d * 500) / (int)RampS[k][1];
}

//target of the ramp of step k after n ticks
static int RampRampTarget(int k, int from, int n){
    int t = RampS[k][0];
    int d = (int)(((INT32)n * (INT32)RampS[k][1]) / 500);   //0.1 C/s x 0.02 s per tick
    if(t >= from) return (from + d < t) ? from + d : t;
    return (from - d > t) ? from - d : t;
}

//One 20 ms tick. actual = measured temperature (whole C), inStand = iron in its stand, fault = heater/sensor fault or tip change,
//menuOpen = the station menu (or another screen) is open, activity = knob or button touched.
//Returns the target temperature (whole C). When it leaves the mode RampSt is RS_OFF and the caller keeps its own target.
static int RampTick(int actual, int inStand, int fault, int menuOpen, int activity){
    int tgt = RampP[0];
    int nr, c, ready;
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
    //lift start: the iron leaves its stand while the station waits (idle or lockout)
    if(RampP[12] == 1 && RampWasInStand && !inStand && (RampSt == RS_IDLE || RampSt == RS_LOCK)){
        RampLiftArmed = 1;
        RampLiftN = 0;
    }
    if(inStand) RampLiftArmed = 0;
    RampWasInStand = (UINT8)inStand;
    if(RampLiftArmed && RampLiftN < 30000) RampLiftN++;

    if(RampAbortReq){
        RampAbortReq = 0;
        if(RampSt == RS_RAMP || RampSt == RS_PEAK){
            RampGoCool();
            BeepTicks = 6;
        }
        RampLiftArmed = 0;
    }
    if(RampPressed){
        RampPressed = 0;
        if(RampSt == RS_RAMP || RampSt == RS_PEAK){          //a press while running aborts
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

    ready = (RampSt == RS_IDLE && RampOk >= 25 && !menuOpen) ? 1 : 0;
    //lift start after the delay (as soon as the station is ready, if it was not yet)
    if(RampLiftArmed && ready && RampLiftN >= (int)RampP[13] * 5){
        RampStart();
        RampIdle = 0;
        CapEvent(2);
        return RampFrom;
    }
    //contact detection (experimental, start 2 or 3) runs in IDLE and LOCKOUT; it also feeds the capture buffer
    if(RampSt == RS_IDLE || RampSt == RS_LOCK){
        if(RampDetect(inStand, ready) && RampP[12] >= 2){
            RampStart();
            RampIdle = 0;
            CapEvent(2);
            return RampFrom;
        }
    }
    else CapRecord(PIDVars[0].CTemp[0], DispTemp, RampDutyPm());

    switch(RampSt){
        case RS_IDLE:
            tgt = RampP[0];
            if(activity || RampLiftArmed) RampIdle = 0; else RampIdle++;
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
            nr = RampRampTicks(RampStep, RampFrom);
            tgt = RampRampTarget(RampStep, RampFrom, RampN);
            //the hold starts when the target has reached the step temperature and the tip (unfiltered reading) is within 5 C of it,
            //or at the latest 1.5 s after the ramp should have ended
            c = (PIDVars[0].CTemp[0] + 1) >> 1;
            if(RampN >= nr && ((c >= (int)RampS[RampStep][0] - 5 && c <= (int)RampS[RampStep][0] + 5) || RampN >= nr + 75)){
                RampSt = RS_PEAK;
                RampN = 0;
            }
            break;

        case RS_PEAK:
            tgt = RampS[RampStep][0];
            if(inStand || menuOpen){
                RampGoCool();
                BeepTicks = 6;
                tgt = RampP[0];
                break;
            }
            RampN++;
            if(RampN >= (int)RampS[RampStep][2] * 5){
                RampFrom = RampS[RampStep][0];
                RampStep++;
                RampN = 0;
                if(RampStep >= (int)RampP[15]){              //profile done
                    RampGoCool();
                    BeepTicks = 4;
                    tgt = RampP[0];
                }
                else{
                    RampSt = RS_RAMP;
                    BeepTicks = 1;
                }
            }
            break;

        case RS_COOL:                                        //not used any more (the lockout follows the profile at once)
            RampSt = RS_LOCK;
            RampN = 0;
            tgt = RampP[0];
            break;

        case RS_LOCK:
            tgt = RampP[0];
            RampN++;
            if(RampN >= (int)RampP[4] * 50){
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

//ms left in the whole profile (nominal)
static long RampMsLeft(void){
    long t = 0;
    int k, from;
    if(RampSt != RS_RAMP && RampSt != RS_PEAK) return 0;
    if(RampSt == RS_RAMP){
        t += (long)(RampRampTicks(RampStep, RampFrom) - RampN);
        if(t < 0) t = 0;
        t += (long)RampS[RampStep][2] * 5;
    }
    else{
        t += (long)RampS[RampStep][2] * 5 - RampN;
        if(t < 0) t = 0;
    }
    from = RampS[RampStep][0];
    for(k = RampStep + 1; k < (int)RampP[15]; k++){
        t += (long)RampRampTicks(k, from) + (long)RampS[k][2] * 5;
        from = RampS[k][0];
    }
    return t * 20;
}

static int RampPutNum(char * o, int k, int v){
    char d[6];
    int n = 0;
    if(v < 0) v = 0;
    do{ d[n++] = (char)('0' + v % 10); v /= 10; } while(v && n < 5);
    while(n && k < 21) o[k++] = d[--n];
    return k;
}

static int RampPutStr(char * o, int k, const char * s){
    while(*s && k < 21) o[k++] = *s++;
    return k;
}

//Text for the first display line: 21 characters.
static void RampHeader(char * o){
    int k = 0, left;
    k = RampPutStr(o, k, "RAMP  ");
    switch(RampSt){
        case RS_IDLE:
            if(CdWipeHold) k = RampPutStr(o, k, "WIPE IGNORED");
            else if(RampLiftArmed){
                left = (int)RampP[13] * 5 - RampLiftN;
                if(left > 0){
                    k = RampPutStr(o, k, "START IN ");
                    k = RampPutNum(o, k, (left + 49) / 50);
                    k = RampPutStr(o, k, "s");
                }
                else k = RampPutStr(o, k, "WAIT");
            }
            else if(RampOk >= 25) k = RampPutStr(o, k, (RampP[12] == 1) ? "READY LIFT" : ((RampP[12] >= 2) ? "READY AUTO" : "READY"));
            else k = RampPutStr(o, k, (RampActualNow > (int)RampP[8]) ? "COOLING" : "HEATING");
            break;
        case RS_RAMP:
        case RS_PEAK:
            k = RampPutStr(o, k, "S");
            k = RampPutNum(o, k, RampStep + 1);
            k = RampPutStr(o, k, "/");
            k = RampPutNum(o, k, RampP[15]);
            k = RampPutStr(o, k, " ");
            k = RampPutNum(o, k, (int)((RampMsLeft() + 999) / 1000));
            k = RampPutStr(o, k, "s");
            k = RampPutStr(o, k, (RampSt == RS_RAMP) ? " RAMP" : " HOLD");
            break;
        case RS_LOCK:
            k = RampPutStr(o, k, "LOCKOUT ");
            left = (int)RampP[4] - RampN / 50;
            k = RampPutNum(o, k, left);
            k = RampPutStr(o, k, "s");
            break;
        default: break;
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

//0 done, 2 out of range, 3 a profile is running
int RemoteRampSet(const unsigned int * v){
    UINT16 n[RAMP_NPAR];
    int i;
    RampLoad();
    if(RampSt == RS_RAMP || RampSt == RS_PEAK) return 3;
    for(i = 0; i < RAMP_NPAR; i++){
        if(v[i] > 65535u) return 2;
        n[i] = (UINT16)v[i];
    }
    if(!v[9] && !v[10] && !v[11] && !v[12]){       //an app that knows only 9 values (protocol 7): keep the newer values
        for(i = 9; i < RAMP_NPAR; i++) n[i] = RampP[i];
    }
    else if(!v[14] && !v[15]){                      //an app that knows 13 values (protocol 8): keep the profile values
        for(i = 12; i < RAMP_NPAR; i++) n[i] = RampP[i];
    }
    if(!RampValidAll(n, RampS)) return 2;
    for(i = 0; i < RAMP_NPAR; i++) RampP[i] = n[i];
    RampSave();
    return 0;
}

//profile steps: s = RAMP_NSTEP x 3 values (temperature C, rate 0.1 C/s, hold 0.1 s)
void RemoteRampStepsGet(unsigned int * s){
    int i, j;
    RampLoad();
    for(i = 0; i < RAMP_NSTEP; i++) for(j = 0; j < 3; j++) s[i * 3 + j] = RampS[i][j];
}

//count = number of steps; 0 done, 2 out of range, 3 a profile is running
int RemoteRampStepsSet(int count, const unsigned int * s){
    UINT16 n[RAMP_NPAR];
    UINT16 st[RAMP_NSTEP][3];
    int i, j;
    RampLoad();
    if(RampSt == RS_RAMP || RampSt == RS_PEAK) return 3;
    for(i = 0; i < RAMP_NPAR; i++) n[i] = RampP[i];
    n[15] = (UINT16)count;
    for(i = 0; i < RAMP_NSTEP; i++){
        for(j = 0; j < 3; j++){
            if(s[i * 3 + j] > 65535u) return 2;
            st[i][j] = (UINT16)s[i * 3 + j];
        }
    }
    for(i = count; i < RAMP_NSTEP; i++){            //unused steps: keep valid placeholder values
        if(st[i][0] < 100 || st[i][0] > 350) st[i][0] = 250;
        if(st[i][1] < 1 || st[i][1] > n[14]) st[i][1] = 1;
        if(st[i][2] > 6000) st[i][2] = 0;
    }
    if(!RampValidAll(n, st)) return 2;
    for(i = 0; i < RAMP_NPAR; i++) RampP[i] = n[i];
    for(i = 0; i < RAMP_NSTEP; i++) for(j = 0; j < 3; j++) RampS[i][j] = st[i][j];
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

//o[0] state, o[1] ms left in the state (nominal; while running: the whole profile), o[2] target now, o[3] highest temperature since the start,
//o[4] ready (1/0), o[5] actual temperature, o[6] step (1 based, 0 = not running), o[7] lift start: 0 none, 1 counting the delay,
//2 waiting until the station is ready, o[8] ms left of the start delay
void RemoteRampStatus(unsigned int * o){
    long ms = 0, dl = 0;
    RampLoad();
    switch(RampSt){
        case RS_RAMP:
        case RS_PEAK: ms = RampMsLeft(); break;
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
    o[6] = (RampSt == RS_RAMP || RampSt == RS_PEAK) ? (unsigned int)(RampStep + 1) : 0;
    o[7] = 0;
    if(RampLiftArmed){
        dl = ((long)RampP[13] * 5 - RampLiftN) * 20;
        o[7] = (dl > 0) ? 1 : 2;
        if(dl < 0) dl = 0;
    }
    o[8] = (unsigned int)dl;
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

