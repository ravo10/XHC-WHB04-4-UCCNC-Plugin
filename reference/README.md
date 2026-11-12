# Reference material (not included)

The protocol and API notes in [`docs/`](../docs) came from analysing these files.
They belong to their makers and are **not** in this repository. Get them from the
original sources:

| File | Owner | Used for |
|------|-------|----------|
| `ShuttlePro.dll` V3.87.1 (Mach3 plugin "XHC-HB04BX-FOR-MACH3"), macros `M930`, `m933`, `m999` | XHC / ChengDu XinHeCheng Technology | USB protocol of the WHB04B → [`docs/PROTOCOL.md`](../docs/PROTOCOL.md) |
| WHB04B handwheel manuals (PDF) | XHC | Key and knob functions |
| `XHC-HB04_pendant.dll` v1.5, `UCR200Plugin.dll`, `UCR201Plugin.dll` | CNCdrive Kft. (ship with UCCNC) | UCCNC plugin API → [`docs/UCCNC_API.md`](../docs/UCCNC_API.md) |

The XHC files come with the pendant (CD / XHC website). The UCCNC plugins are in the
`Plugins` folder of every UCCNC installation.
