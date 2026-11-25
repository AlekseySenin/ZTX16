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
- The eight-channel hardware model arrived in Audio-1 (below). The host diagnostic sine tone is still
  available for checking the device path.

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

## VM-5 — structs and ref/out/in

### Structs: heap instances with value semantics

- A struct value is a reference to a value-type instance on the heap (`VmClass.IsValueType`). A **null
  reference is the default value**: every field reads as zero (`LoadField` on a null value-type instance
  yields 0), so `default`, `new S[n]` and fresh fields cost no allocation.
- **Every storage location owns its own instance.** Assigning a struct to a local, argument, field, static
  field or array element copies the fields into the location's instance (`Materialize*` creates it on
  first use, `CopyInto` copies; nested struct fields are copied deeply). Two locations therefore never share
  an instance, and writes through an address (`v.X = 1`, `arr[i].X = 1`, `obj.Pos.X = 1`, struct methods
  mutating `this`) affect exactly that location.
- **The address of a struct location is its instance.** `ldloca`/`ldflda`/`ldelema`/`ldsflda` on a struct
  materialize and push the instance; struct instance methods receive it as `this`.
- Reading a struct pushes the location's instance without copying; the copy happens when the value is
  stored. A struct parameter is shared with the caller until the callee writes to it or takes its address —
  then the callee copies it on entry.
- **Known deviation:** a struct value read and then, within the same expression, overwritten in its source
  location before being stored may observe the new contents (e.g. `F(a, a = b)`). Ordinary code is not
  affected.
- Struct equality used by `ZTX16.Collections` (`Intrinsics.Equal`/`Hash`, e.g. dictionary keys) compares
  fields by value, like the default `ValueType.Equals`. **User overrides of `Equals`/`GetHashCode` on
  structs are not used** by collections.
- Every `new S(...)` allocates a temporary instance; struct-heavy inner loops create garbage. Scalar
  replacement is a possible future optimization.
- Not supported: boxing (struct → `object`/interface), `constrained.` calls (`ToString()`/`Equals()` on a
  struct or generic `T`), `Nullable<T>` (`int?`), tuples (`ValueTuple`), ref fields, explicit layouts.

### ref / out / in and ref locals

- A managed pointer to a non-struct location is a **pointer object**: to a field, an array element, a
  global, or a **cell**. Locals and parameters whose address is taken (non-struct) are moved into cells
  for the whole method, so the variable and all pointers to it always agree.
- Pointers record the declared type of the location, so writes narrow correctly (`stind.i1` into a `byte`
  stores 255, not -1).
- Supported: `ref`/`out`/`in` parameters of any supported type, `ref` locals, `ref` to fields, array
  elements and static fields, `ldind`/`stind`/`ldobj`/`stobj`/`initobj`.
- `Dictionary.TryGetValue` is now available.

### Other

- Constant array initializers (`new int[] { 1, 2, 3 }`, compiled to `RuntimeHelpers.InitializeArray` with
  embedded data) are expanded into element stores.
- Executable format version 3 (value-type flag and nested value fields per class).

## Cartridge-1 — Project → Cartridge

### Project

- **Project Config:** `ztx16.project.json` at the project root (JSON; comments and trailing commas
  allowed). Fields: `name`, `gameId` (lower-case letters, digits, `.`, `-`, `_`; the Storage namespace),
  `version`, `language.name` (`csharp`), `directories` (category → relative directory).
- **Categories:** `code` (required) and one per resource type: `palettes`, `tilemaps`, `sprites`, `fonts`,
  `animations`, `samples`, `wavetables`, `instruments`, `sequences`, `songs`, `levels`. New projects get the
  standard mapping (same names as directories); developers may change it.
- Sources: every `*.cs` under the code directory (recursively). Resources: files with the type's extension
  under its directory (recursively); the **resource name is the file name without extension**, nested
  directories are organizational only, and duplicate names within a type are a build error.
- Unknown files, files in unmapped directories, files with other extensions and anything under `bin/` or
  `obj/` are ignored and never reach a cartridge. Missing directories are normal.
