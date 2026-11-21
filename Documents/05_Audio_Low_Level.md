# ZTX16 — Low-Level Audio API

## Hardware model

Exactly eight persistent physical audio channels:

- channels 0–5: Synth;
- channels 6–7: Sample/Wavetable.

Channels are fixed hardware resources. They are not dynamically allocated or destroyed.

Shared hardware resources:

- two LFOs;
- two Filters;
- two FX units.

These are separate from channels.

## Synth channels

Each Synth channel has one hard-bound oscillator.

Waveforms:

- Sine
- Triangle
- Saw
- Reverse Saw
- Square
- Noise

No Pulse or Pulse Width mode.

Frequency range: 0.01 Hz–20 kHz.

Common channel controls:

- Frequency
- Volume
- Pan
- ADSR
- NoteOn
- NoteOff
- Stop
- Mute/Unmute

## Sample/Wavetable channels

A channel can use either a Sample resource or a Wavetable resource.

Sample:

- mono;
- 44.1 kHz;
- 8-bit PCM;
- one-shot or loop;
- loop start/end;
- Playback Rate multiplier, nominal 1.0.

Wavetable:

- single cycle;
- exactly 64 PCM samples;
- 8-bit PCM;
- cyclic playback;
- frequency 0.01 Hz–20 kHz.

Sample reaching physical end becomes Idle. A Wavetable has no natural end.

## ADSR

Every physical channel owns exactly one ADSR.

ADSR stages:

- Attack
- Decay
- Sustain
- Release

ADSR is a modulation source, not merely a volume envelope.

It can be assigned to one permitted modulation destination at a time with its own Amount.

## LFO

Two global LFOs exist.

Waveforms:

- Triangle
- Saw
- Reverse Saw
- Square

Frequency range: 0.01–60 Hz.

LFOs run continuously and are not restarted by NoteOn/NoteOff merely because a voice changes.

One LFO may modulate multiple destinations. Every LFO-to-destination connection has its own Amount.

## Modulation order

For a modulated parameter, hardware order is fixed:

Base → LFO1 → LFO2 → ADSR → Final Value

The developer cannot reorder these sources.

Current permitted channel destinations:

Synth:
- Frequency: LFO1/LFO2/ADSR
- Volume: LFO1/LFO2/ADSR
- Pan: not a base modulation destination
- Waveform: not modulated

Sample:
- Playback Rate: LFO1/LFO2/ADSR
- Volume: LFO1/LFO2/ADSR
- Pan: not a base modulation destination
- Position/loop state: not modulated

Wavetable:
- Frequency: LFO1/LFO2/ADSR
- Volume: LFO1/LFO2/ADSR
- Pan: not a base modulation destination
- phase/playback state: not modulated

LFOs may modulate permitted Filter and FX parameters. ADSR does not directly route to FX parameters.

## Filters and effects

Two Filter units support:

- Low-pass
- High-pass
- Band-pass
- Cutoff
- Resonance

Two FX units can each be configured as one of:

- Chorus: Delay, Depth, Mix
- Delay: Time, Feedback, Mix
- Reverb: Size, Decay, Mix

There is no Chorus Rate parameter; global LFOs provide modulation.

Each of Filter1, Filter2, FX1 and FX2 can be:

- unassigned;
- assigned as an insert to one physical channel;
- assigned globally after mixing.

A unit cannot be both insert and global simultaneously. Reassigning it replaces the previous assignment.

## Signal path

Conceptually:

Channel source → ADSR/volume/pan → channel insert processing → per-channel finalizing Mixer Volume → summing → global processing → Master Volume → Safety Limiter → Output

There is no Audio Bus/Mixer Group hierarchy.

## Finalizing Mixer

Every physical channel has one finalizing Mixer Volume applied after channel processing and before final summing/global processing.

This is a low-level hardware primitive, not an Audio Bus system.

## Effective values

For modulated parameters, the low-level API exposes read-only Effective Values.

Effective Value never becomes Base Value.

The API should expose the current runtime LFO value and current ADSR output as readable runtime information.

## Voice state

NoteOn starts/retriggers Attack.

NoteOff begins Release.

Stop terminates immediately without Release.

A channel becomes Idle after Release completes.

High-level allocation policy is not part of this low-level API.
