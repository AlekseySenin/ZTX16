# ZTX16 — C# Coding Conventions

These conventions apply to the reference C# implementation unless a project-specific code style file explicitly supersedes a purely stylistic rule.

## 1. General

Use standard modern C# conventions and prioritize readability over cleverness.

Prefer small cohesive types and methods.

Avoid unnecessary abstraction. Introduce interfaces when they express a real architectural boundary, replaceability requirement, or test seam.

Do not create abstractions merely because they are theoretically extensible.

## 2. Naming

### Public types

PascalCase:

- `Graphics`
- `Machine`
- `AnimationPlayback`
- `Collider`

### Public properties

PascalCase:

- `Position`
- `Enabled`
- `TickCount`
- `PlaySpeed`

### Public methods

PascalCase:

- `SetPalette`
- `Clear`
- `Draw`
- `Suspend`

### Private fields

`_camelCase` with a leading underscore:

- `_currentFrame`
- `_assignedChannel`
- `_playbackTime`

### Local variables and parameters

`camelCase`:

- `currentFrame`
- `channelIndex`
- `resourceName`

### Constants

Prefer PascalCase for C# constants:

- `TicksPerSecond`
- `MaxLayers`

Do not use C/C++-style `ALL_CAPS` unless interoperating with an external protocol that requires it.

### Static fields

Static fields follow the same visibility rule as ordinary fields. Private static fields use `_camelCase`; public static members use PascalCase.

## 3. Fields vs properties

Prefer properties for public state.

Use fields for private implementation state.

Do not expose mutable public fields unless there is a strong reason.

## 4. Booleans

Use positive, readable names:

- `Enabled`
- `IsPlaying`
- `HasCollision`

Avoid double negatives.

## 5. Collections

Expose read-only views where callers must not mutate ownership-controlled collections.

Use collection types appropriate to the actual contract rather than returning implementation-specific containers unnecessarily.

## 6. Nullability

Enable nullable reference types.

Make nullability explicit in public contracts.

Do not use null as an undocumented state signal when a dedicated state/value is clearer.

## 7. Exceptions and validation

Validate public API arguments at the boundary.

Invalid indices, impossible states and invalid resource references should fail explicitly according to the API contract.

Do not silently clamp values unless clamping is explicitly part of the ZTX16 contract.

Do not use exceptions as ordinary control flow in hot loops.

## 8. Immutability

Cartridge resources are conceptually immutable.

Runtime state must not modify resource definitions accidentally.

Use immutable/read-only structures where that makes the ownership model obvious, but do not introduce excessive allocation or wrappers solely for ideological purity.

## 9. Architecture

Respect dependency direction.

Examples:

- Graphics must not reference Level types.
- Collision must not depend on Graphics.
- Machine must not duplicate subsystem state.
- Animation must not render.
- Level may use Graphics.
- Editors may use platform APIs and resources.

Avoid circular dependencies between assemblies/namespaces.

## 10. Performance

The platform has fixed-size hot-path structures in several subsystems. Prefer predictable memory layouts and allocation-free update paths where practical.

Do not prematurely optimize editor UI or tooling at the expense of maintainability.

Measure before introducing complex pooling or custom memory management.

## 11. Comments

Comment architectural intent and non-obvious invariants, not obvious syntax.

When code intentionally differs from a common C# pattern because of a ZTX16 contract, explain the reason.

## 12. Documentation in code

Public types and members that form the platform API should have XML documentation sufficient for IDE users.

Keep XML documentation synchronized with the authoritative ZTX16 specification.

## 13. API naming rule

Names should describe the ZTX16 concept, not the current implementation technique.

For example, use `Machine` rather than exposing an implementation-specific `RuntimeHost` concept as the public machine API.

## 14. Do not invent platform semantics

If an implementation encounters an unspecified behavior:

1. check the documentation package;
2. check surrounding subsystem invariants;
3. prefer the smallest conventional behavior;
4. record the decision if it affects the public contract;
5. do not silently create a new architectural concept.
