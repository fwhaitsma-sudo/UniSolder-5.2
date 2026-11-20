#include <stdio.h>
#include <string.h>
#define main profsim_main
#include "ramp_profsim.c"
#undef main
int main(void){
    char nm[11]; unsigned int c, s[18]; unsigned int a[18] = {180,15,300, 250,20,80, 0,0,0, 0,0,0, 0,0,0, 0,0,0};
    int i, r;
    memset(eep, 0xFF, sizeof eep);
    r = RemoteRampSlotRead(3, nm, &c, s); printf("read empty %d\n", r);
    r = RemoteRampSlotWrite(3, "GND PLANE", 2, a); printf("write %d\n", r);
    r = RemoteRampSlotRead(3, nm, &c, s); printf("read %d '%s' n%u %u/%u/%u %u/%u/%u\n", r, nm, c, s[0],s[1],s[2],s[3],s[4],s[5]);
    printf("active before load %u\n", RemoteRampActiveSlot());
    r = RemoteRampSlotLoad(3); printf("load %d active %u steps %d/%d hold %d,%d\n", r, RemoteRampActiveSlot(), RampS[0][0], RampS[1][0], RampS[0][2], RampS[1][2]);
    { unsigned int b[18]; for(i=0;i<18;i++) b[i]= (i<6)? a[i]:(i%3==0?250:(i%3==1?1:0));
      r = RemoteRampStepsSet(2, b); printf("same steps set %d active %u\n", r, RemoteRampActiveSlot());
      b[2]=310; r = RemoteRampStepsSet(2, b); printf("changed set %d active %u\n", r, RemoteRampActiveSlot()); }
    r = RemoteRampSlotWrite(4, "a\x01", 1, a); printf("bad name %d\n", r);
    { unsigned int f[18]={200,500,0}; for(i=3;i<18;i++) f[i]= (i%3==0?250:(i%3==1?1:0));
      r = RemoteRampSlotWrite(5,"FAST",1,f); printf("write fast %d\n", r); r = RemoteRampSlotLoad(5); printf("load fast (rate 50 C/s > max 4) %d\n", r); }
    r = RemoteRampSlotLoad(3); printf("load 3 again %d\n", r);
    RampLoaded = 0; RampLoad(); printf("after reload active %u\n", RemoteRampActiveSlot());
    r = RemoteRampSlotDelete(3); printf("delete %d active %u\n", r, RemoteRampActiveSlot()); r = RemoteRampSlotRead(3, nm, &c, s); printf("read deleted %d\n", r);
    return 0;
}
