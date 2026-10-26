using System;

namespace kaliteConfig.Services.Notifications;

public enum AppNoticeSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record AppNotice(string Title, string Message, AppNoticeSeverity Severity);

public interface IAppNotificationService
{
    event Action<AppNotice>? Posted;

    void Post(string title, string message, AppNoticeSeverity severity = AppNoticeSeverity.Info);
}

public sealed class AppNotificationService : IAppNotificationService
{
    public event Action<AppNotice>? Posted;

    public void Post(string title, string message, AppNoticeSeverity severity = AppNoticeSeverity.Info)
    {
        var notice = new AppNotice(title ?? string.Empty, message ?? string.Empty, severity);
        Action<AppNotice>? handler = Posted;
        if (handler is null) return;
        foreach (Delegate target in handler.GetInvocationList())
        {
            try { ((Action<AppNotice>)target)(notice); }
            catch { }
        }
    }
}
