# ZTX16 — Graphics API

## Display

- Logical resolution: 256×256 pixels.
- Pixel-perfect rendering.
- Final rasterization occurs on integer physical pixels.
- Viewport is a persistent 256×256 buffer.
- Viewport changes only through explicit drawing/transfer operations.
- Viewport does not follow Layers automatically.

## Palette

Global color space: 512 possible RGB 3:3:3 colors.

Runtime Palette:

- 32 Color values;
- ColorIndex 0..31;
- ColorIndex 0 = transparent;
- ColorIndex 1..31 = visible;
- palette is global to Graphics;
- physical Layers do not own palettes.

Conceptual API:

- `Graphics.SetPalette(palette)`
- `Graphics.Palette.GetColor(index)`
- `Graphics.Palette.SetColor(index, color)`

Invalid palette index is an error.

Public resource lookup uses resource type + string name, not numeric resource IDs.

## Physical Graphics Layers

Exactly eight persistent physical layers, 0 through 7.

Layer 0 is lowest; Layer 7 is highest. Runtime reordering is not supported.

A Layer is only a raster pixel buffer containing ColorIndex values.

A Layer does not own or retain:

- Tilemaps;
- Sprites;
- Actors;
- Level Layers;
- scene objects;
- provenance/history;
- persistent camera state;
- persistent scroll offset.

Drawing a Tile, Tile Region, Sprite, text, or other supported visual rasterizes immediately into the selected Layer.

Writing ColorIndex 0 is a real erase operation.

`Layer.Enabled` excludes the layer from final composition while preserving its buffer.

`Layer.Clear()` clears to 0.

`Layer.Fill(color)` fills the layer.

`Graphics.ClearLayers()` clears all physical layers.

## Layer to Viewport

`Layer.Draw(x, y)` is an explicit one-shot transfer/rasterization of the layer into the Viewport. There is no persistent binding.

The authoritative ZTX16 coordinate origin is lower-left.

A layer smaller than the Viewport repeats across the Viewport. A layer larger than the Viewport is clipped at Viewport boundaries. The Viewport itself does not wrap.

## Composition

For each Viewport pixel, physical Layers are considered from Layer 7 down to Layer 0. The first non-zero pixel is visible.

The final composed image is conceptually a ninth result layer; this is not a separately addressable runtime layer.

## Tiles and Tilemaps

Tile size: 8×8 pixels.

Tilemap: exactly 32×32 Tiles, therefore 256×256 pixels.

Tile IDs are zero-based within a Tilemap.

A Tilemap is an independent resource.

Tilemap storage is a one-dimensional row-major array. Storage order is not a coordinate-system exception.

Conceptual region operation:

- `DrawTileRegion(layerPosition, tilemapPosition, regionSize)`
- `regionSize` is expressed in Tiles.

There is no special Empty Tile ID. Transparency is represented by ColorIndex 0 in raster data.

## Sprites

A Sprite is a composition of Tiles. Its Tile IDs need not be adjacent in the source Tilemap.

A Sprite Instance may change individual Tile IDs at runtime.

A Sprite resource does not own persistent Palette or Transform state.

## PixelOffset

PixelOffset is a per-draw operation.

It cyclically shifts pixels inside each 8×8 Tile. It does not change Tile ID, Tilemap data, or the Tile resource.

An offset of `(8,0)` produces no visible change.

## Transform

Transform is per draw operation:

- FlipX
- FlipY
- Scale X/Y
- Rotation
- Pivot

Order is fixed:

Flip → Scale → Rotation → pixel-perfect rasterization.

Scale may be integer or fractional and non-uniform.

Rotation is expressed in degrees.

Pivot defaults to center. Normalized pivot `(0,0)` is lower-left and `(1,1)` is upper-right. Nine common presets plus arbitrary normalized coordinates are supported conceptually.

The draw position represents the lower-left corner of the final transformed object.

## Level relationship

Graphics knows nothing about Level, Actors or logical Level Layers.

Level and other higher-level systems may rasterize into any physical Graphics Layer. Multiple logical layers may be rasterized sequentially into the same physical layer.

This dependency is one-way: Level → Graphics.
