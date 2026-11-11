#define _IO_C

#include <xc.h>
#include "typedefs.h"
#include "mcu.h"
#include "io.h"
#include "pars.h"
#include "main.h"
#include "PID.h"
#include "isr.h"
#include "iron.h"
#include "menu.h"
#include "EEP.h"
#include "usb/usb.h"
#include "usb/usb_driver.h"
#include "usb/usb_function_hid.h"

//#define TXP (*((USBPacket *)USBTxBuffer))
//#define RXP (*((USBPacket *)USBRxBuffer))

//#define IO_IDLE 0
//#define IO_BUSY 1

static unsigned int IO_TICKS;
static unsigned int IO_BUSY;

static unsigned int BuffPos;

void ProcessIO();

//---- helpers for the remote control and settings backup commands ----
#define REMOTE_KEY0     0x5A        //two key bytes are needed to write the settings or restart
#define REMOTE_KEY1     0xA5
#define SETTINGS_SIZE   64          //EEPROM bytes 0-63: settings, name, odd degrees (the log from 64 on is not part of it)
#define SETTINGS_CHUNK  48          //most bytes per read or write command

static void Put16(int i, unsigned int v){
    TXP.Data[i] = v & 0xFF;
    TXP.Data[i + 1] = (v >> 8) & 0xFF;
}

//the menu pincode is neither read nor written by the settings commands
static int InPinArea(int a){
    return (a >= PIN_EEP_ADDR) && (a <= PIN_EEP_ADDR + PIN_LEN + 1);
}

void IOInit(){
    IO_TICKS = ISRTicks;
    IO_BUSY = 1;
    USBDriverInit();
    RXP.Command = 0;
}

void IOTasks(){
    USBDeviceTasks();
    if((USBDeviceState < CONFIGURED_STATE)||(USBSuspendControl==1)) return;
    ProcessIO();   // This is where all the actual bootloader related data transfer/self programming takes place
}

