# ZTX16

Fantasy computer platform. The architecture and specifications are in [Documents/](Documents/README.md); decisions taken during implementation are in [Documents/IMPLEMENTATION_NOTES.md](Documents/IMPLEMENTATION_NOTES.md).

## Requirements

- .NET 10 SDK
- Windows or Linux (SDL3 native libraries come with the `ppy.SDL3-CS` package)

## Build and test

```
dotnet build ZTX16.sln
dotnet test ZTX16.sln
```

## Make and run a cartridge

A ZTX16 Project is a directory with `ztx16.project.json`, C# code in `code/` and resources such as palettes in `palettes/`. `ztx16c` compiles the code, translates it for the ZTX16 VM and packs everything into one cartridge file (`.ztx`), which can also be extracted back into a project. See [samples/HelloSquare](samples/HelloSquare/code/Game.cs).

```
dotnet run --project src/ZTX16.Toolchain -- new MyGame
dotnet run --project src/ZTX16.Toolchain -- build MyGame          # → MyGame/bin/MyGame.ztx
dotnet run --project src/ZTX16.Host -- --exe MyGame/bin/MyGame.ztx
dotnet run --project src/ZTX16.Toolchain -- extract MyGame/bin/MyGame.ztx MyGameCopy
```

The sample also has a `.csproj` for IDE support; `dotnet build` on it builds the cartridge:

```
dotnet build samples/HelloSquare
dotnet run --project src/ZTX16.Host -- --exe samples/HelloSquare/bin/Debug/net10.0/HelloSquare.ztx
```

HelloSquare: arrows / WASD / D-pad / left stick move a ship sprite (from an indexed PNG tilemap) with an animated engine flame; it turns to face its direction, slides along the brick walls of the "stage" level and collects its gems; text uses the built-in font; Space toggles the scrolling background, P cycles palettes, C shows colliders, Escape exits.

Resource files in a project: palettes `*.pal` (text), tilemaps `*.png` (indexed color, up to 256×256), sprites `*.sprite`, fonts `*.font`, animations `*.anim` and levels `*.level` (JSON). See [IMPLEMENTATION_NOTES.md](Documents/IMPLEMENTATION_NOTES.md) for the formats.

A cartridge is one class implementing `ZTX16.ICartridge` (`Initialize`, `Tick`, `Render`, optional `Deinitialize`). The toolchain supports numeric types, `bool`, `char`, enums, classes with inheritance and virtual methods, interfaces, generics, structs, arrays, strings (including `ToString()` of numbers) and null, on a garbage-collected heap. Collections come from `ZTX16.Collections` (`List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Stack<T>`, `Queue<T>`). Structs (with value semantics) and `ref`/`out`/`in` parameters are supported. Lambdas, exceptions, boxing, `int?` and tuples are not supported yet and are reported as build errors.

## Host options

```
dotnet run --project src/ZTX16.Host -- [--scale 1..8] [--fullscreen] [--exe <file.ztx | file.ztxe> | --vm-demo]
```

Without `--exe` the host runs its built-in diagnostic program; `--vm-demo` runs a hand-assembled bytecode demo.

| Key | Action |
|---|---|
| F11 | toggle fullscreen (host) |
| F10 | cycle window scale 1–4× (host) |

Diagnostic program: arrows / WASD / D-pad / left stick move the square (its height sets the tone pitch), Space toggles the test tone, 1–8 toggle Display Layers, Escape exits. Red marker = lower-left (0,0), blue marker = upper-right.

## Layout

- `src/ZTX16.Sdk` — cartridge API (`ZTX16.*`); `[Syscall]` declarations are the platform ABI.
- `src/ZTX16.VM` — executable format, verifier, interpreter.
- `src/ZTX16.Abi` — reads the ABI from the SDK for runtime and toolchain.
- `src/ZTX16.Platform` — runtime subsystems (`ZTX16.Platform.*`); no host dependencies.
- `src/ZTX16.Core` — core library (`ZTX16.Collections`), compiled into every cartridge as source.
- `src/ZTX16.Cartridge` — Project Config, project discovery, resource formats, cartridge file, extraction.
- `src/ZTX16.Toolchain` — `ztx16c` (new / build / extract / translate), IL → ZTX16 translator, cartridge build targets.
- `src/ZTX16.Host` — SDL3 reference host.
- `samples/` — example cartridges.
- `tests/ZTX16.Tests` — unit, VM, toolchain and differential tests.
