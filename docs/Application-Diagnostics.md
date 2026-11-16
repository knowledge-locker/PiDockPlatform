# Shared app diagnostics and local notifications

PiDockOS already provides scoped structured logs, typed measurements and durable alarm transitions. This increment adds an opt-in **local notification inbox** for newly raised or updated alarms. It performs no email, push, cloud or other external delivery. Docker stdout/stderr collection, redaction and centrally coordinated log verbosity remain separate development work.

Discover target support through `/api/platform/v1/app-diagnostics` or `pidock://platform/app-diagnostics`. A local notification policy is host configuration, not a capability an app grants itself:

```json
{
  "PiDock": {
    "Apps": {
      "Notifications": {
        "Policies": [ { "appId": "pump", "maximumPriority": "Warning" } ]
      }
    }
  }
}
```

At most sixteen unique app policies are accepted. Priorities use the common ordering Critical=1, Warning=2, Alert=3, Notice=4; Warning includes Critical and Warning. No configured policies means no new notifications, preserving previous alarm behavior. Disabling a policy does not erase existing unread notifications. Policy changes apply to future alarm transitions; they do not backfill historical alarms. Alarms acknowledged or cleared do not create additional notifications.

Each entry contains only app/alarm identity, condition, priority, transition, creation time and an optional durable read receipt. It omits the alarm message and arbitrary app fields; read the scoped current alarm separately. Notification acknowledgement means the notification was read. It does not acknowledge or resolve the underlying alarm.

Notification creation and the corresponding alarm change are saved atomically in the existing private alarm state. Repeated identical alarm raises do not duplicate notifications. The inbox retains at most 32 unread entries per app, 128 unread globally and 128 previously read entries. Read entries are trimmed on subsequent alarm transitions. If unread capacity is exhausted, the corresponding alarm change fails with the shared capacity response instead of silently dropping an unread notification; the persistent rejected-notification counter increases. Operators must acknowledge older entries and retry. The caller must not assume an HTTP failure undoes a separately accepted structured log, including a priority-triggered log that also attempted to raise an alarm.

The app-facing HTTPS routes use current platform service credentials:

| Route | Scope | Behavior |
| --- | --- | --- |
| `GET /api/apps/v1/notifications` | `notifications.read` | Bounded own-app inbox; explicit read-source grants may include another app |
| `POST /api/apps/v1/notifications/{id}/acknowledge` | `notifications.acknowledge` | Durable idempotent read receipt, restricted to the same permitted sources |

`ApplicationNotificationsClient` accepts the app's authenticated HTTPS client. A read receipt records the authenticated credential ID, never an actor supplied by an app request. Unknown or unauthorized notification IDs are not disclosed. A read-only discovery credential does not grant access to the inbox or acknowledgement. Use the shared authenticated UI bridge for app controls; keep platform credentials outside the frame.

Private alarm state and read receipts survive process restart; notification suppression/retention does not depend on a browser session. The shared history outbox still supplies alarm event replay under its existing bounds. This local inbox is not an external notification transport, disaster-recovery copy, exact-once delivery guarantee or proof that a human saw the message. App authors should treat capacity failures as operational faults and keep their own domain safe-state behavior independent of notification availability.
