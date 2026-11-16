# Fixed app health reader

API/MCP authoring clients can obtain the same bundled source from authenticated `/api/platform/v1/app-health/reader.c` or `pidock://platform/app-health/reader.c`; live health discovery supplies its SHA-256.

Build `pidock-health-probe.c` for the app image's **Linux target architecture**, then install it at `/usr/lib/pidock/health-probe` owned by root with mode 0555. A static executable can be reused by .NET and other-language images without adding a shell or language runtime. Example in a Linux build stage with a C compiler and static C library installed:

```sh
cc -std=c11 -static -Os -Wall -Wextra -Werror pidock-health-probe.c -o health-probe
```

Copy the built binary from that stage into the final image, outside the writable/noexec mounts:

```dockerfile
COPY --from=health-probe-build --chmod=0555 /build/health-probe /usr/lib/pidock/health-probe
```

Adjust only the build-stage output path to your build. Do not change the runtime path or add runtime arguments. PiDockOS invokes exactly this reader as UID/GID 1654 with no privileges or TTY. An app that requests health reporting must include it and implement the [reporting contract](../../docs/Application-Health.md).

The reader accepts no arguments. It opens only `/run/pidock/health/health.json`, does not follow a final symlink, requires a regular file of 1–1024 bytes, reads at most 1025 bytes, and writes at most 1024 stdout bytes. It emits no diagnostics or stderr. Nonblocking open prevents hanging on a FIFO. Exit 0 returns report bytes; exit 2 means missing report; exit 3 means invalid file/access/arguments. PiDockOS validates the JSON, startup identity, time and sequence itself. Never use this helper to read credentials or arbitrary files.

Docker's file-export API does not expose the app's live tmpfs report in the validated environment, so PiDockOS uses this bounded in-container reader. The app runs normally without elevated access, host bind mounts, an exposed health port or Docker access.
