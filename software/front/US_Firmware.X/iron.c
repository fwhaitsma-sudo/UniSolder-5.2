#define _IRON_C
#include "iron.h"
#include "isr.h"
#include "mcu.h"
#include "PID.h"
#include "main.h"
#include "EEP.h"
//ID       1    2    3    4    5    6    7    8    9   10   11   12   13   14   15   16   17   18   19   20   21   22   23   24   25
//ID(HEX) 01   02   03   04   05   06   07   08   09   0A   0B   0C   0D   0E   0F   10   11   12   13   14   15   16   17   18   19
//R      100  110  120  130  150  180  200  220  240  270  300  330  390  430  470  560  680  820   1K  1.2K 1.5k  2k   3k  5.6k inf.
const UINT16 IDHash[25] = { 87,  95, 104, 112, 123, 143, 161, 175, 188, 204, 223, 241, 264, 291, 310, 338, 380, 425, 470, 516, 563, 620, 690, 774, 875 };

const t_SensorConfig NTC2252_B_1 = { //2252 ohm NTC on SENSEB and Vout1-
	{
		SENSOR_PTC,                 //Type
		0,                          //HChannel
		0,                          //InputP
		1,                          //InputN
		0,                          //InputInv
		1,                          //CBandA
		1,                          //CBandB
	},
	0,                              //CurrentA
	8,                              //CurrentB
	8,                              //Gain
	0,                              //Offset
	{                               //TPoly        
		 1.2979092150861948e+002,
		-2.5097860800982363e-001,
		 3.4666249152754547e-004,
		-2.8852033003098756e-007,
		 1.4543174658214588e-010,
		-4.5529961497559201e-014,
		 8.8932649360324082e-018,
		-1.0529530493819297e-021,
		 6.9074971847220621e-026,
		-1.9252347314948564e-030,
	},
};

const t_IronPars NoIronPars = {
	0,
	{0x0000},
	"NO INSTRUMENT           ",
	{
		{ //t_IronConfig [0]
			{ //t_SensorConfig
				{
					SENSOR_NONE,        //Type
					0,                  //HChannel
					1,                  //InputP
					0,                  //InputN
					1,                  //InputInv
					1,                  //CBandA
					1,                  //CBandB
				},
				128,                    //CurrentA
				128,                    //CurrentB
				128,                    //Gain
				0,                      //Offset
				{                       //TPoly
					0,                  //c0
					0,                  //c1
					0,                  //c2
					0,                  //c3
					0,                  //c4
					0,                  //c5
					0,                  //c6
					0,                  //c7
					0,                  //c8
					0                   //c9
				},
			},
			0,                      //HRCompCurrent
			0,                      //WSLength
			0,                      //PID_DGain
			0,                      //PID_KP
			0,                      //PID_KI
			0,                      //PID_OVSGain
			0,                      //PID_PMax
			0,                      //PID_PNom        
		},
		{{{0}}} //t_IronConfig [1]
	},
	NULL
};

