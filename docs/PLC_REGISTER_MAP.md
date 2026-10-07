# PLC Register Map

Prompt 4 supplies the Mitsubishi PLC connection endpoint and D-device register list. These values are loaded from persisted Settings and remain editable at runtime.

## Connection Defaults

| Setting | Default |
| --- | --- |
| Protocol | Modbus TCP |
| PLC IP address | `192.168.255.1` |
| Port | `502` |
| Unit/slave ID | `1` |
| D-register Modbus offset | `0` |

`DRegisterModbusOffset` is intentionally configurable. The supplied addresses are Mitsubishi D-device registers, not verified Modbus PDU addresses. The current resolver maps `Dnnnn` to Modbus holding-register start address `nnnn + DRegisterModbusOffset`; adjust the offset during PLC commissioning if the PLC exposes D devices with a different base.

## PLC to PC Signals

| Signal | Mitsubishi Register | Direction | Default Data Type | Notes |
| --- | --- | --- | --- | --- |
| Target Parts Per Shift | `D2000` | Read | `UInt16` | Verify datatype. |
| Actual Part Count | `D2005` | Read | `UInt16` | Captured in the immediate current snapshot. |
| OK / NG | `D2010` | Read | `UInt16` | Editable `ValueMap`; current initial default is `1=OK;2=NG`. Raw and resolved values are stored. |
| Date | `D2015-D2019` | Read | `AsciiString`, length `5` D registers | Provisional ASCII span; verify PLC encoding. |
| Time | `D2020-D2024` | Read | `AsciiString`, length `5` D registers | Provisional ASCII span; verify PLC encoding. |
| Leak Test Value | `D2025-D2029` | Read | `AsciiString`, length `5` D registers | Provisional ASCII decimal text such as `0.034`; verify PLC encoding. |
| Auto / Manual | `D2030` | Read | `UInt16` | Editable `ValueMap`; current initial default is `1=Auto;2=Manual`. Raw and resolved values are stored. |
| Error | `D2035` | Read | `UInt16` | Editable placeholder `ValueMap` defaults to `0=No Error;1=Error 1;2=Error 2;3=Error 3;4=Error 4;5=Error 5`. Raw and resolved values are stored. |
| Running Status | `D2040` | Read | `UInt16` | Editable placeholder `ValueMap` defaults to `0=Stopped;1=Running;2=Status 2;3=Status 3;4=Status 4;5=Status 5`. Raw and resolved values are stored. |
| QR Code Value | `D2050` | Read | `AsciiString`, length `10` D registers | Verify length and encoding. |
| Part Number | `D2060` | Read | `AsciiString`, length `10` D registers | A new valid non-zero Part Number is checked against SQLite and kept pending until D1075 is HIGH. Verify length and encoding; validation prevents overlap with every other enabled range. |
| Serial Number | `D2000` | Read | Unconfigured | Disabled by default with only the start address supplied. Set the real datatype, register count/range, byte order, and encoding in PLC Mapping before enabling it. Its decoded value is stored in the immediate snapshot. |
| Part Data Ready | `D1075` | Read only | `UInt16` | PLC-controlled trigger with editable `0=LOW;1=HIGH` mapping. Every HIGH poll evaluates the current Part Number; LOW is not required between different Part Numbers, and the PC never writes this mapping. |

`D2000` is also the existing Target Parts Per Shift default. Keep Serial Number disabled until the PLC datatype/range/encoding and ownership of that overlapping register are commissioned; Settings rejects overlapping enabled mappings instead of silently moving either signal.

## PC to PLC Signals

| Signal | Mitsubishi Register | Direction | Default Data Type | Notes |
| --- | --- | --- | --- | --- |
| System Ready | `D2100` | Write | `UInt16` | Set high once the app services, database, PLC client, and station runtime are operational; set low on normal shutdown if PLC communication remains available. |
| Communication OK | `D2105` | Write | `UInt16` | Set high only after successful PLC read communication; set low on repeated read failures when technically possible. |
| Data Saved | `D2110` | Write | `UInt16` | Write configured ON only after SQLite commit and live-file update; keep it ON for about two seconds, then explicitly write configured OFF. A failed acknowledgement is retried without inserting another History row. |

These addresses are backward-compatible defaults, not safety constants. Each handshake mapping is editable. The write allow-list contains only the three signal names, and runtime writes resolve the current configured address and ON/OFF value map; every other signal is forbidden and logged.

## Commissioning Items Still Required

The implementation exposes editable address, address type, datatype, register count, byte order for two-or-more-register ranges, value map, and D-register offset fields. The following still require live PLC verification:

* D-register to Modbus device allocation/addressing offset. Mitsubishi Modbus device assignments are PLC-configuration-specific and must be confirmed with the programmer.
* Unit/slave ID.
* OK / NG numeric values beyond the initial `1=OK;2=NG` defaults.
* Auto / Manual numeric values beyond the initial `1=Auto;2=Manual` defaults.
* Running Status numeric values beyond the editable placeholder defaults.
* Date and Time encoding and register count.
* Leak Test Value datatype/register count and decimal parsing.
* QR Code and Part Number register lengths and encoding.
* Serial Number datatype, register count/range, byte order, encoding, and resolution of the existing Target Parts Per Shift overlap. Its default start address is `D2000`.
* Live confirmation of the configured OFF/ON representations and addresses for PC writes.
* Live confirmation of `D1075 = 1` saving one valid Part Number immediately, no duplicate across held-HIGH polls, and a changed Part Number saving once without D1075 returning LOW.
* Confirmation that a failed database insert keeps the captured part pending and retries even if D1075 later returns LOW, and that the PLC holds all configured production values for the immediate snapshot. Data Saved remains a separate acknowledgement after local durability.
