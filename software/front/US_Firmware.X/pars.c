#define _PARS_C

#include "main.h"
#include "OLED.h"
#include "pars.h"
#include "isr.h"
#include "EEP.h"

void ParDispStr(int par, int col, int row, int num);
void ParDispNum(int par, int col, int row, int num);
void ParDispNumOff(int par, int col, int row, int num);
void ParDispTemp(int par, int col, int row, int temp);
void ParDispCF(int par, int col, int row, int num);
void ParDispDeg(int par, int col, int row, int num);

const char * StrDispRot[]   = {"0   ", "180 "};
const char * StrResume[]    = {"OFF ", "KEY ", "HOLD", "BOTH"};
const char * StrButtons[]   = {"+/- ", "-/+ "};
const char * StrOffOnAuto[] = {"OFF ", "ON  ", "AUTO"};
const char * StrMenuUp[]    = {"KEY+", "KEY-"};

const unsigned char MenuOrder[] = {18,19,0,1,2,22,3,4,5,6,7,11,13,8,10,14,9,12,16,17,23};

const t_ParDef ParDef[] = {
//  NAME            DEF  MIN      MAX      IMMEDIATE SUFFIX STRINGS       DISPFUNC    

    {"        T1 ", 125, MINTEMP, MAXTEMP, 0,            0, 0,            &ParDispTemp}, //0
    {"        T2 ", 150, MINTEMP, MAXTEMP, 0,            0, 0,            &ParDispTemp}, //1
    {"        T3 ", 175, MINTEMP, MAXTEMP, 0,            0, 0,            &ParDispTemp}, //2
    {"  HOLDER T ", 150, MINTEMP, MAXTEMP, 0,            0, 0,            &ParDispTemp}, //3
    {"   SLEEP T ", 125, MINTEMP, MAXTEMP, 0,            0, 0,            &ParDispTemp}, //4
    {"     SLEEP ",  20,       0,     255, 0,          "s", 0,            &ParDispNumOff}, //5
    {"  STAND-BY ",  30,       0,     255, 0,          "m", 0,            &ParDispNumOff}, //6
    {"    HOLDER ",   2,       0,       2, 0,            0, StrOffOnAuto, &ParDispStr}, //7
    {"   DEGREES ",   0,       0,       1, 1,            0, 0,            &ParDispCF}, //8
    {"BRIGHTNESS ",  15,       1,      15, 1,            0, 0,            &ParDispNum}, //9
    {"   BUTTONS ",   0,       0,       1, 0,            0, StrButtons,   &ParDispStr}, //10
    {"   WAKE UP ",   0,       0,       3, 0,            0, StrResume,    &ParDispStr}, //11   
    {"  ROTATION ",   0,       0,       1, 1,            0, StrDispRot,   &ParDispStr}, //12
    {"SENSOR FLT ",   0,       0,     255, 0,            0, 0,            &ParDispNumOff}, //13
    {" MENU DOWN ",   0,       0,       1, 0,            0, StrMenuUp,    &ParDispStr}, //14
    {"     INPUT ",   0,       0,       5, 0,            0, 0,            0}, //15   
    {" CALIBRATE ",   0,       0,       0, 0,            0, 0,            0}, //16
    {" INST.INFO ",   0,       0,       0, 0,            0, 0,            0}, //17
    {" TEMP.STEP ",   1,       1,      25, 0,            0, 0,            &ParDispTemp}, //18
    {" DISP.STEP ",   1,       1,       5, 0,            0, 0,            &ParDispDeg}, //19
    {"  ACT.IRON ",   0,       0,       1, 0,            0, 0,            0}, //20 not in menu
    {"  ALT.TEMP ", 150, MINTEMP, MAXTEMP, 0,            0, 0,            0}, //21 not in menu
    {"        T4 ", 200, MINTEMP, MAXTEMP, 0,            0, 0,            &ParDispTemp}, //22
    {"   VERSION ",   0,       0,       0, 0,            0, 0,            0}, //23
};

void ParDispStr(int par, int col, int row, int num){
    OLEDPrint816(col, row, ParDef[par].Strings[num], 0);
}

void ParDispNum(int par, int col, int row, int num){
    OLEDPrintNum816(col, row, 3, num);
    if(ParDef[par].Suffix) OLEDPrint816(col + 24, row, ParDef[par].Suffix, 0);
}

void ParDispNumOff(int par, int col, int row, int num){
    if(num){
        ParDispNum(par, col, row, num);
    }
    else{
        OLEDPrint816(col, row, StrOffOnAuto[0], 0);
    }
}

void ParDispCF(int par, int col, int row, int num){
    OLEDWrite(col, 4, row, (void *)degrees4x16, 8);
    OLEDPrint816(col + 4, row, num ? "F" : "C", 0);
}

// Plain number of degrees followed by the degree sign and C/F (used for DISP.STEP)
void ParDispDeg(int par, int col, int row, int num){
    ParDispNum(par, col, row, num);
    ParDispCF(par, col + 24, row, pars.Deg);
}

void ParDispTemp(int par, int col, int row, int temp){
    if(pars.Deg){
        ParDispNum(par, col, row, ((temp * 461) >> 7) + 32);
    }
    else{
        ParDispNum(par, col, row, temp * 2);
    }
    ParDispCF(par, col + 24, row, pars.Deg);
}

void LoadPars(void)
{
    int i;
    UINT8 b,oldb;

    EEPRead(0, (UINT8 *)&pars, sizeof(pars));
    // Settings added after the original firmware: on a station that has never
    // stored them, their EEPROM bytes hold junk (usually 0xFF). Repair each one
    // on its own here, so the check below does not reset ALL settings.
    for(i = FIRST_CUSTOM_PAR; i < sizeof(pars.b); i++){
        if((pars.b[i] < ParDef[i].Min) || (pars.b[i] > ParDef[i].Max)){
            pars.b[i] = ParDef[i].Default;
        }
    }
    for(i = 0; i < sizeof(pars.b); i++){
        if((pars.b[i] < ParDef[i].Min) || (pars.b[i] > ParDef[i].Max)){
            for(i = 0; i < sizeof(pars.b); i++){
                pars.b[i] = ParDef[i].Default;
            }
            break;
        }
    }

    TTemp = 150;
    oldb = EEPRead(64 + 63, 0, 1);
    for(i = 0; i < 64; i++){
        b = EEPRead(i + 64, 0, 1);
        if((oldb == 0xFF) && (b >= MINTEMP) && (b <= MAXTEMP)){
            TTemp = b;
            break;
        }
        oldb = b;
    }
}

void SavePars(void)
{
    int i;
    UINT8 b, oldb;

    for(i = 0; i < sizeof(pars.b); i++){
        if(EEPRead(i, 0 ,1) != pars.b[i]) EEPWriteImm(i, pars.b[i]);
    }
    
    oldb = EEPRead(64 + 63, 0, 1);
    for(i = 0; i < 64; i++){
        b = EEPRead(64 + i, 0, 1);
        if((oldb == 0xFF) && (b >= MINTEMP) && (b <= MAXTEMP)) break;
        oldb = b;
    }
    i &= 63;
    b = EEPRead(64 + i, 0, 1);
    if(b != TTemp){
        EEPWriteImm(i + 64, 0xFF);
        EEPWriteImm(((i + 1) & 63) + 64, TTemp);
    }
}

#undef _PARS_C

