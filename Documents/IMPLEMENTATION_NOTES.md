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

## VM-1 — executable format, verifier, interpreter

- **Instruction set.** Typed stack machine (`ZTX16.VM.OpCode`): every opcode has fixed operand types, so
  verification is one pass and the interpreter does no runtime type tests. Values are raw 64-bit slots.
- **Executable format.** `ZTXE` magic + format version 1 (`ExecutableFormat`). Syscalls are imported by
  name and signature and linked at load; unknown names fail to link.
- **Verification.** Stack balance and types at every instruction and merge point, branch targets on
  instruction boundaries, local/global/call/import ranges, no fall-through off the end.
- **Traps.** Integer division by zero and `MinValue / -1`, exhausted instruction budget, call depth,
  value-stack overflow, and any exception thrown by a syscall. A trap is an unhandled cartridge failure.
- **Provisional limits** (implementation constants, not machine-profile guarantees): call depth 256,
  64K value slots, 10M instructions per lifecycle call (`VmProgram.InstructionBudgetPerCall`).
- **Float → int** conversions saturate and map NaN to 0 (the .NET 9+ behaviour), for every width,
  including the unsigned opcodes `F32ToU32`, `F64ToU32`, `F32ToU64`, `F64ToU64`.

## VM-2 — C# toolchain

### Namespaces and assemblies

- `ZTX16.*` names belong to the **cartridge API** (`src/ZTX16.Sdk`): `ZTX16.Graphics`, `ZTX16.Input`,
  `ZTX16.Machine`, `ZTX16.Key`, ….
- The runtime implementation lives in `ZTX16.Platform.*` (`GraphicsSystem`, `InputSystem`, …). Host code
  never occupies cartridge API names.
- `src/ZTX16.Abi` reads the ABI from the SDK (`SdkAbi`); runtime and toolchain both depend on it.
- `src/ZTX16.Toolchain` builds `ztx16c`, the IL → ZTX16 translator. Cartridges reference it only for build order.
- Input enums (`Key`, `MouseButton`, `GamepadButton`, `GamepadAxis`) moved into the SDK; the runtime uses the same types.

### Provisional cartridge shape (until the VM has a heap)

- Entry point: a static class marked `[Cartridge]` with `static void Initialize()`, `Tick()`, `Render()`
  and optional `Deinitialize()`. It becomes the platform lifecycle interface once classes are supported.
- SDK API is flat and static (`Graphics.FillRect(layer, …)`); object-style APIs follow with the heap.

### Translation rules

- Supported: static methods and static fields of numeric types, `bool`, `char` and enums; arithmetic,
  bitwise ops, shifts, comparisons, all conversions between them, branches, `switch`, calls to cartridge
  methods and SDK syscalls. Anything else is a build error naming the method and IL offset.
- Static constructors of every type the cartridge touches run once, eagerly, in type-definition order,
  before `Initialize`. (The CLR runs them lazily; cartridges must not rely on lazy ordering.)
- Stores into `byte`/`sbyte`/`short`/`ushort`/`char` locations truncate, as the CLI specifies.
- Conversions from floating point to sub-32-bit integers go through `int` first.
- Generated code is always run through the VM verifier; a failure there is reported as an internal
  toolchain error.
- Correctness is checked by differential tests: the same C# methods run on .NET and in the VM, in both
  Debug and Release compilation, and must produce identical results (`ToolchainSemanticsTests`).

### Build integration

A cartridge `.csproj` imports `src/ZTX16.Toolchain/build/ZTX16.Cartridge.targets`. After `Build` it runs
`ztx16c` and writes `bin/<config>/net10.0/<AssemblyName>.ztxe`. See `samples/HelloSquare`. The proper
Project → Cartridge build (resources, Project Config, packed cartridge) is the next milestone.
