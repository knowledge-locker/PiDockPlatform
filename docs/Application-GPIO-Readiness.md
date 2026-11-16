# Physical GPIO readiness

Physical GPIO is unavailable. The shared `ApplicationGpioReadinessService` provides a read-only target preflight so app authors can distinguish detected host facts from the missing implementation and board acceptance evidence. It does not request lines, read pin levels, write outputs, mount devices or issue grants.

The local probe reports Linux versus another operating system, host architecture, a bounded model string from the fixed kernel device-tree model path when available, and up to 16 `gpiochip` filenames. A filename is presence evidence only: it is not proof of a character device, accessible GPIO ABI, stable line numbering or permission. Containers can be ARM64 without being a Raspberry Pi; an exposed device-tree model can also be incomplete. Incomplete observations remain explicit. Public metadata contains no arbitrary file content or configurable host paths.

`physicalAvailable` remains false even when a Linux ARM64 host reports a Raspberry Pi model and chip nodes. Neither app configuration nor simulated I/O test success can remove the current driver and physical-conformance blockers. The [Development I/O simulator](Application-IO.md) remains separate and may be used for disposable app fixtures today.

## Required board acceptance

Before enabling a physical adapter, implement and verify all of the following on each supported board/kernel combination:

1. A supported Linux GPIO character-device driver/ABI and verified board identity. Resolve the initial supported-board matrix explicitly; do not infer Pi 3/4/5 support from their common header.
2. Platform aliases with validated chip/line-offset to BCM/header mapping, reserved-pin policy and supported polarity/pull capabilities. The simulator's header table is not physical detection.
3. Broker-owned exclusive line requests and explicit per-app grants. No unrestricted device mount or app bypass. Deny conflicts, permission failures, revoked grants and stale lease generations.
4. Coherent timestamped input sampling, complete output-batch validation and documented commit behavior. State accurately whether kernel writes are atomic and whether output values are commanded or electrically measured.
5. Independent bounded lease expiry and safe maintenance/stop/restart/health-failure handoff. Test process crash, Docker failure, broker loss, power loss and reboot; a userspace cleanup callback cannot guarantee electrical fallback when the host is dead.
6. Wiring-appropriate electrical fallback, pull behavior and startup transition measurements. Where safety depends on power-loss behavior, use external hardware and define that boundary explicitly.
7. Measured latency, jitter, resource pressure, disconnect/error freshness and fault recovery. Do not promise a 20 ms scan through the current low-rate simulation HTTP API.
8. Engineering force/release interaction with physical leases, normal versus effective values, authority expiry and audit. Shared variable engineering currently grants no physical forcing.

Record the target versions, fixture wiring, test procedure and observed results. A completed software unit test, app health report or preflight observation is not a hardware certification. No physical-board or electrical test was performed as part of this preflight increment.
