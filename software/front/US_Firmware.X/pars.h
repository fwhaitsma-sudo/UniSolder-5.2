/* 
 * File:   pars.h
 * Author: Sparky
 *
 * Created on ??????????, 2014, ?????? 25, 20:49
 */

#ifndef PARS_H
#define	PARS_H

#include <GenericTypeDefs.h>

#ifdef	__cplusplus
extern "C" {
#endif

typedef union {
    struct __PACKED {
        UINT8 RTemp[3];                             //Reset temperatures
        UINT8 HTemp;                                //Holder temperature
        UINT8 SlTemp;                               //Sleep temperature
        UINT8 SlTime;                               //Sleep Time (0(off) - 240 seconds(holder with sensor)/minutes(holder without sensor))
        UINT8 STBTime;                              //StandBy Time (0(off) - 240 minutes)
        UINT8 Holder;                               //Holder sensor (0(off), 1(on), 2(auto)
        UINT8 Deg;                                  //Celsius/Farenheit (0-Celsous), 1-Farenheit))
        UINT8 Bri;                                  //display brightness (1-16)
        UINT8 Buttons;                              //Buttons swap (0 - +/-, 1 - -/+)
        UINT8 WakeUp;                               //Wake up from standby (0-off, 1-long key press, 2-holder sensor, 3-key or sensor)
        UINT8 DispRot;                              //Display rotation (0 - 0deg, 1 - 180deg)
        UINT8 NapFilterTicks;                       //Holder/stand sensor filter ticks
        UINT8 MenuDown;                             //Menu down key (0 - key+, 1 - key-)
        UINT8 Input;                                //Input type (0 - keys, 1 - rotary encoder)
        UINT8 Cal;
        UINT8 Debug;
        UINT8 TempStep;                             //temperature step in degrees C (1-50)
        UINT8 DispStep;                             //displayed temperature resolution (1-5 degrees C/F)
        UINT8 ActIron;                              //two-iron adapter: active iron (0 - iron 1, 1 - iron 2)
        UINT8 TTempAlt;                             //two-iron adapter: set temperature of the other iron
        UINT8 RTemp4;                               //fourth reset temperature (preset 4)
        UINT8 RTempMem[4];                          //per preset: last temperature before it was set to OFF (restored when turned on again)
    };
    UINT8 b[27];
}pars_t;

typedef struct {
    const char * Name;
    UINT8 Default;
    UINT8 Min;
    UINT8 Max;
    UINT8 Immediate;
    const char * Suffix;
    const char ** Strings;    
    void (*OLEDDispFunc)(int, int, int, int);
}t_ParDef;

#define FIRST_CUSTOM_PAR   19   //settings from here on were added after the original firmware

#define NAME_LEN           10   //characters in the name shown at power-up
#define NAME_EEP_ADDR      32   //EEPROM: marker byte, then NAME_LEN characters and a 0 (free area between the settings and the temperature log)
#define NAME_EEP_MARK      0xA5

#define PIN_LEN            4    //digits in the menu pincode
#define PIN_EEP_ADDR       44   //EEPROM: marker byte, then PIN_LEN digits (as characters) and a 0
#define PIN_EEP_MARK       0x5A

#define TFINE_EEP_ADDR     50   //EEPROM: bit 0 = the set temperature is odd, bit 1 = the other iron's temperature is odd (stored values are in units of 2 C)

#define NB_OF_MENU_PARAMS  (sizeof(MenuOrder) / sizeof(MenuOrder[0])

#ifndef _PARS_C
extern const char MenuOrder[23];
extern const t_ParDef ParDef[30];
extern char UserName[NAME_LEN + 1];  //name shown at power-up, empty = show the logo
extern void SaveName(void);
extern char UserPin[PIN_LEN + 1];    //menu pincode, empty = no pincode
extern void SavePin(void);
extern UINT8 TAltOdd;                //two-iron adapter: the other iron's set temperature is odd (pars.TTempAlt holds it in units of 2 C)
extern void LoadPars(void);
extern void SavePars();
#endif

#ifdef	__cplusplus
}
#endif

#endif	/* PARS_H */

