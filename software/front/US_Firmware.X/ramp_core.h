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
 * (JBC auto-start, default). (2 and 3 were the pad contact start, removed 2026-11-20: not reliable in tests.)
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
//the profile), 5 idle time-out min, 6-7 not used any more (contact duty jump, dip), 8 ready limit C (a profile can start when the tip is
//at most this hot), 9-11 not used any more (contact cooling constant, blank after lift, wipe ripple), 12 start (see above),
//13 start delay 0.1 s, 14 maximum rate 0.1 C/s (no step may be faster), 15 number of steps
static UINT16 RampP[RAMP_NPAR];
static const UINT16 RampDef[RAMP_NPAR] = {165, 275, 45, 11,  5, 10,  5, 3, 200, 16,  3,  30, 1,  30,   40, 2};
static const UINT16 RampMin[RAMP_NPAR] = {100, 110,   5,   0,  0,  1,  3,  2, 110,  5,  0,   0, 0,   0,    5, 1};
static const UINT16 RampMax[RAMP_NPAR] = {340, 350, 100, 100, 60, 60, 40, 20, 340, 50, 10, 200, 1, 100, 1000, RAMP_NSTEP};

//profile steps: temperature C, rate 0.1 C/s, hold 0.1 s. Default (tin-lead, Kyocera AVX TCJ D-case, design note 2.7):
//preheat 190 C at 2 C/s, soak 20 s; reflow 250 C at 2 C/s, peak 5 s (240 until 2026-11-20: a ground plane was molten for only about 8 s).
static UINT16 RampS[RAMP_NSTEP][3];
static const UINT16 RampSDef[RAMP_NSTEP][3] = {{190, 20, 200}, {250, 20, 50}, {250, 20, 0}, {250, 20, 0}, {250, 20, 0}, {250, 20, 0}};

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
        if(v[12] > 1) v[12] = RampDef[12];          //contact start (2, 3) removed: lift start
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
        return RampFrom;
    }

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
            if(RampLiftArmed){
                left = (int)RampP[13] * 5 - RampLiftN;
                if(left > 0){
                    k = RampPutStr(o, k, "START IN ");
                    k = RampPutNum(o, k, (left + 49) / 50);
                    k = RampPutStr(o, k, "s");
                }
                else k = RampPutStr(o, k, "WAIT");
            }
            else if(RampOk >= 25) k = RampPutStr(o, k, (RampP[12] == 1) ? "READY LIFT" : "READY");
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
