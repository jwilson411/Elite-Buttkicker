# Windows audio enumeration checklist

Linux CI can prove the catalog, resolver, settings round-trip, and API
selection against fake devices. It cannot open WASAPI, play a tone, or
click the Windows UI. None of the checks below have been run. Do not
treat a green `dotnet test` as playback.

Machine: Windows, a real render endpoint, and the ButtKicker output you
actually use. Record the MMDevice endpoint id from the log line
`Audio devices - WASAPI render position … endpoint='…'` so a later
report can name the same device.

## Before you start

- [ ] Fresh install or a settings reset, then pick the ButtKicker output
      in the web UI (or the setup wizard). Confirm the log shows
      `configured: endpoint id '…' is name '…'` rather than a DeviceId
      match or a WaveOut device number.
- [ ] Restart the app. The same endpoint id and friendly name come back
      from `user-settings.json` (`audioDeviceEndpointId` plus
      `audioDeviceName`). The previously selected row is still highlighted.

## Reorder

- [ ] In Windows sound settings, change the default device or the order
      of playback devices so the ButtKicker is no longer at the same
      list position.
- [ ] Restart EDButtkicker. Playback (and the highlighted row) still
      names the saved endpoint, not whichever device now occupies the
      old index.

## Unplug / disable

- [ ] Disable or unplug the saved output. The UI must not highlight a
      neighbour. Logs should say `UNRESOLVED` or `UNAVAILABLE` and
      playback should fall back to the system default.
- [ ] Plug it back in. The saved endpoint is selected again without
      picking the device by hand.

## Duplicate names

- [ ] Two render endpoints that share a friendly name (two "USB Audio
      Device" entries, or similar). Select the second. Restart. The
      saved endpoint id still opens that one, not the first match by
      name.

## Newly connected

- [ ] Plug in a new output so it appears *ahead* of the saved device in
      the WASAPI list. The saved selection must not move to the new
      device.

## Default highlight

- [ ] Choose "Default Audio Device". Settings store an empty endpoint id
      and empty name. The default row is highlighted. After restart,
      Windows' current default is what actually opens.
- [ ] The endpoint Windows reports as the multimedia default is marked
      as the default in the device list even when it is not the saved
      selection.

## Reboot

- [ ] Reboot Windows, start EDButtkicker, play a test tone. The tone
      comes from the saved ButtKicker output, not the system default,
      unless that output is gone.

## Out of this checklist

- Linux or macOS audio
- Buttplug.io
- Claiming that this repository's CI played audio
