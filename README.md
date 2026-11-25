# ZTX16

Fantasy computer platform. The architecture and specifications are in [Documents/](Documents/README.md); decisions taken during implementation are in [Documents/IMPLEMENTATION_NOTES.md](Documents/IMPLEMENTATION_NOTES.md).

## Requirements

- .NET 10 SDK
- Windows or Linux (SDL3 native libraries come with the `ppy.SDL3-CS` package)

## Build, test, run

```
dotnet build ZTX16.sln
dotnet test ZTX16.sln
dotnet run --project src/ZTX16.Host -- [--scale 1..8] [--fullscreen]
```

The host currently runs the built-in diagnostic program:

| Input | Action |
|---|---|
| Arrows / WASD / D-pad / left stick | move the square (its height sets the tone pitch) |
| Space | toggle the test tone |
| 1–8 | toggle Display Layer 1–8 |
| Escape | exit |
| F11 | toggle fullscreen (host) |
| F10 | cycle window scale 1–4× (host) |

Red marker = lower-left (0,0), blue marker = upper-right. The top indicator row shows Space, mouse L/M/R, four gamepad slots, the tone, and a ticks-per-second bar.

## Layout

- `src/ZTX16.Platform` — platform subsystems; no host dependencies.
- `src/ZTX16.Host` — SDL3 reference host.
- `tests/ZTX16.Tests` — platform unit tests.
