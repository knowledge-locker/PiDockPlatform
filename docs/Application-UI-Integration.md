# PiDockOS application UI integration

For target acceptance, use the [scoped development gate record](Application-Development-Gate.md) and [readiness assessment](Application-Readiness.md). The reviewed Docker simulation target permits app development; this is not blanket deployment or hardware approval for other targets.

Agreed platform requirement, November 13, 2026. Every hosted application with a user interface must fit into PiDockOS as a host-openable interface component and reuse its visual language. This contract belongs to PiDockOS and applies to all applications, regardless of implementation language. A Docker deployment boundary does not create a separate product shell.

## Browser authorization window

PiDockOS, PiDockCloud and hosted PiDock apps use a five-minute browser-session authorization window. Successful login or password/authenticator confirmation starts the window; commands do not extend it. Hosted app commands, including Logic Engine edits, activation and engineering controls, use the host’s shared confirmation dialog after the window expires. Apps never collect owner credentials or receive authorization timestamps or reusable owner tokens. Existing per-app grants, command names, session validity and service-credential checks still apply on every command.

Live reading and subscriptions retain their existing session checks without prompting. The host bounds an authorization prompt to five minutes and cancels it when the workspace is reloaded, changed or closed. Cancellation sends no command. The SDK negotiates support for waiting for host authorization. Older SDKs may use existing authorization but receive `authorization_required` after expiry, without queuing a prompt or late command. The current SDK allows time for the host prompt before waiting for the result; unknown outcomes are never automatically retried. App-specific consequence reviews, such as Logic Engine’s activation/reset review, remain separate from credential authorization.

## Current implementation and required work

The first reusable preview is implemented. The appliance shell remains `src/PiDock.Appliance.Web/Components/Layout/MainLayout.razor`. Its legacy styles now import shared color/font tokens from `src/PiDock.Web.Shared/wwwroot/ui/1.0/tokens.css`. The versioned companion `components.css` provides opt-in `.pidock-app` / `pd-*` primitives for panels, layouts, buttons, fields, status badges, tables and accessible focus treatment. Other frontend languages can use the same CSS without a .NET dependency.

`GET /api/platform/v1/ui` requires the existing discovery credential or owner session and describes UI/style versions, asset paths, SHA-256 hashes and explicit limits. The CSS endpoints below `/api/platform/v1/ui/styles/1.0/` are public, non-secret, immutable assets. These exact bytes are embedded in the shared assembly and also shipped as static assets. Versioned assets must not be changed under the same published version; add a version for future incompatible or content changes. Unknown names return 404.

A separate `application-ui-contribution` schema registers `schemaVersion`, `appId`, `id`, `title`, `entryPath`, `requiredUiVersion` and `requiredStyleVersion`. Both version requirements must currently equal `1.0`. This is not a change to `application-manifest` or cloud publication/installation. The owner supplies local host configuration under `PiDock:ApplicationUi:Registrations`; app requests cannot register themselves or select arbitrary target URLs. The [sample setup](../tools/PiDock.AppUi.Sample/README.md) gives the complete configuration.

Registrations bind a contribution to a literal loopback HTTP origin on ports 1024–65535, with no credentials, path prefix, query or fragment. Configuration is reread on access. Invalid entries, duplicates, unsupported versions or more than 16 entries fail closed; disabling/removing a registration removes subsequent content access. DNS targets, external origins, redirects and forwarded owner/service credentials are not supported. The current broker can publish a restricted container port to loopback; the service must be started and approved independently. Automatic install-to-UI registration and app health tracking remain future work.

Registered interfaces appear in the owner navigation and Applications list and open at `/apps/{appId}/{id}`. The host renders a frame with `sandbox="allow-scripts"`; **no same-origin, form, popup or top-navigation permission is granted**. The content gateway is `/app-ui/{appId}/{id}/{path}`. It accepts bounded GET/HEAD resources only, without query strings, rejects unsupported content types/encodings and responses over 2 MiB, uses a ten-second deadline and a 240-request/minute per-client limit, and never follows redirects or forwards browser credentials/upstream cookies or headers. An unavailable service displays a generic error inside the frame. Reloading or closing the interface never stops the background app.

