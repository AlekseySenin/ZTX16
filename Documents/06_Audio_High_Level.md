# ZTX16 — High-Level Audio API

## Model

The high-level model is:

**Instrument → Voice → Channel Group → Physical Channel**

Instrument is a resource.

Voice is runtime state for one sounding instance.

Channel Group defines which physical channels are eligible for that Voice.

Physical Channel is the low-level hardware resource.

## Instrument

Instrument is immutable base sound configuration.

A Voice uses the Instrument as its base and may override runtime parameters.

Instrument may express desired modulation Amount/destination, but does not own LFOs, Filters or FX and does not set their global waveform/frequency.

## Voice

Voice represents one sounding runtime instance.

It knows conceptually:

- Instrument;
- Channel Group;
- assigned physical channel;
- current note/frequency/rate;
- runtime overrides;
- playback state;
- Steal Lock state.

Voice does not allocate itself and does not manage shared processors.

## Channel allocation

High-level allocator policy:

1. Free First is mandatory/default.
2. If all candidates are occupied, apply Steal Policy.

Steal policies:

- By Order — default
- Steal Oldest
- Steal Quietest

There is no Voice Priority system.

Steal Lock prevents a Voice from being selected for stealing until that Voice ends. Free channels remain usable.

When a busy physical channel is reused, the new owner fully reconfigures the relevant instrument/channel state and retriggers the voice. The old voice does not morph into the new sound.

If every eligible channel is Steal Locked, exact failure/selection behavior is an implementation detail and must be explicit in code rather than silently violating the lock.

## Audio Processing Manager

A separate high-level Audio Processing Manager manages shared:

- LFOs;
- Filters;
- FX;
- routing;
- processor parameters.

Routing is explicit. There is no automatic processor allocation among Voices.

## Sequence

Sequence is strictly monophonic.

One Sequence is intended to drive one Voice at a time.

Sequence contains its Instrument plus an ordered timeline of notes, pauses and other sequence events.

Changing Sequence therefore changes the Instrument used by its playback.

Sequence has no concrete physical channel knowledge.

## Sequence Playback

Sequence Playback is runtime state that executes a Sequence through a Voice.

It owns sequence position/timing and playback state.

There is one internal timing model: Sequence Ticks.

Time Scale is the fundamental relation between Sequence Ticks and runtime ticks.

SetBPM is convenience syntax that converts BPM into the corresponding Sequence Tick rate. BPM is not a second timeline.

Polyphony is achieved by multiple Sequence Playbacks, not by a built-in polyphonic Sequence.

## Song

Song is an immutable resource that explicitly controls the complete physical ZTX16 audio configuration over time.

Song is hard-bound to physical channels. It does not use the allocator to find free channels.

Song has conceptual access to all eight physical channels and shared LFO, Filter and FX resources.

A Song can schedule:

- Sequence assignment to a specific physical channel;
- LFO settings/routing;
- Filter settings/routing/binding;
- Effect settings/routing/binding.

Instrument is not a separate Song event. Instrument changes as a consequence of Sequence assignment because Sequence contains its Instrument.

## Song Playback

Song Playback is runtime execution state.

It tracks Song time and applies scheduled events.

It directly controls the physical channels specified by the Song.

It does not use the Voice allocator.

## Song channel stealing

Song owns its configured channels, but SFX and standalone Sequence Playback may temporarily steal them.

Song never steals a channel back.

When stolen:

- the new owner replaces the Instrument configuration on the channel;
- Filter/FX/LFO bindings remain unchanged;
- Song Playback has no control over the stolen channel;
- Song timeline continues normally;
- events occurring while stolen are not replayed retroactively.

When the stolen playback ends, the channel returns to Song ownership and Song restores the Instrument required by the Sequence currently assigned to that channel.

Shared processing configuration remains in place.

## Song events

Base event categories:

- Sequence Event
- LFO Event
- Filter Event
- Effect Event

The exact event payload types are implementation details.

## Explicitly out of scope

- automatic Filter/FX allocation;
- Audio Bus hierarchy;
- Voice Priority;
- built-in polyphonic Sequence;
- gameplay-specific sound-effect semantics;
- editor-only tracker data model separate from runtime resources.
