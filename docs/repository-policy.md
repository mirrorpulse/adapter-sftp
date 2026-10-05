# Repository and release policy

MirrorPulse repositories follow the [CfSharp branch and release model](https://github.com/MirrorPulse/CfSharp).

## Branches and review

`develop` is the integration branch. Open contributor pull requests against it.
`main` accepts only pull requests from this repository's `develop` branch, with
exactly one `breaking`, `feature` or `fix` label. These select major, minor or
patch increments for stable releases. Preview versions use `X.Y.Z-preview.N`.
Development and CI artifacts are not formal releases.

`main` requires one approving review, discards stale approvals after a push,
requires current CI checks and resolved conversations, and enforces linear
history. Administrators obey the same protection. Force pushes and branch
deletion are forbidden. Required checks use actual check-run names, without
assuming a workflow-name prefix. Any member with the required repository
permission can provide the approving review; no named PR reviewer is enforced.

## Publication

The shared publication model uses the `stable` environment, restricted to protected
branches and approved by a reviewer other than the initiating actor. Preview
publication is explicitly requested from `develop` through the `preview`
environment. Provider signing remains a separate credential boundary; never
expose signing secrets in pull request builds. SDK and provider package versions
are independent of the negotiated Worker protocol version.

The current provider release workflow accepts numeric package versions and uses `adapter-signing` and `adapter-release`. The shared `stable` and `preview` settings are configured, but package version validation and this release workflow still require migration before provider previews can be published. Environment settings alone do not migrate an existing workflow.

Release tags cannot be updated or deleted, including by administrators. SDK tags
use `sdk-vVERSION`; provider and application tags use `vVERSION`. Published
versions and their original artifacts are never rebuilt or overwritten.

Workflow files do not create GitHub protection. The product repository contains
the shared configuration and `eng/sync-repository-governance.ps1`: its default
is a dry run; `-Apply` configures settings and verifies the API readback. Existing
environment secrets are not read or replaced. Stable deployment reviewers are
copied from the actual CfSharp environment configuration at application time.
