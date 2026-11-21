# ZTX16 — Editor Environment

## General principle

All built-in editors are ZTX16 applications running inside the ZTX16 environment.

They use the same 256×256 display, pixel-perfect Graphics, Input, Font/Text and resource models as other ZTX16 applications.

Editors work on real ZTX16 resources. They must not introduce incompatible editor-only resource representations.

Third-party editors are equally valid if they honor the project/resource contracts.

## 1. Console / Terminal

MS-DOS-inspired shell and file manager.

Responsibilities:

- directory navigation;
- create/delete/rename files and folders;
- project folder creation;
- listing/inspection;
- import/export;
- launch projects/apps/cartridges;
- diagnostics/system commands;
- run project scripts.

Project scripts initially support simple sequential commands. Conditions, loops and variables are future extensions.

The Console is the project workflow hub. A separate Project/Cartridge Manager editor is not part of the initial model.

Typical editor shortcuts may launch Code, Graphics and Sound editors, but the exact shortcut system is implementation-defined.

## 2. Code Editor

Built-in lightweight IDE.

Responsibilities:

- open/create source files;
- syntax highlighting;
- error highlighting;
- completion;
- save;
- basic navigation;
- run/test integration.

C# is the reference language implementation.

The first version does not require a full desktop IDE feature set such as debugger, profiler, Git client, package manager or AI assistant.

## 3. Graphics Editor

One universal Graphics application with interconnected modes/tools for:

- Palette;
- Tiles/Tilemap;
- Sprite;
- Animation.

Conceptual workflow:

Palette → Tilemap/Tiles → Sprite → Animation

Resources remain separate runtime resource types.

The editor should support navigation between related resources.

## 4. Font Editor

Dedicated typography-oriented application.

Responsibilities:

- Font creation/editing;
- Glyph bitmap editing;
- character-to-glyph mapping;
- Advance X;
- Line Height;
- text preview using real ZTX16 text rendering semantics.

It may reuse isolated graphics editing primitives such as a pixel brush or color picker.

## 5. Sound / Music Editor

One application with three conceptual modes:

1. Voice/Instrument Editor
2. Sequence Editor
3. Song Editor

Instrument editor works with sound configuration and Voice-related playback preview.

Sequence editor is tracker-like and edits monophonic Sequences.

Song editor works with physical channel assignments, timeline events and shared LFO/Filter/FX configuration.

Resources remain real runtime resources.

## 6. Level Editor

Works on one logical Level Visual Layer at a time.

The editor displays Actors, their Visual Representations and Colliders in the logical Level coordinate space.

Suggested visualization mapping:

- physical Graphics Layer 0 — rasterized current logical Level Layer;
- physical Graphics Layer 1 — collider/debug visualization;
- physical Graphics Layers 2+ — editor UI and additional visualization.

This is an editor arrangement, not a platform binding.

Properties should conceptually expose:

- Actor ID;
- Transform;
- Visual Representation;
- selected resource;
- Collider.

There are no semantic Actor types.

## 7. Editor session state

Each editor is a persistent application session.

Switching editors does not reset the previous editor's state.

Session state may include:

- open resource/file;
- current mode;
- selection;
- viewport/scroll;
- playhead;
- preview state;
- editor-local settings.

Session state is distinct from resource data.

Resource edits are visible to other editors using the same resource.

## 8. Interoperability

Required conceptual workflows include:

- Graphics → Level → Graphics
- Code → Graphics → Code
- Instrument → Sequence → Song → Instrument
- Font → Code/Graphics

Editors should make these transitions natural without requiring repeated manual reopening.

## 9. Custom editors

Third-party developers may replace standard editors through project workflow/configuration.

A custom editor should follow the relevant Project Contract and resource contracts.

The built-in editors are reference tools, not mandatory runtime dependencies.

## 10. UI scope

This document intentionally does not prescribe exact windows, panels, button placement, pixel dimensions or visual layouts.

Cloud Code should implement practical UX consistent with the conceptual tool boundaries and 256×256 constraint.
