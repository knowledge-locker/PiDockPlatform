# Managed app capability checks

Managed apps may request `GET /api/apps/v1/runtime-support` using an explicitly
provisioned platform credential with `system.read`. HTTPS and the shared app
service authentication/rate limits apply. `ApplicationRuntimeSupportClient.Read`
uses the schema-1 `application-runtime-support` contract from the shared SDK.

The endpoint aggregates non-secret resource, health, lifecycle, orchestration,
simulated I/O, engineering and runtime-access support. Broker reads share a
two-second deadline; missing/unreachable support returns 503. Disabled capabilities
remain explicitly disabled. Responses are `no-store`. The metadata is not an
atomic authorization grant: each resource operation must still check current
credential, grant, startup binding, ownership and lifecycle admission.

This supplies app-facing capability checks without putting a discovery-agent
credential in managed credential files. Managed platform credentials still cannot
call `/api/platform/v1`; that read-only agent/Owner policy remains separate.
No startup delivery identity, private observations, credentials or lifecycle
mutation methods are exposed by this endpoint. Apps cannot Stop/Restart themselves
or override operator intent through it.
