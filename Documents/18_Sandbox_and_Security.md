# ZTX16 — Cartridge Sandbox and Security Model

## Status

Architecturally approved at principle level.

## 1. Core rule

A Cartridge does not receive direct access to the Host operating system.

The Cartridge sees the ZTX16 platform, not Windows/Linux as a general-purpose environment.

Conceptually:

**Cartridge → ZTX16 VM → ZTX16 APIs → Host Backends**

The Cartridge must not bypass the ZTX16 API boundary.

## 2. Filesystem isolation

Cartridge code cannot read or write arbitrary host filesystem paths.

Persistent data is accessed through the already-defined ZTX16 Storage API and its Game ID namespace.

The ZTX16 Console and authoring tools may perform import/export operations on behalf of the user. Those tools are not equivalent to granting arbitrary filesystem access to running Cartridge code.

## 3. External asset import

The platform/editor may import common external formats into ZTX16-owned representations, for example:

- PNG into ZTX16 graphics resources;
- WAV into ZTX16 audio resources.

After import, Cartridge runtime code works with ZTX16 resources rather than arbitrary source files on the host.

## 4. Operating-system APIs

Direct OS API access is not part of the base Cartridge contract.

The MVP does not need to expose Windows/Linux APIs to Cartridge code.

If a future capability genuinely requires host integration, it should be introduced as an explicit ZTX16 platform service rather than by exposing arbitrary OS calls.

## 5. Network

Networking is a future capability, not an MVP requirement.

Future network functionality should be exposed through an explicit ZTX16 Network API and remain subject to platform/runtime policy.

Potential uses include downloading and uploading Projects or Cartridges through approved web resources.

## 6. Why the VM provides the sandbox boundary

Because Cartridge code executes as ZTX16 executable code inside a controlled VM, the runtime can define exactly which operations are available to the code.

An instruction or runtime operation that is not part of the ZTX16 execution/platform contract simply does not exist as an available Cartridge capability.

This is fundamentally different from executing an arbitrary .NET Assembly with access to the normal .NET class library.

## 7. Host trust boundary

Host-specific components are trusted runtime infrastructure.

Cartridge code is untrusted application content from the Host's perspective.

The VM and runtime therefore form the security boundary between Cartridge code and host capabilities.

## 8. Security scope

The MVP security goal is capability isolation, not a complete security certification or hardened hostile-code environment.

The implementation must still avoid obvious escape paths such as:

- arbitrary filesystem APIs;
- arbitrary process creation;
- unrestricted native code execution;
- direct host socket access;
- uncontrolled reflection into host/runtime objects.

Detailed threat modeling, fuzzing, VM hardening and exploit resistance are future engineering work.

## 9. Editor exception

ZTX16 authoring tools run with greater privileges than a normal Cartridge because they must import/export Projects and interact with the user's authoring storage.

This does not grant those capabilities to a running Cartridge.
