# Cloud Code — ZTX16 Development Instructions

## Mission

Cloud Code is implementing the ZTX16 platform and its initial built-in editor environment.

The documentation in this directory is the primary architectural reference. **Consult it before making architectural or public-API decisions.**

## Authority and precedence

1. The current user-approved ZTX16 architecture and decisions recorded in this documentation package.
2. The module specification relevant to the code being changed.
3. Cross-system invariants in `15_Cross_System_Contracts.md`.
4. Coding conventions in `12_Coding_Conventions.md`.
5. Existing implementation, when the specification intentionally leaves a detail open.

Older ZTX16 documents outside this package may contain obsolete decisions. Do not copy them blindly.

## Implementation policy

### 1. Do not redesign closed architecture

If the documentation says a concept is closed, implement it. Do not reopen the architecture because another design might be theoretically more elegant.

### 2. Do not invent missing architecture

If a detail is explicitly implementation-defined, choose a conventional maintainable implementation.

If a choice changes a public contract, resource model, dependency direction, or behavior visible to cartridge code, document the decision and treat it as an architectural change rather than silently choosing it.

### 3. Keep layers clean

Respect dependency direction.

Examples:

- Graphics must not know about Level.
- Collision must not know about Graphics.
- Animation must not render.
- Machine must not duplicate subsystem APIs.
- Level may use Graphics.
- Editors may use platform APIs.

### 4. Resources vs runtime state

Never mutate cartridge resources to represent runtime state.

Create runtime objects/state where the architecture calls for them.

### 5. Public API quality

Public APIs should be:

- small;
- explicit;
- strongly typed;
- predictable;
- discoverable;
- consistent with neighboring subsystem APIs.

Avoid exposing implementation handles, internal numeric IDs, or storage/layout details unless the specification explicitly requires them.

### 6. Performance

Implement the obvious correct architecture first.

For fixed-size hot paths, prefer predictable allocation behavior and data-oriented structures where practical. Do not build a complicated memory-management framework without measured need.

### 7. Error behavior

Validate public inputs at API boundaries.

Do not silently clamp or ignore invalid values unless the specification explicitly defines that behavior.

### 8. Editors

Editors are ZTX16 applications.

Do not create privileged desktop-only dependencies merely to make an editor easier to implement.

Exact editor UI is intentionally left to implementation. Preserve the conceptual division of tools defined in `11_Editor_Environment.md`.

## Coding conventions

Follow `12_Coding_Conventions.md`.

Core naming rules:

- public types: `PascalCase`;
- public properties: `PascalCase`;
- public methods: `PascalCase`;
- private fields: `_camelCase`;
- local variables: `camelCase`;
- method parameters: `camelCase`;
- constants: `PascalCase`;
- private static fields: `_camelCase`;
- public static members: `PascalCase`.

Prefer properties over public mutable fields.

Enable nullable reference types.

Use XML documentation for public platform API.

## Reference implementation language

C# is the first/reference implementation language.

The platform architecture must not be unnecessarily coupled to C#-specific concepts where a language-independent contract is intended.

The initial implementation may use .NET 8+ and Roslyn/toolchain facilities where appropriate. Roslyn is a build/toolchain concern, not a requirement that the runtime ship Roslyn.

## Documentation synchronization

When implementing a public API, update the relevant documentation alongside the code if the implementation establishes a previously open contract.

Do not keep two competing descriptions of the same concept.

When replacing an obsolete concept, remove or clearly archive the old implementation/documentation rather than leaving it discoverable as if still valid.

## Important architectural reminders

- 60 Hz fixed tick.
- No DeltaTime.
- No catch-up.
- Lower-left coordinate origin everywhere.
- Eight physical Graphics Layers are raster buffers.
- Logical Level Layers are independent and unlimited by physical layer count.
- Collision has no static Collision Map and no Group/Mask model.
- Input has no Player abstraction.
- Audio has eight physical channels.
- Song is hard-bound to physical channels.
- Project Config controls directory/resource mapping.
- Cartridge is packed/self-contained and can be extracted back into a Project.
- Editors run inside ZTX16.

## What belongs in implementation rather than architecture

Do not spend architecture time on:

- private helper structure;
- exact internal memory layout unless it affects a public contract;
- serialization byte layout;
- binary cartridge layout;
- compression implementation;
- editor pixel-perfect widget dimensions;
- private caching strategies;
- internal IDs/offsets/handles.

Solve these using normal engineering judgment.

## When uncertain

Before asking for a new architectural decision, first check:

1. this package;
2. the relevant module document;
3. `15_Cross_System_Contracts.md`.

If the issue is still genuinely ambiguous and changes the public architecture, surface it explicitly rather than silently guessing.

Otherwise, proceed with the smallest reasonable implementation.
