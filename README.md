# MirrorPulse SFTP Adapter

This repository contains the independent SFTP Worker process for MirrorPulse. Each configured instance runs in its own process and communicates with the Host through a current-user Named Pipe.

## Build

Run `pwsh ./eng/verify.ps1` to restore and build the Worker for Windows x64 and ARM64. The reusable IPC SDK is under `src/MirrorPulse.Adapter.Sdk/`.

## Release status

The Worker implementation is under product integration. No signed production `.mpadapter` release has been published from this repository. Release packages will include both architectures, a verified file inventory, and a detached package signature.

Licensed under Apache-2.0. See [LICENSE](LICENSE).

## Release governance

The release scripts and pinned staged workflow follow the template at commit
544c594. Version/tag inputs enter scripts through environment data and are
validated before paths or builds are created. Build has no signing secrets;
signing uses the `adapter-signing` environment; publishing alone has write
permission and uses `adapter-release`. Manual dispatch defaults to a verified
signed artifact without publishing a tag or Release.

Run `pwsh ./eng/verify-release.ps1` for hostile input rejection and a dual-RID
package signed with a disposable in-memory key. Production keys are read only
from signing-step environment variables. No private key file is read or exported.
The embedded inventory is verified before upload; MirrorPulse independently
verifies publisher trust at installation.

The repository owner must configure environment reviewers, trusted branch/tag
rules and signing-secret scope. YAML environment names alone do not enforce those
protections. Existing organization secrets remain compatible until that migration.
The current framework-dependent v1 runtime is retained by this release change.

The release workflow also verifies the newly signed candidate using MirrorPulse
16c6742 and real Local/WebDAV/SMB/FTP/SFTP Host/Worker fixtures on a disposable
runner. It records both source commits and the candidate package hash. Publishing
requires that protocol gate; signed dry-run assets remain unpublished.
