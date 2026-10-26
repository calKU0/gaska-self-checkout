using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Gaska.SelfCheckout.Web.Services;

/// <summary>
/// Fiserv certification: the ECR↔POS messages of one test case go to a file of their own
/// (<c>logs/polcard/cert/test_05_20261022_143012.log</c>) – Fiserv wants a separate log per test, with its number in
/// the file name. The file name comes from the <see cref="Property"/> log context property set by
/// <see cref="TerminalTestRunner"/>; the daily protocol log keeps every message as before.
/// </summary>
public sealed class CertificationLogSink(string directory) : ILogEventSink
{
    public const string Property = "CertTest";

    private readonly MessageTemplateTextFormatter _formatter = new("{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Message:lj}{NewLine}{Exception}");
    private readonly Lock _sync = new();

    public string Directory => directory;

    public void Emit(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue(Property, out var value) || value is not ScalarValue { Value: string name })
            return;

        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(directory);
            using var writer = File.AppendText(Path.Combine(directory, name + ".log"));
            _formatter.Format(logEvent, writer);
        }
    }
}
