# ZTX16 — Cartridge Execution and Virtual Machine

## Status

Architecturally approved.

## 1. Core decision

A ZTX16 Cartridge has its own platform executable representation.

The Cartridge executable is **not defined as a .NET Assembly**.

The long-term architecture is:

**Source Language → ZTX16 Compiler/Toolchain → ZTX16 Executable → ZTX16 Runtime/VM**

C# is the first/reference development language, but it is not part of the ZTX16 runtime contract.

## 2. ZTX16 Executable

The executable portion of a Cartridge uses a proprietary/platform-defined ZTX16 executable format.

The format is intended to be:

- platform-defined;
- versionable;
- language-independent at runtime;
- suitable for validation before execution;
- executable by the ZTX16 VM;
- independent of .NET runtime requirements.

Exact binary layout, encoding, instruction representation, metadata tables and versioning scheme are implementation questions.

## 3. Virtual Machine

ZTX16 uses a small virtual machine designed specifically to execute Cartridge code.

The VM should provide only the minimal execution primitives required by the platform contract.

The VM is not intended to reproduce a general-purpose CPU or .NET runtime.

The VM is responsible for executing code and controlling access to platform capabilities through defined ZTX16 runtime interfaces.

## 4. Platform API boundary

Cartridge code interacts with the machine through ZTX16 APIs.

The VM/runtime provides the bridge between executable code and platform subsystems such as:

- Machine;
- Graphics;
- Audio;
- Input;
- Resources;
- Storage;
- Random;
- Level;
- Collision;
- other future ZTX16 platform services.

Host-specific APIs are outside the Cartridge execution contract.

## 5. Entry point

A Cartridge exposes a platform-defined entry point implementing the previously approved lifecycle contract:

- Initialize
- Tick
- Render

Deinitialization is a controlled shutdown operation and is not guaranteed after an unhandled failure.

The exact executable metadata used to identify the entry point is implementation-defined.

## 6. Runtime lifecycle

The host/runtime performs the following conceptual process:

1. Load Cartridge.
2. Validate Cartridge and executable compatibility.
3. Initialize VM/runtime execution state.
4. Locate and instantiate the Cartridge entry point.
5. Call Initialize.
6. Run the fixed runtime loop.
7. Call controlled shutdown/deinitialization when appropriate.
8. Release execution state and return control to the host/OS.

The runtime owns the loop and scheduling.

## 7. VM and timing

The VM does not replace the ZTX16 runtime timing model.

The runtime remains responsible for the 60 Hz simulation loop:

**Input → Game Logic → Audio Update → Render**

The VM executes Cartridge code during the appropriate lifecycle calls.

Audio sample generation continues at audio-device/sample timing inside the Audio Engine; it is not performed by the Cartridge at 60 Hz.

## 8. Memory and execution limits

The VM must provide bounded and controllable execution/memory behavior suitable for a platform cartridge.

Exact limits are implementation/profile decisions and must not be invented as public API guarantees until the machine profile is defined.

## 9. Language independence

The executable format belongs to ZTX16, not to C#.

Potential future toolchains may target the same executable contract from other languages.

Adding a language should not require redesigning the runtime platform APIs.

## 10. MVP implementation strategy

The MVP should implement the ZTX16 executable and VM directly rather than using .NET Assembly as the Cartridge format.

C#/.NET remains the implementation environment for the host, runtime and initial toolchain where practical.

The first C# compiler path may initially support only the language/runtime subset required to demonstrate the ZTX16 platform. This subset must not be mistaken for a permanent restriction on the ZTX16 executable format.

## 11. Approved MVP execution decisions

Approved November 2026.

### Compilation path

**C# source → Roslyn → IL assembly → ZTX16 translator → ZTX16 executable**

Roslyn performs parsing and type checking. The resulting IL assembly is an intermediate toolchain artifact only; it is never shipped in a Cartridge and never executed by the runtime. The translator accepts a defined subset of IL and rejects anything outside it with a build error.

Other source languages may target the ZTX16 executable directly without going through IL.

### VM model

- Stack-based VM. Its instruction set is designed for ZTX16, but stays close enough to IL that translation is mostly direct.
- Bytecode is verified at load time: stack balance, types of locals and arguments, branch targets, call targets. A cartridge that fails verification does not start.
- Managed heap with a tracing garbage collector (simple mark-sweep in the MVP). Heap size is bounded by the machine profile.
- Value types (structs) are supported in addition to reference types.

### Platform API binding

- Cartridge code reaches the machine only through a **syscall table**: numbered platform operations implemented by the runtime.
- The platform API is described once. From that description the toolchain generates both the C# SDK stubs that cartridge code compiles against and the VM-side bindings.
- The syscall table is the sandbox boundary: an operation that is not in the table does not exist for the cartridge.
- Platform APIs called from the VM should be coarse-grained (draw a sprite, a tile region or a rectangle) rather than per-pixel, so that hot loops run natively in the runtime.

### Initial C# subset

First iteration:

- `int`, `long`, `float`, `double`, `bool`, `char`, enums;
- arithmetic, comparison, logic, control flow;
- static and instance methods, fields, properties;
- structs, classes, single inheritance, virtual methods, interfaces;
- one-dimensional arrays, strings.

Planned next iterations, in rough order: generics and core collections (`List<T>`, `Dictionary<TKey,TValue>`), delegates and lambdas, exceptions, a minimal standard library.

Not planned for the MVP: reflection, `unsafe` code, P/Invoke, threads, `async`/`await`, LINQ, `dynamic`.