void ProcessIO(){
    static UINT16 _IronID=0; 

    if(!IO_BUSY && !HIDTxHandleBusy(USBInHandle)){
        if(_IronID != IronID){
            _IronID = IronID;
            TXP.Command = 1;
            TXP.Data16[0] = IronID;
            USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
        }
        else if(IO_TICKS != ADCStep && !HIDTxHandleBusy(USBInHandle)){
            IO_TICKS = ADCStep;
            if(IO_TICKS & 1){
                BuffPos++;
                if(BuffPos>127){
                    BuffPos &= 127;
                }
                TXP.Command=3;
                TXP.LiveData.Ticks=IO_TICKS;
                TXP.LiveData.CTTemp=CTTemp >> 1;                                 //
                TXP.LiveData.CTemp=PIDVars[0].CTemp[0];                                //
                TXP.LiveData.ADCTemp=PIDVars[0].ADCTemp[0];                            //
                TXP.LiveData.TAvgF=PIDVars[0].TAvgF[0] >> ADCAVG;                        //
                TXP.LiveData.CHRes=PIDVars[0].HRAvg >> ADCAVG;// (PIDVars[0].Delta[0]>>4)+250;//HRAvg>>ADCAVG;
                TXP.LiveData.TAvgP=PIDVars[0].TAvgP[0];
                TXP.LiveData.Heater=PHEATER;
                TXP.LiveData.WSDelta[0] = PIDVars[0].WSDelta[0].val + 2048;                //
                TXP.LiveData.WSDelta[1] = PIDVars[0].WSDelta[1].val + 2048;   //
                TXP.LiveData.WSDelta[2] = PIDVars[0].WSDelta[2].val + 2048;   //
                TXP.LiveData.WSDelta[3] = PIDVars[0].WSDelta[3].val + 2048;   //
                TXP.LiveData.WSDelta[4] = PIDVars[0].WSDelta[4].val + 2048;   //
                TXP.LiveData.WSDelta[5] = PIDVars[0].WSDelta[5].val + 2048;   //
                TXP.LiveData.WSDelta[6] = PIDVars[0].WSDelta[6].val + 2048;   //
                TXP.LiveData.WSDelta[7] = PIDVars[0].WSDelta[7].val + 2048;   //
                TXP.LiveData.DestinationReached=PIDVars[0].DestinationReached;         //
                TXP.LiveData.Duty = (UINT16)(PIDVars[0].PIDDuty>>8);                       //
                USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
            }
        }        
    }
    else {
        if(!HIDRxHandleBusy(USBOutHandle)){
            IO_BUSY = 1;
            switch(RXP.Command){
                case 0x81: //jump to bootloader                    
                    mcuJumpToBootLoader();
                    break;
                case 0x61: //get operating mode
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command=0x61;
                        TXP.OpMode=1;
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY=0;
                    }
                    break;
                case 2: //Query device
                    BeepTicks=5;
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command=2;
                        TXP.QueryDev.PacketDataFieldSize = 64;
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        BeepTicks=20;
                        InvertTicks=20;
                        IO_BUSY=0;
                    }
                    break;
                case 3: //Set current iron PID parameters
                    IronPars.Config[0].SensorConfig.Gain = RXP.IronPars.Gain;
                    IronPars.Config[0].PID_KP = RXP.IronPars.PID_KP;
                    IronPars.Config[0].PID_KI = RXP.IronPars.PID_KI;
                    IronPars.Config[0].PID_DGain = RXP.IronPars.PID_DGain;
                    IronPars.Config[0].PID_OVSGain = RXP.IronPars.PID_OVSGain;
                    IO_BUSY = 0;
                    break;
                case 4: //Get current iron PID parameters
                    if(!HIDTxHandleBusy(USBInHandle)){
                        BeepTicks=20;
                        TXP.Command=4;
                        TXP.IronPars.Gain = IronPars.Config[0].SensorConfig.Gain;
                        TXP.IronPars.Offset = IronPars.Config[0].SensorConfig.Offset;
                        TXP.IronPars.PID_KP = IronPars.Config[0].PID_KP;
                        TXP.IronPars.PID_KI = IronPars.Config[0].PID_KI;
                        TXP.IronPars.PID_DGain = IronPars.Config[0].PID_DGain;
                        TXP.IronPars.PID_OVSGain = IronPars.Config[0].PID_OVSGain;
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x80: //application info: [version minor, version major, board version, protocol (1 = remote control + settings)]
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x80;
                        TXP.Data[0] = VERSION_MINOR;
                        TXP.Data[1] = VERSION_MAJOR;
                        TXP.Data[2] = BoardVersion;
                        TXP.Data[3] = 2;
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x10: //get state
                    if(!HIDTxHandleBusy(USBInHandle)){
                        RemoteState rs;
                        RemoteGetState(&rs);
                        TXP.Command = 0x10;
                        Put16(0, rs.Set);
                        Put16(2, rs.Target);
                        Put16(4, rs.Preset[0]);
                        Put16(6, rs.Preset[1]);
                        Put16(8, rs.Preset[2]);
                        Put16(10, rs.Preset[3]);
                        Put16(12, rs.Holder);
                        Put16(14, rs.Sleep);
                        Put16(16, MINTEMP * 2);
                        Put16(18, MAXTEMP * 2);
                        TXP.Data[20] = rs.Mode;
                        TXP.Data[21] = rs.Flags;
                        TXP.Data[22] = rs.ActIron;
                        TXP.Data[23] = pars.TempStep;
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x11: //set temperature (degrees C, little endian); replies with the temperature that was set
                    if(!HIDTxHandleBusy(USBInHandle)){
                        unsigned int t = RXP.Data[0] | (RXP.Data[1] << 8);
                        TXP.Command = 0x11;
                        Put16(0, RemoteSetTemp(t));
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x12: //select preset T1-T4 (0-3)
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x12;
                        Put16(0, RemotePreset(RXP.Data[0]));
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x13: //sleep (1) or wake up (0); replies with 1 while sleeping
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x13;
                        TXP.Data[0] = RemoteSleep(RXP.Data[0] ? 1 : 0);
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x14: //two-iron adapter: switch iron; replies with the active iron
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x14;
                        TXP.Data[0] = RemoteSwitchIron();
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x15: //save the PID values in use for the connected iron in the EEPROM; replies [status 0 ok / 1 no iron / 2 out of range / 3 full]
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x15;
                        TXP.Data[0] = IronPidSave();
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x16: //restore the standard PID values of the connected iron (forget saved ones); replies [status 0 saved values forgotten / 1 no iron / 2 none were saved, standard loaded anyway]
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x16;
                        TXP.Data[0] = IronPidClear();
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x17: //are PID values saved for the connected iron; replies [1 yes / 0 no]
                    if(!HIDTxHandleBusy(USBInHandle)){
                        TXP.Command = 0x17;
                        TXP.Data[0] = IronPidIsSaved();
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x20: //read settings: [address, length]; replies [address, length, status, data...] (status 2 = bad range)
                    if(!HIDTxHandleBusy(USBInHandle)){
                        int a = RXP.Data[0];
                        int n = RXP.Data[1];
                        int i;
                        TXP.Command = 0x20;
                        TXP.Data[0] = a;
                        if(n < 1 || n > SETTINGS_CHUNK || a + n > SETTINGS_SIZE){
                            TXP.Data[1] = 0;
                            TXP.Data[2] = 2;
                        }
                        else{
                            TXP.Data[1] = n;
                            TXP.Data[2] = 0;
                            EEPRead(a, (UINT8 *)&TXP.Data[3], n);
                            for(i = 0; i < n; i++) if(InPinArea(a + i)) TXP.Data[3 + i] = 0;
                        }
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x21: //write settings: [key 0x5A 0xA5, address, length, data...]; replies [status] (0 ok, 1 bad key, 2 bad range)
                    if(!HIDTxHandleBusy(USBInHandle)){
                        int a = RXP.Data[2];
                        int n = RXP.Data[3];
                        int i;
                        TXP.Command = 0x21;
                        if(RXP.Data[0] != REMOTE_KEY0 || RXP.Data[1] != REMOTE_KEY1){
                            TXP.Data[0] = 1;
                        }
                        else if(n < 1 || n > SETTINGS_CHUNK || a + n > SETTINGS_SIZE){
                            TXP.Data[0] = 2;
                        }
                        else{
                            for(i = 0; i < n; i++){
                                UINT8 v = RXP.Data[4 + i];
                                if(!InPinArea(a + i) && (EEPRead(a + i, 0, 1) != v)) EEPWriteImm(a + i, v);
                            }
                            TXP.Data[0] = 0;
                        }
                        USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
                        IO_BUSY = 0;
                    }
                    break;
                case 0x22: //restart the station so that restored settings are loaded: [key 0x5A 0xA5]
                    if(RXP.Data[0] == REMOTE_KEY0 && RXP.Data[1] == REMOTE_KEY1){
                        _delay_ms(50);  //let the last EEPROM write finish
                        mcuReset();
                    }
                    IO_BUSY = 0;
                    break;
                default:
                    IO_BUSY = 0;
                    break;
            }
            if(!IO_BUSY) USBOutHandle = HIDRxPacket(HID_EP, (BYTE *)&RXP, 64);
        }
    }
}

#undef _IO_C
