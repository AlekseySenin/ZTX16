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

### Cartridge shape

- VM-2 used a provisional static class marked `[Cartridge]`. **VM-3 replaced it** with the specified
  lifecycle interface `ZTX16.ICartridge` (see below); `[Cartridge]` no longer exists.
- SDK API is still flat and static (`Graphics.FillRect(layer, …)`); object-style APIs are future work.

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

## VM-3 — managed heap

### VM

- New value type `Ref`: a handle to a heap object (index + 1), 0 is null.
- Heap objects: class instances (one slot per field, base fields first), one-dimensional arrays with
  typed storage (8/16/32/64-bit integers, f32, f64, refs), immutable UTF-16 strings.
- Classes are module metadata: base class, full field layout, all implemented interfaces, and a dispatch
  table selector → function. Virtual and interface calls both go through **selectors**
  (`CallVirtual`); type tests use the class's precomputed assignability set.
- **Garbage collector:** non-moving mark-sweep, run when 4 MB have been allocated since the last
  collection or when the heap would exceed its limit. **Precise roots**: string literals, `Ref` globals,
  `Ref` locals of every active frame, and the `Ref` entries of the operand stack taken from verifier
  stack maps recorded at each safepoint (allocations and calls). In calling frames, the operands that
  became the callee's parameters are scanned as the callee's locals.
- **Provisional limits:** heap 32 MB, collection threshold 4 MB (`VmHeap`).
- Executable format version 2 adds class, field, selector and string tables.
- Runtime checks that keep the heap memory- and type-safe without a class-aware verifier: null
  dereference, field owner vs. object class, array storage vs. element access, array bounds, negative
  array length, invalid cast, missing dispatch entry. All are traps.

### Toolchain

- **Entry point:** exactly one non-abstract class implementing `ZTX16.ICartridge`, with a parameterless
  constructor. The runtime creates it once after all static constructors ran. `Deinitialize` is an
  optional default interface method; it is exported only if the cartridge implements it.
- Supported: classes (fields, constructors with base-constructor chaining, single inheritance, `virtual`
  / `override` / `abstract`, `base.` calls, `sealed`), interfaces (implicit and explicit implementation,
  default interface methods, interface inheritance), `is` / `as` / casts, `null`, one-dimensional and jagged
  arrays of numeric types, `bool`, `char`, enums and references, `foreach` over arrays, string literals,
  `Length`, indexer, `==` / `!=`, `string.Concat` (2–4 strings, i.e. `+`), `string.IsNullOrEmpty`.
- Overrides and implicit interface implementations are matched by name and CLR signature. Hiding a
  virtual method with `new virtual` is not distinguished from overriding it.
- Non-virtual instance calls are direct calls without a null check at the call site; a null receiver
  traps at its first field access.
- Not supported yet (clear build errors): structs, generics (including `List<T>`), delegates and lambdas,
  exceptions, boxing, `ref`/`out`/`in`, multi-dimensional arrays, `System.Object` virtuals
  (`ToString`, `Equals`, `GetHashCode`), string formatting/interpolation, and other BCL types.
- Correctness: differential tests for virtual dispatch, interfaces, type tests, arrays, strings and linked
  structures, plus GC tests where the only reference to a live object is in a caller's local, on a
  caller's operand stack, or in a static array while several collections run (`HeapTests`).

## VM-4 — generics and collections

### Generics

- **Monomorphization.** The toolchain translates a separate copy of every generic class, interface and
  method for each distinct set of type arguments it reaches (`List<int>` and `List<string>` are different
  VM classes with different code). The VM itself has no notion of generics.
- Supported: generic classes and interfaces, generic static and instance methods, nested generic types,
  static fields per instantiation (each with its own static constructor), generic arguments of any
  supported type, `default(T)` in locals.
- Not supported yet: generic virtual methods, `default(T)` written directly into a field or array
  element address, constraints-based calls on `T` (`T.Equals`, `T.ToString` through `constrained.`).

### Core library: ZTX16.Collections

- `List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>`, `Stack<T>`, `Queue<T>` in `src/ZTX16.Core`, written in
  the supported subset of C#.
- **Shipped as source**: the cartridge build compiles `src/ZTX16.Core/**/*.cs` into every cartridge, so the
  library is translated (and monomorphized) together with cartridge code and the translator still works on
  a single assembly. Only what a cartridge reaches is translated.
- BCL collections (`System.Collections.Generic.*`) are not available; the toolchain points to the ZTX16
  equivalents. Do not import both namespaces in one file (ambiguous names).
- Misuse (index out of range, missing key, duplicate key, empty stack/queue) stops the cartridge with a
  clear message through `Intrinsics.Fail`, i.e. a VM trap.
- `Dictionary` keys compare by value for numbers and strings and by identity for other objects.
  `Keys`/`Values` return entries in insertion order (freed slots are reused). `TryGetValue` is not
  available yet (no `out` parameters); use `GetValueOrDefault` or `ContainsKey`.
- `foreach` over `List<T>` uses a class enumerator (no `IDisposable`, so no `try/finally`).
- Collections implement `System.Collections.IEnumerable` only so C# allows collection initializers; the
  toolchain ignores that interface.

### VM additions

- `Hash` (numbers by value, strings by FNV-1a content hash, other references by identity), `ValueEquals`,
  `Trap` (stop with a message), `ToText` (`ToString()` of numbers, `char`, `bool`, invariant culture),
  `StringConcatArray`.
- **The interpreter is no longer recursive.** VM calls push frames onto explicit arrays, so VM call depth
  (limit 256) never consumes the host's stack. Found when the larger interpreter frame overflowed the
  test thread's stack.

### Language version

Cartridges compile with **C# 12** (`LangVersion` in the cartridge targets). From C# 13 on, calls such as
`string.Concat` with five or more parts bind to `params ReadOnlySpan<T>` overloads built on inline-array
structs, which the VM does not support; C# 12 uses arrays.
