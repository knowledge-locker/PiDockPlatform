using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PiDock.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityState>))]
public enum CapabilityState { Available, Planned, Simulated }
[JsonConverter(typeof(JsonStringEnumConverter<CloudRegistrationState>))]
public enum CloudRegistrationState { Unregistered, Registered, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter<DiscoveryStatus>))]
public enum DiscoveryStatus { NotConfigured, NotDiscovered }
public enum OperationalPriority { Critical = 1, Warning = 2, Alert = 3, Notice = 4 }

public sealed record PlatformCapability(string Id, CapabilityState State, string Description, string[] Operations);
public sealed record CloudTarget(CloudRegistrationState Registration, string? BaseUrl, string? TenantId,
    string? DeviceId, DiscoveryStatus Connectivity, DiscoveryStatus Publishing, string? PublishingEndpoint,
    string? CatalogEndpoint, bool CanPublish, string Reason);
public sealed record PlatformDiscoveryDocument(int SchemaVersion, string ContractVersion, string ApiVersion,
    string Product, string Stage, string Architecture, string? BoardModel, CloudTarget Cloud,
    PlatformCapability[] Capabilities, string[] ContractIds, string[] PermittedActions);
public sealed record ContractDescriptor(string Id, string Version, string Status, string SchemaPath);

// Registry-reference authoring and shared log ingestion contracts. Installation/grants remain separate.
public sealed record PlatformApplicationManifest(int SchemaVersion, string Id, string Version,
    string RequiredApiVersion, string Image, string[] RequestedCapabilities);
public sealed record ApplicationLogEvent(int SchemaVersion, Guid EventId, string EventCode,
    DateTimeOffset ObservedAt, string DiagnosticLevel, OperationalPriority? Priority,
    string Message, string? CorrelationId, Dictionary<string, JsonElement> Fields);

