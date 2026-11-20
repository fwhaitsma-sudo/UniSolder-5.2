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
#include <plib.h>
#include <sys/attribs.h>
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

static volatile USBPacket *cTX;
static void Put16(int i, unsigned int v){
    cTX->Data[i] = v & 0xFF;
    cTX->Data[i + 1] = (v >> 8) & 0xFF;
}

//the menu pincode is neither read nor written by the settings commands
static int InPinArea(int a){
    return (a >= PIN_EEP_ADDR) && (a <= PIN_EEP_ADDR + PIN_LEN + 1);
}

static void FillLive(volatile USBPacket *p, unsigned int ticks){
    p->LiveData.Ticks=ticks;
    p->LiveData.CTTemp=CTTemp >> 1;                                 //
    p->LiveData.CTemp=PIDVars[0].CTemp[0];                                //
    p->LiveData.ADCTemp=PIDVars[0].ADCTemp[0];                            //
    p->LiveData.TAvgF=PIDVars[0].TAvgF[0] >> ADCAVG;                        //
    p->LiveData.CHRes=PIDVars[0].HRAvg >> ADCAVG;// (PIDVars[0].Delta[0]>>4)+250;//HRAvg>>ADCAVG;
    p->LiveData.TAvgP=PIDVars[0].TAvgP[0];
    p->LiveData.Heater=PHEATER;
    p->LiveData.WSDelta[0] = PIDVars[0].WSDelta[0].val + 2048;                //
    p->LiveData.WSDelta[1] = PIDVars[0].WSDelta[1].val + 2048;   //
    p->LiveData.WSDelta[2] = PIDVars[0].WSDelta[2].val + 2048;   //
    p->LiveData.WSDelta[3] = PIDVars[0].WSDelta[3].val + 2048;   //
    p->LiveData.WSDelta[4] = PIDVars[0].WSDelta[4].val + 2048;   //
    p->LiveData.WSDelta[5] = PIDVars[0].WSDelta[5].val + 2048;   //
    p->LiveData.WSDelta[6] = PIDVars[0].WSDelta[6].val + 2048;   //
    p->LiveData.WSDelta[7] = PIDVars[0].WSDelta[7].val + 2048;   //
    p->LiveData.DestinationReached=PIDVars[0].DestinationReached;         //
    p->LiveData.Duty = (UINT16)(PIDVars[0].PIDDuty>>8);                       //
}

//Runs one received command; returns 1 when finished, 0 when it has to be retried (transmit buffer busy).
//cRX/cTX/cUart select the channel: USB (HID packets) or the UART link.
static volatile USBPacket *cRX;
static int cUart;

static int TxBusy(void);
static void TxSend(void);

