#define _PARS_C

#include "main.h"
#include "OLED.h"
#include "pars.h"
#include "isr.h"
#include "EEP.h"
#include <string.h>

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

const unsigned char MenuOrder[] = {18,19,0,1,2,22,3,4,5,6,7,11,13,8,10,14,9,12,28,27,16,17,30,31,29};

char UserName[NAME_LEN + 1];
char UserPin[PIN_LEN + 1];
UINT8 TAltOdd;
UINT16 ParOdd;

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
    {" TEMP.STEP ",   2,       1,      50, 0,            0, 0,            &ParDispDeg}, //18
    {" DISP.STEP ",   1,       1,       5, 0,            0, 0,            &ParDispDeg}, //19
    {"  ACT.IRON ",   0,       0,       1, 0,            0, 0,            0}, //20 not in menu
    {"  ALT.TEMP ", 150, MINTEMP, MAXTEMP, 0,            0, 0,            0}, //21 not in menu
    {"        T4 ", 200,       0, MAXTEMP, 0,            0, 0,            &ParDispPreset}, //22
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //23 not in menu: T1 memory
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //24 not in menu: T2 memory
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //25 not in menu: T3 memory
    {"   MEMORY  ",   0,       0, MAXTEMP, 0,            0, 0,            0}, //26 not in menu: T4 memory
    {" SND PITCH ",   7,       1,       9, 1,            0, 0,            &ParDispNum}, //27 beep pitch
    {"     SOUND ",   1,       0,       1, 0,            0, StrOffOnAuto, &ParDispStr}, //28 sounds on/off
    {"   VERSION ",   0,       0,       0, 0,            0, 0,            0}, //29
    {"      NAME ",   0,       0,       0, 0,            0, 0,            0}, //30 name shown at power-up
    {"   PINCODE ",   0,       0,       0, 0,            0, 0,            0}, //31 menu pincode
};

//All values are right-aligned to the right edge of the display (the col argument is not used).
#define VAL_RIGHT 128

UINT8 ParShowUnit;      //1 while the value being edited is drawn: temperatures then show their unit

//degree sign and C/F: 12 pixels
static void ParUnitCF(int row, int deg){
    OLEDWrite(VAL_RIGHT - 12, 4, row, (void *)degrees4x16, 8);
    OLEDPrint816(VAL_RIGHT - 8, row, deg ? "F" : "C", 0);
}

//number of up to 3 digits; for temperatures the unit only follows it while editing
static void ParNumUnit(int row, int val, int withUnit){
    OLEDPrintNum816(VAL_RIGHT - 24 - (withUnit ? 12 : 0), row, 3, val);
    if(withUnit) ParUnitCF(row, pars.Deg);
}

void ParDispStr(int par, int col, int row, int num){
    const char * s = ParDef[par].Strings[num];
    int n = strlen(s);
    while(n > 0 && s[n - 1] == ' ') n--;     //the table pads the texts to 4 characters
    OLEDPrint816(VAL_RIGHT - 8 * n, row, s, n);
}

void ParDispNum(int par, int col, int row, int num){
    int w = 24;
    if(ParDef[par].Suffix) w += 8;
    OLEDPrintNum816(VAL_RIGHT - w, row, 3, num);
    if(ParDef[par].Suffix) OLEDPrint816(VAL_RIGHT - 8, row, ParDef[par].Suffix, 0);
}

void ParDispNumOff(int par, int col, int row, int num){
    if(num){
        ParDispNum(par, col, row, num);
    }
    else{
        OLEDPrint816(VAL_RIGHT - 24, row, "OFF", 3);
    }
}

//the DEGREES setting itself: C or F
void ParDispCF(int par, int col, int row, int num){
    ParUnitCF(row, num);
}

// Plain number of degrees (used for TEMP.STEP and DISP.STEP); the unit shows while editing
void ParDispDeg(int par, int col, int row, int num){
    ParNumUnit(row, num, ParShowUnit);
}

//preset: 0 means OFF (the slot is skipped when cycling the presets)
void ParDispPreset(int par, int col, int row, int temp){
    if(temp == 0){
        OLEDPrint816(VAL_RIGHT - 24, row, "OFF", 3);
    }
    else{
        ParDispTemp(par, col, row, temp);
    }
}

