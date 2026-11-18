namespace PlexManager.Core.Operations;

public enum OperationLevel
{
    Detail,
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One line of progress from an operation. Replaces the Console.WriteLine calls the console tools used.</summary>
public sealed record OperationEvent(OperationLevel Level, string Message, int? Percent = null, DateTimeOffset? At = null)
{
    public DateTimeOffset When => At ?? DateTimeOffset.Now;
}

/// <summary>Where an operation sends its progress: the console for the CLI, a job's log for the UI.</summary>
public interface IOperationLog
{
    void Report(OperationEvent e);
}

public static class OperationLogExtensions
{
    public static void Detail(this IOperationLog log, string message) => log.Report(new OperationEvent(OperationLevel.Detail, message));

    public static void Info(this IOperationLog log, string message) => log.Report(new OperationEvent(OperationLevel.Info, message));

    public static void Success(this IOperationLog log, string message) => log.Report(new OperationEvent(OperationLevel.Success, message));

    public static void Warn(this IOperationLog log, string message) => log.Report(new OperationEvent(OperationLevel.Warning, message));

    public static void Error(this IOperationLog log, string message) => log.Report(new OperationEvent(OperationLevel.Error, message));

    public static void Progress(this IOperationLog log, string message, int percent) =>
        log.Report(new OperationEvent(OperationLevel.Detail, message, percent));
}

/// <summary>Prints progress to the console, the way the original tools did.</summary>
public sealed class ConsoleOperationLog : IOperationLog
{
    public void Report(OperationEvent e)
    {
        var writer = e.Level == OperationLevel.Error ? Console.Error : Console.Out;
        var prefix = e.Level switch
        {
            OperationLevel.Detail => "  ",
            OperationLevel.Warning => "! ",
            OperationLevel.Error => "",
            _ => "",
        };
        writer.WriteLine(prefix + e.Message);
    }
}

/// <summary>Sends every line to several logs, so a UI job can also write a log file.</summary>
public sealed class CompositeOperationLog(params IOperationLog[] logs) : IOperationLog
{
    public void Report(OperationEvent e)
    {
        foreach (var log in logs)
        {
            log.Report(e);
        }
    }
}

/// <summary>Throws nothing and keeps nothing: for calls that only need a log to satisfy a signature.</summary>
public sealed class NullOperationLog : IOperationLog
{
    public static readonly NullOperationLog Instance = new();

    public void Report(OperationEvent e)
    {
    }
}
