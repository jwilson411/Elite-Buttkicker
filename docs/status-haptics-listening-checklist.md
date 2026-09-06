# Status.json haptics listening checklist

Linux CI can prove Status.json polling, Flags/Flags2 edges, first-read
silence, partial JSON skip, missing mappings, shutdown, and
MaxIntensity=0 not muting a multi-layer generator. It cannot open
WASAPI, play a tone, or feel a ButtKicker. None of the checks below
have been run on hardware. Do not treat a green `dotnet test` as
playback.

Machine: Windows, Elite Dangerous running, Status.json updating under
the configured journal folder, and the ButtKicker output you actually
use.

## Before you start

- [ ] EDButtkicker is pointed at the same journal folder the game
      writes. Logs show `Watching Status.json at:` with that path, then
      `Status.json found, beginning monitoring`.
- [ ] A restart with the game already in a state (gear down, hardpoints
      out, on foot) does **not** fire those patterns. First read is
      baseline, not an event.

## Flags (ship)

- [ ] Landing gear down / up
- [ ] Hardpoints deployed / retracted
- [ ] Cargo scoop deployed / retracted
- [ ] Silent running on / off
- [ ] Night vision on / off
- [ ] FSD cooldown starting
- [ ] Low fuel warning appearing
- [ ] Overheating appearing

FSD charging is detected but has no default mapping. Confirm it stays
silent unless you add one.

## Flags2 (on foot / environment / glide)

These used to be dropped when Flags did not also change. Toggle them
without changing ship flags.

- [ ] On foot
- [ ] Low oxygen
- [ ] Low health
- [ ] Cold / very cold
- [ ] Hot / very hot
- [ ] Glide on / off
- [ ] FSD jump in progress (Flags2 bit, distinct from the journal
      `FSDJump` arrival pattern)

## Shutdown

- [ ] Quit the app while a Status.json edge would otherwise fire.
      The process exits without sitting on a two-second delay, and
      further Status.json writes after stop do not rumble.

## Out of this checklist

- Linux or macOS audio
- Buttplug.io
- Claiming that this repository's CI played audio or felt a ButtKicker
- Retuning the existing multi-stage landing-gear / dock / FSDJump
  sequences