static int HandleCommand(void){
    int done = 0;
        switch(cRX->Command){
            case 0x81: //jump to bootloader                    
                if(!cUart) mcuJumpToBootLoader();   //never over the wireless link
                done = 1;
                break;
            case 0x61: //get operating mode
                if(!TxBusy()){
                    cTX->Command=0x61;
                    cTX->OpMode=1;
                    TxSend();
                    done = 1;
                }
                break;
            case 2: //Query device
                BeepTicks=5;
                if(!TxBusy()){
                    cTX->Command=2;
                    cTX->QueryDev.PacketDataFieldSize = 64;
                    TxSend();
                    BeepTicks=20;
                    InvertTicks=20;
                    done = 1;
                }
                break;
            case 3: //Set current iron PID parameters
                IronPars.Config[0].SensorConfig.Gain = cRX->IronPars.Gain;
                IronPars.Config[0].PID_KP = cRX->IronPars.PID_KP;
                IronPars.Config[0].PID_KI = cRX->IronPars.PID_KI;
                IronPars.Config[0].PID_DGain = cRX->IronPars.PID_DGain;
                IronPars.Config[0].PID_OVSGain = cRX->IronPars.PID_OVSGain;
                done = 1;
                break;
            case 4: //Get current iron PID parameters
                if(!TxBusy()){
                    BeepTicks=20;
                    cTX->Command=4;
                    cTX->IronPars.Gain = IronPars.Config[0].SensorConfig.Gain;
                    cTX->IronPars.Offset = IronPars.Config[0].SensorConfig.Offset;
                    cTX->IronPars.PID_KP = IronPars.Config[0].PID_KP;
                    cTX->IronPars.PID_KI = IronPars.Config[0].PID_KI;
                    cTX->IronPars.PID_DGain = IronPars.Config[0].PID_DGain;
                    cTX->IronPars.PID_OVSGain = IronPars.Config[0].PID_OVSGain;
                    TxSend();
                    done = 1;
                }
                break;
            case 0x80: //application info: [version minor, version major, board version, protocol (1 = remote control + settings, 2 = PID save, 3 = live settings 0x40/0x41, 4 = name 0x42/0x43, 5 = saved PID backup 0x44/0x45, 6 = state flag 32: iron in its stand now, 7 = ramp mode 0x50-0x53, state flag 64, 8 = 13 ramp values (contact detector and capture 0x54/0x55, removed again), 9 = 16 ramp values, profile steps 0x56/0x57, step + lift start in 0x53)]
                if(!TxBusy()){
                    cTX->Command = 0x80;
                    cTX->Data[0] = VERSION_MINOR;
                    cTX->Data[1] = VERSION_MAJOR;
                    cTX->Data[2] = BoardVersion;
                    cTX->Data[3] = 9;
                    TxSend();
                    done = 1;
                }
                break;
            case 0x10: //get state
                if(!TxBusy()){
                    RemoteState rs;
                    RemoteGetState(&rs);
                    cTX->Command = 0x10;
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
                    cTX->Data[20] = rs.Mode;
                    cTX->Data[21] = rs.Flags;
                    cTX->Data[22] = rs.ActIron;
                    cTX->Data[23] = pars.TempStep;
                    TxSend();
                    done = 1;
                }
                break;
            case 0x11: //set temperature (degrees C, little endian); replies with the temperature that was set
                if(!TxBusy()){
                    unsigned int t = cRX->Data[0] | (cRX->Data[1] << 8);
                    cTX->Command = 0x11;
                    Put16(0, RemoteSetTemp(t));
                    TxSend();
                    done = 1;
                }
                break;
            case 0x12: //select preset T1-T4 (0-3)
                if(!TxBusy()){
                    cTX->Command = 0x12;
                    Put16(0, RemotePreset(cRX->Data[0]));
                    TxSend();
                    done = 1;
                }
                break;
            case 0x13: //sleep (1) or wake up (0); replies with 1 while sleeping
                if(!TxBusy()){
                    cTX->Command = 0x13;
                    cTX->Data[0] = RemoteSleep(cRX->Data[0] ? 1 : 0);
                    TxSend();
                    done = 1;
                }
                break;
            case 0x14: //two-iron adapter: switch iron; replies with the active iron
                if(!TxBusy()){
                    cTX->Command = 0x14;
                    cTX->Data[0] = RemoteSwitchIron();
                    TxSend();
                    done = 1;
                }
                break;
            case 0x15: //save the PID values in use for the connected iron in the EEPROM; replies [status 0 ok / 1 no iron / 2 out of range / 3 full]
                if(!TxBusy()){
                    cTX->Command = 0x15;
                    cTX->Data[0] = IronPidSave();
                    TxSend();
                    done = 1;
                }
                break;
            case 0x16: //restore the standard PID values of the connected iron (forget saved ones); replies [status 0 saved values forgotten / 1 no iron / 2 none were saved, standard loaded anyway]
                if(!TxBusy()){
                    cTX->Command = 0x16;
                    cTX->Data[0] = IronPidClear();
                    TxSend();
                    done = 1;
                }
                break;
            case 0x17: //are PID values saved for the connected iron; replies [1 yes / 0 no]
                if(!TxBusy()){
                    cTX->Command = 0x17;
                    cTX->Data[0] = IronPidIsSaved();
                    TxSend();
                    done = 1;
                }
                break;
            case 0x40: //read all settings in menu units; replies 29 x u16 (setting 0..28)
                if(!TxBusy()){
                    int i;
                    cTX->Command = 0x40;
                    for(i = 0; i < 29; i++) Put16(i * 2, RemoteGetParam(i));
                    TxSend();
                    done = 1;
                }
                break;
            case 0x41: //set one setting: [id, value lo, value hi]; replies [id, status, value lo, value hi] (status 0 done, 1 not settable, 2 range, 3 station in menu)
                if(!TxBusy()){
                    int p = cRX->Data[0];
                    int st = RemoteSetParam(p, cRX->Data[1] | (cRX->Data[2] << 8));
                    cTX->Command = 0x41;
                    cTX->Data[0] = p;
                    cTX->Data[1] = st;
                    Put16(2, RemoteGetParam(p));
                    TxSend();
                    done = 1;
                }
                break;
            case 0x42: //read the power-up name; replies NAME_LEN characters padded with spaces
                if(!TxBusy()){
                    int i;
                    char nm[NAME_LEN];
                    RemoteGetName(nm);
                    cTX->Command = 0x42;
                    for(i = 0; i < NAME_LEN; i++) cTX->Data[i] = nm[i];
                    TxSend();
                    done = 1;
                }
                break;
            case 0x43: //set the power-up name [NAME_LEN characters]; replies [status 0 done, 2 bad character, 3 station in menu]
                if(!TxBusy()){
                    cTX->Command = 0x43;
                    cTX->Data[0] = RemoteSetName((const unsigned char *)&cRX->Data[0]);
                    TxSend();
                    done = 1;
                }
                break;
            case 0x44: //read saved PID slot [slot 0-7]; replies [slot, status (0 ok, 2 bad slot), 12 bytes, zeros = empty]
                if(!TxBusy()){
                    UINT8 sb[12];
                    int i;
                    int sl = cRX->Data[0];
                    int st = IronPidSlotRead(sl, sb);
                    cTX->Command = 0x44;
                    cTX->Data[0] = sl;
                    cTX->Data[1] = st;
                    for(i = 0; i < 12; i++) cTX->Data[2 + i] = st ? 0 : sb[i];
                    TxSend();
                    done = 1;
                }
                break;
            case 0x45: //write saved PID slot [key 0x5A 0xA5, slot, 12 bytes]; replies [status] (0 ok, 1 bad key, 2 bad slot or values)
                if(!TxBusy()){
                    UINT8 sb[12];
                    int i;
                    cTX->Command = 0x45;
                    if(cRX->Data[0] != REMOTE_KEY0 || cRX->Data[1] != REMOTE_KEY1){
                        cTX->Data[0] = 1;
                    }
                    else{
                        for(i = 0; i < 12; i++) sb[i] = cRX->Data[3 + i];
                        cTX->Data[0] = IronPidSlotWrite(cRX->Data[2], sb);
                    }
                    TxSend();
                    done = 1;
                }
                break;
            case 0x50: //read the ramp mode parameters; replies [status 0, 16 x u16: idle C, 3 unused (single ramp), lockout s, time-out min, 2 unused (contact), ready limit C,
                       //3 unused (contact), start 0 knob/1 lift, start delay 0.1 s,
                       //maximum rate 0.1 C/s, number of steps] (protocol 7 apps read the first 9, protocol 8 apps the first 13)
                if(!TxBusy()){
                    unsigned int rv[16];
                    int i;
                    RemoteRampGet(rv);
                    cTX->Command = 0x50;
                    cTX->Data[0] = 0;
                    for(i = 0; i < 16; i++) Put16(1 + i * 2, rv[i]);
                    TxSend();
                    done = 1;
                }
                break;
            case 0x51: //set the ramp mode parameters [key 0x5A 0xA5, 16 x u16]; replies [status 0 ok, 1 bad key, 2 out of range, 3 a profile is running]
                       //(values 10-13 all 0 = a protocol 7 app, values 15-16 both 0 = a protocol 8 app: the newer values are kept)
                if(!TxBusy()){
                    unsigned int rv[16];
                    int i;
                    cTX->Command = 0x51;
                    if(cRX->Data[0] != REMOTE_KEY0 || cRX->Data[1] != REMOTE_KEY1){
                        cTX->Data[0] = 1;
                    }
                    else{
                        for(i = 0; i < 16; i++) rv[i] = cRX->Data[2 + i * 2] | (cRX->Data[3 + i * 2] << 8);
                        cTX->Data[0] = RemoteRampSet(rv);
                    }
                    TxSend();
                    done = 1;
                }
                break;
            case 0x52: //ramp mode control [0 leave, 1 enter, 2 trigger, 3 abort]; replies [status 0 done / 1 not possible / 3 in menu, state]
                if(!TxBusy()){
                    unsigned int ro[9];
                    cTX->Command = 0x52;
                    cTX->Data[0] = RemoteRampControl(cRX->Data[0]);
                    RemoteRampStatus(ro);
                    cTX->Data[1] = ro[0];
                    TxSend();
                    done = 1;
                }
                break;
            case 0x53: //ramp mode status; replies [state 0 off/1 idle/2 ramp/3 peak/4 cool/5 lockout, ready, u16 ms left, u16 target C, u16 highest temperature C, u16 actual C,
                       //bytes 10-21 zero (were the contact detector of protocol 8, removed), (protocol 9) step 1-6 (0 = not running), lift start 0 none/1 delay/
                       //2 waiting until ready, u16 ms left of the start delay] (while running, u16 ms left = the whole profile)
                if(!TxBusy()){
                    unsigned int ro[9];
                    int i;
                    RemoteRampStatus(ro);
                    for(i = 10; i < 22; i++) cTX->Data[i] = 0;
                    cTX->Data[22] = ro[6];
                    cTX->Data[23] = ro[7];
                    Put16(24, ro[8]);
                    cTX->Command = 0x53;
                    cTX->Data[0] = ro[0];
                    cTX->Data[1] = ro[4];
                    Put16(2, ro[1]);
                    Put16(4, ro[2]);
                    Put16(6, ro[3]);
                    Put16(8, ro[5]);
                    TxSend();
                    done = 1;
                }
                break;
            case 0x56: //read the profile steps; replies [status 0, number of steps, 6 x (u16 temperature C, u16 rate 0.1 C/s, u16 hold 0.1 s)]
                if(!TxBusy()){
                    unsigned int sv[18];
                    unsigned int pv[16];
                    int i;
                    RemoteRampStepsGet(sv);
                    RemoteRampGet(pv);
                    cTX->Command = 0x56;
                    cTX->Data[0] = 0;
                    cTX->Data[1] = pv[15];
                    for(i = 0; i < 18; i++) Put16(2 + i * 2, sv[i]);
                    TxSend();
                    done = 1;
                }
                break;
            case 0x57: //set the profile steps [key 0x5A 0xA5, number of steps, 6 x (u16 temperature C, u16 rate 0.1 C/s, u16 hold 0.1 s)];
                       //replies [status 0 ok, 1 bad key, 2 out of range, 3 a profile is running]
                if(!TxBusy()){
                    unsigned int sv[18];
                    int i;
                    cTX->Command = 0x57;
                    if(cRX->Data[0] != REMOTE_KEY0 || cRX->Data[1] != REMOTE_KEY1){
                        cTX->Data[0] = 1;
                    }
                    else{
                        for(i = 0; i < 18; i++) sv[i] = cRX->Data[3 + i * 2] | (cRX->Data[4 + i * 2] << 8);
                        cTX->Data[0] = RemoteRampStepsSet(cRX->Data[2], sv);
                    }
                    TxSend();
                    done = 1;
                }
                break;
            case 0x20: //read settings: [address, length]; replies [address, length, status, data...] (status 2 = bad range)
                if(!TxBusy()){
                    int a = cRX->Data[0];
                    int n = cRX->Data[1];
                    int i;
                    cTX->Command = 0x20;
                    cTX->Data[0] = a;
                    if(n < 1 || n > SETTINGS_CHUNK || a + n > SETTINGS_SIZE){
                        cTX->Data[1] = 0;
                        cTX->Data[2] = 2;
                    }
                    else{
                        cTX->Data[1] = n;
                        cTX->Data[2] = 0;
                        EEPRead(a, (UINT8 *)&cTX->Data[3], n);
                        for(i = 0; i < n; i++) if(InPinArea(a + i)) cTX->Data[3 + i] = 0;
                    }
                    TxSend();
                    done = 1;
                }
                break;
            case 0x21: //write settings: [key 0x5A 0xA5, address, length, data...]; replies [status] (0 ok, 1 bad key, 2 bad range)
                if(!TxBusy()){
                    int a = cRX->Data[2];
                    int n = cRX->Data[3];
                    int i;
                    cTX->Command = 0x21;
                    if(cRX->Data[0] != REMOTE_KEY0 || cRX->Data[1] != REMOTE_KEY1){
                        cTX->Data[0] = 1;
                    }
                    else if(n < 1 || n > SETTINGS_CHUNK || a + n > SETTINGS_SIZE){
                        cTX->Data[0] = 2;
                    }
                    else{
                        for(i = 0; i < n; i++){
                            UINT8 v = cRX->Data[4 + i];
                            if(!InPinArea(a + i) && (EEPRead(a + i, 0, 1) != v)) EEPWriteImm(a + i, v);
                        }
                        cTX->Data[0] = 0;
                    }
                    TxSend();
                    done = 1;
                }
                break;
            case 0x22: //restart the station so that restored settings are loaded: [key 0x5A 0xA5]
                if(cRX->Data[0] == REMOTE_KEY0 && cRX->Data[1] == REMOTE_KEY1){
                    _delay_ms(50);  //let the last EEPROM write finish
                    mcuReset();
                }
                done = 1;
                break;
            default:
                done = 1;
                break;
        }
    return done;
}