Opaque-origin frames cannot send the PiDockOS owner cookie for subresources. Therefore host configuration must explicitly approve exact non-secret CSS, JS, image and font paths in `publicAssets`. Those exact files become publicly readable with CORS; HTML, JSON, runtime data and every unlisted resource remain owner-protected. Extension/type mismatches are rejected. Never approve private configuration or secrets as public assets. Relative URLs resolve below the contribution's content route. Public asset access also disappears when the registration is disabled. This explicit allowance preserves sandbox isolation rather than weakening it for asset loading.

The optional [authenticated UI bridge 1.0](Application-UI-Bridge.md) now supplies explicit per-contribution named commands and live NDJSON data through a private MessageChannel and the authenticated host Blazor circuit. The server rechecks the current Owner session, registration, grants and an expiring server-held app credential; no owner cookie or service token goes to the frame. Subscriptions have payload/rate/concurrency limits, acknowledgements, cursor/gap handling, idle deadlines, cancellation and revocation checks. App-operation authority is limited to the named grant; hardware/maintenance permissions remain separate. Resource proxy methods stay GET/HEAD and CSP still blocks direct frame network connections and forms.

API/MCP discovery and schemas describe both the resource host and bridge. Public bridge SDK assets are versioned independently from the unchanged style bundle. Runtime commands are not new agent/MCP mutation tools. User identity disclosure, richer multi-role/step-up policies and package-driven registration remain planned. Frame load is presentation feedback, not a health attestation. The current Owner session can drive only its registered app's configured operations; an already accepted command can complete after UI closure or authorization loss, so an uncertain acknowledgement is never described as a rollback.

The disposable `tools/PiDock.AppUi.Sample` server and the existing appliance UI review harness exercise the real host components without changing a live account, deployment or hardware. Automated checks cover schemas, asset hashes, session/role isolation, configured target restrictions, relative/static access policy, revocation, redirect/body/type rejection and HTTP method limits. Bridge tests additionally cover command authorization/deduplication/limits, cursor ordering, bad frames, stale streams, consumer cancellation and revocation. Browser checks cover host launch/return/reload, local interaction, shared action styling and a 390-pixel viewport. The full platform conformance gate has not passed.

## Responsibility boundaries

| PiDockOS owns | An application owns |
| --- | --- |
| Outer shell, main navigation and approved menu/tab/launch surfaces | Its workspace rendered inside the approved host surface |
| Sign-in, session validation and platform authorization | Authorization of its own operations using the approved identity contract |
| Application registration, installation and lifecycle presentation | Domain logic, app configuration and app health |
| Shared visual assets, compatibility and platform status conventions | Domain visuals composed using those assets and conventions |
| Permission and hardware grants, platform diagnostics and shared services | Requests for explicitly granted services and accurate domain diagnostics |

Apps must not recreate a competing PiDockOS sidebar, header, login page or global notification system inside their hosted workspace. App-specific toolbars, editors, panels and navigation are permitted where they support the app's task. Closing a UI surface must not implicitly stop a background workload; background execution and presentation have separate lifetimes.

## Shared look and interaction

Use the actual PiDockOS typography, palette, surface hierarchy, spacing, radii, borders, buttons, form controls, focus treatment, status labels and responsive conventions. Current source uses a raspberry action accent, leaf-green health indicators and warm neutral surfaces. A concept mockup's independent palette is not the approved product identity.

Extend the extracted reusable tokens and scoped primitives rather than maintaining copied theme values in every app. The preview ships a versioned, language-neutral CSS bundle and sample; a .NET wrapper may be supplied but must not be the only consumption path. Define compatibility and asset integrity/update behavior. Global shell selectors in the current CSS require review before third-party reuse; blindly loading the whole stylesheet into an app can style unrelated elements.

Define approved semantic treatments for simulation, disconnected/stale values, warnings and testing overrides. Preserve visible text and keyboard access instead of relying on color or hover. Discover supported appearances rather than promising a dark theme that the current shell does not implement. Responsive sizing, dense editor workspaces, collapsible panels and accessibility must be demonstrated inside the real shell.

## UI contract scope and remaining work

The 1.0 preview fixes contribution metadata, exact UI/style compatibility, owner-configured resource routing and an optional independently versioned command/live-data bridge. Use the explicit bridge grant rather than inferring access from a workspace launch. Further lifecycle, role and distribution features still need reviewed extensions.

