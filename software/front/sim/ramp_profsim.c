#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
typedef unsigned char UINT8; typedef unsigned short UINT16; typedef unsigned int UINT32; typedef short INT16; typedef int INT32;
enum { DEFAULT_MENU, SET_TEMPERATURE, RESET_TEMPERATURE, MENU, STANDBY };
#define SENSOR_NONE 0
typedef struct { struct { int Type; } SensorConfig; } t_cfg;
struct { t_cfg Config[2]; } IronPars = {{{{1}},{{0}}}};
typedef struct { int CTemp[2]; UINT32 PIDDutyFull; int TAvgF[2]; } t_pv;
t_pv PIDVars[2];
int CTTemp, CJTemp = -546, CRTemp = 33, DispTemp, CMode, ManualSleep, BeepTicks;
UINT8 eep[8192];
UINT8 EEPRead(UINT16 a, UINT8 *b, UINT16 c){ int i; if(!b) return eep[a]; for(i=0;i<c;i++) b[i]=eep[a+i]; return 0; }
void EEPWriteImm(UINT16 a, UINT8 b){ eep[a]=b; }
void RemoteTouch(void){}
int IronPairActive(void){ return 0; }
#include "../US_Firmware.X/ramp_core.h"
int main(int argc, char **argv){
    double T = 165, dt = 0.02; int t, scen = argc > 1 ? atoi(argv[1]) : 1; char h[22]; unsigned int st[9];
    /* scen 1: in stand 0-5 s, lifted at 5 s, stays out; 2: lifted at 5, back in stand at 30 (abort); 3: tip hot 220 at lift */
    if(scen == 3) T = 220;
    RampEnter();
    CTTemp = 165;
    for(t = 0; t < 50*100; t++){
        double sec = t*dt; int inStand = sec < 5 || (scen == 2 && sec >= 30 && sec < 40);
        double tgt = CTTemp, P;
        P = (tgt - T) * 0.5; if (P < 0) P = 0; if (P > 1) P = 1;
        T += dt * (P * 60.0 - (T - 20) * 0.010);
        PIDVars[0].CTemp[0] = (int)floor(2*T + 0.5);
        PIDVars[0].PIDDutyFull = (UINT32)(P * 0x00FFFFFF);
        if(t == 0) DispTemp = PIDVars[0].CTemp[0]*8; else { DispTemp -= DispTemp >> 3; DispTemp += PIDVars[0].CTemp[0]; }
        CTTemp = RampTick((int)T, inStand, 0, 0, 0);
        if(t % 50 == 0){ RampHeader(h); RemoteRampStatus(st); printf("%5.1f %s tgt %3d T %6.1f st%u step%u ms%u lift%u dl%u\n", sec, h, CTTemp, T, st[0], st[6], st[1], st[7], st[8]); }
    }
    return 0;
}