//---- UART link: ESP32 Bluetooth bridge on UART2 (RF4 = RX, RF5 = TX), 115200 8N1 ----
//Frame: 0x7E, 64 payload bytes (same packet layout as USB), CRC-8 (poly 0x07) over the payload.
#define UART_BAUD_BRG   86          //BRGH=1: 40 MHz / (4 * 115200) - 1
#define UART_MASK_RX    255
#define UART_MASK_TX    511
#define UART_FRAME      66
#define UART_GAP_TICKS  (CORETIMER_FREQ / 50)       //20 ms without a byte inside a frame: start hunting again
#define UART_LINK_TICKS (CORETIMER_FREQ * 3)        //link is active 3 s after the last good frame

static volatile UINT8 uRxBuf[UART_MASK_RX + 1];
static volatile UINT16 uRxHead, uRxTail;
static volatile UINT8 uTxBuf[UART_MASK_TX + 1];
static volatile UINT16 uTxHead, uTxTail;
static volatile USBPacket URXP, UTXP;
static int uState, uPos, uPending;
static UINT8 uCrc;
static UINT32 uLastByte, uLastFrame;
static int uLinkSeen;
static UINT16 uIronID;
static unsigned int uTicks;

static UINT8 Crc8Step(UINT8 c, UINT8 b){
    int i;
    c ^= b;
    for(i = 0; i < 8; i++) c = (c & 0x80) ? (UINT8)((c << 1) ^ 0x07) : (UINT8)(c << 1);
    return c;
}