- A `.csproj` may sit in the project for IDE support; it is an unrelated file to ZTX16. Importing
  `ZTX16.Cartridge.targets` makes `dotnet build` run `ztx16c build` on the project.

### Resource formats (this iteration)

- **Palette:** project file `*.pal`, text, exactly 32 lines `R G B` (0..7), `#` comments, entry 0 is the
  backdrop. Cartridge data: 32 little-endian u16 (RGB 3:3:3). Other resource types have no project format
  yet; their directories are accepted but their files are ignored.

### Cartridge file (`.ztx`)

- `"ZTXC"`, u16 format version (1), then a deflate stream with: the Project Config text (verbatim), the
  ZTX16 executable, resources (type, name, data) and the project sources (path, bytes).
- Sources and the config are packaged so a cartridge can be **extracted back into an ordinary project**;
  extracting and rebuilding produces the same executable (the build is deterministic).
- Extraction refuses non-empty targets and any path that would leave the target directory.

### Toolchain (`ztx16c`)

- `ztx16c new <dir> [--name N] [--id ID]`, `ztx16c build <project> [-o file.ztx]` (default
  `<project>/bin/<Name>.ztx`), `ztx16c extract <file.ztx> <dir>`, and the earlier `ztx16c <assembly.dll>`.
- `build` compiles with **Roslyn inside the toolchain** (C# 12, nullable enabled, Release, deterministic)
  against `System.Runtime` and `ZTX16.Sdk`, together with the core library sources shipped next to the
  toolchain (`core/`). No `.csproj` is needed.
- Errors are reported as `file(line,col): error …` for C# and `error ZTX0001: …` otherwise.

### Runtime

- The host runs a `.ztx` directly (`--exe file.ztx`); its resources become the session's `ResourceStore`.
- SDK: `ResourceType` (ABI enum), `Resources.Exists/Count/Name` (names in ordinal order),
  `Graphics.UsePalette(name)` (a missing palette stops the cartridge).
- Syscalls receive an `ISyscallContext` to read string arguments and return strings; `string` is part of
  the syscall ABI (as a reference).

## Graphics-2 — tiles, sprites, fonts and text

### Resources

- **Tilemap** (`tilemaps/*.png`): an **indexed-color PNG** (bit depth 1/2/4/8, no interlacing) up to
  256×256 with sides that are multiples of 8; the palette index of each pixel is its ColorIndex (0 =
  transparent, at most 31). The image sits at the lower-left of the tilemap, so **tile 0 is the image's
  lower-left tile** and `id = row * 32 + column` with row 0 at the bottom (the ZTX16 origin rule). The cartridge
  stores 65 536 ColorIndex bytes (bottom row first) plus the PNG's first 32 palette colors, so extraction
  writes back a PNG with the artist's colors. The toolchain has its own PNG codec (no dependency).
- **Sprite** (`sprites/*.sprite`): JSON `{ "tilemap": …, "width": …, "tiles": [ … ] }`, tile IDs listed
  **bottom row first**, grids up to 32×32 tiles.
- **Font** (`fonts/*.font`): JSON with `tilemap`, `glyphWidth`/`glyphHeight` (tiles, 1..4), `lineHeight`
  and default `advance` (pixels), `ranges` (`first`, `last`, `tile`) and per-character `characters`
  overrides (`char` or `code`, `tile`, `advance`). A range lays glyphs out left to right from its start tile
  and wraps at the right edge to the glyph row **below** — the way font sheets are drawn. A glyph is the
  region that starts at its tile (lower-left) and extends right and up.
- Sprites and fonts must reference an existing Tilemap resource (build error otherwise).
- **Built-in system font:** 8×8 monospaced glyphs for ASCII 32..126 (data from the public-domain
  `font8x8_basic`), line height 9, selected with the font name `""`. It needs no resources, so text works
  in every cartridge (and later in the Console and editors).

### Drawing

- `Graphics.DrawTiles` (fast path), `Graphics.DrawTilesTransformed` and `Graphics.DrawTileGrid` (arbitrary
  tile IDs drawn as one image; used by `Sprite`). Transform order is Flip → Scale → Rotation, around a
  normalized pivot that lands on the draw position; rotation is counter-clockwise; PixelOffset shifts
  cyclically inside every tile.
- The rasterizer maps each destination pixel center back into the source (nearest neighbour). Scale 1 with a
  rotation that is a multiple of 360° uses an exact copy path; 90°/180°/270° use exact sines and cosines, so
  right-angle rotations are pixel-perfect. Other angles use `MathF` and may differ in the last pixel across
  host math libraries.
- Core library (`ZTX16` namespace, compiled into cartridges): `struct Transform` (with `Flipped`, `Scaled`,
  `Rotated`, `WithPivot`, `Centered`), `Tiles.Draw`, `class Sprite` (Sprite Instance loaded from a resource;
  tiles changeable at runtime), `TextStyle.Color(index)`.

### Text

- `Text.Draw`, `Text.DrawBox` (width limit, optional word wrap keeping whole words and splitting longer
  ones, height limit), `Text.Width`, `Text.LineHeight`.
- The position is the **lower-left corner of the first line**; further lines go down. In a box with a height,
  the first line sits at the top and drawing stops at the first line that would cross the bottom edge.
- Text stream commands: New Line = `'\n'`; Set Color = `'\u0001'` followed by one character whose code is
  the ColorIndex (`TextStyle.Color` builds it). Tab is an ordinary character.
- **Character set (provisional):** codes 0..127 are ASCII; 128..255 currently follow Latin-1 until the
  platform-defined extended set (Cyrillic, Katakana, …) is allocated; characters above U+00FF render as `?`.

### Toolchain

- The C# compiler concatenates strings with characters through `ReadOnlySpan<char>` (`"a" + c`, also
  `"a" + c.ToString()` in Release builds). The toolchain models `ReadOnlySpan<char>` as a string: the
  implicit conversion is a no-op, `new ReadOnlySpan<char>(ref c)` becomes a one-character string, and
  `string.Concat` over spans becomes string concatenation.
