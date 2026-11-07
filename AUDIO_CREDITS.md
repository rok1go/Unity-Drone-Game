# Recorded audio

The old synthesized placeholders have been replaced with edits of these recordings.
Both source pages explicitly mark the recordings CC0 1.0 (public domain):
https://creativecommons.org/publicdomain/zero/1.0/

## Drone motors

- **FPV Drone Flight 3**, qubodup, Freesound sound 854466.
- https://freesound.org/people/qubodup/sounds/854466/
- HQ preview: https://cdn.freesound.org/previews/854/854466_71257-hq.mp3
- The source page credits Brandon Dorrill / Fort Benning Public Affairs Office,
  *Maneuver Battle Lab Quarterly Drone Race EPK(H)*, 17 April 2026.
- Source retained in `Assets/AudioSources/FpvFlight3_Source.mp3`.
- Edit: mono, removal of low-frequency wind/DC, excerpt from 4–9 seconds,
  equal-power loop crossfade, gain adjustment. Final loop: 48 kHz PCM16.

## Explosion

- **M67 Fragmentation Grenade Explosion 3**, qubodup, Freesound sound 67472.
- https://freesound.org/people/qubodup/sounds/67472/
- HQ preview: https://cdn.freesound.org/previews/67/67472_71257-hq.mp3
- The source page identifies the underlying material as public-domain US military video.
- Source retained in `Assets/AudioSources/M67Explosion3_Source.mp3`.
- Edit: mono mix for positional audio, low-frequency/DC cleanup, onset trim,
  tiny edge fades and peak-safe gain. The recorded crack and decay are preserved.

The HQ MP3 previews were decoded using Unity's AudioClip importer into `.audio-work/`.
`node Tools/prepare-recorded-audio.cjs` rebuilds the two runtime WAV files from those
decoded source WAV files. No synthetic oscillator or noise layers are added.

## Window glass

The glass break uses the recording supplied with the map task,
`glass-breaking-sound-effect_wLZSIYn.mp3`. The runtime clip
`Assets/Resources/Audio/GlassBreak.wav` is a 0.9-second mono excerpt with
edge fades, low and high frequency filtering, and 3 dB of gain reduction. The game
uses separate playback levels for interaction and explosions.
