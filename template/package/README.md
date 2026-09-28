# `.mpadapter` package layout

The package root is a ZIP archive with the `.mpadapter` extension:

```text
manifest.json
worker/
  win-x64/MirrorPulse.Adapter.Worker.exe
  win-arm64/MirrorPulse.Adapter.Worker.exe
locales/
  en-US.json
```

Detached signature metadata is supplied by the release pipeline. Package entries use forward-slash relative paths and are hashed before signing.
