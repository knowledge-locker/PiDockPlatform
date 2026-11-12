# Pi 5 GPIO maintenance

The root host broker uses Linux's GPIO character-device v2 API and discovers the Pi 5 RP1 chip by label. It does not depend on chip numbering, access `/dev/mem`, or take a line away from an existing kernel or application consumer.

With no GPIO policy, the interface can report header line configuration and consumers but cannot claim any lines or drive outputs. This is the default for the current test appliance: its owner confirmed on November 12, 2026 that nothing is connected to the header. Unknown voltage levels remain unknown; observing line direction is not a voltage measurement.

Maintenance claims require a current **local** owner session and the shared password/fresh-TOTP dialog. Cloud-relayed sessions cannot claim pins. Each claim belongs to a server-generated page session, lasts at most 60 seconds using a monotonic clock, and is released when the page is disposed. A broker timer expires claims even if the browser disappears. Normal broker shutdown also releases claims.

## Host policy

Only a local administrator should configure wiring policy, after documenting voltage compatibility, physical wiring and safe fallback levels. No web-provided configuration can expand it. Configure the broker through root-owned systemd service settings, not the unprivileged appliance web service. For example, **only after wiring review**, allow GPIO17 as an input and GPIO18 as an output whose normal-release fallback is LOW:

```ini
[Service]
Environment=Gpio__Inputs__0=17
Environment=Gpio__Outputs__18=false
```

Restarting the broker applies policy. Empty policy is appropriate for an unwired appliance. Reserved bus pins (GPIO2,3,7–11,14,15), EEPROM pins and fixed supply pins cannot be claimed even if erroneously listed in the policy. Existing consumers cause the kernel to reject the request. Direction, initial output value, and line reservation are configured in one kernel request.

The output fallback is written before closing a claim on release, expiry or normal shutdown. **Process crashes, kernel failure and power loss do not guarantee that fallback level.** Provide external pull resistors and hardware interlocks where an electrical state matters. This interface is not a safety controller. PWM, bus transactions and application handoff protocols are outside this maintenance implementation.

## Acceptance

Automated tests cover default denial, reserved pins, invalid durations, kernel conflicts, per-session release, monotonic expiry, fallback-before-close and ABI request layout. Physical acceptance is still required on the Pi 5: detect the actual RP1 chip; compare line direction/consumer reports with the host; verify HIGH/LOW and expiry using a meter on an agreed spare pin; test service termination and power-loss behavior with the intended external hardware. No physical output testing was performed on the unwired test appliance.

Kernel contract: [GPIO character-device API](https://docs.kernel.org/userspace-api/gpio/chardev.html) and [request a line](https://docs.kernel.org/userspace-api/gpio/gpio-v2-get-line-ioctl.html).
