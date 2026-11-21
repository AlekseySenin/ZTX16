# ZTX16 — Font and Text API

## Font resource

Font is a separate bitmap resource.

Font bitmap data uses the existing Tilemap model. There is no separate bitmap storage format for glyphs.

All glyphs in a Font share one common Glyph size, expressed in Tiles, such as 1×1, 1×2 or 2×2 Tiles.

A Glyph is identified by the Tile Index at which its bitmap region begins in the Font Tilemap.

## Character mapping

Character Index and Glyph Index are distinct.

Font contains a character-to-glyph table.

Multiple characters may reference the same Glyph. A Font may contain only one unique Glyph if desired.

Unused characters may reference an empty Glyph.

Every character still has its own Advance X.

Space and Tab are ordinary characters, not special object types.

## Character set

The base ZTX16 character set contains 256 codes.

The first 128 codes are ASCII-compatible. The extended 128 codes are platform-defined and may contain Cyrillic, Katakana and other selected characters.

Exact allocation is an implementation/data-definition task, not a Unicode model.

Unicode is not part of the base runtime text model.

## Typography

Each character has:

- Glyph reference;
- Advance X in pixels.

Font also has Line Height, independent of Glyph height.

Baseline positioning and advanced typography are outside the base API.

Advance is independent of bitmap width, allowing monospaced and proportional fonts.

## Glyph color

Base Glyphs are single-color bitmap shapes.

Font does not store display colors per pixel.

The Text Renderer supplies ColorIndex for visible glyph pixels.

## Text commands

The base text stream has two control commands:

- New Line
- Set Color

Reset Color is not a separate command.

Tab remains a normal character.

Control commands are embedded in the text stream rather than represented by separate TextCommand objects.

## Rendering

Text renders into a selected Graphics Layer.

Three conceptual modes exist:

1. Unbounded sequential rendering.
2. Width-constrained rendering.
3. Rectangle-constrained rendering.

`WordWrap` is a Boolean renderer property.

When enabled, wrapping attempts to preserve whole words using Space/Tab. A word longer than the available width may be split character-by-character.

Rectangle-constrained rendering stops when text reaches the lower boundary of the rectangle.

Text uses the normal Graphics coordinate convention and rasterization model.

## Editor requirements

Font Editor must support:

- Font resource creation/editing;
- individual Glyph bitmap editing;
- character-to-glyph mapping;
- Advance X;
- Line Height;
- text preview using actual ZTX16 rendering semantics.
