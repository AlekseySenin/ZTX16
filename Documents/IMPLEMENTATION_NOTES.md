# ZTX16 — Implementation Notes

Decisions taken during implementation where the specification leaves a detail open.
Each entry names the code that establishes it. When a decision changes a public contract,
the owning module document must be updated as well.

## Phase 1 — host shell, display, layers, input, audio device

### Solution layout

- `src/ZTX16.Platform` — platform subsystems. Has no reference to SDL3, OpenGL or any host technology.
- `src/ZTX16.Host` — reference host (SDL3). The only project that touches host APIs.
- `tests/ZTX16.Tests` — unit tests for the platform.

Host-facing hooks (composition, event injection, audio rendering) are `internal` and exposed to
the host and tests with `InternalsVisibleTo`, so they do not appear as public platform API.

### Presentation backend

The host presents through SDL3's renderer, explicitly created with the `opengl` driver, rather than
a hand-written OpenGL pipeline. This fulfils "SDL3/host presentation → OpenGL" with less code. Integer
scaling and letterboxing use `SDL_LOGICAL_PRESENTATION_INTEGER_SCALE`, with nearest-neighbour texture
sampling. A custom OpenGL path can replace it later without changing platform code. (`SdlHost`)

Host hotkeys F11 (fullscreen) and F10 (cycle window scale 1–4×) are taken by the host and are not
forwarded to the session.

### Graphics

- Backdrop color, Display window wrapping and Display origin state were decided in this phase and are
  now specified in `02_Graphics.md` (Display Layers, Composition).
- **Layer size limit.** Provisionally 1–8 screens per axis (`GraphicsLayer.MaxScreensPerAxis`). This
  is an implementation constant, not a machine-profile guarantee.
- **Default layer size.** 1×1 screen.
- **Storage order.** Row-major, starting at the bottom row (y = 0). The host flips rows only at
  upload time.
- **Type naming.** The graphics root type is `GraphicsSystem` (namespace `ZTX16.Graphics`). A class
  named `Graphics` inside a namespace of the same name causes resolution problems in C#. The
  cartridge-facing name will be settled together with the VM API bridge.

### Runtime

- **Tick scheduling.** Lateness under one tick period counts as jitter and keeps the cadence. A delay
  of one full period or more counts as an overrun: the schedule restarts from the late tick, with no
  catch-up. (`TickScheduler`)
- **Suspend.** `Machine.Suspend()` requests are ignored by the MVP host. The host itself stops ticks
  while the window is minimized (a runtime-controlled suspend).
- **Lifecycle bridge.** `IRuntimeProgram` (Initialize/Tick/Render/Deinitialize) is the host-side
  interface the VM will implement. It is not the cartridge format. Until the VM exists, only trusted
  built-in programs (diagnostics) implement it.

### Input

- Host events are queued and applied at the start of each tick (Input phase), so a tick sees a stable
  snapshot. A press and release within one tick reports both `Pressed` and `Released`.
- On host focus loss, all keys and mouse buttons are released, so no input stays stuck down.
- Gamepads use 4 slots. SDL joystick IDs are mapped to slots in the host and never exposed.
  Disconnecting a pad releases its buttons and zeroes its axes.
- Gamepad face buttons by position: South = Action1, East = Action2, West = Action3, North = Action4.
- Mouse coordinates are converted to the logical display and flipped to the lower-left origin.
  Positions outside the display are reported as-is (they may be negative or ≥ 256).
- Text input (ZTX16 character set) is not implemented yet.

### Audio

- Output: 48 kHz, stereo, 32-bit float, through an SDL3 audio stream callback on the audio thread.
- The eight-channel hardware model is not implemented yet. The engine renders silence plus a host
  diagnostic sine tone.
