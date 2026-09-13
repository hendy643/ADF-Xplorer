# Credits & Third-Party Dependencies

ADFXplorer builds directly on the work of several other open-source projects. Credit where it's due:

## Runtime dependency

- **[WinFsp](https://github.com/winfsp/winfsp)** by Bill Zissimopoulos and contributors — the
  user-mode filesystem framework ("FUSE for Windows") that actually presents the mounted `.adf` as a
  drive/folder to Windows. Licensed GPLv3 with a FLOSS exception. Must be installed separately on any
  machine that mounts an image (https://winfsp.dev) — not redistributed by this project.

## Library dependency

- **[WinFsp.Native](https://github.com/hooyao/winfsp-native)** by hooyao — the modern .NET 8+ binding
  for WinFsp (`IFileSystem`/`FileSystemHost`, source-generated P/Invoke) that `AdfXplorer.WinFsp` is
  built against, in place of the official WinFsp.Net binding (which targets classic .NET Framework).
  MIT licensed.

## Format documentation

The Amiga OFS on-disk block layouts implemented in `src/AdfXplorer.Core/FileSystems/Ofs/` (boot block,
root block, file header block, data block byte offsets) were taken directly from:

- **[ADFlib](https://github.com/adflib/ADFlib)**'s `src/adf_blk.h` — the canonical, actively
  maintained C struct definitions for Amiga Disk Format blocks, which this project's
  `OfsBlockOffsets` constants mirror field-for-field.
- **ADFlib's `doc/FAQ/adf_info_V0_9.txt`**, itself derived from Laurent Clévy's original
  **[ADF format FAQ](https://adflib.github.io/FAQ/adf_info.html)** — the source for the AmigaDOS
  directory hash function implemented in `AmigaHash.cs`.

The Rigid Disk Block (RDB) partition-table reader in `src/AdfXplorer.Core/DiskImage/RigidDiskBlock.cs`
(used for WinUAE-style `.hdf` hard disk images) was written from two independently-checked sources that
agree field-for-field on the `RigidDiskBlock`/`PartitionBlock` layout:

- **[Amiga rigid disk block](https://en.wikipedia.org/wiki/Amiga_rigid_disk_block)** (Wikipedia).
- The Linux kernel's own RDB parser struct definitions,
  **[`include/uapi/linux/affs_hardblocks.h`](https://github.com/torvalds/linux/blob/master/include/uapi/linux/affs_hardblocks.h)**
  — a long-maintained, independent implementation of the same format.

The RDB creation writer (`src/AdfXplorer.Core/DiskImage/RigidDiskBlockWriter.cs`), specifically the
`FileSysHeaderBlock`/`LoadSegBlock` struct layout used to embed a third-party filesystem driver binary
(e.g. SFS, PFS3) directly into a created `.hdf`, was taken from:

- **[amitools](https://github.com/cnvogelg/amitools)** by Christian Vogelgsang — specifically its
  `rdbtool`'s `amitools/fs/block/rdb/FSHeaderBlock.py` and `LoadSegBlock.py`, a maintained Python
  implementation that reads/writes real RDB images accepted by AmigaOS. MIT licensed.

## Build tooling

- **[Inno Setup](https://jrsoftware.org/isinfo.php)** by Jordan Russell and Martijn Laan — builds the
  Windows installer package (`.exe`) in `src/AdfXplorer.Installer/`.
