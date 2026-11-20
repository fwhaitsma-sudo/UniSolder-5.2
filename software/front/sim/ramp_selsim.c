#include <string.h>
#define main profsim_main
#include "ramp_profsim.c"
#undef main
int main(void){
    char l[22]; unsigned int a[18] = {180,15,300, 250,20,80, 260,20,30, 0,0,0, 0,0,0, 0,0,0}; unsigned int f[18]={200,500,0};
    memset(eep, 0xFF, sizeof eep);
    printf("open empty %d\n", RampSelOpen());
    RemoteRampSlotWrite(2, "GND PLANE", 3, a); RemoteRampSlotWrite(6, "FAST", 1, f); RemoteRampSlotLoad(2);
    printf("open %d\n", RampSelOpen());
    RampSelLine(0,l); printf("[%s]\n", l); RampSelLine(1,l); printf("[%s]\n", l); RampSelLine(2,l); printf("[%s]\n", l);
    RampSelStep(1); RampSelLine(0,l); printf("[%s]\n", l); RampSelLine(1,l); printf("[%s]\n", l);
    printf("load fast %d\n", RampSelLoad()); RampSelLine(2,l); printf("[%s]\n", l);
    RampSelStep(1); printf("load gnd %d active %u\n", RampSelLoad(), RemoteRampActiveSlot());
    return 0;
}