//temp is in degrees C
void ParDispTemp(int par, int col, int row, int temp){
    ParNumUnit(row, pars.Deg ? (((temp * 461) >> 8) + 32) : temp, ParShowUnit);
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

//Reads the menu pincode from EEPROM. Without the marker byte, or with anything
//other than exactly PIN_LEN digits, there is no pincode.
static void LoadPin(void)
{
    int i;

    for(i = 0; i <= PIN_LEN; i++) UserPin[i] = 0;
    if(EEPRead(PIN_EEP_ADDR, 0, 1) != PIN_EEP_MARK) return;
    EEPRead(PIN_EEP_ADDR + 1, (UINT8 *)UserPin, PIN_LEN + 1);
    UserPin[PIN_LEN] = 0;
    for(i = 0; i < PIN_LEN; i++){
        if((UserPin[i] < '0') || (UserPin[i] > '9')){
            for(i = 0; i <= PIN_LEN; i++) UserPin[i] = 0;
            return;
        }
    }
}

//Writes the menu pincode to EEPROM at once (only the bytes that changed).
void SavePin(void)
{
    int i;

    if(EEPRead(PIN_EEP_ADDR, 0, 1) != PIN_EEP_MARK) EEPWriteImm(PIN_EEP_ADDR, PIN_EEP_MARK);
    for(i = 0; i <= PIN_LEN; i++){
        if(EEPRead(PIN_EEP_ADDR + 1 + i, 0, 1) != (UINT8)UserPin[i]) EEPWriteImm(PIN_EEP_ADDR + 1 + i, UserPin[i]);
    }
}

//Writes the odd degrees of T1-T4, HOLDER T and SLEEP T to EEPROM at once (only the bytes that changed).
void SaveParOdd(void)
{
    if(EEPRead(PAROD_EEP_ADDR, 0, 1) != PAROD_EEP_MARK) EEPWriteImm(PAROD_EEP_ADDR, PAROD_EEP_MARK);
    if(EEPRead(PAROD_EEP_ADDR + 1, 0, 1) != (UINT8)(ParOdd & 0xFF)) EEPWriteImm(PAROD_EEP_ADDR + 1, ParOdd & 0xFF);
    if(EEPRead(PAROD_EEP_ADDR + 2, 0, 1) != (UINT8)(ParOdd >> 8)) EEPWriteImm(PAROD_EEP_ADDR + 2, ParOdd >> 8);
}

void LoadPars(void)
{
    int i;
    int reset = 0;
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
            reset = 1;
            break;
        }
    }

    LoadName();
    LoadPin();

    //odd degree of T1-T4, HOLDER T, SLEEP T and the OFF memories; only valid on a stored value below the maximum
    ParOdd = 0;
    if(!reset && (EEPRead(PAROD_EEP_ADDR, 0, 1) == PAROD_EEP_MARK)){
        static const UINT8 fineIdx[6] = {0, 1, 2, 22, 3, 4};
        ParOdd = EEPRead(PAROD_EEP_ADDR + 1, 0, 1) | (EEPRead(PAROD_EEP_ADDR + 2, 0, 1) << 8);
        ParOdd &= 0x0F3F;
        for(i = 0; i < 6; i++){
            if((pars.b[fineIdx[i]] == 0) || (pars.b[fineIdx[i]] >= ParDef[fineIdx[i]].Max)) ParOdd &= ~(1 << i);
        }
        for(i = 0; i < 4; i++){
            if((pars.RTempMem[i] == 0) || (pars.RTempMem[i] >= MAXTEMP)) ParOdd &= ~(1 << (8 + i));
        }
    }

    TTemp = 300;                                    //degrees C; the log holds it in units of 2 C
    oldb = EEPRead(64 + 63, 0, 1);
    for(i = 0; i < 64; i++){
        b = EEPRead(i + 64, 0, 1);
        if((oldb == 0xFF) && (b >= MINTEMP) && (b <= MAXTEMP)){
            TTemp = b * 2;
            break;
        }
        oldb = b;
    }
    //the odd degree of the set temperatures is kept in its own byte
    b = EEPRead(TFINE_EEP_ADDR, 0, 1);
    if(b > 3) b = 0;
    TTemp += b & 1;
    TAltOdd = (b >> 1) & 1;
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
    if(b != (UINT8)(TTemp >> 1)){
        EEPWriteImm(i + 64, 0xFF);
        EEPWriteImm(((i + 1) & 63) + 64, TTemp >> 1);
    }
    b = (TTemp & 1) | ((TAltOdd & 1) << 1);
    if(EEPRead(TFINE_EEP_ADDR, 0, 1) != b) EEPWriteImm(TFINE_EEP_ADDR, b);
}

#undef _PARS_C

