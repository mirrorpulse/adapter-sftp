# MirrorPulse SFTP Adapter

This repository contains the independent SFTP Worker process for MirrorPulse. Each configured instance runs in its own process and communicates with the Host through a current-user Named Pipe.

## Build

Run `pwsh ./eng/verify.ps1` to restore and build the Worker for Windows x64 and ARM64. The reusable IPC SDK is under `src/MirrorPulse.Adapter.Sdk/`.

## Release status

The Worker implementation is under product integration. No signed production `.mpadapter` release has been published from this repository. Release packages will include both architectures, a verified file inventory, and a detached package signature.

Licensed under Apache-2.0. See [LICENSE](LICENSE).