- Syscalls can read `int[]` arguments (`ISyscallContext.ReadInt32Array`); arrays are part of the ABI as
  references.

## Animation and collision

### Animation

- Animation resource: `animations/<name>.anim` (JSON), e.g.
  `{"frames":[{"duration":5,"tiles":[[0,0],[1,1]]},{"duration":5,"tiles":[[0,2],[1,3]]}]}`. Each frame has a
  duration in ticks (≥ 1) and a list of `[spriteTileIndex, tileId]` swaps; the index addresses the sprite's
  tiles row-major, **bottom row first** (as in `.sprite`), up to 1023; tile IDs are 0..1023. A frame changes
  only the tiles it lists. Animations name no tilemap or sprite: the sprite they are played on decides.
- Syscalls `animation.frame_count/duration/swap_count/swap_index/swap_tile` read the resource; the Core
  library builds on them: `Animation.Load(name)` copies the frames into the cartridge heap.
- `AnimationPlayback(animation, sprite, mode)` is the runtime state: `Frame`, `FrameTime`, `PlaySpeed`
  (≥ 0, fractional allowed), `Mode` (`Loop` / `OneShot`), `Play`/`Pause`/`Resume`. It only changes the
  sprite's tiles (`Sprite.SetTileAt`); it never draws. `Update()` once per tick adds `PlaySpeed` to
  `FrameTime` and advances as many frames as needed; `OneShot` stops on the last frame, keeping its tiles.
- No callbacks: `Started`, `Looped` and `Finished` are true during the one `Update` in which that happened.
- A frame that sets a tile index the sprite does not have fails the cartridge.

### Collision

- `Collider(x, y, width, height)` with `Enabled`; area is half-open `[X, X+W) × [Y, Y+H)`, so rectangles that
  only touch do not overlap. `ColliderSet` is a game-defined group (a collider may be in several sets).
- `Collision.Overlaps`, `Contains`, `Overlapping(rect|collider, set, results?)`: disabled colliders never
  match; a collider never matches itself.
- `Collision.Move(moving, targetX, targetY, set)` is a swept AABB test that does not move anything. It returns
  a `CollisionHit` with the earliest collider, the contact point, the face normal (−1/0/1) and the safe
  position (touching, not overlapping; the target if nothing was hit). Ending exactly in contact is not a hit.
  Starting inside a collider reports it at once with a zero normal and the start as safe position.
