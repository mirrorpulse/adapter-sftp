# MirrorPulse Adapter Template

This directory is the starting point for an Adapter that extends MirrorPulse with a remote storage protocol or service.

Adapter template code is distributed under the [Apache License 2.0](../../LICENSE). A packaged Adapter may include its own license terms for its implementation and dependencies; declare those terms in its release documentation.

## Repository layout

```text
manifest.json                 Adapter metadata used by MirrorPulse
package/                      Package staging directory
  manifest.json               Manifest copied into the package root
  locales/en-US.json          Localized display metadata
  worker/win-x64/             Windows x64 Worker payload
  worker/win-arm64/           Windows ARM64 Worker payload
```

Keep the repository independently buildable. The package root must contain `manifest.json`, every declared Worker payload, and each locale resource referenced by `localeMetadata`.

## Manifest contract

Set a globally unique `adapterId`, publisher, semantic `version`, supported protocol range, and a Worker entrypoint for every supported runtime. Declare installation and instance limits when the Adapter must restrict duplicate installations or concurrent instances. Each `rootDefinitions` entry supplies the user-facing label and directory name for one first-level sync-root directory.

Declare only capabilities that the Worker actually uses. `network`, `sourceDirectory`, `remoteChanges`, and `rangeRead` are permission requests reviewed during installation. Localized labels and descriptions belong in `locales/` and are referenced by `localeMetadata`.

## Worker process contract

MirrorPulse starts one Worker process for each Adapter instance and connects to it through a current-user Named Pipe. The Worker must authenticate the pipe connection using the session handshake, respect cancellation, and return structured errors. The Worker may access its authorized source directory and network endpoints, but it must not assume access to other Adapter instances.

MirrorPulse supplies configuration, credential references, cache paths, and synchronization state through the IPC contract. Do not write persistent application configuration from the Worker. Use the supplied transfer cache for temporary files and clean temporary data promptly after a transfer completes or is cancelled.

## Configuration and storage

Adapter settings are owned by MirrorPulse. Treat configuration values as versioned input and remain forward-compatible with fields you do not recognize. Credentials are referenced by opaque identifiers; the Worker requests the reference and never persists secret material in the package or its own data directory.

## Packaging and signing

Build self-contained or framework-dependent Worker payloads according to the release policy, then stage them under the runtime paths in `manifest.json`. Create a `.mpadapter` ZIP with forward-slash entry names. Release artifacts are signed before distribution; unsigned packages require the user's global developer-mode choice and a security warning.

## Localization

Ship at least `en-US`. Add one JSON resource per locale and list every locale in `locales` and `localeMetadata`. Keep resource keys stable across versions so MirrorPulse can fall back to the default locale.

## Release checklist

1. Run the Adapter contract tests and build both Windows runtime payloads.
2. Validate manifest limits, capabilities, root definitions, and localization paths.
3. Build the `.mpadapter` package and verify its file list and hashes.
4. Sign the package and publish the release asset from a version tag.
5. Test installation, Worker startup, sync, offline queueing, and clean shutdown with a fresh Adapter instance.
