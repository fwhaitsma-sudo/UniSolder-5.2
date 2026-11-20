#include <stdio.h>
#include <stdlib.h>
#include <math.h>
typedef unsigned char UINT8; typedef unsigned short UINT16; typedef unsigned int UINT32; typedef short INT16; typedef int INT32;
enum { DEFAULT_MENU, SET_TEMPERATURE, RESET_TEMPERATURE, MENU, STANDBY };
#define SENSOR_NONE 0
typedef struct { struct { int Type; } SensorConfig; } t_cfg;
struct { t_cfg Config[2]; } IronPars = {{{{1}},{{0}}}};
typedef struct { int CTemp[2]; UINT32 PIDDutyFull; int TAvgF[2]; } t_pv;
t_pv PIDVars[2];
int CTTemp, CJTemp = -546, CRTemp = 33, DispTemp, CMode, ManualSleep, BeepTicks;
UINT8 eep[1024];
UINT8 EEPRead(UINT16 a, UINT8 *b, UINT16 c){ int i; if(!b) return eep[a]; for(i=0;i<c;i++) b[i]=eep[a+i]; return 0; }
void EEPWriteImm(UINT16 a, UINT8 b){ eep[a]=b; }
void RemoteTouch(void){}
int IronPairActive(void){ return 0; }
#include "../US_Firmware.X/ramp_core.h"

/* thermal model: T' = -k0 (T - room) * (1 + contact factor) + heater gain * duty */
int main(int argc, char **argv){
    double T = 275, room = 16.5, dt = 0.02;
    int t, scen = atoi(argv[1]);
    srand(argc>3?atoi(argv[3]):1);
    RampEnter(); RampP[12] = 2; RampP[8] = 230; if(argc>2) RampP[11]=atoi(argv[2]);
    RampTick(275, 0, 0, 0, 0);
    for(t = 0; t < 50*60; t++){
        double sec = t*dt, kfree = 0.010, kc = kfree, duty, tgt;
        int inStand = 0;
        /* scenario: 1 pad contact at 20 s for 3 s; 2 brass wool strokes 3 Hz at 20 s for 3 s; 3 none; 4 wipe then pad */
        if(scen==1 && sec>=20 && sec<23) kc = 0.023;
        if(scen==2 && sec>=20 && sec<23) kc = (fmod(sec,0.33)<0.15)?0.040:kfree;
        if(scen==4 && sec>=12 && sec<14) kc = (fmod(sec,0.33)<0.15)?0.040:kfree;
        if(scen==4 && sec>=25 && sec<28) kc = 0.023;
        if(scen==5){ inStand = sec < 19.5; if(sec>=20 && sec<23) kc = 0.023; }
        tgt = CTTemp;
        duty = (tgt - T) * 0.1 + (T-room)*kfree/4.0; if(duty<0) duty=0; if(tgt<=T) duty = 0; if(duty>1) duty=1;
        T += dt*(-kc*(T-room) + 4.0*duty*10*kfree*30);
        PIDVars[0].CTemp[0] = (int)floor(2*(T + ((rand()%100)-50)/200.0) + 0.5);
        PIDVars[0].PIDDutyFull = (UINT32)(duty*0x00FFFFFF);
        DispTemp -= DispTemp >> 3; DispTemp += PIDVars[0].CTemp[0];
        if(t==0) DispTemp = PIDVars[0].CTemp[0]*8;
        CTTemp = RampTick(DispTemp>>4, inStand, 0, 0, 0);
        if(t%25==0 || (CdFlags&4)) if(t%5==0) printf("%5.2f st%d T%6.1f tgt%3d k%3d rip%3d fl%02x cont%d hold%d starts%d wipes%d\n", sec, RampSt, T, CTTemp, CdK, CdRip, CdFlags, CdCont, CdWipeHold, CdStarts, CdWipes);
    }
    return 0;
}