public static class PlatformContracts
{
    public const string Version = "1.0";
    public const string ApiVersion = "v1";
    public const string ApiRoot = "/api/platform/v1";
    private static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>
    {
        ["discovery"] = typeof(PlatformDiscoveryDocument),
        ["application-manifest"] = typeof(PlatformApplicationManifest),
        ["application-runtime-manifest"] = typeof(ApplicationManifest),
        ["application-runtime-support"] = typeof(ApplicationRuntimeSupport),
        ["application-runtime-access-support"] = typeof(ApplicationRuntimeAccessSupport),
        ["application-authoring-plan"] = typeof(ApplicationAuthoringPlan),
        ["application-authoring-report"] = typeof(ApplicationAuthoringReport),
        ["application-storage-support"] = typeof(ApplicationStorageSupport),
        ["application-storage-status"] = typeof(ApplicationStorageStatus),
        ["application-storage-policy"] = typeof(ApplicationStoragePolicy),
        ["application-install-support"] = typeof(ApplicationInstallSupport),
        ["application-install-approval"] = typeof(ApplicationInstallApproval),
        ["application-removal-support"] = typeof(ApplicationRemovalSupport),
        ["application-removal-status"] = typeof(ApplicationRemovalStatus),
        ["application-backup-support"] = typeof(ApplicationBackupSupport),
        ["application-backup-info"] = typeof(ApplicationBackupInfo),
        ["application-data-restore-status"] = typeof(ApplicationDataRestoreStatus),
        ["application-io-support"] = typeof(ApplicationIoSupport),
        ["application-gpio-readiness"] = typeof(ApplicationGpioReadinessStatus),
        ["application-io-grant"] = typeof(ApplicationIoGrant),
        ["application-io-signal"] = typeof(ApplicationIoSignal),
        ["application-io-lease"] = typeof(ApplicationIoLease),
        ["application-io-frame"] = typeof(ApplicationIoFrame),
        ["application-engineering-support"] = typeof(ApplicationEngineeringSupport),
        ["application-signal-registration"] = typeof(ApplicationSignalRegistration),
        ["application-signal-frame"] = typeof(ApplicationSignalFrame),
        ["application-signal-snapshot"] = typeof(ApplicationSignalSnapshot),
        ["application-signal-write-request"] = typeof(ApplicationSignalWriteRequest),
        ["application-signal-force-request"] = typeof(ApplicationSignalForceRequest),
        ["application-signal-release-request"] = typeof(ApplicationSignalReleaseRequest),
        ["application-update-policy-request"] = typeof(ApplicationUpdatePolicyRequest),
        ["application-update-policy"] = typeof(ApplicationUpdatePolicy),
        ["application-update-support"] = typeof(ApplicationUpdateSupport),
        ["application-update-status"] = typeof(ApplicationUpdateStatus),
        ["application-update-request"] = typeof(ApplicationUpdateRequest),
        ["application-dependency"] = typeof(ApplicationDependency),
        ["application-dependency-status"] = typeof(ApplicationDependencyStatus),
        ["application-orchestration-support"] = typeof(ApplicationOrchestrationSupport),
        ["application-orchestration-status"] = typeof(ApplicationOrchestrationStatus),
        ["application-health-policy"] = typeof(ApplicationHealthPolicy),
        ["application-health-report"] = typeof(ApplicationHealthReport),
        ["application-health-support"] = typeof(ApplicationHealthSupport),
        ["application-health-status"] = typeof(ApplicationHealthStatus),
        ["application-lifecycle-support"] = typeof(ApplicationLifecycleSupport),
        ["application-lifecycle-status"] = typeof(ApplicationLifecycleStatus),
        ["application-resource-support"] = typeof(ApplicationResourceSupport),
        ["application-credential-info"] = typeof(ApplicationCredentialInfo),
        ["application-log-event"] = typeof(ApplicationLogEvent),
        ["application-diagnostics-support"] = typeof(ApplicationDiagnosticsSupport),
        ["application-notification-policy"] = typeof(ApplicationNotificationPolicy),
        ["application-notification"] = typeof(ApplicationNotification),
        ["application-ui-contribution"] = typeof(ApplicationUiContribution),
        ["application-ui-discovery"] = typeof(ApplicationUiDiscovery),
        ["application-ui-command"] = typeof(ApplicationUiCommand),
        ["application-ui-command-result"] = typeof(ApplicationUiCommandResult),
        ["application-ui-event"] = typeof(ApplicationUiEvent),
        ["application-ui-bridge"] = typeof(ApplicationUiBridgeDiscovery),
        ["application-ui-bridge-grant"] = typeof(ApplicationUiBridgeGrant),
        ["application-ui-cursor"] = typeof(ApplicationUiCursor),
        ["application-release"] = typeof(ApplicationReleaseRequest),
        ["measurement"] = typeof(ApplicationMeasurement),
        ["shared-event"] = typeof(SharedServiceEvent),
        ["cloud-applications"] = typeof(CloudApplicationDiscovery),
        ["application-alarm-request"] = typeof(ApplicationAlarmRequest),
        ["application-alarm"] = typeof(ApplicationAlarm)
    };
    private static readonly JsonSerializerOptions SchemaOptions = new(StateFiles.Json)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static ContractDescriptor[] List() => Types.Keys.Select(id =>
        new ContractDescriptor(id, Version, "preview",
            $"{ApiRoot}/contracts/{id}")).ToArray();

