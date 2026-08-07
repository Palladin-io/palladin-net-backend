# Module: Notification

## Projects
- `Palladin.Module.Notification/` — inbox, email, SignalR, push delivery and persistence.
- `Palladin.Module.Notification.Contracts/` — fan-out and email commands, delivery events and notification scopes.

## Business responsibility
Owns user-facing notifications: the in-app inbox, real-time delivery, system push, and per-user delivery preferences. It is the central fan-out point — other modules send it one command and it resolves recipients and channels.

## Dependencies / references
- **ProjectReferences:** `Palladin.Module.Identity.Contracts`, `Palladin.Module.Notification.Contracts` (its own contract project).
- **Publishes** (`Palladin.Module.Notification.Contracts.Events`): `PushTokenRegisteredEvent`, `PushTokenRemovedEvent`, `PushNotificationSentEvent`, `WebNotificationSentEvent`.
- **Consumes commands** (`Palladin.Module.Notification.Contracts.Commands`): `BroadcastNotificationCommand` (the fan-out entry point used by Vault and Agents), `UpdateUserScope`, `SendEmailCommand`.
- **Consumes** (from Identity): `UserUpsertedEvent`.

## Technologies used
EF Core + Postgres, MassTransit, **SignalR** (`NotificationHub`, JWT-authenticated), **Firebase** push (web + mobile via `FirebasePushNotificationService`).

## Key aggregates (name + role)
- **InboxItem** — a stored notification. Canonical Vault notifications contain opaque IDs and structural facts only; clients resolve presentation locally after unlock.
- **PushToken** — a device/browser push registration (value object: no lifecycle events — the only place `ExecuteDeleteAsync` is permitted here).
- **NotificationPreference** — per-user, per-type Inbox/Realtime/Push toggles.
- **Scope** — fan-out membership used to target recipients.
- **User** — read-model replica (superset: also email + permissions).
- **EmailDelivery** — durable idempotency marker keyed by the sender-provided business occurrence; prevents successful command redelivery from sending the same e-mail twice.

## Contracts (namespaces / types)
- The cross-module contract project exposes commands through `Palladin.Module.Notification.Contracts.Commands` and shared values through `Palladin.Module.Notification.Contracts.ValueObjects`. Senders depend on this project, never on Notification internals.
- Persistence contexts: `NotificationDbWriteContext`, `NotificationDbReadContext`, `NotificationDomainWriteContext`, `NotificationDomainReadContext`.

## Critical points / invariants
- `BroadcastNotificationCommand` is the single fan-out path — modules must not deliver notifications themselves.
- SignalR delivers no OS-level notification; clients must show a local notification for foreground SignalR events.
- Vault-derived commands are validated against a fail-closed metadata allowlist. Vault/Entry names, request reasons, notes, domains, account identity and server-composed deep links are forbidden in Inbox and SignalR payloads.
- FCM Web and FCM/APNs receive only type, generic category, opaque subject ID and NodaTime `occurredAt`. Visible push copy never interpolates command metadata, so lock-screen/browser notifications remain generic while locked.
- `GrantPending` is mandatory for Inbox and SignalR and push-enabled by default, so an approval request reaches an open web client through SignalR and a closed/background client through Firebase. Agents never subscribe to either channel and continue polling REST.
- Notification list responses expose `SubjectId`; together with structural metadata it lets an unlocked client resolve and route locally. Server-generated Vault deep links are intentionally absent.
- The canonical cutover irreversibly truncates legacy Inbox rows that may contain presentation data while preserving scopes, preferences and push tokens.
- Delivery respects `NotificationPreference` per channel — do not bypass the preference check.
- Commands for one-time transactional e-mails must carry a deterministic `IdempotencyKey`. Notification claims that key transactionally before calling the provider; a failed provider call rolls the marker back so MassTransit can retry.
