# ZTX16 — Runtime and Machine API

## Runtime contract

- Nominal tick rate: 60 Hz.
- One tick = 1/60 second logical time.
- No DeltaTime.
- No catch-up ticks.
- Tick order: Input → Game Logic → Audio Update → Render.
- Logical time is represented by tick count.
- `Elapsed Time` is real wall-clock elapsed time since session start.
- `Ticks Per Second` is observed simulation throughput, not the nominal rate.

## Cartridge lifecycle

The runtime performs validation/compatibility checks before initialization. Exact validation rules are implementation-defined.

Normal exit performs controlled deinitialization. Crash termination must use emergency cleanup and must not depend on cartridge deinitialization.

## Machine API

Namespace: `ZTX16`

Primary type: `Machine`.

Machine is not a catch-all utility namespace and must not duplicate subsystem APIs.

Conceptual public surface:

- `TickCount` — number of simulation ticks elapsed since session start.
- `ElapsedTime` — real elapsed time since session start.
- `TicksPerSecond` — measured simulation throughput.
- `Profile` / capabilities information for the current machine/session.
- `Exit()` — request session termination.
- `Suspend()` — request session suspension.

Resume is not exposed as a cartridge-controlled operation.

Exact C# types for time values and profile data may be selected during implementation without changing the model.

## Machine profile

Machine capability information may report machine-level capabilities, but subsystem APIs remain authoritative for subsystem-specific details.

Do not duplicate Graphics, Audio, Input or Storage state inside Machine.

## System events

System/runtime events exist conceptually at the OS/runtime level, but the cartridge does not receive them in the base architecture.

The following are therefore not part of the base cartridge API:

- suspend/resume notifications;
- focus notifications;
- system-menu notifications;
- device connection notifications as lifecycle events;
- host window events.
