# MirrorPulse Adapters

This repository template is the starting point for an independently packaged MirrorPulse Adapter Worker.

## Layout

- `src/` contains reusable Worker SDK code.
- `samples/` contains a minimal executable Worker.
- `template/` contains the manifest and `.mpadapter` package skeleton.
- `eng/` contains repository validation and packaging scripts.

Adapters communicate with MirrorPulse over the current-user Named Pipe contract and receive configuration, credentials references, source-directory grants, and cache paths from MirrorPulse at runtime.

The template does not implement a storage protocol. Provider repositories should add their own protocol code and publish a signed `.mpadapter` release.
