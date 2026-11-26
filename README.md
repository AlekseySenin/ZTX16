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

HelloSquare: arrows / WASD / D-pad / left stick move a ship sprite (from an indexed PNG tilemap) with an animated engine flame; it turns to face its direction, slides along the brick walls of the "stage" level and collects its gems (scattered randomly from the second launch; the best time is saved), with sound-effect voices and a background song; text uses the built-in font; Space toggles the scrolling background, P cycles palettes, C shows colliders, M toggles the music, Escape exits.

Resource files in a project: palettes `*.pal` (text), tilemaps `*.png` (indexed color, up to 256×256), sprites `*.sprite`, fonts `*.font`, animations `*.anim`, levels `*.level`, wavetables `*.wavetable`, instruments `*.instrument`, sequences `*.sequence` and songs `*.song` (JSON), samples `*.wav`. See [IMPLEMENTATION_NOTES.md](Documents/IMPLEMENTATION_NOTES.md) for the formats.

A cartridge is one class implementing `ZTX16.ICartridge` (`Initialize`, `Tick`, `Render`, optional `Deinitialize`). The toolchain supports numeric types, `bool`, `char`, enums, classes with inheritance and virtual methods, interfaces, generics, structs, arrays, strings (including `ToString()` of numbers) and null, on a garbage-collected heap. Collections come from `ZTX16.Collections` (`List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Stack<T>`, `Queue<T>`). Structs (with value semantics) and `ref`/`out`/`in` parameters are supported. Lambdas, exceptions, boxing, `int?` and tuples are not supported yet and are reported as build errors.

## ZTX16.exe — run cartridges without a terminal

```
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

This produces `dist/ZTX16/ZTX16.exe` — one self-contained file with everything inside (the .NET runtime, SDL3, the Console, the code editor and the cartridge compiler; nothing to install) — and `dist/ZTX16/cartridges/` with the sample cartridges. The exe works on its own: copy it anywhere. Then:

- **double-click `ZTX16.exe`**: boots into the code editor with your last file (on the first start: a new Hello World project); **F1** switches to the Console (type HELP), **F2** back to the editor; create, build and run projects in your workspace (`Documents\ZTX16`); F12 returns from a running game;
- **drag a `.ztx` onto `ZTX16.exe`**, or use **Open with → ZTX16.exe** on a `.ztx` file (tick "always" to make `.ztx` files open with a double-click);
- from a terminal: `ZTX16.exe game.ztx [--scale N] [--fullscreen]` — messages appear in that terminal.

The window title shows the cartridge name. Without a terminal, errors (a broken cartridge, a cartridge failure) are shown in a message box. The executable is not code-signed, so Windows SmartScreen or Smart App Control may ask before the first start.

## Host options

```
dotnet run --project src/ZTX16.Host -- [--scale 1..8] [--fullscreen] [--workspace <dir>] [<file.ztx | file.ztxe> | --exe <file> | --vm-demo | --diagnostic]
dotnet run --project src/ZTX16.Host -- --build <project>
```

Without a cartridge the host boots into the ZTX16 Console (system apps in `system/`, built from `apps/`; if they are missing it asks for a cartridge with the file dialog); `--diagnostic` runs its built-in diagnostic program and `--vm-demo` a hand-assembled bytecode demo.

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
- `src/ZTX16.Host` — SDL3 reference host (`ZTX16.exe`).
- `apps/Console` — the ZTX16 Console (system app: shell, file manager, project workflow).
- `apps/Code` — the ZTX16 code editor (system app: C# editing, compile with errors and warnings, run).
- `apps/Graphics` — the ZTX16 graphics editor (system app: tilemap pixels, palettes, import/export).
- `tools/publish.ps1` — publishes the single-file `ZTX16.exe` with the system apps, build support and sample cartridges to `dist/`.
- `samples/` — example cartridges.
- `tests/ZTX16.Tests` — unit, VM, toolchain and differential tests.
