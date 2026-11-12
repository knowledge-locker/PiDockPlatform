# PiDockCloud workspaces and teams

A user has one account and an independent role in each workspace they join. A workspace is a tenant: it owns PiDockOS registrations, device history, application releases and operations. Leaving or removing a member preserves these resources. Team means the members of a workspace.

## Establish the Owner first

Stop the Cloud process and run its existing `--commission` command from a trusted interactive server console, with the same state directory and protection keys as the service. This establishes the one platform Owner and requires a password and two consecutive authenticator codes. Restart the Cloud process. Subsequent Cloud commissioning attempts cannot create additional Owners or tenant users; use online signup and team management instead. PiDockOS commissioning remains its separate single local appliance Owner flow.

The sign-in page explains the bootstrap requirement. Both displaying and submitting account signup return 403 until the platform Owner exists. Public requests cannot establish the Owner. Do not run more than one Cloud process against the file-backed state directory.

## Control public workspace signup

The platform Owner can open **Workspaces & team → Workspace signup settings → Allow multi-tenant signup**. This setting defaults to **Enabled**, including on upgrades, and is saved in protected account state. Only the Owner can change it; changes are audited.

When disabled, public workspace signup and creation of additional workspaces by ordinary users are blocked on the server as well as hidden in the interface. A signup form opened before the setting changed cannot bypass the restriction. The Owner can still create a workspace, so an invitation-only installation can be established even before its first team exists. Existing workspaces, memberships, switching and device registrations remain available.

Team invitations continue to work in both modes. A new invited user creates an account with password and authenticator verification, joins only the invited workspace with the assigned role, and does not create another workspace. Existing users sign in and accept the invitation normally. Invalid, expired, revoked or already-used invitations cannot authorize account creation.

## Create an account and workspace

When multi-tenant signup is enabled, choose **Create your account and workspace** on the sign-in page. Choose an ASCII username using letters, digits or hyphens, a password of 14–256 characters, and a workspace name. Add the displayed private time-based setup key to an external authenticator (six digits, 30 seconds). Enter its current code to finish setup within 15 minutes. Setup creates the account, workspace and Administrator membership together, and signs the user in. The accepted setup code cannot be reused to sign in.

When multi-tenant signup is enabled, an existing user can choose **Workspaces & team → Create another workspace**. Their account, password and authenticator remain the same. They become Administrator of the new workspace.

## Invite and manage teammates

In the selected workspace, an Administrator chooses the invited username and role under **Workspaces & team**. Share the generated invitation link privately; email delivery is not integrated. Each invitation is bound to the specified username, expires after seven days, can be cancelled and can be accepted only once. Reissuing an invitation for the same username/workspace invalidates the earlier link. Only a hash of the invitation token is stored.

Recipients sign in through the invitation link. New recipients use **Create your invited account**, choose a password and complete authenticator setup using the invited username. Their account and invited membership are created together and the invitation is consumed; no personal workspace is created. Existing recipients sign in and accept the team invitation. Acceptance selects the invited workspace. Other memberships and roles are preserved.

Administrators can change member roles and remove users from the current workspace. A workspace's last Administrator cannot be demoted or removed. Team management never grants the platform Owner role. Changes invalidate the affected user's existing sessions, requiring sign-in again. Removing or demoting an Administrator also cancels their outstanding invitations in that workspace. Selecting another workspace also invalidates earlier sessions/tabs to prevent old interactive pages from retaining access. The selected browser retains its existing remembered-sign-in setting and original expiry; switching does not extend its session lifetime.

| Role | Current permissions |
| --- | --- |
| Platform Owner | Global workspace listing, recent membership/support activity, and full Administrator access after explicitly selecting a workspace |
| Administrator | Team roles/invitations, fleet/history, device enrollment and cloud operations/releases subject to fresh MFA and appliance approval where applicable |
| Operator | Read fleet and device history; no team, enrollment, release or device-operation changes |
| Viewer | Read fleet and device history; no configuration changes |
| Auditor | Read fleet, device history and selected workspace membership/support activity; no configuration changes |

Operator incident acknowledgement and alarm configuration, resource-specific grants and separate export grants remain future work. The existing history export behavior remains workspace-scoped for all read roles. Cloud roles never grant local PiDockOS ownership, bypass appliance approval or provide device I/O control.

## Switch workspaces and Owner support

Use the workspace selector in the header or **Workspaces & team**. A full-page navigation installs the newly validated workspace session. Every workspace selection applies the membership role there. Requests and live data services check the account session and membership, so removed users and old workspace sessions lose access without waiting for cookie expiry.

The platform Owner signs in to **Platform administration**, with no tenant device data selected. Choose any workspace for support; the portal displays an **Owner support access** banner. Return to platform administration through team management or the selector. Selections, workspace creation, invitations, acceptances, membership changes and removals are recorded with actor, workspace and UTC time. The file-backed preview retains the latest 10,000 events and displays the latest 100 in scope; this is not the final append-only production audit service.

## Existing installations

The protected account store can read its original account-array format and derives workspace memberships from existing tenant/role assignments. Tenant IDs and device registration records remain unchanged. The first successful account-state write persists the new Cloud account schema; retain a coordinated backup of state and protection keys before upgrading. Older Cloud builds cannot read the new schema, so rollback requires the matching pre-upgrade state backup. PiDockOS retains its original account-array schema.

If the old Cloud account file contains multiple Owners, startup fails with a specific diagnostic rather than choosing one or granting multiple platform Owners. Resolve this locally while the service is stopped: identify the authorized platform Owner, convert other legacy Owner entries to appropriate tenant Administrator roles using a controlled migration of the protected store, and preserve its protection keys. Never delete account state or device registrations to repair this condition.

Account recovery, verified email and email delivery are not implemented. State is still a protected single-writer local file, not a distributed membership database. Backups must include both account state and data-protection keys.
