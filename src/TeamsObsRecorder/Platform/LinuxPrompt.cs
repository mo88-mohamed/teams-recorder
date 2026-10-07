namespace TeamsObsRecorder.Platform;

/// <summary>
/// Record/Dismiss prompt on Linux: a desktop notification with action buttons via notify-send
/// (libnotify 0.7.9+), or a zenity dialog when notify-send can't show buttons.
/// </summary>
public sealed class LinuxPrompt : IPrompt
{
    private const string AppName = "Teams OBS Recorder";
    private bool? _notifySendHasActions;

    public PromptChoice Ask(string title, string message, TimeSpan timeout)
    {
        var wait = timeout + TimeSpan.FromSeconds(5);
        if (NotifySendHasActions())
        {
            var (code, output) = Shell.Run("notify-send", new[]
            {
                "--app-name", AppName, "--urgency", "critical",
                "--expire-time", ((int)timeout.TotalMilliseconds).ToString(), "--wait",
                "--action", "record=Record", "--action", "dismiss=Dismiss",
                title, Markup(message), // the body may be read as markup
            }, wait);
            return code == 0 && output.Trim() == "record" ? PromptChoice.Record : PromptChoice.Dismiss;
        }

        if (Shell.Which("zenity") is not null)
        {
            var (code, _) = Shell.Run("zenity", new[]
            {
                "--question", $"--title={AppName}", $"--text={Markup(title)}\n\n{Markup(message)}",
                "--ok-label=Record", "--cancel-label=Dismiss", $"--timeout={(int)timeout.TotalSeconds}",
            }, wait);
            return code == 0 ? PromptChoice.Record : PromptChoice.Dismiss;
        }

        throw new InvalidOperationException("Need notify-send (libnotify >= 0.7.9) or zenity to show the prompt");
    }

    /// <summary>zenity reads --text as Pango markup; window titles must show as plain text.</summary>
    internal static string Markup(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private bool NotifySendHasActions()
    {
        if (_notifySendHasActions is null)
        {
            _notifySendHasActions = Shell.Which("notify-send") is not null &&
                Shell.Run("notify-send", new[] { "--help" }, TimeSpan.FromSeconds(5)).Output.Contains("--action");
        }
        return _notifySendHasActions.Value;
    }
}