- No physics, sliding or response: the game decides. HelloSquare slides along walls by moving one axis at a
  time.
- `CollisionDebug.Draw(layer, collider|set, color)` draws 1-pixel outlines.
- The Core project disables implicit usings (they made `List<T>` ambiguous with `ZTX16.Collections.List<T>`).

## Level

- Level resource: `levels/<name>.level` (JSON). A level has logical layers; each layer has a unique name, an
  optional tilemap (required when it has Tile or Tile Region actors), a size in screens (`width`/`height`,
  1..16, default 1, i.e. multiples of 256×256 pixels) and actors in drawing order:
  ```json
  { "layers": [ { "name": "walls", "tilemap": "art", "width": 1, "height": 1, "actors": [
      { "id": "floor", "x": 0, "y": 0, "region": [16, 16, 32, 1], "collider": [0, 0, 256, 8] },
      { "id": "gem", "x": 40, "y": 40, "tile": 5, "rotation": 45, "pivot": [0.5, 0.5] },
      { "id": "hero", "x": 120, "y": 32, "sprite": "ship", "flipX": true, "scale": 2 },
      { "id": "exit", "x": 240, "y": 0, "collider": [0, 0, 16, 256] } ] } ] }
  ```
  An actor has an ID unique in the level, a position, an optional transform (`flipX`, `flipY`, `scale` as a
  number or `[x, y]`, `rotation`, `pivot`; same meaning as `Transform`), at most one visual — `tile` (ID),
  `region` (`[column, row from bottom, width, height]` in tiles of the layer's tilemap) or `sprite` (Sprite
  resource name) — and an optional `collider` `[x, y, width, height]` relative to its position.
- The build checks that layer tilemaps and actor sprites exist.
- Runtime transport: the platform serves the level as a flat stream of 32-bit values plus a string table
  (`level.value_count/value/float_value/string`, layout in `LevelFormat`); the Core library decodes it. This
  keeps the syscall surface small and the object model entirely in cartridge code.
- Core: `Level.Load(name)` creates mutable runtime state; the resource never changes and loading again gives
  a fresh copy. `Level`: `Name`, `LayerCount`, `GetLayer`, `FindLayer(name)`, `FindActor(id)`, `Colliders`.
  `LevelLayer`: `Name`, `Index`, `Tilemap`, `Width`/`Height` (pixels), `GetActor`, `ActorCount`, `Colliders`,
  `Rasterize(graphicsLayer, offsetX, offsetY)`. `Actor` (the Actor Runtime Reference): `Id`, `Layer`, `X`/`Y`
  + `SetPosition` (moves its collider by the same amount), `Transform`, `Visible`, `Visual` + `SetTile`,
  `SetTileRegion`, `SetSprite`, `ClearVisual`, `Sprite` (its own Sprite Instance, so an `AnimationPlayback`
  can animate it), `Collider` (settable/removable; the layer and level `Colliders` sets follow), `Draw`.
- `Rasterize` draws the visible actors in order and does not clear the target, so several logical layers
  compose into one Graphics Layer. Drawing is clipped to the target; when the logical layer is larger than
  the target Graphics Layer the cartridge gets a warning (`Machine.Warn`).
- `Machine.Warn(message)` (`machine.warn`) reports non-fatal problems. The runtime keeps distinct messages
  (`Machine.Warnings`, at most 256) and raises `Warned` once per message; the reference host prints
  `ZTX16: warning: …` to stderr.
- HelloSquare: the "stage" level has a `walls` layer (brick Tile Region actors with colliders, rasterized once)
  and a `gems` layer (tile actors with colliders, rasterized every frame); touching a gem hides it and removes
  its collider.

## Audio-1 — low-level audio hardware

- `Audio` (SDK, `audio.*` syscalls) is the flat hardware API; enums `Waveform`, `LfoWaveform`,
  `ModulationDestination` (Pitch, Volume), `ChannelState`, `FilterMode`, `EffectType`, `ProcessorUnit`,
  `ProcessorParameter`.
