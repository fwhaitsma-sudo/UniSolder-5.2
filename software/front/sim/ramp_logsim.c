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
UINT8 eep[1024];
UINT8 EEPRead(UINT16 a, UINT8 *b, UINT16 c){ int i; if(!b) return eep[a]; for(i=0;i<c;i++) b[i]=eep[a+i]; return 0; }
void EEPWriteImm(UINT16 a, UINT8 b){ eep[a]=b; }
void RemoteTouch(void){}
int IronPairActive(void){ return 0; }
#include "../US_Firmware.X/ramp_core.h"
double tt[400], aa[400], pp[400]; int n;
int main(int argc, char **argv){
    char line[256]; FILE *f = fopen(argv[1], "r"); int t, i = 0, prev = 0;
    fgets(line, 256, f);
    while(fgets(line, 256, f)){ double x[9]; if(sscanf(line, "%lf,%lf,%lf,%lf", &x[0],&x[1],&x[2],&x[3])==4){ tt[n]=x[0]; aa[n]=x[2]; pp[n]=x[3]; n++; } }
    { int q; static double b2[400]; for(q=1;q<n-1;q++) b2[q]=(aa[q-1]+aa[q]+aa[q+1])/3; for(q=1;q<n-1;q++) aa[q]=b2[q]; } RampLoad(); RampSt = RS_IDLE; srand(1);
    for(t = 0; t*0.02 < tt[n-1]; t++){
        double sec = t*0.02, T, P;
        while(i < n-2 && tt[i+1] <= sec) i++;
        T = aa[i] + (aa[i+1]-aa[i])*(sec-tt[i])/(tt[i+1]-tt[i]); P = pp[i];
        PIDVars[0].CTemp[0] = (int)floor(2*(T + ((rand()%100)-50)/200.0) + 0.5);
        PIDVars[0].PIDDutyFull = (UINT32)(P/100*0x00FFFFFF);
        if(t==0) DispTemp = PIDVars[0].CTemp[0]*8; else { DispTemp -= DispTemp>>3; DispTemp += PIDVars[0].CTemp[0]; }
        RampDetect(0, 0);
        if((CdFlags & 4) != prev || t % 50 == 0) printf("%5.2f T%6.1f P%4.1f k%3d rip%3d fl%02x cont%d hold%d wipes%d\n", sec, T, P, CdK, CdRip, CdFlags, CdCont, CdWipeHold, CdWipes);
        prev = CdFlags & 4;
    }
    return 0;
}
