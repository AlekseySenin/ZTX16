# ZTX16 — Project and Cartridge

## Project

Project is the canonical authoring representation of a ZTX16 application.

It is a structured directory on persistent storage.

The root must contain a valid Project Config.

The Config is what identifies a directory as a ZTX16 Project.

## Project Config

Project Config is mandatory.

A new Project is created with a standard Config and standard directory/resource mapping.

The standard structure is a convention, not a platform restriction.

The Config determines which directories correspond to which resource/source categories.

A developer may change that mapping to create a custom project organization.

Nested subdirectories are allowed inside mapped directories and are purely organizational unless the Config gives them semantic meaning.

Missing resource directories are normal and are created on demand.

Unknown, unrelated or invalid files may exist in the Project. They are ignored by ZTX16 and are not included in Cartridge Build.

This permits README files, third-party editor data and other project-local material.

## Code directory

The default source-code directory is conceptually `code/`, not a language-specific directory such as `csharp/`.

Language selection and language-specific configuration belong in Project Config.

## Cartridge

Cartridge is the packed/distribution representation of a Project.

It contains the project's compiled executable representation plus all required packaged assets/resources and required metadata in one self-contained file.

The Cartridge contains a platform-defined ZTX16 executable representation. The exact binary layout, VM instruction encoding and executable metadata are implementation details.

The Cartridge must be sufficient for runtime loading without requiring the original Project directory.

## Build

Conceptual flow:

**Project → Build/Pack → Cartridge**

The build process:

1. reads Project Config;
2. discovers mapped source/resources;
3. ignores unrelated/unmapped files;
4. validates required content;
5. compiles/builds code into the ZTX16 executable representation;
6. packages required resources and metadata;
7. produces a self-contained Cartridge.

The exact build toolchain is implementation-defined.

## Extract / Import

Cartridge can conceptually be extracted/imported back into a Project.

The operation creates:

- a Project root;
- Project Config;
- directory structure described by that Config;
- corresponding project-side source/resource representations.

The result is an ordinary editable Project, not a special runtime-only container.

## Resource identity

Public resource identity is:

**Resource Type + Resource Name**

Names are unique within a resource type.

Public APIs do not expose numeric resource IDs.

Internal cartridge/runtime IDs, offsets and handles are implementation details.