const t_IronPars Irons[] = {
	{
		0,
		{0x1011},
		"PACE TD-100 BLACK       ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					110,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						56.8,                   //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				4,                          //PID_DGain
				(UINT16)(0.07 * 32768),     //PID_KP
				(UINT16)(0.001 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				120,                        //PID_PMax
				120,                        //PID_PNom
			},
			{{{0}}}
		},
		&NTC2252_B_1                        //ColdJunctionSensorConfig
	},

	{
		0,
		{0x0F11},
		"PACE TD-200 BLUE        ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					53,                         //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						25.89,                  //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				3,                          //PID_DGain
				(UINT16)(0.2 * 32768),      //PID_KP
				(UINT16)(0.005 * 32768),    //PID_KI
				6,                          //PID_OVSGain
				120,                        //PID_PMax
				120                         //PID_PNom
			},
			{{{0}}}
		},
		&NTC2252_B_1                        //ColdJunctionSensorConfig
	},

	{
		0,
		{0x1813},
		"HAKKO T15               ",
		{//--Type----HChannel----SChannel----CBand----CurrentA----CurrentB----HRCompCurrent---Gain----Offset--SoftGain----SoftOffset--WSLength----PID_DGain---PID_KP--------------------PID_KI--------------------PID_OVFGain-PID_PMax
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					100,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						49.65,                     //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				2,                          //PID_DGain
				(UINT16)(0.08 * 32768),     //PID_KP
				(UINT16)(0.0015 * 32768),   //PID_KI
				3,                          //PID_OVSGain
				70,                         //PID_PMax
				60                          //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x1213},
		"HAKKO FX8801            ",
		{//-----------------------------------------------------------------------------------------
			{
				{
					{
						SENSOR_PTC,             //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					205,                        //CurrentA
					0,                          //CurrentB
					13,                         //Gain
					331,                        //Offset
					{                           //TPoly
						-165.5,                 //c0
						3.98513793,             //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                          //HRCompCurrent
				0,                          //WSLength
				20,                         //PID_DGain
				(UINT16)(0.4 * 32768),      //PID_KP
				(UINT16)(0.02 * 32768),     //PID_KI
				14,                         //PID_OVSGain
				65,                         //PID_PMax
				65                          //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x0501},
		"CHIN. HAKKO 907         ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					49,                         //Gain
					0,                          //Offset
					{                           //TPoly (TC Type K)
						0,                      //c0
						2.508355e+1,            //c1
						7.860106e-2,            //c2
					   -2.503131e-1,            //c3
						8.315270e-2,            //c4
					   -1.228034e-2,            //c5
						9.804036e-4,            //c6
					   -4.413030e-5,            //c7
						1.057734e-6,            //c8
					   -1.052755e-8             //c9
					},
				},
				0,                          //HRCompCurrent
				0,                          //WSLength
				11,                         //PID_DGain
				(UINT16)(0.4 * 32768),      //PID_KP
				(UINT16)(0.01 * 32768),     //PID_KI
				16,                         //PID_OVSGain
				50,                         //PID_PMax
				50                          //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x1805},
		"JBC C245                ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						1,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					0,                          //CurrentA
					10,                         //CurrentB
					87,                         //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						42.85,                  //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                          //HRCompCurrent
				1,                          //WSLength
				11,                         //PID_DGain
				(UINT16)(0.3 * 32768),      //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				10,                         //PID_OVSGain
				130,                        //PID_PMax
				130                         //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x1817},
		"JBC C210                ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					202,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						126,                    //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				40,                         //PID_PMax
				40                          //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x1317},
		"JBC C105/C115           ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					200,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						126,                    //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				14,                         //PID_PMax
				14                          //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x1313},
		"JBC Microtweezers       ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						1,                      //HChannel
						0,                      //InputP
						1,                      //InputN
						0,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					0,                          //CurrentA
					10,                         //CurrentB
					202,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						123,                    //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                             //HRCompCurrent                        
				-1,                             //WSLength
				1,                              //PID_DGain
				(UINT16)(0.25 * 32768),         //PID_KP
				(UINT16)(0.004 * 32768),        //PID_KI
				4,                              //PID_OVSGain
				40,                             //PID_PMax
				40                              //PID_PNom
			},
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					202,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						123,                    //c1 *
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.004 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				40,                         //PID_PMax
				40                          //PID_PNom
			}
		},
		NULL
	},

	{
		0,
		{0x1515},
		"JBC Nanotweezers       *",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						1,                      //HChannel
						0,                      //InputP
						1,                      //InputN
						0,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					0,                          //CurrentA
					10,                         //CurrentB
					220,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						123,                    //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				14,                         //PID_PMax
				14                          //PID_PNom
			},
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					220,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						123,                  //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				14,                         //PID_PMax
				14                          //PID_PNom
			}
		},
		NULL
	},

	{
		0,
		{0x1111},
		"Weller WMRT             ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					128,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						67.95,                  //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                          //HRCompCurrent                        
				0,                          //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.004 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				40,                         //PID_PMax
				40                          //PID_PNom
			},
			{
				{
					{
						SENSOR_TC,              //Type
						1,                      //HChannel
						0,                      //InputP
						1,                      //InputN
						0,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					0,                          //CurrentA
					10,                         //CurrentB
					128,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						67.95,                  //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                          //HRCompCurrent
				0,                          //WSLength
				1,                          //PID_DGain
				(UINT16)(0.025 * 32768),    //PID_KP
				(UINT16)(0.004 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				40,                         //PID_PMax
				40                          //PID_PNom
			}
		},
		NULL
	},

	{
		0,
		{0x1803},
		"WELLER WSP80            ",
		{
			{
				{
					{
						SENSOR_PTC,             //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					195,                        //CurrentA
					0,                          //CurrentB
					49,                         //Gain
					627,                        //Offset
					{                           //TPoly
						-313.5,                 //c0
						14.2881775,             //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                          //HRCompCurrent
				0,                          //WSLength
				30,                         //PID_DGain
				(UINT16)(0.2 * 32768),      //PID_KP
				(UINT16)(0.008 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				80,                         //PID_PMax
				80                          //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{0x020B},
		"ERSA RT80               ",
		{//-----------------------------------------------------------------------------------------
			{
				{
					{
						SENSOR_PTC,             //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						0,                      //CBandA
						1,                      //CBandB
					},
					167,                        //CurrentA
					0,                          //CurrentB
					21,                         //Gain
					284,                        //Offset
					{                           //TPoly
						-145.7299,              //c0
						86.2582,                //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                         //HRCompCurrent                        
				-1,                        //WSLength
				0,                         //PID_DGain
				(UINT16)(0.4 * 32768),     //PID_KP
				(UINT16)(0.02 * 32768),    //PID_KI
				4,                         //PID_OVSGain
				80,                        //PID_PMax
				80                         //PID_PNom
			},
			{{{0}}}
		},
		NULL
	},

	{
		0,
		{ 0x0707 },
			"ATTEN GT-N100           ",
		{
			{
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP
						0,                      //InputN
						1,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					56,                         //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						28.25,                   //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent                        
				-1,                          //WSLength
				30,                         //PID_DGain
				(UINT16)(0.8 * 32768),      //PID_KP
				(UINT16)(0.002 * 32768),    //PID_KI
				10,                         //PID_OVSGain
				50,                         //PID_PMax
				50                          //PID_PNom
			},
			{
				{
					{
						SENSOR_TC,              //Type
						1,                      //HChannel
						0,                      //InputP
						1,                      //InputN
						0,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					0,                          //CurrentA
					10,                         //CurrentB
					56,                         //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						28.25,                   //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                          //WSLength
				30,                         //PID_DGain
				(UINT16)(0.8 * 32768),      //PID_KP
				(UINT16)(0.002 * 32768),    //PID_KI
				10,                         //PID_OVSGain
				50,                         //PID_PMax
				50                          //PID_PNom
			}
		},
		NULL
	}
};

/* Two-iron adapters. ID code 0x1705 = 150 ohm between ID and Vout1-,
 * 3.0k between ID and Vout2- (not used by any single instrument).
 *
 * Iron 1, JBC C245 on channel 1, thermocouple read single-ended on SENSEA:
 *   green (shell) -> SENSEA and EARTH, red -> Vout1-, blue -> Vout1+
 *   Same gain/polynomial as the standard C245 profile, but this wiring is
 *   UNTESTED: check it against a tip thermometer before normal use.
 * Iron 2, JBC C210 on channel 2, wired like the second tip of the JBC tweezers:
 *   red -> Vout2+ and SENSEB, blue -> Vout2-, green (shell) -> EARTH
 */
const t_IronPair IronPairs[] = {
	{
		{0x1705},
		{
			"[1] JBC C245 (UNCAL)    ",
			"[2] JBC C210            "
		},
		{
			{ //iron 1: JBC C245 on channel 1
				{
					{
						SENSOR_TC,              //Type
						0,                      //HChannel
						1,                      //InputP (SENSEA)
						0,                      //InputN (GND)
						0,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					10,                         //CurrentA
					0,                          //CurrentB
					87,                         //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						42.85,                  //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				0,                          //HRCompCurrent
				1,                          //WSLength
				11,                         //PID_DGain
				(UINT16)(0.3 * 32768),      //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				10,                         //PID_OVSGain
				130,                        //PID_PMax
				130                         //PID_PNom
			},
			{ //iron 2: JBC C210 on channel 2
				{
					{
						SENSOR_TC,              //Type
						1,                      //HChannel
						0,                      //InputP (GND)
						1,                      //InputN (SENSEB)
						0,                      //InputInv
						1,                      //CBandA
						1,                      //CBandB
					},
					0,                          //CurrentA
					10,                         //CurrentB
					202,                        //Gain
					0,                          //Offset
					{                           //TPoly
						0,                      //c0
						126,                    //c1
						0,                      //c2
						0,                      //c3
						0,                      //c4
						0,                      //c5
						0,                      //c6
						0,                      //c7
						0,                      //c8
						0                       //c9
					},
				},
				10,                         //HRCompCurrent
				-1,                         //WSLength
				1,                          //PID_DGain
				(UINT16)(0.25 * 32768),     //PID_KP
				(UINT16)(0.003 * 32768),    //PID_KI
				4,                          //PID_OVSGain
				40,                         //PID_PMax
				40                          //PID_PNom
			}
		},
		NULL
	},
};

static const t_IronPair * CurPair;          //connected two-iron adapter, or NULL

//Put the selected iron of the current adapter into IronPars as a single iron.
static void IronPairLoad(void) {
	t_IronPars p = NoIronPars;
	int n = pars.ActIron ? 1 : 0;
	int i;

	p.ID = CurPair->ID;
	for (i = 0; i < sizeof(p.Name); i++) p.Name[i] = CurPair->Name[n][i];
	p.Config[0] = CurPair->Config[n];
	p.ColdJunctionSensorConfig = CurPair->ColdJunctionSensorConfig;
	IronPars = p;
}

int IronPairActive(void) {
	return CurPair != NULL;
}

void IronPairReload(void) {
	//Forces IronIdentify to run again. The ID is already stable, so it picks the
	//adapter's iron selected by pars.ActIron through the normal code path.
	IronID = 0x1919;
}

//Saved PID values per iron (set from the phone app after a calibration), kept in the EEPROM.
//Slot: mark, ID lo, ID hi, iron of a two-iron adapter (0/1), Gain (2), KP (2), KI (2), DGain, OVSGain. 16 bytes per slot, so a slot never crosses a 32 byte EEPROM page.
#define PIDS_ADDR   256
#define PIDS_SLOTS  8
#define PIDS_STRIDE 16
#define PIDS_LEN    12
#define PIDS_MARK   0xC7

static volatile UINT8 PidLoadPending;

static UINT8 PidWhich(void) {
	return (CurPair != NULL && pars.ActIron) ? 1 : 0;
}

//Returns the slot holding this iron, or -1; *freeSlot gets an unused slot (or -1).
static int PidSlotFind(UINT16 id, UINT8 which, UINT8 *buf, int *freeSlot) {
	int i;
	*freeSlot = -1;
	for (i = 0; i < PIDS_SLOTS; i++) {
		EEPRead(PIDS_ADDR + i * PIDS_STRIDE, buf, PIDS_LEN);
		if (buf[0] == PIDS_MARK) {
			if (buf[1] == (UINT8)(id & 0xFF) && buf[2] == (UINT8)(id >> 8) && buf[3] == which) return i;
		}
		else if (*freeSlot < 0) *freeSlot = i;
	}
	return -1;
}

static int PidValid(UINT16 gain, UINT16 kp, UINT16 ki, UINT8 dg, UINT8 ovs) {
	return gain >= 8 && gain <= 256 && kp <= 32767 && ki <= 32767 && dg <= 32 && ovs <= 100;
}

//Saves the PID values now in use for the connected iron. Returns 0 on success, 1 no iron, 2 values out of range, 3 EEPROM full.
int IronPidSave(void) {
	UINT8 buf[PIDS_LEN], nb[PIDS_LEN];
	int slot, freeSlot, i;
	t_IronConfig *c = &IronPars.Config[0];
	if (IronID == 0x1919 || !c->SensorConfig.Type) return 1;
	if (c->PID_OVSGain > 100 || !PidValid(c->SensorConfig.Gain, c->PID_KP, c->PID_KI, c->PID_DGain, (UINT8)c->PID_OVSGain)) return 2;
	slot = PidSlotFind(IronID, PidWhich(), buf, &freeSlot);
	if (slot < 0) slot = freeSlot;
	if (slot < 0) return 3;
	nb[0] = PIDS_MARK;
	nb[1] = IronID & 0xFF;
	nb[2] = IronID >> 8;
	nb[3] = PidWhich();
	nb[4] = c->SensorConfig.Gain & 0xFF;
	nb[5] = c->SensorConfig.Gain >> 8;
	nb[6] = c->PID_KP & 0xFF;
	nb[7] = c->PID_KP >> 8;
	nb[8] = c->PID_KI & 0xFF;
	nb[9] = c->PID_KI >> 8;
	nb[10] = c->PID_DGain;
	nb[11] = (UINT8)c->PID_OVSGain;
	EEPRead(PIDS_ADDR + slot * PIDS_STRIDE, buf, PIDS_LEN);
	for (i = 1; i < PIDS_LEN; i++) {   //the mark goes last, so a half written slot is never used
		if (buf[i] != nb[i] || buf[0] != PIDS_MARK) EEPWriteImm(PIDS_ADDR + slot * PIDS_STRIDE + i, nb[i]);
	}
	if (buf[0] != PIDS_MARK) EEPWriteImm(PIDS_ADDR + slot * PIDS_STRIDE, PIDS_MARK);
	return 0;
}

//Restores the standard (built-in) values of the connected iron: forgets the saved values, if any, and loads the built-in ones. Returns 0 saved values forgotten, 1 no iron, 2 nothing was saved (built-in values loaded anyway).
int IronPidClear(void) {
	UINT8 buf[PIDS_LEN];
	int slot, freeSlot;
	if (IronID == 0x1919) return 1;
	slot = PidSlotFind(IronID, PidWhich(), buf, &freeSlot);
	if (slot >= 0) EEPWriteImm(PIDS_ADDR + slot * PIDS_STRIDE, 0xFF);
	IronID = 0x1919;   //identify the iron again, which loads the built-in values (also drops values changed from the app but not saved)
	return slot >= 0 ? 0 : 2;
}

//The values the iron has after identification (built-in, or saved from the app): used for tips without calibration.
static UINT16 BaseGain, BaseKP, BaseKI;
static UINT8 BaseDGain, BaseOVSGain;
static UINT8 BaseValid;

//Puts the PID values of the tip in use on the iron: its own calibration when it has one, otherwise the iron's own values.
void TipApplyActive(void) {
	t_Tip t;
	t_IronConfig *c = &IronPars.Config[0];
	if (IronID == 0x1919 || !c->SensorConfig.Type || !BaseValid) return;
	c->SensorConfig.Gain = BaseGain;
	c->PID_KP = BaseKP;
	c->PID_KI = BaseKI;
	c->PID_DGain = BaseDGain;
	c->PID_OVSGain = BaseOVSGain;
	if (TipActInst < TIP_INST && TipActTip < TIP_PER_INST && TipRead(TipActInst, TipActTip, &t) && t.Cal) {
		c->SensorConfig.Gain = t.Gain;
		c->PID_KP = t.KP;
		c->PID_KI = t.KI;
		c->PID_DGain = t.DGain;
		c->PID_OVSGain = t.OVSGain;
	}
}

//Called from the main loop after an iron was identified.
static void IronPidApply(void) {
	UINT8 buf[PIDS_LEN];
	int freeSlot;
	UINT16 gain, kp, ki;
	t_IronConfig *c = &IronPars.Config[0];
	if (IronID == 0x1919 || !c->SensorConfig.Type) return;
	if (PidSlotFind(IronID, PidWhich(), buf, &freeSlot) >= 0) {
		gain = buf[4] | (buf[5] << 8);
		kp = buf[6] | (buf[7] << 8);
		ki = buf[8] | (buf[9] << 8);
		if (PidValid(gain, kp, ki, buf[10], buf[11])) {
			c->SensorConfig.Gain = gain;
			c->PID_KP = kp;
			c->PID_KI = ki;
			c->PID_DGain = buf[10];
			c->PID_OVSGain = buf[11];
		}
	}
	BaseGain = c->SensorConfig.Gain;
	BaseKP = c->PID_KP;
	BaseKI = c->PID_KI;
	BaseDGain = c->PID_DGain;
	BaseOVSGain = (UINT8)c->PID_OVSGain;
	BaseValid = 1;
	TipApplyActive();
}

int IronPidIsSaved(void) {
	UINT8 buf[PIDS_LEN];
	int freeSlot;
	if (IronID == 0x1919) return 0;
	return PidSlotFind(IronID, PidWhich(), buf, &freeSlot) >= 0;
}

volatile int IronTicks;

void IronIdentify() {
	static UINT16_VAL OID;
	static UINT8 IDCnt;
	UINT16_VAL CID;
	UINT16_VAL NewIronID;
	int i;
	UINT16 w;

	ISRStop();

	I2CData.CurrentA.ui16 = 0;
	I2CData.CurrentB.ui16 = 0;
	I2CAddCommands(I2C_SET_CPOT);
	CBANDA = 1;
	CBANDB = 1;
	while (!I2CIdle);

	HCH = 0;
	ID_3S = 1;
	ID_OUT = 1;
	mcuADCStartManualAVdd();

	_delay_us(1000);
	w = mcuADCReadWait(ADCH_ID, 16) >> 4;
	HCH = 1;
	for (i = 0; i <= 24; i++)if (w < IDHash[i])break;
	CID.v[0] = i;

	_delay_us(1000);
	w = mcuADCReadWait(ADCH_ID, 16) >> 4;
	HCH = 0;
	for (i = 0; i <= 24; i++)if (w < IDHash[i])break;
	CID.v[1] = i;

	NewIronID.Val = 0x1919;
	if (CID.Val == OID.Val) {
		if (IDCnt < 255)IDCnt++;
		if (IDCnt > 2)NewIronID.Val = CID.Val;
	}
	else {
		IDCnt = 0;
	}
	OID.Val = CID.Val;

	if (NewIronID.v[0] >= 0x19)NewIronID.v[0] = NewIronID.v[1];
	if (NewIronID.v[1] >= 0x19)NewIronID.v[1] = NewIronID.v[0];

	if (NewIronID.Val != 0x1919) {
		if (IronID != NewIronID.Val) {
			IronPars = NoIronPars;
			for (i = 2; i--; ) {
				PIDVars[i].HR = 0x7FFF;
				PIDVars[i].HNewData = 0;
				PIDVars[i].HInitData = 1;
				PIDVars[i].HP = 0;
				PIDVars[i].HPMax = 0;
				PIDVars[i].HI = 0;
				PIDVars[i].HV = 0;
				PIDVars[i].OffDelay = 1600;
			}
			int found = 0;
			CurPair = NULL;
			for (i = (sizeof(Irons) / sizeof(Irons[0])); i--; ) {
				if (Irons[i].ID.Val == NewIronID.Val) {
					IronPars = Irons[i];
					for (i = 2; i--; )PIDVars[i].HR = 8;
					found = 1;
					break;
				}
			}
			if (!found) { //not a single instrument - try the two-iron adapters
				for (i = (sizeof(IronPairs) / sizeof(IronPairs[0])); i--; ) {
					if (IronPairs[i].ID.Val == NewIronID.Val) {
						CurPair = &IronPairs[i];
						IronPairLoad();
						for (i = 2; i--; )PIDVars[i].HR = 8;
						break;
					}
				}
			}
			PIDInit();
			BaseValid = 0;
			PidLoadPending = 1;
		}
	}
	else {
		if (IronID != 0x1919)IronPars = NoIronPars;
		CurPair = NULL;
		for (i = 2; i--; ) {
			PIDVars[i].HInitData = 1;
			PIDVars[i].HP = 0;
			PIDVars[i].HPMax = 0;
			PIDVars[i].HI = 0;
			PIDVars[i].HV = 0;
			PIDVars[i].OffDelay = 1600;
			PIDVars[i].Power = 3;
		}
	}
	IronID = NewIronID.Val;
	ID_OUT = 0;
	ID_3S = 0;
	ISRStart();
}

void IronInit() {
	IronID = 0x1919;
	IronPars = NoIronPars;
}

void IronTasks() {
	int i;
	if (PidLoadPending && IronID != 0x1919) {
		PidLoadPending = 0;
		IronPidApply();
	}
	if (IronTicks != ISRTicks) {
		IronTicks = ISRTicks;
		if (!mainFlags.Calibration && (IronTicks & 15) == 15) {
			switch (IronID) {
			case 0x1919:
				IronIdentify();
				break;
			default:
				for (i = 2; i--;) {
					if (IronPars.Config[i].SensorConfig.Type && (PIDVars[i].NoSensor || PIDVars[i].NoHeater)) {
						IronIdentify();
						break;
					}
				}
				break;
			}
		}
	}
}

#undef _IRON_C
