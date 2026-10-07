#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace TeamsObsRecorder.Platform;

/// <summary>Record/Dismiss prompt on Windows as a toast notification with two buttons.</summary>
public sealed class WindowsToastPrompt : IPrompt
{
    public PromptChoice Ask(string title, string message, TimeSpan timeout)
    {
        var tag = Guid.NewGuid().ToString("N")[..16];
        var result = new TaskCompletionSource<PromptChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        OnActivated handler = e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue("prompt", out var p) || p != tag) return;
            result.TrySetResult(args.TryGetValue("action", out var a) && a == "record"
                ? PromptChoice.Record
                : PromptChoice.Dismiss); // clicking the toast body counts as Dismiss
        };
        ToastNotificationManagerCompat.OnActivated += handler;
        try
        {
            new ToastContentBuilder()
                .AddArgument("prompt", tag)
                .AddText(title)
                .AddText(message)
                .AddButton(new ToastButton().SetContent("Record")
                    .AddArgument("prompt", tag).AddArgument("action", "record"))
                .AddButton(new ToastButton().SetContent("Dismiss")
                    .AddArgument("prompt", tag).AddArgument("action", "dismiss"))
                // Reminder keeps the toast on screen until the user answers.
                .SetToastScenario(ToastScenario.Reminder)
                .Show(toast =>
                {
                    toast.Tag = tag;
                    toast.ExpirationTime = DateTimeOffset.Now + timeout;
                    toast.Dismissed += (_, e) =>
                    {
                        if (e.Reason == ToastDismissalReason.UserCanceled)
                            result.TrySetResult(PromptChoice.Dismiss);
                    };
                    toast.Failed += (_, _) => result.TrySetResult(PromptChoice.Dismiss);
                });

            if (result.Task.Wait(timeout)) return result.Task.Result;
            try { ToastNotificationManagerCompat.History.Remove(tag); } catch { /* already gone */ }
            return PromptChoice.Dismiss;
        }
        finally
        {
            ToastNotificationManagerCompat.OnActivated -= handler;
        }
    }
}
#endif
