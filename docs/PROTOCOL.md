# XHC WHB04B-4 / WHB04B-6 USB protocol

This was worked out by disassembling the vendor Mach3 plugin
`ShuttlePro.dll` V3.87.1 (see [`reference/`](../reference/README.md); not included). It matches the
open-source LinuxCNC `xhc-whb04b-6` driver. Addresses in brackets point to the
place in the DLL where each fact was confirmed.

## USB identity

| Item       | Value |
|------------|-------|
| Vendor ID  | `0x10CE` |
| Product ID | `0xEB93` (wireless WHB04B-4 / -6 dongle) |
| Class      | HID, **two top-level collections** |

The old wired **HB04** uses `VID_10CE&PID_EB70` and a different report format.
That is why the existing UCCNC "XHC HB04" plugin can see the device but cannot
use it.

Windows lists the dongle as two HID device interfaces:

| Collection | Path contains | Opened as | Used for |
|------------|---------------|-----------|----------|
| 1 | `&col01` | `GENERIC_READ`, share R/W, overlapped | **input reports** (buttons, knobs, wheel) |
| 2 | `&col02` | `GENERIC_WRITE`, share R/W | **feature reports** (LCD) |

[`0x10004c7a`–`0x10004d31`: it matches `vid_10ce`, `pid_eb93`, then `col02` → write handle, `col01` → read handle]

## Input report (dongle → PC), 8 bytes

The driver calls `ReadFile` with a 16-byte buffer and handles up to two 8-byte
reports per read [`0x100060bc`].

| Byte | Meaning |
|------|---------|
| 0 | Report ID, always `0x04` |
| 1 | Random byte (used by the checksum) |
| 2 | Key code 1 |
| 3 | Key code 2 (non-zero when two keys are held, e.g. Fn + key) |
| 4 | Feed/step knob position (mask `0x3F`) |
| 5 | Axis knob position |
| 6 | Jog wheel delta since the last report, **signed** 8-bit (`>0x80` → negative) |
| 7 | Checksum |

### Keys [`0x10005250`]

The driver decodes the two key bytes like this:

```
if (b3 == 0) { key = b2; modifier = 0;  }   // single key (or nothing)
else         { key = b3; modifier = b2; }   // modifier is 0x0C when Fn is held
```

It only acts when the 16-bit word `b2|b3<<8` changes. A word of 0 means all
keys are released.

| Code | Key (printed on pendant) | Alone | With Fn (Mach3 driver behaviour) |
|------|--------------------------|-------|-----------------------------------|
| 0x01 | RESET            | Reset (OEM 1021)          | – |
| 0x02 | STOP             | Stop (OEM 1003)           | – |
| 0x03 | START/PAUSE      | toggles Cycle start (1000) / Feed hold (1001) | – |
| 0x04 | Macro-1 / Feed+  | Macro 1                   | Feed override + |
| 0x05 | Macro-2 / Feed-  | Macro 2                   | Feed override − |
| 0x06 | Macro-3 / Spindle+ | Macro 3                 | Spindle override + |
| 0x07 | Macro-4 / Spindle- | Macro 4                 | Spindle override − |
| 0x08 | Macro-5 / M-HOME | Macro 5                   | Home all (`M999`) |
| 0x09 | Macro-6 / Safe-Z | Macro 6                   | Go to safe Z (OEM 104) |
| 0x0A | Macro-7 / W-HOME | Macro 7                   | Go to work zero (`M933`) |
| 0x0B | Macro-8 / S-ON/OFF | Macro 8                 | Spindle on/off (OEM 110) |
| 0x0C | Fn               | modifier only             | – |
| 0x0D | Macro-9 / Probe-Z | Macro 9                  | Probe Z (`M930`) |
| 0x0E | MPG              | set continuous (MPG) mode | – |
| 0x0F | Step/Continuous  | toggles step ↔ continuous (`0x10005345`) | – |
| 0x10 | Macro-10         | Macro 10                  | – |

Codes `0x31`–`0x33` are ignored by the driver.

### Axis knob (byte 5) [`0x10005830`: `switch (b5 - 0x11)`]

| Code | Position |
|------|----------|
| 0x06 | OFF |
| 0x11 | X |
| 0x12 | Y |
| 0x13 | Z |
| 0x14 | A |
| 0x15 | B (WHB04B-6 only) |
| 0x16 | C (WHB04B-6 only) |

When the axis changes, the driver drops any wheel delta in that same report
and resets its jog accumulators.

### Feed/step knob (byte 4 & 0x3F)

| Code | Step mode | Continuous mode |
|------|-----------|-----------------|
| 0x0D | 0.001 | 2 %   |
| 0x0E | 0.01  | 5 %   |
| 0x0F | 0.1   | 10 %  |
| 0x10 | 1.0   | 30 %  |
| 0x1A | –     | 60 %  |
| 0x1B | –     | 100 % |
| 0x1C | Lead  | Lead  |

### Checksum [`0x100061ef`]

`seed` is the byte the PC last sent at offset 2 of the LCD data (the Mach3
driver uses `SYSTEMTIME.wDay`).

```
c        = (byte)~b1
expected = (byte)((c + b2) ^ (seed | c))
valid    = expected == b7
```

When the checksum fails, the Mach3 driver throws the report away and sends a
**seed packet** (a single feature report, see below) to resynchronise the
pendant [`0x10005f70`].

## LCD output (PC → dongle): feature reports

Every write is `HidD_SetFeature(col02, buf, 8)` with `buf[0] = 0x06` (report
ID) followed by 7 payload bytes [`0x100067c0`].

### Seed packet

```
06 FE FD <seed> 20 20 20 20
```

### Display block (20 bytes, sent as 3 feature reports of 7 payload bytes)

[`0x100043ef`–`0x10004a30`]

| Offset | Size | Meaning |
|--------|------|---------|
| 0  | 2 | Header `FE FD` |
| 2  | 1 | Seed |
| 3  | 1 | Flags (see below) |
| 4  | 2 | Axis 1 integer part (LE uint16) |
| 6  | 2 | Axis 1 fraction × 10000 (LE uint16), bit 15 = negative |
| 8  | 2 | Axis 2 integer part |
| 10 | 2 | Axis 2 fraction / sign |
| 12 | 2 | Axis 3 integer part |
| 14 | 2 | Axis 3 fraction / sign |
| 16 | 2 | Feed rate (LE uint16) |
| 18 | 2 | Spindle speed (LE uint16) |

Flags byte:

| Bits | Meaning |
|------|---------|
| 0–1 | Mode: 0 = CONT, 1 = STEP, 2 = MPG, 3 = percent |
| 6   | Show machine coordinates (LCD shows `X1`, `Y1`, …) — Mach3 uses LED 800 |
| 7   | Reset/E-stop active |

Chunking: bytes 0–6 → report 1, bytes 7–13 → report 2, bytes 14–19 (+ one zero
byte of padding) → report 3. The Mach3 driver only resends when the block has
changed (`memcmp`), and at least every few loop cycles.

The pendant shows three axes at a time: X/Y/Z when the knob is on X, Y or Z,
and A/B/C when it is on A, B or C.
