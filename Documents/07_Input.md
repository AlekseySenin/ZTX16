# ZTX16 — Input API

## Principles

ZTX16 models devices, not players.

The platform does not assign controllers or keyboards to players. Game code decides how physical inputs map to gameplay.

Device indices are current slots, not persistent hardware identities.

Hot-swapping is supported.

## Digital inputs

Digital inputs expose:

- current state: Up/Down;
- transitions: Pressed/Released.

This applies to controller buttons, D-pad directions, stick buttons, physical keyboard keys and mouse buttons.

Multiple digital inputs may be active simultaneously. There is no artificial simultaneous-input limit.

## Analog inputs

Analog inputs expose current values and change events.

Sticks: -1 … +1 per axis.

Triggers: 0 … +1.

Dead zones and analog-to-digital interpretation are outside the hardware model and belong to game/mapping code.

## Controller naming

Manufacturer-neutral base semantics include:

- D-pad Up/Down/Left/Right
- Action 1–4
- Left/Right Shoulder
- Left/Right Stick Press
- Start
- Select

Canonical name is `Select`, not `Back`.

## Keyboard

Gameplay keyboard input is based on physical key identity, not the character produced by the active OS keyboard layout.

The reference naming follows physical US-QWERTY positions.

Keyboard gameplay input and text input are separate systems.

## Mouse

Base mouse data:

- absolute X/Y;
- relative X/Y;
- left/right/middle buttons;
- wheel delta/change.

Mouse/pointer coordinates use the ZTX16 lower-left origin when exposed as ZTX16 screen coordinates.

The older draft's top-left mouse convention is obsolete and must not be implemented.

## Abstract inputs

Abstract inputs are convenience mappings, not mandatory hardware architecture.

Multiple physical sources may map to one abstract input.

No automatic Player ownership exists.

## Text input

Text input uses the platform-defined 256-code ZTX16 Character Set, not Unicode, in the base runtime model.

Unicode/IME handling may exist at host/editor/import boundaries but is not the base cartridge text model.

## System events

Runtime/System Events are conceptually separate from physical Input.

The base cartridge architecture does not expose suspend/resume/focus/device-lifecycle events to cartridge code.

Device connection/disconnection remains an OS/runtime concern required to support hot-swappable devices.

## Deferred implementation details

- exact C# input types;
- event signatures;
- mapping syntax;
- device enumeration representation;
- host-window scaling behavior;
- exact text-input implementation.