| Contract area | Required information and behavior |
| --- | --- |
| Contribution identity | Stable app/contribution IDs, display title, approved icon and supported host UI version |
| Launch | Approved menu, tab or other placement; app entry point; install/running/disabled/unavailable visibility rules |
| Routing | Host-approved local service target, path prefix, asset base paths, deep links, back navigation and refresh |
| Context | Minimal authorized user/app context, locale, appearance and available size; no service token in URLs or browser storage |
| Lifecycle | Ready/error, open/close, mount/unmount, suspended/hidden states, reconnect and unsaved-change handling |
| Live data | Explicit transport compatibility, authorization, bounded subscriptions, cancellation, stale/gap indicators and recovery |
| Compatibility | UI contract and design-system versions, validation, unsupported-version errors and upgrade rollback |

Use one generic registration/launch mechanism for all applications. Do not add a hard-coded product route as a substitute. Installation validation must bind a contribution to the approved app and its service, not an arbitrary third-party URL.

The initial architecture uses an isolated frame and a host-approved content gateway to the app's loopback service. Parent-shell privileges are not shared with the frame. The optional bridge extends it through the authenticated host circuit without loosening sandbox, CSP or static resource authorization. Do not load arbitrary app JavaScript into the privileged shell. A native component adapter must preserve equivalent isolation and authorization boundaries. Richer hosting requires an extended versioned contract and conformance fixture, not a per-app workaround.

The bridge uses exact iframe/port binding with explicit message types, correlation and bounded payloads. The SDK verifies its parent origin; the host does not trust arbitrary `null`-origin window messages. The one target wildcard required to transfer a port to an opaque frame is not an origin trust policy. Owner/service credentials never enter the frame. For any choice, validate paths/targets, reject open proxy behavior, and recheck session and app-operation authorization at server boundaries. Preserve HTTPS, CSRF protections where cookies are used, and WebSocket/stream authorization if supported.

## Implementation order and acceptance

1. **Design-system extraction (initial preview implemented):** inventory current shell styles; publish scoped tokens/primitives and a versioned sample; verify representative app controls against the real shell.
2. **UI contract (resource host and optional authenticated bridge 1.0 implemented):** agree registration, routes, context, lifecycle, permissions, isolation and supported streaming. Publish schemas, compatibility/error rules, and honest capability status through API and MCP.
3. **Host integration (configured preview implemented):** implement contribution registration, approved launch points and container UI routing, with removal/revocation cleanup and unavailable-app presentation.
4. **Conformance fixture (initial fixture implemented):** use a disposable sample app, not a hosted product, to prove launch, refresh/deep links, reconnect, narrow screens, keyboard operation, denied access, logout/revocation, app failure and update rollback. Verify source/target isolation and that UI closure does not stop background work.
5. **General availability to app authors:** bundle the same guidance, schemas and examples in API authoring and MCP resources, with explicit versions and available/planned status. Pass this gate alongside the existing local application platform conformance gate before hosted product integration.

App planning can proceed now. App plans must identify these platform prerequisites and defer concrete route/manifest/authentication assumptions until the relevant contracts exist. The same rule applies to local-only installations; PiDockCloud is not a dependency of local UI launch.

## Durable app data and private credentials

Use the generic [app resources](Application-Resources.md) contract for per-app durable data, credential files, rotation/revocation and managed restart delivery. Probe `/api/platform/v1/app-resources` or `pidock://platform/app-resources` before depending on broker opt-in. Keep these facilities server-side; frames receive neither credentials nor a data filesystem API.

App execution and recovery are independent of UI subscriptions. The generic [app lifecycle contract](Application-Lifecycle.md) supplies opt-in supervision, durable Start/Stop intent and current credential delivery after exit/reboot. Closing a workspace must not be used as a runtime Stop operation.

## Current shared app readiness

The shared [readiness inventory](Application-Readiness.md) distinguishes disposable conformance fixtures, product integration and physical release. Discover each target's backup, storage, image approval, removal, simulated I/O, engineering and diagnostic support through API/MCP before depending on it. These are general PiDockOS facilities and do not create a product-specific hosting path. The fixture launch instructions are in [AppUi.Sample](../tools/PiDock.AppUi.Sample/README.md). Its connected launcher now exercises the actual shared simulated I/O and engineering APIs, including normal/effective watch values, applied simulated-output batches and instance/revision-fenced force/release. The review coordinator is simulated; production broker/hardware conformance remains separate.

The connected sample also exposes an isolated host lifecycle review using shared startup-bound resource authority. UI closure still does not stop the process. Lifecycle authority and cleanup live on the host, and a disconnected frame marks readings unconfirmed rather than claiming cleanup. See [runtime access](Application-Runtime-Access.md).
