using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace StealthDesk.Hosting;

public static class FileLogging
{
  /// <summary>
  /// Writes logs to the console and to a file that starts over every day.
  /// The day is added to the file name (<c>agent.log</c> → <c>agent20260929.log</c>);
  /// files older than <paramref name="keepDays"/> are deleted.
  /// </summary>
  /// <remarks>An unattended process has no console to read, so the file is what remains when something fails.</remarks>
  public static IHostApplicationBuilder AddDailyFileLog(this IHostApplicationBuilder builder, string logFile, int keepDays)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
    ArgumentOutOfRangeException.ThrowIfLessThan(keepDays, 1);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);

    var logger = new LoggerConfiguration()
      .MinimumLevel.Is(builder.Environment.IsDevelopment() ? LogEventLevel.Debug : LogEventLevel.Information)
      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
      .MinimumLevel.Override("System", LogEventLevel.Warning)
      .Enrich.FromLogContext()
      .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {SourceContext}: {Message:lj}{NewLine}{Exception}")
      .WriteTo.File(
        logFile,
        rollingInterval: RollingInterval.Day,
        retainedFileTimeLimit: TimeSpan.FromDays(keepDays),
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {SourceContext}: {Message:lj}{NewLine}{Exception}")
      .CreateLogger();

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(logger, dispose: true);
    return builder;
  }
}
