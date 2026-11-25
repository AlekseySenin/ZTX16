# ZTX16 — Cloud Code Documentation Package

Status: **Implementation baseline — November 2026**

This package is the authoritative implementation reference for the initial ZTX16 Cloud Code development phase.

## Authority

When implementing ZTX16, Cloud Code should consult these documents before introducing architecture, APIs, resource formats, editor behavior, or cross-system dependencies.

Priority:
1. `00_Architecture.md`
2. `01_Runtime_and_Machine.md`
3. subsystem specifications (`02`–`09`)
4. `10_Project_and_Cartridge.md`
5. `11_Editor_Environment.md`
6. `16_Host_and_Backend.md`, `17_Cartridge_Execution_and_VM.md`, `18_Sandbox_and_Security.md`
7. `14_Resources_Storage_Random.md`, `15_Cross_System_Contracts.md`
8. `12_Coding_Conventions.md`
9. `13_Implementation_Status.md`

`PROJECT_DECISIONS.md` records approved decisions in summary form. `19_Production_Phase_Baseline.md` is the implementation handoff and order of work. `IMPLEMENTATION_NOTES.md` records decisions taken during implementation. If a summary and a module document disagree, the module document is fixed to match the approved decision.

If an implementation detail is not specified here, choose a conventional, maintainable solution and document it in the implementation. Do not invent a new architectural concept merely to solve an implementation problem.

## Package contents

- `00_Architecture.md` — platform-wide architecture and boundaries
- `01_Runtime_and_Machine.md` — execution model and Machine API
- `02_Graphics.md` — graphics hardware/API
- `03_Font_and_Text.md` — bitmap fonts and text
- `04_Animation.md` — animation resources/playback
- `05_Audio_Low_Level.md` — physical audio model/API
- `06_Audio_High_Level.md` — Instrument/Voice/Sequence/Song model
- `07_Input.md` — input hardware/API model
- `08_Collision.md` — collision/geometry API
- `09_Level.md` — Level/Layer/Actor model
- `10_Project_and_Cartridge.md` — project, config, build, cartridge
- `11_Editor_Environment.md` — built-in editors and editor architecture
- `12_Coding_Conventions.md` — C# coding conventions
- `13_Implementation_Status.md` — closed decisions, implementation details, future work
- `14_Resources_Storage_Random.md` — Resource, Storage and DeterministicRandom APIs
- `15_Cross_System_Contracts.md` — dependency rules and invariants
- `16_Host_and_Backend.md` — host platforms, SDL3/OpenGL backends, display scaling
- `17_Cartridge_Execution_and_VM.md` — executable format, VM, compilation path
- `18_Sandbox_and_Security.md` — cartridge sandbox and authoring-tool privileges
- `19_Production_Phase_Baseline.md` — approved foundation and implementation order
- `PROJECT_DECISIONS.md` — summary of approved decisions
- `IMPLEMENTATION_NOTES.md` — decisions taken during implementation
- `Cloud.md` — instructions for Cloud Code

The package deliberately avoids freezing binary formats, serialization layouts, exact ABI/executable representation, or other implementation details that were explicitly deferred to development.
