# UI review

The appliance and cloud screens can be reviewed without a Raspberry Pi, real user enrollment, or changes to HTTPS trust. Two separate Development-only tools render the application source components directly. They bind only to 127.0.0.1 and use disposable state and an automatically generated fixture identity. The normal product authentication flow is not changed.

```sh
# Terminal 1
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project tools/PiDock.UiReview
# Terminal 2
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project tools/PiDock.Cloud.UiReview
```

Open [the appliance review](http://127.0.0.1:7351) and [the cloud review](http://127.0.0.1:7352). These HTTP endpoints carry no real credentials and are not deployment endpoints. Production mode refuses to start either tool. Keep the review process separate from production hosting.

The appliance combines local host measurements, sample storage capacity, and simulated GPIO. The cloud uses clearly named sample devices in an isolated review tenant. The workbench device receives synthetic updates; other samples demonstrate stale/offline readings. MQTT and the host Docker broker are disconnected. No physical outputs or user application data can be changed. Review state is deleted on normal shutdown.

## Findings addressed

- Storage previews could remain visible after edits. Numeric fields now invalidate the preview as they change; apply also checks the reviewed policy against the submitted values.
- Reserved GPIO bus pins appeared controllable until the server rejected a request. The UI and backend now share eligibility checks; unavailable actions are disabled and explained.
- Selected pins lacked a clear selection cue and accessible name. Added selection outlines, physical pin labels, pressed state and inspector focus.
- The GPIO inspector stretched alongside the full header. It now stays compact and sticky on desktop and appears before the map on small screens.
- Inactive interfaces overwhelmed the overview. It now shows up to four active links, with the full inventory on the Interfaces page.
- All cloud contact states shared the same colour. Online, stale and offline now have distinct labelled badges; stale rows identify readings as last known.
- Cloud timestamps did not identify their time zone. The column now explicitly displays UTC.
- Headings used slogans instead of clear page names. Updated page titles and moved storage implementation notes into expandable details.
- Added skip navigation, visible keyboard focus, clearer unavailable measurements and more usable touch targets.

## Validation

Reviewed desktop layouts at 1280 pixels and mobile layouts at 390 pixels. Browser checks exercised reserved-pin rejection in the UI, an assignable simulated output, override feedback, pin-inspector focus, and storage preview invalidation after editing. The mobile GPIO and cloud pages were checked for document-level horizontal overflow; wide tables scroll inside their cards.

Existing authentication and isolation tests remain required. This review does not validate Pi GPIO timing, electrical behavior, authenticated time or physical recovery.

## Remaining UI work

The overview also shows active resource alarms and telemetry delivery counters (pending, purged unsent and rejected writes). The cloud includes reported alarm counts and distinguishes historical-only devices from live contact. The updated source components were rebuilt and checked in the loopback review tools; MQTT remains disconnected in these UI fixtures.

Device onboarding and authenticator enrollment still use the console. File browsing, network configuration, actual traffic captures, complete alarm workflows, cloud user administration and real application persistence remain implementation milestones. Single-use device enrollment now exists in the product; the review tools display its real forms with network enrollment disabled. Screen-reader testing beyond semantic markup and a full contrast audit are still needed. The review tool's Sign out button simply returns to its fixture dashboard; authentication behavior is covered by the normal application tests.

## Raspberry inspired colour palette

Both applications now share a light palette with raspberry primary actions and navigation (#C51A4A), leaf-green healthy states and storage bars, white cards and warm neutral canvas (#F6F4F3). Amber remains reserved for warnings and forced outputs. Selected GPIO pins use raspberry outlines; health states keep explicit text labels. This is an inspired PiDock theme, not an official Raspberry Pi identity.

Primary button text measures 5.80:1 contrast; the checked text/background pairs for body copy, navigation, GPIO, and status badges all exceed 4.5:1. This check covers those pairs rather than a full accessibility certification. Shared CSS tokens keep the appliance, cloud, sign-in and review banner consistent.
