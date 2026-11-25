# ZTX16 — Host and Backend Architecture

## Status

Architecturally approved for MVP.

## 1. Host platforms

The first ZTX16 host implementation targets:

- Windows
- Linux

Other host platforms remain possible future targets.

## 2. Host technology

The reference host is implemented in C#/.NET.

SDL3 is the host integration layer for the MVP.

The ZTX16 platform API must not expose SDL3 types directly. SDL3 belongs behind host/backend boundaries.

## 3. Graphics backend

MVP graphics stack:

**ZTX16 Graphics Runtime → SDL3/host presentation → OpenGL**

The Graphics API is independent of OpenGL. OpenGL is an implementation backend used to present the ZTX16 raster output on the host.

The architecture should leave room for additional graphics backends later without changing the public ZTX16 Graphics API.

ZTX16 is fundamentally a 2D pixel platform; the MVP does not require a general-purpose 3D renderer.

## 4. Audio backend

MVP audio stack:

**ZTX16 Audio Engine → SDL3 Audio → Host audio device**

The ZTX16 Audio Engine owns synthesis, samples/wavetables, envelopes, filters, effects, routing and mixing according to the ZTX16 audio model.

SDL3 is responsible only for connecting the generated PCM stream to the host audio device.

The public ZTX16 Audio API must not expose SDL3 audio types.

## 5. Input backend

MVP input stack:

**Host devices → SDL3 input → ZTX16 Input Model → Cartridge**

SDL3 supplies host-level keyboard, mouse, gamepad and other supported device information/events.

The ZTX16 Input Model translates these into the already-defined platform concepts:

- physical devices;
- digital state and transitions;
- analog values/change events;
- physical keyboard input;
- text input;
- optional abstract inputs.

SDL3 device names, event structures and platform-specific identifiers must not become part of the public ZTX16 cartridge API.

## 6. Display and scaling

ZTX16 has a logical display resolution of 256×256 pixels.

The host may display the logical output in:

- windowed mode;
- fullscreen mode.

MVP scaling is integer pixel scaling, for example:

- 1× = 256×256;
- 2× = 512×512;
- 3× = 768×768.

Non-integer scaling is not required for the MVP because it conflicts with the pixel-perfect rendering model.

When the selected host display is not an exact integer multiple of 256×256, unused space may remain as borders around the logical display.

## 7. Host/backend boundary

Host implementations are responsible for translating host facilities into ZTX16 platform facilities.

The cartridge must not depend on:

- SDL3;
- OpenGL;
- operating-system window APIs;
- host audio APIs;
- host input APIs;
- host filesystem APIs.

This boundary is essential to keeping ZTX16 a platform rather than a wrapper around a particular host technology.

## 8. MVP principle

Use the simplest reliable host implementation that preserves the ZTX16 platform contract.

Do not expose backend-specific functionality merely because the backend provides it.