    public static JsonNode? Schema(string id)
    {
        if (!Types.TryGetValue(id, out var type)) return null;
        var schema = SchemaOptions.GetJsonSchemaAsNode(type);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = $"urn:pidock:contracts:{Version}:{id}";
        if (schema["properties"]?["schemaVersion"] is { } schemaVersion) schemaVersion["const"] = 1;
        if (id is "application-alarm-request" or "application-alarm")
            schema["properties"]!["priority"] = JsonNode.Parse("""{"type":"integer","enum":[1,2,3,4]}""");
        if (id == "application-log-event")
        {
            schema["properties"]!["priority"] = JsonNode.Parse("""{"type":["integer","null"],"enum":[1,2,3,4,null]}""");
            schema["properties"]!["diagnosticLevel"]!["enum"] = JsonNode.Parse("""["Trace","Debug","Information","Warning","Error","Critical"]""");
        }
        if (id == "application-manifest")
        {
            schema["properties"]!["requiredApiVersion"]!["const"] = ApiVersion;
            schema["properties"]!["image"]!["pattern"] = "^[a-z0-9][a-z0-9./:_-]{0,200}@sha256:[a-f0-9]{64}$";
        }
        if (id == "application-ui-contribution")
        {
            schema["properties"]!["requiredUiVersion"]!["const"] = ApplicationUiRules.Version;
            schema["properties"]!["requiredStyleVersion"]!["const"] = ApplicationUiRules.StyleVersion;
            schema["properties"]!["entryPath"]!["pattern"] = "^(?!/)(?!.*(?:^|/)\\.\\.?(?:/|$))[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$";
        }
        if (id is "application-ui-event" or "application-ui-cursor")
        {
            schema["properties"]!["sequence"]!["minimum"] = 0;
            schema["properties"]!["sequence"]!["maximum"] = ApplicationUiBridgeRules.MaximumSequence;
        }
        if (id == "application-ui-event") schema["properties"]!["kind"]!["enum"] = JsonNode.Parse("[\"snapshot\",\"update\",\"heartbeat\"]");
        if (id == "application-ui-command") schema["properties"]!["arguments"] = JsonNode.Parse("{\"type\":\"object\"}");
        return schema;
    }

