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
compare-and-swap guarantee or a content snapshot. Mutations currently return
`ConditionalMutationUnavailable` while safe publication and recovery are being
implemented. Existing files are never overwritten based on a stat precheck.

## Verification

Run `pwsh -File eng/setup-test-environment.ps1`, then
`pwsh -File eng/verify.ps1`. The isolated Paramiko fixture serves two independent
SSH sources with different credentials and keys. The process tests exercise
actual SSH authentication, pinned and interactive key decisions, root-bound
pagination and ranges, stale reads and explicit disabled-root behavior.

Self-contained packaging and protected signed v2 publication are subsequent
acceptance gates. The current source tests do not establish those gates.