- **Timing and threads.** Control calls are validated at once on the runtime thread (bad channel/unit
  indices, unsupported operations for a channel kind and non-finite numbers trap the cartridge; out-of-range
  numbers are clamped) and queued. The Audio Update phase of the tick applies the whole batch under the
  engine lock, so a tick's changes start together; calls made in Initialize or Render apply at the next Audio
  Update. The audio thread renders under the same lock. Read-backs (`GetChannelState`, `GetEffectivePitch`,
  `GetEffectiveVolume`, `GetLfoValue`, `GetAdsrValue`) return the state after the last rendered audio.
- **Channels.** 0–5 Synth (Sine, Triangle, Saw, Reverse Saw, Square, Noise — naive, non-band-limited; noise
  is a deterministic xorshift value changing at the channel frequency), 6–7 Sample/Wavetable. Frequency
  0.01..20000 Hz, volume 0..1, constant-power pan, mute (the channel keeps running), finalizing mixer volume.
  Defaults: 440 Hz, volume 1, ADSR 5 ms / 0 / 1 / 50 ms on Volume with amount 1.
- **Voice state.** NoteOn starts or retriggers Attack from the current envelope level (oscillator phase
  resets only from Idle; a sample always restarts); NoteOff starts Release from the current level; Stop is
  immediate. Release ending → Idle. A sample channel without a source ignores NoteOn.
- **Samples** play at 44.1 kHz × Playback Rate (0..64) with linear interpolation; one-shot by default
  (`UseSample` resets looping); reaching the physical end → Idle. `SetSampleLoop(loop, start, end)` (end −1 =
  sample end). **Wavetables**: 64 samples, linear interpolation, cyclic.
- **Modulation**, fixed order Base → LFO1 → LFO2 → ADSR. Pitch amounts are semitones (±96, summed, then
  `2^(s/12)`); volume amounts are factors (LFO: `v·(1 + a·lfo)`; ADSR: `v·(1 − a + a·env)` for a ≥ 0,
  `v·(1 + a·env)` for a < 0); the result is clamped. Each channel ADSR has one destination
  (`RouteAdsr`); each LFO→destination connection has its own amount (0 disconnects). LFOs: Triangle, Saw,
  Reverse Saw, Square, 0.01..60 Hz, output −1..1, always running.
- **Filters** (2): stereo TPT state-variable, LP/HP/BP, cutoff 20..20000 Hz (≤ 0.45 × output rate),
  resonance 0..1. LFOs modulate Cutoff (semitones) and Resonance (additive).
- **FX** (2): Chorus (Delay 1..40 ms, Depth 0..20 ms, Mix) — an LFO routed to Depth swings the delay by
  ±Depth ms, so without one it is a static short delay; Delay (Time 1..1000 ms, Feedback 0..0.95, Mix);
  Reverb (Freeverb-style: 4 combs + 2 allpasses per side; Size 0..1, Decay 0..1, Mix). LFO amounts on FX
  parameters are additive in the parameter's units. Changing the type clears the buffers.
- **Routing.** `AssignProcessor(unit, target)`: −1 none, 0..7 insert on that channel, 8 (`Audio.Global`)
  after mixing; reassigning replaces (and clears the unit's state). Units run in the order Filter1, Filter2,
  FX1, FX2 wherever they are assigned.
- **Signal path.** source → ADSR/volume/pan → inserts → mixer volume → sum → global units → master volume →
  safety limiter (instant attack, ~0.1 s release) → clamp to −1..1.
- **Resources.** Sample: project `.wav` (PCM 8/16/24/32-bit or float, any channel count/rate; the build
  downmixes, resamples linearly to 44.1 kHz and quantizes to signed 8-bit, max 60 s); cartridge i32 frame
  count + bytes; extract writes an 8-bit mono WAV. Wavetable: project `.wavetable` JSON
  `{ "samples": [64 integers −128..127] }`; cartridge 64 bytes.
- HelloSquare: gem chime (square, two notes), wall thud (noise), engine hum while moving (wavetable + LFO
  vibrato + insert low-pass), fanfare sample for the last gem, global reverb; M mutes (master volume 0).
- Not yet: the high-level model (Instrument, Voice, allocator, Sequence, Song).
