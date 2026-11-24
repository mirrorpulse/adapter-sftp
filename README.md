# MirrorPulse SFTP Adapter

This optional Adapter connects MirrorPulse to SFTP servers. Install the signed
`.mpadapter` from this repository's releases in Adapter management or with
`MirrorPulse adapter install PATH`. The Worker uses the published Adapter SDK
1.0.0 and SSH.NET 2026.0.0. A deployment declares one root. Install the package
once and create a separate deployment for each server or folder.

## Authorized roots and trust

Enter an `sftp://` folder URL without credentials, a username, and a password
in the signed deployment form. Paste the server's SHA-256 SSH host key
fingerprint obtained from its administrator. The form accepts the 43-character
base64 digest with or without OpenSSH's `SHA256:` prefix. The Worker refuses a
different key. The Host saves the password and passes an opaque reference
to the Worker. Each enabled deployment opens its own connection. A disabled
deployment requests no credential and opens no connection.

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
refuse uploads before receiving bytes or namespace mutations before commands.
Same-root file moves retain a verified copy; deletion renames the source to its
recovery name. Directory creation and empty-directory deletion use mature
nonrecursive APIs. Existing move destinations, cross-root moves and directory
tree moves are refused. A directory containing recovery evidence is not empty.

An existing file is retained under `.mp-recovery-<operationId>` before the
verified stage is renamed into place. `.mp-stage-<operationId>` and
`.mp-journal-<operationId>` are also reserved; these names are not projected in
normal directory pages. The remote receipt binds the root, path, preconditions,
length and content digest. Stable retries verify the result rather than blindly
republish it. Unknown results retain data and evidence and return
`MutationOutcomeAmbiguous` with a root-relative recovery path. Retained copies
and receipts consume remote space; the Worker has no local persistent store.
Local transfer leases are released after success, failure or receive cancellation.

Remote proof survives Worker restart. Corrupt or oversized proof blocks mutation.
True disconnects after publication or namespace changes return an unknown result;
stable retries inspect the source, target and retained content before proceeding.
Publication reaches a terminal result before queued Cancel is processed, so a
cancellation acknowledgement is not proof of remote rollback. A changed host key
is refused on reconnect even if the previous unknown key was approved interactively.

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
binding, edits with unchanged size/time, read-only refusal and cache cancellation,
retained moves/deletes, nested directories and empty-only deletion, actual
disconnects, Worker restart, damaged proof, publication cancellation and key
rotation during readback.

Publication additionally requires the signed package and product Host gates
described below.

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
SDK 1.0.0 assets and pins the product Host source. The exact signed Worker
runs the provider's network suite on both architectures. The product gate
installs the single-root package, validates the signed form, saves credentials
through the Host, and verifies a pinned SSH listing, a revision-bound range read
and stable upload recovery after the Worker restarts.
Both native profiles must pass against the exact signed candidate before formal
v2 publication. Signing keys are supplied only to the protected
signing job and are never read from private files or exported. Existing assets
and tags remain immutable.