void __ISR(_UART_2_VECTOR, IPL3SOFT) U2ISR(void){
    if(INTGetFlag(INT_U2RX)){
        while(U2STAbits.URXDA){
            UINT8 b = (UINT8)U2RXREG;
            UINT16 n = (uRxHead + 1) & UART_MASK_RX;
            if(n != uRxTail){ uRxBuf[uRxHead] = b; uRxHead = n; }
        }
        INTClearFlag(INT_U2RX);
    }
    if(U2STAbits.OERR) U2STAbits.OERR = 0;
    INTClearFlag(INT_U2E);
    if(INTGetEnable(INT_U2TX) && INTGetFlag(INT_U2TX)){
        while(!U2STAbits.UTXBF && uTxTail != uTxHead){
            U2TXREG = uTxBuf[uTxTail];
            uTxTail = (uTxTail + 1) & UART_MASK_TX;
        }
        if(uTxTail == uTxHead) INTEnable(INT_U2TX, INT_DISABLED);
        INTClearFlag(INT_U2TX);
    }
}

static void UartInit(void){
    TRISFbits.TRISF4 = 1;
    U2MODE = 0;
    U2STA = 0;
    U2BRG = UART_BAUD_BRG;
    U2MODEbits.BRGH = 1;
    INTSetVectorPriority(INT_UART_2_VECTOR, INT_PRIORITY_LEVEL_3);
    INTClearFlag(INT_U2RX);
    INTClearFlag(INT_U2TX);
    INTClearFlag(INT_U2E);
    INTEnable(INT_U2RX, INT_ENABLED);
    U2STAbits.URXEN = 1;
    U2STAbits.UTXEN = 1;
    U2MODEbits.ON = 1;
    uState = 0; uPending = 0; uLinkSeen = 0;
    uRxHead = uRxTail = uTxHead = uTxTail = 0;
    uIronID = 0xFFFF;
}

