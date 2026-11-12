# UCCNC plugin API: what the stock plugins use

This was read from the .NET metadata and IL of the stock UCCNC plugins in
[`reference/`](../reference/README.md) (not included: `XHC-HB04_pendant.dll` v1.5 by CNCdrive,
`UCR200Plugin.dll`, `UCR201Plugin.dll`).

## Why the existing XHC-HB04 plugin cannot drive the WHB04B-4

- It opens **VID 0x10CE / PID 0xEB70** (decimal 4302 / 60272). That is the wired
  HB04. The wireless WHB04B dongle is **PID 0xEB93**.
- It expects a completely different report layout. The axis knob is in byte 3,
  with codes 17–21 and 24. The wheel is in byte 4. Buttons are numbered 1–23.
- Its LCD packet is the HB04 layout, not the WHB04B layout.

## Plugin shape

- References assembly **`Plugininterface`, version 1.0.0.0, unsigned** (ships
  with UCCNC).
- Entry class: **`Plugins.UCCNCplugin`**, with these public methods:

| Method | Signature |
|--------|-----------|
| `Init_event` | `void (Plugininterface.Entry UC)` |
| `Getproperties_event` | `Entry.Pluginproperties (Entry.Pluginproperties)` (sets `author`, `pluginname`, `pluginversion`) |
| `Configure_event` | `void ()` |
| `Startup_event` | `void ()` |
| `Showup_event` | `void ()` |
| `Shutdown_event` | `void ()` |
| `Loop_event` | `void ()`, called on every UCCNC loop cycle |
| `Buttonpress_event` | `void (int buttonnumber, bool onscreen)` |

## `Plugininterface.Entry` members used

```
int    JogOnSpeed(int axis, bool direction, double speedPercent)   // speed 0 stops the jog
int    MPGJogOff(int axis)
int    AddLinearMoveRel(int axis, double distance, int count, double speedPercent, bool direction)
bool   GetLED(int number)
void   Callbutton(int code)
void   Code(string gcode)
string Getfield(bool isAS3, int number)
double Getfielddouble(bool isAS3, int number)
void   Setfield(bool isAS3, double value, int number)
void   Validatefield(bool isAS3, int number)
double GetXpos() .. GetCpos()          // work coordinates
double GetXmachpos() .. GetCmachpos()  // machine coordinates
List<Entry.Functionproperties> Getbuttonsdescriptions()  // functioncode, functiondescription
string Readkey(string section, string key, string default)
void   Writekey(string section, string key, string value)
```

Axis index: 0 = X, 1 = Y, 2 = Z, 3 = A, 4 = B, 5 = C.

## Codes used by the HB04 plugin

| What | Code |
|------|------|
| Reset toggle | button 144 |
| Cycle stop | button 130 |
| Home all | button 113 |
| Cycle start (when LED 18 is on) | button 128 |
| Feed hold / release (LED 217 = feed hold active) | buttons 523 / 524 |
| Rewind | button 127 |
| Probe Z | button 196 |
| Spindle on/off | button 114 |
| Go to work zero | button 131 |
| Zero X / Y / Z / A | buttons 100–103 |
| Feed override + / − | buttons 132 / 133 |
| Spindle override + / − | buttons 134 / 135 |
| MPG axis select X..A | buttons 220–223, LEDs 155–158 |
| MPG step 0.001 / 0.01 / 0.1 / 1 | LEDs 148–151 |
| MPG mode (cont / single / multi) | LEDs 152–154 |
| Safe Z | `Code("G0 Z" + Getfield(true, 225))` |
| Steps per unit of axis *n* | `Getfielddouble(true, 8 + n*15)` |
| LCD feed / spindle values | `Getfield(true, 867)`, `Getfield(true, 869)` |

Step jogging in the HB04 plugin works like this. It adds up the distance from
the wheel, turns it into whole motor steps (`1 / stepsPerUnit`), and calls
`AddLinearMoveRel(axis, 1/stepsPerUnit, steps, 100, direction)`.

Continuous jogging calls `JogOnSpeed(axis, dir, percent)` on every loop cycle,
and calls it with 0 % to stop.
