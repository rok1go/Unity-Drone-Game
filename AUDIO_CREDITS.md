# Recorded audio

## Foley replacement — 7 November 2026

The game no longer synthesizes footsteps, impacts, cloth, door, friction, rain or receiver sounds.
These recordings replace the former `SoundBank` generators:

| Recording / pack | Author | Source and license | Runtime use |
| --- | --- | --- | --- |
| Impact Sounds 1.0 | Kenney | https://kenney.nl/assets/impact-sounds — CC0 1.0 | Five recorded variants per material/weight, concrete/wood/carpet footsteps, glass and plank breakage |
| RPG Audio | Kenney | https://kenney.nl/assets/rpg-audio — CC0 1.0 | Cloth, latch/click and hammer contact |
| Door Open, Door Close Set | qubodup / Iwan Gabovitch | https://opengameart.org/content/door-open-door-close-set — CC0 1.0 | Three opening and three closing takes |
| Chair - Dragged on the ground (735345) | Vrymaa | https://freesound.org/people/Vrymaa/sounds/735345/ — CC0 1.0 | Three 0.55 s excerpts, mono, 70 Hz high-pass, edge fades, RMS/peak-safe gain |
| Bricks/Stones/Rocks/Gravel Falling (567249) | iwanPlays | https://freesound.org/people/iwanPlays/sounds/567249/ — CC0 1.0 | Recorded rubble after wall breakage and collapse |
| Radio noise and static (753584) | LukaCafuka | https://freesound.org/people/LukaCafuka/sounds/753584/ — CC0 1.0 | 12 s receiver excerpt for TVs/radios; no music during searching |
| Rain Loop (unfiltered) (321647) | hargissssound | https://freesound.org/people/hargissssound/sounds/321647/ — CC0 1.0 | 20 s mono rain excerpt, high-pass, loop crossfade and RMS-safe gain |
| Spy Loop | wipics | https://opengameart.org/content/spy-loop — CC0 1.0 | `Spy_2.mp3`, preparation phase only; stops at the phase deadline |

Freesound HQ MP3 previews were downloaded from the credited sound pages. Source recordings
for the edited loops are retained under `Assets/AudioSources`. Rebuild the edits with
**Drone Game → Rebuild recorded foley edits** (`Assets/Editor/RecordedFoleyImport.cs`).
Kenney's original license is included as `Assets/Resources/Audio/Foley/Kenney-Impact-License.txt`.
CC0 text: https://creativecommons.org/publicdomain/zero/1.0/ .

Rubble source is itself a credited CC0 mix of adamgryu 336023, alancat 389303 and
spookymodem 202098, as identified on iwanPlays' source page.

Playback uses restrained gain and pitch changes (normally ±2%), material families,
spatial cooldowns, concurrency limits and final stereo-linked peak protection.

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

## Glass and vehicles — slots for recorded takes (7 November 2026)

`SoundBank` loads these names from `Assets/Resources/Audio/Foley/` when present and otherwise falls
back to the recordings already in the project (window: `Audio/GlassBreak.wav`; shards and knocks:
Kenney glass takes; cars: Kenney metal takes). All sources below are CC0 1.0 (checked on their pages):

| File name | Recording | Source |
| --- | --- | --- |
| `WindowBreak` | Glass Break — unfa (221528) | https://freesound.org/people/unfa/sounds/221528/ |
| `GlassShardsFall` | Glass Shattering and Falling — 221Beimesche (336425), shard tail | https://freesound.org/people/221Beimesche/sounds/336425/ |
| `CarDoorClose` | car door close — monotraum (208695) | https://freesound.org/people/monotraum/sounds/208695/ |
| `CarDoorOpen` | Car Door open and close — NachtmahrTV (556689), opening part | https://freesound.org/people/NachtmahrTV/sounds/556689/ |
| `CarImpact` | Collision — qubodup (332058) | https://freesound.org/people/qubodup/sounds/332058/ |
| `CarCrash` | Car Crash — squareal (237375) | https://freesound.org/people/squareal/sounds/237375/ |
