# ZTX16 — Resources, Storage and Deterministic Random

## Resource model

A Resource is immutable data shipped in the Cartridge.

Runtime may create mutable state derived from a Resource, but may not modify the Cartridge resource itself.

Public resource identity is:

**Resource Type + Resource Name**

Names are unique within each type.

Public operations:

- Get
- Exists
- Enumerate

`Enumerate` returns resource identifiers/names, not loaded resource objects.

`Get` returns a strongly typed resource at the language SDK level.

Public API does not expose generic numeric Resource IDs or generic Resource Handles.

Internal IDs/offsets/handles are implementation details.

## Resource types

Current resource families include:

Graphics:

- Palette
- Tilemap
- Sprite
- Font

Animation:

- Animation

Audio:

- Sample
- Wavetable
- Instrument
- Sequence
- Song

Other:

- Level

Code is packaged into the Cartridge executable representation, but should not be forced into the same public runtime Resource API merely because it is stored in the Project.

Potential future resource types such as Filter/Effect/LFO presets should be added only when a concrete runtime need exists.

## Storage

Storage is persistent writable data owned by the ZTX16 platform.

The current cartridge automatically receives its Game ID namespace; cartridge code does not manually pass Game ID to each Storage operation.

Storage isolation is logical separation, not necessarily a security boundary.

A Storage Entry contains:

- Type
- Name
- opaque binary Data

Operations:

- Write
- Read
- Exists
- Delete
- Enumerate

Storage does not understand the structure of the binary data.

Serialization is a separate layer. JSON is permitted as a serializer but is not mandated by Storage.

Storage survives cartridge termination/relaunch.

Do not call entries “files” in the public API. Use Storage Entry or Save Entry.

## DeterministicRandom

`ZTX16.DeterministicRandom` is the platform deterministic PRNG.

It is the only platform randomness available to cartridge code. `.NET System.Random` belongs to host/toolchain code and is not part of the cartridge execution contract.

Seed is supplied when the generator is created.

Range is supplied by each random operation rather than being fixed at construction.

Same seed + same sequence of calls produces the same results as guaranteed by ZTX16.

Base operations:

- integer in range;
- integer 0..max convenience;
- float 0..1;
- float min..max;
- Boolean;
- Choice from a collection/array.

Range convention is lower-inclusive, upper-exclusive for numeric ranges.

Choice advances the same PRNG state.

No Gaussian, weighted random, noise or random-vector operations are required in the base API. Libraries may add them.
