# PiDockOS app data helper

This trusted, generic helper applies a broker-validated staged snapshot to one stopped app volume. It is not an application entry point or a user script runner. Build/pinning, isolation, limits, maintenance prerequisites, retries and exclusions are documented in [the shared backup contract](../../docs/Application-Backups.md).

Fixed entry point: `dotnet /app/PiDock.AppData.Helper.dll apply|finish <32-hex-operation-id>`. The only persistent mount is `/data`. The broker stages the validated archive and protected identity before launching it; no file or shell command is accepted from the browser. Keep the broker feature disabled unless a reviewed immutable ARM64 helper image has been preloaded.
