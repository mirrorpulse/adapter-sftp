# MirrorPulse SFTP Adapter

An independent process Worker for the MirrorPulse current-user sync root.
The v2 development branch uses the fixed published `MirrorPulse.Adapter.Sdk` 0.2.1
package and SSH.NET 2026.0.0. Historical v1 release assets remain unchanged.

## Authorized roots and trust

Each enabled root has its own SFTP connection, endpoint, username, credential
reference and SHA256 host-key pin. Configuration and trust decisions belong to
MirrorPulse. Disabled roots request no credential and open no connection.
Unknown keys require a decision bound to the root, request and fingerprint;
changed pinned keys are rejected. Passwords never appear in ordinary output.

Directory pages, stat results and range streams carry the root key. Cursors
cannot be replayed against a different root or directory. Directory entries are
bounded to 8192 items and 4096-character names. Unsupported entries and symbolic
links are refused; traversal, absolute paths and control characters are rejected.
A server-side jail is still required to enforce filesystem confinement against
concurrent server namespace changes.

## Current development boundary

The read path checks the requested metadata revision before and after transfer.
Size and modification time form an observation token, not a server-side
compare-and-swap guarantee or a content snapshot. File uploads use the SDK
transfer lease, sibling staging, full-content readback and checks before
publication. Each root accepts `mutationPolicy` as `Optimistic` (default) or
`ReadOnly`. Invalid policies fail before credential requests. Read-only roots
refuse uploads before receiving bytes. Namespace mutations currently return
`ConditionalMutationUnavailable` until their separate recovery profile passes.

An existing file is retained under `.mp-recovery-<operationId>` before the
verified stage is renamed into place. `.mp-stage-<operationId>` and
`.mp-journal-<operationId>` are also reserved; these names are not projected in
normal directory pages. The remote receipt binds the root, path, preconditions,
length and content digest. Stable retries verify the result rather than blindly
republish it. Unknown results retain data and evidence and return
`MutationOutcomeAmbiguous` with a root-relative recovery path. Retained copies
and receipts consume remote space; the Worker has no local persistent store.
Local transfer leases are released after success, failure or receive cancellation.

SSH.NET supplies ordinary SFTP rename, upload and stream APIs. External writers
can race the last check and rename, and a retained copy may miss the last
concurrent edit. This is optimistic synchronization without CAS or exactly-once
guarantees. A broken connection is reconnected before readback; host-key trust
is checked again against the accepted fingerprint.

## Verification

Run `pwsh -File eng/setup-test-environment.ps1`, then
`pwsh -File eng/verify.ps1`. The isolated Paramiko fixture serves two independent
SSH sources with different credentials and keys. The process tests exercise
actual SSH authentication, pinned and interactive key decisions, root-bound
pagination and ranges, stale reads and explicit disabled-root behavior, verified
uploads, retained originals, empty and multi-frame content, stable operation
binding, edits with unchanged size/time, read-only refusal and cache cancellation.

Protected signed v2 publication and production Host acceptance are subsequent
acceptance gates. The current source tests do not establish those gates.

## Package execution

The development package includes a private .NET runtime for `win-x64` and
`win-arm64`, including `createdump.exe`, runtime notices and the exact locked
third-party dependency licenses. Signing and verification use the shared ordinal
canonical inventory. Conformance launches the signed payload with shared runtime
lookup disabled and checks the actual loaded `coreclr.dll` path.

CI runs the source and signed package profiles on native x64 and ARM64 runners.
Original TRX and package hash receipts are retained as artifacts. Organization
signing, production Host acceptance and protected publication remain separate
release gates.

## Publication

Preview candidates are resolved from `develop` as `X.Y.Z-preview.N`. A manual
`publish=false` run validates a disposable candidate. Actual preview publication
requires `publish=true` and the exact `PUBLISH` confirmation. Stable publication
starts from a reviewed `develop` PR merged into `main`, with one `breaking`,
`feature` or `fix` classification, and requires the protected `stable` approval.

The controller builds once, signs and freezes the source, version and asset
hashes, then runs the exact candidate on native x64 and ARM64. It consumes fixed
SDK 0.2.1 assets and the fixed production Host verifier. The Host profile checks
independent pinned SSH sources, credentials, CfSharp reads, disabled roots,
private runtime loading and safe mutation refusal. Safe write capabilities remain
an open release requirement. Signing keys are supplied only to the protected
signing job and are never read from private files or exported. Existing assets
and tags remain immutable.
