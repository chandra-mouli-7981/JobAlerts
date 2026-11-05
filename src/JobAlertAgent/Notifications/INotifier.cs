namespace JobAlertAgent.Notifications;

public interface INotifier
{
    /// <summary>Sends one message. Messages use simple HTML (b, a) with one entry per "- " line.</summary>
    Task SendAsync(string htmlMessage, CancellationToken ct);
}

/// <summary>Prints messages instead of sending them (used for --dry-run).</summary>
public sealed class ConsoleNotifier : INotifier
{
    private readonly TextWriter _out;

    public ConsoleNotifier(TextWriter? output = null) => _out = output ?? Console.Out;

    public Task SendAsync(string htmlMessage, CancellationToken ct)
    {
        _out.WriteLine("---- message (dry run) ----");
        _out.WriteLine(htmlMessage);
        _out.WriteLine("---------------------------");
        return Task.CompletedTask;
    }
}
