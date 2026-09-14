<h1 align="center">AdfXplorer</h1>

<p align="center">
  Mount classic Amiga <code>.adf</code> and <code>.hdf</code> disk images as real drives in Windows Explorer.
</p>

## What it does

AdfXplorer lets you open an Amiga floppy disk image (`.adf`) or hard disk image (`.hdf`) the same
way you'd open a USB stick: it shows up as a drive letter in **This PC**, and you can browse,
copy files in and out, rename, and delete, all from Explorer — no emulator, no separate extraction
step, no third-party archive tool required.

It's aimed at anyone working with Amiga software or old backups: retro gaming and hobbyist
Amiga/UAE users moving files between a modern PC and disk images, and anyone who's inherited a pile
of `.adf`/`.hdf` files from an old Amiga and just wants to get files off of them.

## How it works

AdfXplorer is a small system-tray application built on two things:

- **[WinFsp](https://winfsp.dev)** — a user-mode filesystem framework for Windows (conceptually
  "FUSE for Windows"). AdfXplorer implements a WinFsp filesystem driver that translates Explorer's
  file operations (list folder, read file, create file, delete, rename, ...) into reads and writes
  against the Amiga on-disk format.
- **A from-scratch reader/writer for the Amiga disk formats themselves.** AdfXplorer doesn't shell
  out to an emulator or a converter tool — it parses and writes the actual on-disk block structures
  (boot block, root block, file headers, directory hash chains, checksums, RDB partition tables)
  directly, in C#. See [`CREDITS.md`](CREDITS.md) for the format documentation and reference
  implementations this was built against.

Because it reads and writes the real on-disk structures (including block checksums), a `.hdf` or
`.adf` you edit through AdfXplorer stays valid if you take it back to a real Amiga or an emulator
like WinUAE/FS-UAE.

There's no separate "app window" — everything happens from a tray icon. Right-click it (or find it
under the hidden icons arrow) for: mounting an image, creating a new blank image, and
validating/repairing a disk's checksums. Double-clicking a `.adf`/`.hdf`/`.hdz` file in Explorer also mounts
it directly — the installer registers that file association automatically, no setup step needed.
Unmounting is tray-only: pick the volume from the tray menu's mounted-volumes list.

## Supported formats

| Format | Read / mount | Write (create new) |
|---|---|---|
| **ADF** (floppy image, 880 KB DD or 1.76 MB HD) | ✅ | ✅ |
| **OFS** (Original File System) | ✅ | ✅ |
| **FFS** (Fast File System) | ✅ | ✅ |
| **HDF** (hard disk image) with an **RDB** (Rigid Disk Block) partition table | ✅ | ✅ |
| **HDZ** (gzip-compressed HDF) | ✅ (read-only) | ❌ |
| Other RDB partition DOS types (e.g. SFS, PFS3) | Partition table itself is read/created; the partition's own filesystem isn't natively mounted unless it's OFS/FFS | Partitions can be created with any DOS type and an embedded third-party filesystem driver binary, for use in an emulator |

When you mount an `.hdf` that contains an RDB partition table, AdfXplorer mounts **every
OFS/FFS partition it finds**, each as its own drive letter, in one go.

### Checksum validation & repair

Every AmigaDOS/RDB block that carries a checksum is checked whenever AdfXplorer touches it. From
the tray menu you can also run an explicit **Validate** (report-only) or **Repair** (interactively
fix or reject each mismatch) pass over an entire image, including every partition inside it.

## Requirements

- Windows 10/11, x64.
- **[WinFsp](https://winfsp.dev)** must be installed separately — it's the underlying filesystem
  driver framework and isn't bundled with AdfXplorer (see [`CREDITS.md`](CREDITS.md) for why).

## Installation

Download the latest `AdfXplorer-Setup-<version>.exe` from the
[Releases](../../releases) page and run it. That installs the app, adds a Start Menu shortcut, and
registers `.adf`/`.hdf` as file types AdfXplorer opens — no extra setup step.

## Usage

1. Launch AdfXplorer from the Start Menu, or just double-click a `.adf`/`.hdf` file — either way it
   ends up sitting in the system tray, which is the only UI it has.
2. Right-click the tray icon:
   - **Mount...** — pick a `.adf`/`.hdf` file to mount as a new drive letter.
   - **New ADF disk image...** — create a blank floppy image (choose size, OFS/FFS, and a volume
     label).
   - **New HDF disk image...** — create a blank hard disk image with one or more RDB partitions
     (choose drive name, size, DOS type, and optionally an embedded filesystem driver per partition).
   - **Validate... / Repair...** — scan a disk image's checksums, and optionally fix mismatches.
   - Mounted volumes are listed here too, each with its own **Unmount** — this is the only way to
     unmount (there's no Explorer/drive right-click integration).

## Building from source

Requires the .NET 10 SDK. From the repo root:

```powershell
.\build.ps1
```

This builds the solution, publishes the app, and builds the Inno Setup installer
package. See `build.ps1 -?`-equivalent parameters (`-Configuration`, `-Platform`,
`-Version`) for options.

## License

Apache License 2.0 — see [`LICENSE.txt`](LICENSE.txt).

See [`CREDITS.md`](CREDITS.md) for third-party dependencies and the format documentation this
project was built from, and [`AI.txt`](AI.txt) for a note on AI assistance used during development.

## Roadmap to v1.0.0

*(To be filled in.)*
