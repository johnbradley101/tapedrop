# TapeDrop (working name)

A simple Windows 11 app for writing files to LTO tape over LTFS, in the spirit of Hedge Canister.
Tapes are plain LTFS, so any Windows, Linux or macOS machine with an LTFS driver can read them
without this app.

## What it does today

1. **Copy**: drag files and folders in from Explorer (e.g. your USB RAID), click *Copy to tape*.
2. **Backups**: save a named source folder once; later insert a tape and click *Run*.
3. **Catalog**: search which tape holds a file.

Every job mounts the tape, copies with SHA-256 hashing, writes a manifest onto the tape,
unmounts cleanly (so LTFS writes its index), then optionally remounts and re-reads the data
from tape to verify it.

Not built yet: spanning one job across several tapes, restore from the app (copy files off the
mounted tape in Explorer for now), incremental backups, encryption, autoloader support.

## Status: not yet compiled

This code was written without access to a .NET SDK, so it has **not been compiled or run**.
Expect to fix a few compile errors on first build. Start with the tests; they exercise the
whole engine against a folder that stands in for a tape.

## Build and test (Windows 11)

Requirements: .NET 8 SDK. For the app, Visual Studio 2022 with the *.NET desktop development*
and *Windows App SDK / WinUI* workloads.

    dotnet test tests/TapeDrop.Tests          # engine, catalog and runner tests
    dotnet build src/TapeDrop.App -c Debug    # the WinUI 3 app (or open TapeDrop.sln in Visual Studio)
    dotnet run --project src/TapeDrop.App

With no settings file the app runs in **demo mode**: a normal folder
(`%LOCALAPPDATA%\TapeDrop\DemoTape`) acts as the tape so you can try the whole flow without
hardware.

## Connecting the real tape drive

Install your drive vendor's LTFS package for Windows (for example IBM Spectrum Archive Single
Drive Edition or HPE StoreOpen, depending on the drive), and confirm you can mount a tape by
hand first. Then create `%LOCALAPPDATA%\TapeDrop\settings.json`:

    {
      "MountCommand": "<command that mounts the tape>",
      "UnmountCommand": "<command that cleanly unmounts it>",
      "EjectCommand": "<command that ejects it, or leave out>",
      "MountPath": "L:\\",
      "TimeoutSeconds": 300
    }

The commands are deliberately not hard-coded: they differ by vendor package and version, and
should be taken from that package's documentation. `MountPath` is the drive letter the mounted
tape appears as.

Things to check on the real machine (Step 0 of the plan):

- Fibre Channel can present the same drive twice (two paths). Make sure the host sees one
  device, or the vendor driver may pick the wrong one.
- The vendor mount command may need an elevated prompt. Drag and drop into an elevated window
  does not work from a non-elevated Explorer, so test both.
- Measure sustained read speed from the USB RAID. It must stay above the tape's native speed
  (about 360 MB/s for LTO-8, 400 MB/s for LTO-9) or the drive will keep stopping and
  repositioning. Many small files are also slow over LTFS.

## Recovering a tape without this app

Each job writes plain files plus a manifest in the tape's `_tapedrop` folder.

- **Windows:** mount with the vendor LTFS package and copy the files out in Explorer.
- **Linux/macOS:** mount with an LTFS implementation (the open-source `ltfs` package), then
  from the tape's root folder verify a job with:

      sha256sum -c _tapedrop/<job folder name>.sha256

Always unmount a tape cleanly before removing it; that is what writes the LTFS index.

## Layout

    src/TapeDrop.Ltfs      ILtfsProvider, folder "fake tape", command-line vendor adapter
    src/TapeDrop.Catalog   SQLite catalog and saved backup jobs
    src/TapeDrop.Core      planner, copy engine, verifier, manifests, job runners
    src/TapeDrop.App       WinUI 3 app (Copy, Backups, Catalog pages)
    tests/TapeDrop.Tests   xUnit tests (no hardware needed)

## Notes on choices that differ from the first plan

- Manifests are written per job (`_tapedrop/<job folder>.sha256` and `.json`) rather than one
  shared file, because one tape holds many jobs.
- Catalog search uses a simple substring match, not SQLite FTS5. That is plenty for now and
  avoids depending on how SQLite was built.
- The first run of a job on a tape with no LTFS volume label stores a short ID in
  `_tapedrop/tape.id` so the catalog has a stable name for that tape.
