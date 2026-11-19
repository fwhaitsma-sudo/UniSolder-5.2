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
#define RAMP_EEP_MARK   0xD3
#define RAMP_NPAR       8

enum { RS_OFF = 0, RS_IDLE, RS_RAMP, RS_PEAK, RS_COOL, RS_LOCK };

//parameters: idle C, peak C, ramp rate C/s, dwell 0.1 s, lockout s, idle time-out min, contact duty jump % (step 2), contact dip 0.5 C (step 2)
static UINT16 RampP[RAMP_NPAR];
static const UINT16 RampDef[RAMP_NPAR] = {165, 275, 45, 11, 12, 10, 12, 5};
static const UINT16 RampMin[RAMP_NPAR] = {100, 110,   5,   0,  0,  1,  3,  2};
static const UINT16 RampMax[RAMP_NPAR] = {340, 350, 100, 100, 60, 60, 40, 20};

static UINT8 RampSt;            //RS_OFF = classic station
static UINT8 RampLoaded;
static UINT8 RampPressed;       //manual trigger (knob press or app) waiting for the next tick
static UINT8 RampAbortReq;      //abort request from the app
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
    return 1;
}

static void RampLoad(void){
    UINT8 b[RAMP_NPAR * 2];
    UINT16 v[RAMP_NPAR];
    int i;
    if(RampLoaded) return;
    RampLoaded = 1;
    for(i = 0; i < RAMP_NPAR; i++) RampP[i] = RampDef[i];
    if(EEPRead(RAMP_EEP_ADDR, 0, 1) != RAMP_EEP_MARK) return;
    EEPRead(RAMP_EEP_ADDR + 1, b, RAMP_NPAR * 2);
    for(i = 0; i < RAMP_NPAR; i++) v[i] = (UINT16)(b[i * 2] | (b[i * 2 + 1] << 8));
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
    CMode = DEFAULT_MENU;
    BeepTicks = 2;
    return 0;
}

static void RampGoCool(void){
    RampSt = RS_COOL;
    RampN = 0;
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
            RampSt = RS_RAMP;
            RampN = 0;
            RampPeakSeen = 0;
            BeepTicks = 2;
        }
        else{
            BeepTicks = 6;                                   //not ready, in the stand or cooling
        }
    }

    switch(RampSt){
        case RS_IDLE:
            tgt = RampP[0];
            if(activity) RampIdle = 0; else RampIdle++;
            if(RampIdle >= (int)RampP[5] * 3000){            //idle time-out: back to the classic station
                RampLeaveMode();
                BeepTicks = 12;
                return 0;
            }
            //ready: at the idle temperature, or up to 10 C above it (the tip cools slowly; a contact is detected from the drop)
            c = actual - (int)RampP[0];
            if(c >= -5 && c <= 10){ if(RampOk < 1000) RampOk++; } else RampOk = 0;
            break;

        case RS_RAMP:
            if(inStand || menuOpen){                         //iron put back or menu opened: abort
                RampGoCool();
                BeepTicks = 6;
                tgt = RampP[0];
                break;
            }
            RampN++;
            nr = (((int)RampP[1] - (int)RampP[0]) * 50) / (int)RampP[2];     //ticks the ramp takes
            if(RampN < nr) tgt = (int)RampP[0] + (RampN * (int)RampP[2]) / 50;
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

        case RS_COOL:
            tgt = RampP[0];
            RampN++;
            if(actual <= (int)RampP[0] + 10 || RampN >= 500){ //cooled, or 10 s at most
                RampSt = RS_LOCK;
                RampN = 0;
            }
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
        case RS_IDLE: s = (RampOk >= 25) ? "READY" : ((RampActualNow > (int)RampP[0]) ? "COOLING" : "HEATING"); break;
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
            nr = (((int)RampP[1] - (int)RampP[0]) * 50) / (int)RampP[2];
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