static int UartTxFree(void){ return (uTxTail - uTxHead - 1) & UART_MASK_TX; }

static void UartSend(volatile USBPacket *p){
    int i;
    UINT8 c = 0;
    UINT16 h = uTxHead;
    uTxBuf[h] = 0x7E; h = (h + 1) & UART_MASK_TX;
    for(i = 0; i < 64; i++){
        UINT8 b = p->RAWData[i];
        c = Crc8Step(c, b);
        uTxBuf[h] = b; h = (h + 1) & UART_MASK_TX;
    }
    uTxBuf[h] = c; h = (h + 1) & UART_MASK_TX;
    uTxHead = h;
    INTEnable(INT_U2TX, INT_ENABLED);
}

static int TxBusy(void){
    return cUart ? (UartTxFree() < UART_FRAME) : HIDTxHandleBusy(USBInHandle);
}

static void TxSend(void){
    if(cUart) UartSend(cTX);
    else USBInHandle = HIDTxPacket(HID_EP, (BYTE *)cTX, 64);
}

static int LinkActive(void){
    return uLinkSeen && ((UINT32)(ReadCoreTimer() - uLastFrame) < UART_LINK_TICKS);
}

static void UartTasks(void){
    UINT32 now = ReadCoreTimer();
    if(uTxTail != uTxHead && !INTGetEnable(INT_U2TX)) INTEnable(INT_U2TX, INT_ENABLED);

    //parse received bytes into a frame
    while(!uPending && uRxTail != uRxHead){
        UINT8 b = uRxBuf[uRxTail];
        uRxTail = (uRxTail + 1) & UART_MASK_RX;
        if(uState == 1 && (UINT32)(now - uLastByte) > UART_GAP_TICKS) uState = 0;
        uLastByte = now;
        if(uState == 0){
            if(b == 0x7E){ uState = 1; uPos = 0; uCrc = 0; }
        }
        else if(uPos < 64){
            URXP.RAWData[uPos++] = b;
            uCrc = Crc8Step(uCrc, b);
        }
        else{
            uState = 0;
            if(b == uCrc){
                uPending = 1;
                uLinkSeen = 1;
                uLastFrame = now;
            }
        }
    }

    if(uPending){
        cRX = &URXP; cTX = &UTXP; cUart = 1;
        if(HandleCommand()) uPending = 0;
        return;
    }

    //live data while the link is active
    if(LinkActive() && UartTxFree() >= UART_FRAME){
        if(uIronID != IronID){
            uIronID = IronID;
            UTXP.Command = 1;
            UTXP.Data16[0] = IronID;
            UartSend(&UTXP);
        }
        else if(uTicks != ADCStep){
            uTicks = ADCStep;
            if(uTicks & 1){
                UTXP.Command = 3;
                FillLive(&UTXP, uTicks);
                UartSend(&UTXP);
            }
        }
    }
}

void IOInit(){
    IO_TICKS = ISRTicks;
    IO_BUSY = 1;
    USBDriverInit();
    UartInit();
    RXP.Command = 0;
}

void IOTasks(){
    UartTasks();
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
                FillLive(&TXP, IO_TICKS);
                USBInHandle = HIDTxPacket(HID_EP, (BYTE *)&TXP, 64);
            }
        }        
    }
    else {
        if(!HIDRxHandleBusy(USBOutHandle)){
            cRX = &RXP; cTX = &TXP; cUart = 0;
            IO_BUSY = !HandleCommand();
            if(!IO_BUSY) USBOutHandle = HIDRxPacket(HID_EP, (BYTE *)&RXP, 64);
        }
    }
}

#undef _IO_C
