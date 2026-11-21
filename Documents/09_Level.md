# ZTX16 — Level API

## Level

Level is a high-level immutable cartridge resource describing a practical game space.

It is not a game engine.

The conceptual model is:

**Level → Logical Visual Layer → Actors**

## Logical Visual Layer

Each logical Level Visual Layer has:

- its own Tilemap reference;
- its own logical ordering/indexing;
- a collection of Actors.

The number of logical Level Layers is not limited to eight.

Logical Level Layers are independent from physical Graphics Layers.

## Actor

Actor has:

- unique game-defined ID;
- Transform;
- exactly one Visual Representation;
- Collider state/reference.

Actor has no predefined semantic type such as Player, Enemy or Door.

Transform conceptually contains:

- position;
- scale;
- rotation;
- pivot.

## Visual Representation

Initial supported conceptual forms:

- Tile;
- Tile Region;
- Sprite.

Tile/Tile Region Actors use the logical layer's Tilemap context rather than redundantly storing a Tilemap reference.

Exact C# representation and transform capability interfaces are implementation decisions.

## Collider relationship

Actor and Collider are independent.

An Actor may have a Collider without a Visual Representation, or a Visual Representation without a Collider.

Base collision remains independent from Level.

## Runtime

Level resource is immutable.

Loading a Level creates mutable runtime state.

The runtime exposes Actor Runtime References.

An Actor Runtime Reference gives access to the current:

- Transform;
- Visual Representation;
- Collider.

Runtime changes do not modify the Level resource.

Game code can obtain actors belonging to a logical layer, find actors by ID and manipulate runtime references.

## Rasterization

Level may rasterize logical Level Layers into any physical Graphics Layer.

Multiple logical Level Layers may be rasterized sequentially into one physical Graphics Layer.

Graphics remains unaware of Level concepts.

Example:

- Sky → physical Layer 0
- Clouds → physical Layer 0
- Mountains → physical Layer 0

Later the game may replace Sky with Dark Sky without requiring Mountains to move to another physical layer.

## Out of scope

Initial Level API does not include:

- Player/Enemy semantic classes;
- component systems;
- parent/child hierarchies;
- embedded scripts;
- AI;
- event systems;
- physics behavior;
- gameplay components.

Those belong to game code or future higher-level libraries.
