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
void ParDispPreset(int par, int col, int row, int temp);
void ParDispCF(int par, int col, int row, int num);
void ParDispDeg(int par, int col, int row, int num);

const char * StrDispRot[]   = {"0   ", "180 "};
const char * StrResume[]    = {"OFF ", "KEY ", "HOLD", "BOTH"};
const char * StrButtons[]   = {"+/- ", "-/+ "};
const char * StrOffOnAuto[] = {"OFF ", "ON  ", "AUTO"};
const char * StrMenuUp[]    = {"KEY+", "KEY-"};

const unsigned char MenuOrder[] = {18,19,0,1,2,22,3,4,5,6,7,11,13,8,10,14,9,12,16,17,28,27};

char UserName[NAME_LEN + 1];

const t_ParDef ParDef[] = {
//  NAME            DEF  MIN      MAX      IMMEDIATE SUFFIX STRINGS       DISPFUNC    

    {"        T1 ", 125,       0, MAXTEMP, 0,            0, 0,            &ParDispPreset}, //0
    {"        T2 ", 150,       0, MAXTEMP, 0,            0, 0,            &ParDispPreset}, //1
    {"        T3 ", 175,       0, MAXTEMP, 0,            0, 0,            &ParDispPreset}, //2
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
    {"        T4 ", 200,       0, MAXTEMP, 0,            0, 0,            &ParDispPreset}, //22
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //23 not in menu: T1 memory
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //24 not in menu: T2 memory
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //25 not in menu: T3 memory
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //26 not in menu: T4 memory
    {"   VERSION ",   0,       0,       0, 0,            0, 0,            0}, //27
    {"      NAME ",   0,       0,       0, 0,            0, 0,            0}, //28 name shown at power-up
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

//preset: 0 means OFF (the slot is skipped when cycling the presets)
void ParDispPreset(int par, int col, int row, int temp){
    if(temp == 0){
        OLEDPrint816(col, row, StrOffOnAuto[0], 0);
    }
    else{
        ParDispTemp(par, col, row, temp);
    }
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

//Reads the power-up name from EEPROM. Without the marker byte, or with anything
//that is not printable text, the name is empty and the logo is shown.
static void LoadName(void)
{
    int i;
    UINT8 mark = EEPRead(NAME_EEP_ADDR, 0, 1);

    for(i = 0; i <= NAME_LEN; i++) UserName[i] = 0;
    if(mark != NAME_EEP_MARK) return;
    EEPRead(NAME_EEP_ADDR + 1, (UINT8 *)UserName, NAME_LEN + 1);
    UserName[NAME_LEN] = 0;
    for(i = 0; i < NAME_LEN; i++){
        if((UserName[i] < 32) || (UserName[i] > 126)){
            for(; i <= NAME_LEN; i++) UserName[i] = 0;
            break;
        }
    }
}

//Writes the power-up name to EEPROM at once (only the bytes that changed).
void SaveName(void)
{
    int i;

    if(EEPRead(NAME_EEP_ADDR, 0, 1) != NAME_EEP_MARK) EEPWriteImm(NAME_EEP_ADDR, NAME_EEP_MARK);
    for(i = 0; i <= NAME_LEN; i++){
        if(EEPRead(NAME_EEP_ADDR + 1 + i, 0, 1) != (UINT8)UserName[i]) EEPWriteImm(NAME_EEP_ADDR + 1 + i, UserName[i]);
    }
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

    LoadName();

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

