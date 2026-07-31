namespace Palladin.Module.Notification.Infrastructure.Email.Templating;

// A required template resource is missing from the assembly — a build/packaging defect, not a
// runtime input error.
internal sealed class EmailTemplateNotFoundException(string resourceName)
    : Exception($"Email template resource '{resourceName}' was not found.");
