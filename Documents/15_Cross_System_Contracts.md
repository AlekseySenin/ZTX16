# ZTX16 — Cross-System Contracts and Invariants

This document is the short checklist for reviewing new Cloud Code changes.

## Coordinate invariant

Every ZTX16 spatial coordinate uses lower-left origin.

Any host/UI coordinate conversion must terminate at an explicit ZTX16 coordinate boundary.

## Resource/runtime invariant

Resources are immutable. Runtime state is mutable and derived.

Never mutate a resource as a shortcut for changing runtime state.

## Graphics invariant

Physical Graphics Layers are raster buffers only.

They must not acquire knowledge of Level, Actor, Sprite provenance or Tilemap ownership.

## Level invariant

Logical Level Layers are not physical Graphics Layers.

There is no one-to-one layer-count relationship.

Level may rasterize into any physical Graphics Layer.

## Collision invariant

Collision is independent of Graphics and Level.

No collision semantic may be inferred from Graphics Layer number.

## Input invariant

Input models devices, not players.

Game logic owns interpretation/mapping.

## Audio invariant

Low-level Audio models fixed physical hardware.

High-level Audio owns allocation policy.

Shared processing resources are not automatically allocated to Voices.

Song is hard-bound to physical channels and is not an allocator client.

## Runtime invariant

Only the runtime/OS owns lifecycle authority.

Cartridge can request Exit/Suspend but cannot control Resume.

Cartridge does not observe suspend/resume system events.

## Editor invariant

Editors are ordinary ZTX16 applications using real ZTX16 resources.

The editor environment must not become a hidden runtime dependency.

## Project invariant

Project Config defines project directory/resource mapping.

Standard layout is a default convention, not a hardcoded semantic requirement.

Unknown files are allowed and ignored by build.

## Cartridge invariant

Cartridge is a self-contained packed/distribution representation of a Project.

Extracting a Cartridge produces an ordinary Project structure with Project Config.

## Dependency rule

Preferred direction:

Machine/Runtime
↓
Graphics / Audio / Input / Collision / Storage / Random
↓
Animation / Level / other higher-level APIs
↓
Editors / game frameworks

Higher-level modules may depend on lower-level modules; lower-level modules must not depend on higher-level concepts.

## Review rule

Before adding a type or API, ask:

1. Is it a platform primitive or convenience policy?
2. Which layer owns the responsibility?
3. Does it create a dependency in the wrong direction?
4. Does it duplicate an existing subsystem?
5. Does it make an immutable resource mutable?
6. Does it reintroduce an obsolete concept?

If any answer is problematic, stop and resolve the architecture before coding around it.