    public const string AuthoringGuide = """
        SHARED APP DATA AND CREDENTIALS (engineering preview):
        Probe GET /api/platform/v1/app-resources or pidock://platform/app-resources; require enabled=true.
        Runtime ApplicationManifest.Resources requests PersistentData and up to eight named Credentials.
        These runtime requests are separate from the platform package manifest. Never include tokens in manifests.
        Prepare /var/lib/pidock/app-data in the image, owned by UID/GID 1654. A per-app named volume
        survives stop/replacement; discover optional backup/restore and storage admission separately.
        Hard per-app growth quotas, automatic purge and volume migration remain separate work.
        Resource-enabled images must call PiDock.Core.ApplicationBootstrap.InitializeAsync before any output
        or application work. Bootstrap reads a bounded stdin packet, verifies writable app data, writes
        private 0400 credential JSON files under tmpfs /run/secrets/pidock, and acknowledges readiness.
        Read credentials through ApplicationBootstrap.ReadCredential(name); never log or send them to frames.
        Broker-provided PIDOCK_APP_ID and PIDOCK_BOOTSTRAP_PROTOCOL contain no secrets.
        Owner creates/rotates/revokes app-scoped expiring credentials through /app-resources with fresh MFA.
        Purpose ui-bridge is only for the private host-to-app bridge; purpose platform uses explicitly granted
        shared service scopes over HTTPS. Hardware and cross-app read authority are never implied.
        Use PiDockOS managed start/restart to refresh credential files. Rotation/revocation blocks host
        acceptance immediately; apps must be restarted to replace their old file. Docker automatic restart
        is disabled for resource-enabled apps; opt-in PiDockOS supervision performs recovery. Never substitute env
        tokens, browser credentials, host bind mounts, or a Docker socket. Protect and back up both
        the host vault and broker private state/key directories. Root/Docker administrators remain trusted.

        SHARED APP LIFECYCLE (engineering preview):
        Probe GET /api/platform/v1/app-lifecycle or pidock://platform/app-lifecycle; require enabled=true.
        Resource-enabled apps may request Resources.AutomaticRecovery=true or use the Owner recovery control.
        The appliance host supervises without a browser or cloud connection; the broker persists operator intent,
        policy, revision, retry count and deadlines. Stop is durable. Start/Restart clears the attempt budget.
        Automatic starts resolve current vault credentials, never a retained secret snapshot. Missing credentials
        wait without spending attempts. Five attempts use increasing backoff; 60 seconds of running stability
        resets the budget. Interrupted unacknowledged startup is stopped before redelivery. A paused budget
        requires manual Start/Restart; toggling recovery never overrides Stop or resets that budget.
        Do not enable recovery for one-shot jobs or before defining safe startup and retained-state semantics.
        Process exit/reboot recovery does not imply GPIO fallback, package replacement, rollback,
        deleted-container recreation or uninstall. App dependency/shutdown support is discovered separately.

        SHARED APP HEALTH AND READINESS (engineering preview):
        Probe GET /api/platform/v1/app-health or pidock://platform/app-health; require enabled=true.
        Resources.Health requests StartupSeconds (15-300), FreshnessSeconds (10-120), FailureThreshold (1-10)
        and RestartOnFailure (default false). Health alone requests managed bootstrap; data/credentials optional.
        Health discovery advertises the reader source URL and SHA-256. Fetch authenticated
        /api/platform/v1/app-health/reader.c or bundled pidock://platform/app-health/reader.c, verify the
        advertised digest and compile for the image Linux architecture during its build.
        Install the fixed Linux native reader from sdk/health-probe at /usr/lib/pidock/health-probe (root-owned 0555).
        Call ApplicationHealthReporter.Create(bootstrap), then Report(live,ready) at least every five seconds
        based on critical work and local readiness checks. The app-owned tmpfs report is private atomic JSON
        at /run/pidock/health/health.json: schemaVersion 1, current deliveryId, increasing positive sequence,
        UTC reportedAt, live and ready. Ready implies Live. Never include secret/free-form diagnostic data.
        Broker checks run as UID/GID 1654 with a fixed argument-free reader, no shell/privilege/network probe,
        two-second deadline, maximum 1024 report bytes and bounded framing. No app Docker socket is granted.
        Bootstrap acknowledgement is delivery readiness, not app readiness. Missing/invalid/stale reports have
        initial grace; explicit liveness failure counts immediately. Never-ready timeout is a failure; readiness
        withdrawal after first Ready does not restart a Live app. Runtime check failures are Unknown, not app faults.
        Unknown/expired observations are never Ready. Health monitoring runs without enabling automatic recovery.
        Health restarts require both RestartOnFailure and app/platform recovery and use the same durable retry
        budget/Stop intent. Health-enabled apps need 60 seconds of continuous fresh Ready reports to reset it.
        No health contract means not_configured, never an invented Ready. Reporting does not imply UI/command
        gating, GPIO authority, physical safety or independent verification of app logic.

        SHARED APP DEPENDENCIES AND SHUTDOWN (engineering preview):
        Probe GET /api/platform/v1/app-orchestration or pidock://platform/app-orchestration; require enabled=true.
        Resources.Dependencies contains at most eight {appId,condition} entries. Default condition ready requires
        fresh declared health; running explicitly accepts completed bootstrap without a health contract. No self
        references, duplicate targets or known cycles are accepted. Maximum 16 resource apps. Missing/unknown,
        stopped, not-ready or transitively blocked providers hold startup without spending attempts or starting
        the providers. Dependencies grant neither network connectivity nor credentials, service or GPIO access.
        Use Resources.ManagedLifecycle=true for a provider that needs no data, credentials, recovery or health.
        Data/credentials/health/recovery/dependency requests also enable managed lifecycle automatically.
        Manual and automatic startup use the same broker readiness gate. Missing dependencies queue an approved
        start; current credentials are resolved again when it becomes eligible. Deferred starts and automatic
        dependent resumes share the bounded retry budget. Stop persists stopped intent; stopping/restarting a
        provider suspends transitive dependents in reverse dependency order, retaining their running requests.
        Dependency loss also suspends dependents; they resume in order after required providers return. Explicit
        stops and paused budgets are preserved. A dependency never starts an explicitly stopped provider.
        Maintenance pause stops resource apps in reverse order and requires Owner resume. Broker shutdown
        journals the same sequence, retaining intent and allowing ordered resume after broker restart. App
        ShutdownSeconds is 1-10 (default 10); new containers use SIGTERM before Docker's kill timeout. Handle
        that signal, withdraw readiness, finish/cancel work and flush durable state before exit. Never wait for
        a provider already stopping. Host deadline 180 seconds; forced termination/runtime failure cannot
        guarantee cleanup. Legacy non-resource containers are outside coordinated shutdown.
        Broker resources/orchestration/lifecycle permissions are opt-in; preserve state/keys and journal across
        updates. Disabling orchestration does not bypass declared dependency startup gates or a saved pause.
        Unknown/corrupt catalog or journal must be repaired rather than guessed. Package rollout/rollback,
        independent hardware-safe fallback and distributed service orchestration remain separate work.

        OFFLINE AUTHORING CHECKS:
        tools/PiDock.AppAuthoring.Check validates a bounded offline plan against shared runtime/UI/bridge
        contracts. It reports fixed issue codes and target checks without echoing supplied secret values.
        An authoring plan is not an installable package or an authorization grant. A structurally valid
        offline result always leaves TargetVerified and ReadyForDeployment false. Discover the actual
        target, approvals, grants and conformance evidence before deployment. See
        docs/Application-Authoring-Checklist.md and the disposable shared AppUi.Sample.

        SHARED LOCAL APP NOTIFICATIONS (engineering preview):
        Probe app-diagnostics through the platform API or MCP. Explicit local priority policies may
        create bounded durable inbox entries with alarm transitions. Read/acknowledge through the
        scoped app API with notifications.read/notifications.acknowledge; a notification receipt is
        separate from acknowledging its alarm. Full queues reject further eligible transitions rather
        than silently losing notices. No email, SMS, cloud or external message delivery is supplied.
        Docker stdout/stderr capture and coordinated diagnostic verbosity remain separate work.

        SHARED MANAGED APP REMOVAL (engineering preview):
        Probe app-removal through the platform API or MCP. Owner uses /app-removal with fresh authentication.
        Removal requires completed maintenance, no dependent declarations and settled version/data operations.
        Only verified stopped managed containers and private networks are removed; data volumes, snapshots
        and cached images are retained. Protected interruption journals block startup/resume even if the
        feature is disabled. Retry the same removal operation after repair. A separate Owner completion
        confirms external UI registrations, credentials and grants have been removed; these host/cloud
        configuration domains are not inferred by the container broker. The Owner UI checks known local
        registrations, current managed/configured app credentials and I/O grants before acknowledgement.
        Automatic data purge and atomic cross-service uninstall are not implemented. Maintenance remains
        paused after completion. See docs/Application-Removal.md for the required cleanup checklist.

        SHARED STORAGE ADMISSION AND IMAGE APPROVAL (engineering preview):
        Probe app-storage and app-install-trust through the platform API or matching MCP resources.
        Optional storage admission requires an explicitly confirmed local Docker backing filesystem;
        new data volumes must leave the configured capacity reserve. Docker volume usage is an estimate,
        never a per-app enforced quota. Ongoing growth quotas and volume migration remain separate work.
        Optional exact-manifest image approval requires a current explicit Owner decision for the
        pinned image, app ID and every requested runtime setting. Record a reason and expiry up to
        24 hours. Broker validates Linux ARM64 metadata before creating an approved container.
        An approval is an unsigned-image exception, never publisher-signature verification or a
        hardware/UI/credential grant. Expiry/revocation prevents new installs and update candidates;
        existing container restarts retain their original lifecycle contract. Full package import,
        signatures, registry credentials and transactional UI registration remain separate work.

        MANAGED APP CAPABILITY CHECKS (engineering preview):
        Managed platform credentials with system.read may GET /api/apps/v1/runtime-support.
        This bounded, read-only metadata aggregates resource, health, lifecycle, orchestration,
        I/O, engineering and runtime-access support. Require enabled, compatible contracts.
        Missing/unreachable broker support returns 503 rather than invented capabilities.
        It grants no lifecycle mutations, Owner session or platform.discovery.read authority.
        No delivery identity, credential values or private broker observations are returned.
        See docs/Application-Runtime-Support.md; use ApplicationRuntimeSupportClient.Read.

        SHARED RUNTIME AUTHORITY (engineering preview):
        Probe app-runtime-access through API/MCP. Opt-in PiDock:Apps:RuntimeAccess:Enabled binds I/O and
        engineering to the current managed startup. Set ApplicationRuntimeAccess.Bind(platformClient,bootstrap)
        using the private ApplicationBootstrapPacket. Never forward its delivery identity/header to a frame,
        discovery, public DTO, URL or log. Named UI commands still use their separate bridge credential.
        Host observations include requested state, actual process state, bootstrap, dependencies, transitions
        and health; startup Live/not-Ready can initialize I/O, but unavailable/unhealthy observations deny it.
        The selected process can initialize authorized I/O during image staging/readiness verification,
        including rollback verification; switching and pending/failed rollback deny runtime admission.
        This does not permit manual lifecycle mutations during a version operation or release dependents early.
        Pause, Stop, exit, unhealthy status, changed startup or stale/unreachable broker revoke admissions,
        reset simulated outputs and clear forces/queued writes independently of application cleanup.
        Old handles and fenced registrations never revive; use a fresh instance after re-admission.
        Broker polling/rechecks and a three-second observation age are scheduling policies, not a hard
        real-time or physical safe-state guarantee. Disabled legacy preview mode supplies no such binding.
        The lifecycle review controls supervise only a disposable local sample process, not Docker hardware.

        SHARED APP I/O SIMULATION AND ENGINEERING (engineering preview):
        Probe GET /api/platform/v1/app-io and /app-engineering, or matching pidock://platform resources.
        App I/O uses /api/apps/v1/io with explicit io.read/io.write scopes and owner-configured alias grants.
        The first adapter is Development-only simulation, identified as simulation/physical=false.
        sim:bcm aliases identify simulated pins, never physical electrical measurements. Exclusive bounded
        leases, complete batch validation, revision checks and credential/grant expiry apply; release
        returns outputs to configured simulated fallback. Input stimulus additionally requires io.simulate.
        This does not implement a Linux GPIO driver, electrical fallback or hardware certification.
        Generic variable engineering uses /api/apps/v1/engineering, explicit target opt-in and managed
        credentials with signals.runtime or signals.engineer. Declare typed signals and runtime revision,
        publish normal frames and consume effective values/pending set-once writes at runtime boundaries.
        Constants reject writes/forces. Externally read-only values may be declared forceable separately.
        Force is temporary and does not change exposure; release resumes the current normal value.
        Credential loss, stale runtime, maintenance and restart clear transient controls. Audit stores
        action identity/scope/revision without secret values. Watch must display freshness/normal/effective.
        These generic variables do not grant physical GPIO or automatically connect to app I/O leases.
        Use the shared UI bridge for engineering controls; never expose service credentials to frames.
        Generate a fresh runtime instance GUID at every process start. Even same-instance registration
        clears forces and queued writes, so registration retries are an explicit engineering reset.
        Enable shared runtime access to bind registrations to the current private broker delivery.
        Supported-target validation and correctness of work inside the authorized process remain release gates.
        See docs/Application-IO.md and docs/Application-Engineering.md for preview limits.

        SHARED APP DATA BACKUP AND RESTORE (engineering preview):
        Probe GET /api/platform/v1/app-backups or pidock://platform/app-backups before depending on snapshots.
        Owner controls use /app-backups with fresh authentication. Snapshots are bounded, encrypted local
        copies of managed persistent app data, excluding platform credentials and keys. They require a
        completed maintenance pause, the same app identity and matching installed image on restore.
        Restore creates a safety snapshot and retains a durable startup barrier until completion.
        Incomplete restores must be retried or repaired before apps can resume; disabling the feature
        must not bypass this barrier. Successful restore leaves maintenance paused for Owner review.
        Image rollback never reverses data. These local snapshots do not protect against device loss;
        off-device export, scheduled backups and incompatible schema migrations remain separate work.
        Private keys and broker state must be protected separately. No archive or secret bytes are
        exposed through discovery, the app frame or MCP. See docs/Application-Backups.md.

        SHARED APP IMAGE UPDATES AND ROLLBACK (engineering preview):
        Probe GET /api/platform/v1/app-updates or pidock://platform/app-updates; require enabled=true.
        This updates only the immutable image of a Ready managed app. Configuration, app ID, loopback ports,
        credential references, dependency declarations and data volume are preserved. Runtime container IDs
        change; UI contribution registration remains unchanged. Applications must retain compatible interfaces.
        Resources.Updates requires DataCompatibility=backward-compatible, ReadinessSeconds 30-120 and
        StableSeconds 5-30 shorter than readiness, plus declared health. Owner may enable this policy for
        an existing health-enabled app. This is an explicit data-compatibility declaration, not a snapshot:
        rollback never reverses data writes, schemas or migrations. Do not enable it for irreversible migrations.
        Owner queues a digest-pinned image with the expected current image and operation ID. Image pull and
        stopped candidate creation precede downtime; candidate volume mounts disable image copy-up. One active
        version operation is allowed globally. During switching, dependents stop first and remain blocked until
        the replacement passes continuous fresh Ready checks. Current credentials are resolved at each start.
        Failed pull/staging cleans up without restarting the old app. Failed replacement restores the stopped
        retained container, redelivers current credentials and verifies it. A failed rollback holds for Owner
        attention rather than looping. Missing/revoked credentials wait. Stop aborts and preserves stopped intent.
        Maintenance/host shutdown interrupt updates by restoring the previous container stopped; normal ordered
        resume uses current credentials and readiness. Protected operation revisions/pointers survive restart.
        One previous container is retained after commit. Owner may roll it back or explicitly discard that
        stopped container before another update; data volumes are never deleted. Image cache GC remains separate.
        Keep update journals and broker keys even after acceptance; they identify the active container and hide
        retained/staged instances. Never start a retained container manually. A missing retained container or
        corrupt journal requires repair; package release distribution, signatures, configuration migrations,
        data rollback and automatic recreation are not supplied by this image-only capability. Agent access
        remains read-only discovery/planning; app credentials and frames receive no update authority.

        PiDockOS application platform 1.0 engineering preview

        Start with live target discovery. Available means the listed operations exist;
        Planned means no application operation is implemented. Simulated is never physical I/O.
        Read-only discovery, schemas and explicitly authorized cloud publishing are exposed to agents.
        Registry-reference release packages and shared log/measurement/alarm APIs are implemented.
        Package publication does not grant installation or hardware access.
        JSON schemas describe wire shape, not installation policy or hardware compatibility.

        Target Linux ARM64 images pinned by sha256 digest. Current managed Docker deployments
        run as UID/GID 1654, with a read-only root, dropped capabilities and resource limits.
        Do not request the Docker socket, privileged mode or unrestricted host mounts.
        Owner-managed app credentials, volumes and startup delivery are available behind target opt-in.
        Physical device leases and image archive upload remain planned. Development-only simulated app
        leases are discovered separately and must never be presented as physical GPIO.
        Do not build product apps until the platform conformance gate passes.

        UI integration and shared styling are platform-wide requirements for every hosted app.
        Plan a host-openable interface component launched from the PiDockOS menu, tab or another
        approved launch point. PiDockOS owns the outer shell, navigation, session and app lifecycle;
        the app supplies its workspace without a second dashboard shell or separate login flow.
        Reuse the PiDockOS shared UI styles, typography, colors, spacing, controls, focus and status
        conventions. Scoped, versioned assets are available at /api/platform/v1/ui/styles/1.0/tokens.css
        and components.css. Use .pidock-app and pd-* primitives; do not load the legacy shell CSS into apps.
        GET /api/platform/v1/ui and pidock://platform/ui describe asset hashes and hosting limitations.
        application-ui-contribution is a separate 1.0 schema, not an extension to application-manifest.
        The owner provisions PiDock:ApplicationUi:Registrations in local configuration. Each registration
        binds contribution identity, title, entry path and compatible UI/style versions to a literal
        loopback HTTP service origin. There is no app-controlled registration API or arbitrary target URL.
        Open /apps/{appId}/{id} through PiDockOS. Assets use relative paths below the entry point;
        the host content route is /app-ui/{appId}/{id}/{path}. Only GET/HEAD without query strings are
        supported. The sandbox does not allow same-origin access, forms, popups or top navigation.
        Local DOM interaction and the optional 1.0 authenticated bridge are available. Read
        GET /api/platform/v1/ui/bridge or pidock://platform/ui/bridge for SDK assets and limits.
        App frames use PiDockUi.command(name, arguments) and PiDockUi.subscribe(onEvent, onStatus).
        An exact iframe/MessageChannel binding carries requests over the authenticated host Blazor
        circuit. The server rechecks the current Owner session, contribution and explicit named grants.
        Commands share the browser's five-minute authorization window with PiDockOS protected actions.
        The host prompts for credentials after expiry; apps must use the shared SDK and never collect
        Owner credentials. Commands do not extend authorization. Live subscriptions retain session checks.
        App-specific consequence reviews remain separate from credential authorization.
        Host configuration supplies Bridge.CredentialId, CommandsPath, Commands and StreamPath;
        the private Credentials:{id}:Token and ExpiresAt stay server-side. Only this app's literal
        configured loopback service receives its separate Bearer credential. No browser app token exists.
        Commands are bounded, correlated and never automatically retried on an uncertain result.
        App streams use application/x-ndjson with snapshot/update/heartbeat, epoch and sequence;
        five-second idle deadlines, bounded acknowledged delivery and revocation checks prevent stale
        access or unbounded queues. Reconnect uses a cursor; gaps require a fresh snapshot.
        No arbitrary HTTP proxy, generic agent mutation tool or physical hardware permission is granted.
        General user-context disclosure and package-driven registration are planned, not available. Frame load does not attest service health. No owner/service token,
        cookie or response header is forwarded to the app service. Host configuration must explicitly list
        non-secret PublicAssets (CSS, JS, images and fonts) so opaque frames can load resources without
        owner cookies. Those exact assets are publicly readable; HTML, runtime data and all other paths
        remain owner-protected. Use the host to close/reload the UI.
        A separate Docker container does not imply a separately branded application UI. These requirements
        apply to all apps, not one product. See docs/Application-UI-Integration.md and the AppUi.Sample fixture.
        UI hosting conformance and the full platform gate remain prerequisites for product applications.

        Use the registered Cloud.BaseUrl only as target context. Registration is not publishing
        authorization or connectivity evidence. Never derive publishing endpoints from that URL.
        Appliance discovery cannot infer publishing endpoints or authority. Use pidock_cloud_discover
        with separate publisher credentials and a pinned cloud URL matching registration.
        No deployment, hardware-write or network-change MCP tools are exposed.

        Shared structured logs, typed measurements and durable app alarms are available. Priorities: Critical=1,
        Warning=2, Alert=3, Notice=4. DiagnosticLevel is separate from operational Priority.
        Use UTC observation times; the collector stamps identity, receipt time,
        clock trust, boot identity and ingestion order. Never include secrets in events.
        """;
}
