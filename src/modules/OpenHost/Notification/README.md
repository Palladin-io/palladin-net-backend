# Module: Notification

## Projects
- `Palladin.Module.Notification/` — inbox, email, SignalR, push delivery and persistence.
- `Palladin.Module.Notification.Contracts/` — fan-out and email commands, delivery events and notification scopes.

## Business responsibility
Owns user-facing notifications: the in-app inbox, real-time delivery, system push, and per-user delivery preferences. It is the central fan-out point — other modules send it one command and it resolves recipients and channels.

## Dependencies / references
- **ProjectReferences:** `Palladin.Module.Identity.Contracts`, `Palladin.Module.Notification.Contracts` (its own contract project).
- **Publishes** (`Palladin.Module.Notification.Contracts.Events`): `PushTokenRegisteredEvent`, `PushTokenRemovedEvent`, `PushNotificationSentEvent`, `WebNotificationSentEvent`.
- **Consumes commands** (`Palladin.Module.Notification.Contracts.Commands`): `BroadcastNotificationCommand` (the user-notification fan-out entry point used by Vault and Agents), `BroadcastVaultSyncInvalidationCommand` (value-free realtime-only Vault repair hint), `UpdateUserScope`, `SendEmailCommand`, `SendEntryShareVerificationEmailCommand` (expiring guest verification, no account required), `RecordEntryShareReceiptCommand` (explicit sender-only Inbox receipt).
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
- `BroadcastNotificationCommand` is the general fan-out path — modules must not deliver notifications themselves. The explicit sender-only sharing receipt uses `RecordEntryShareReceiptCommand`, not organization fan-out.
- `BroadcastVaultSyncInvalidationCommand` is a separate realtime-only path. For ordinary invalidations, Vault resolves the current authorized Members from committed persistence before publishing the command; removal and deletion tombstones carry explicit former-Member snapshots. Notification validates canonical decimal versions, targets those exact user groups, sends `ReceiveVaultSyncInvalidation`, and writes no Inbox item or push notification.
- SignalR delivers no OS-level notification; clients must show a local notification for foreground SignalR events.
- Vault-derived commands are validated against a fail-closed metadata allowlist. Vault/Entry names, request reasons, notes, domains, account identity and server-composed deep links are forbidden in Inbox and SignalR payloads.
- FCM Web and FCM/APNs receive only type, generic category, opaque subject ID and NodaTime `occurredAt`. Visible push copy never interpolates command metadata, so lock-screen/browser notifications remain generic while locked.
- `GrantPending` is mandatory for Inbox and SignalR and push-enabled by default, so an approval request reaches an open web client through SignalR and a closed/background client through Firebase. Agents never subscribe to either channel and continue polling REST.
- Notification list responses expose `SubjectId`; together with structural metadata it lets an unlocked client resolve and route locally. Server-generated Vault deep links are intentionally absent.
- The canonical cutover irreversibly truncates legacy Inbox rows that may contain presentation data while preserving scopes, preferences and push tokens.
- General fan-out respects `NotificationPreference` per channel. A sharing receipt is an explicitly requested per-share Inbox record, not a globally configurable notification; its separate command honors that per-share choice without changing any stored preference.
- Commands for one-time e-mails must carry a deterministic `IdempotencyKey`. Notification persists a two-minute optimistic `Dispatching` lease before SES, bounds the complete suppression/provider operation to 30 seconds, calls the provider without a database transaction, then marks the delivery `Sent`. The lease therefore cannot expire during a live provider call. A provider failure removes the owned claim so MassTransit can retry; an expired claim can be reclaimed after a process crash. SES has no application idempotency token, so a crash or timeout after provider acceptance but before `Sent` is durably recorded has an explicit at-least-once duplicate window.
- `SendEntryShareVerificationEmailCommand` is a separate expiring transactional slice: it validates the sharing occurrence and six-digit code, ignores commands already expired at consumption, renders `entry-share-verification` in EN/PL and deduplicates by share/session/generation through the same email-delivery lease. Vault retains an encrypted pending code until broker acknowledgement and sets broker TTL. Only the restricted email command carries the server-readable code/address; domain activity, Inbox, push and analytics never receive them. No full sharing URL, decryption key or Entry presentation is accepted. The email contains code generation and UTC expiry, has no action link and does not require a Palladin account. Real-provider acceptance remains a release gate; the existing SES duplicate window still applies.
- Identity's `login-lockout-alert` transactional template is sent only for a verified known account. It presents the four-attempt/five-minute threshold, all unique source IPs collected during the window, UTC detection/expiry times and the fact that password/TOTP login for the account is temporarily blocked regardless of source IP. It deliberately has no unlock button or token; the 15-minute block expires automatically. Identity supplies an account/time-bucket idempotency key so repeated delivery cannot amplify e-mail within the same aligned lockout interval.
- Identity's `waitlist-developer-benefit-activated` transactional template confirms the exact UTC start/end window in EN or PL, uses the public plan name Developer, states that there is no card, subscription, renewal or charge and that the account returns to the plan available independently of the promotion. Identity supplies a per-user idempotency key.
- Transactional HTML templates that contain an action use the same compact centered call-to-action treatment (`14px`, `10px 20px`) in both EN and PL variants.

## Requested first sharing receipt (CVT-644)

Vault publishes `RecordEntryShareReceiptCommand` only for the first confirmed
receipt whose share opted in. Notification persists one Inbox item, addressed
only to the sender, with the share ID as stable item/subject identity. Existing
PK and recipient/type/subject uniqueness fence simultaneous retries; a fresh
consumer retry recognizes the committed row. The payload contains only share,
Vault and Entry IDs, never the recipient address, link, key or Entry presentation.
The wire type is `entry_share_received`; the title key is
`notification.entry_share_received.title`. It is an informational Update, not
an approval/to-do and not a claim that a person read the data.

This slice creates Inbox only, without email, push or SignalR fan-out. Generic
`BroadcastNotificationCommand` rejects this type. Global preference enumeration
omits it and global preference mutation rejects it: the sender's share checkbox
is its sole selection surface, so no hidden preference can undo that choice.
Audit is independent and still records opted-out confirmations. Client
localization/rendering/navigation remain part of the unfinished client rollout.
