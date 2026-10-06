## DynaK Wet Leak Test Project Rules

This project is a production-grade Windows application for a DynaK wet leak testing station.

### UI

* `code.html`, `DESIGN.md`, and `screen.png` are the authoritative Stitch UI/design reference.
* The Stitch interface is the visual source of truth. Do not redesign without explicit instruction.
* Preserve the industrial layout, spacing, typography scale, colors, density, hierarchy, and status presentation as closely as technically possible.
* `PRINT TAG` and `RE-TEST` must remain removed.
* Do not add decorative dashboards, charts, gradients, animations, or unrelated widgets.

### Runtime

* The operator UI must never communicate directly with the PLC.
* PLC/data acquisition must continue independently when the operator UI closes.
* The acquisition process must be capable of running as a Windows background service.
* The app must work offline at runtime, except for local Ethernet communication to the PLC.
* Do not add runtime CDN, cloud, external server, remote database, or internet dependencies.

### Data

* SQLite is the durable source of truth.
* Excel is reporting only and must never be primary storage.
* Use SQLite WAL mode where appropriate for continuous operation.
* Protect duplicate PLC cycles with `station_id + plc_sequence_id`.

### PLC

* Do not invent Mitsubishi PLC IPs, ports, registers, coils, datatypes, endianness, result-ready bits, acknowledgements, or handshake behavior.
* Keep PLC access behind an abstraction so the simulator can be replaced by a real Mitsubishi Modbus TCP implementation later.

### Reliability

* Avoid silent exceptions, uncontrolled retry loops, duplicate records, and UI-driven PLC writes.
* Preserve structured local logging and machine/application event history.
* Keep shift definitions and production targets configuration-driven, including overnight shifts.
