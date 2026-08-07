using FirebaseAdmin.Messaging;

namespace Palladin.Module.Notification.Infrastructure.Push;

// Wraps FirebaseMessaging so it can be absent (no credentials) without breaking DI. Returns null
// when Firebase is not configured, which makes the push service a logged no-op.
internal interface IFirebaseMessagingProvider
{
    FirebaseMessaging? Messaging { get; }
}
