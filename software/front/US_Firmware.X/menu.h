/* 
 * File:   menu.h
 * Author: Sparky
 *
 * Created on ???????, 2013, ??? 2, 23:27
 */

#ifndef MENU_H
#define	MENU_H

#ifdef	__cplusplus
extern "C" {
#endif

/*State machine modes for menu*/
typedef enum{
    DEFAULT_MENU,         /*display temperature and do nothing*/
    SET_TEMPERATURE,      /*set temperature*/
    RESET_TEMPERATURE,    /*reset temperature*/
    MENU,                 /*in menu mode*/
    SET_PARAMS,           /*set parameters*/
    CALIBRATION,          /*calibration*/
    INSTRUMENT_INFO,      /*instrument info (debug)*/
    SET_INPUT_TYPE,       /*input selection*/
    TIP_CHANGE,           /*tip change*/
    VERSION_INFO,         /*version information*/
    NAME_EDIT,            /*edit the name shown at power-up*/
    PIN_ENTRY,            /*enter the pincode to open the menu*/
    PIN_EDIT,             /*set or clear the pincode*/
    STANDBY = 0xFF        /*standby*/
}T_MENU_MODE;

/*Remote control over USB (phone or PC): runs in the main loop like the knob, with the same limits*/
typedef struct {
    unsigned short Set;         /*set temperature, degrees C*/
    unsigned short Target;      /*temperature the heater regulates to now, degrees C (0 = heater off: sleep or standby)*/
    unsigned short Preset[4];   /*T1-T4, degrees C, 0 = OFF*/
    unsigned short Holder;      /*HOLDER T, degrees C*/
    unsigned short Sleep;       /*SLEEP T, degrees C*/
    unsigned char Mode;         /*T_MENU_MODE*/
    unsigned char Flags;        /*bit 0 manual sleep, bit 1 standby, bit 2 two-iron adapter, bit 3 iron on its stand, bit 4 Fahrenheit display*/
    unsigned char ActIron;      /*two-iron adapter: active iron (0 or 1)*/
}RemoteState;
extern void RemoteGetState(RemoteState * s);
extern unsigned int RemoteSetTemp(unsigned int t);     /*clamped to the knob limits; returns the set temperature*/
extern unsigned int RemotePreset(int i);               /*select T1-T4 (0-3) unless it is OFF; returns the set temperature*/
extern int RemoteSleep(int sleep);                     /*1 = manual sleep, 0 = wake up; returns 1 while sleeping*/
extern int RemoteSwitchIron(void);                     /*two-iron adapter: switch iron; returns the active iron*/

extern void MenuInit();
extern void MenuTasks(int powerLost);
extern void SoundTick(void);      //call every 20 ms: plays the beeps

#ifdef	__cplusplus
}
#endif

#endif	/* MENU_H */

