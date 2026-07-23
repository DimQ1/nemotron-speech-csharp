using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Configures OpenTelemetry for the VoiceType app.
/// In development, exports logs to the Aspire Dashboard via OTLP.
/// </summary>
public static class TelemetryConfiguration
{
    public const string ServiceName = "VoiceType.WinUI";
    public const string DefaultOtlpEndpoint = "http://localhost:4317";

    /// <summary>Configure OpenTelemetry logging with OTLP export to Aspire Dashboard.</summary>
    public static void ConfigureLogging(ILoggingBuilder builder)
    {
        builder.ClearProviders();
        builder.AddDebug();

        if (!IsOtlpExportEnabled())
            return;

        var endpoint = GetOtlpEndpoint();
        builder.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.SetResourceBuilder(CreateResource());
            options.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(endpoint);
                otlpOptions.Protocol = OtlpExportProtocol.Grpc;
            });
        });
    }

    /// <summary>
    /// Create and start the OpenTelemetry SDK for metrics/traces.
    /// Call once during app startup. Dispose on shutdown.
    /// </summary>
    public static IDisposable? StartOpenTelemetrySdk()
    {
        if (!IsOtlpExportEnabled())
            return null;

        var endpoint = GetOtlpEndpoint();
        return OpenTelemetrySdk.Create(builder => builder
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: GetAppVersion()))
            .UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(endpoint))
        );
    }

    public static ResourceBuilder CreateResource() =>
        ResourceBuilder.CreateDefault()
            .AddService(ServiceName, serviceVersion: GetAppVersion())
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment",
                    IsDevelopment() ? "development" : "production"),
                new KeyValuePair<string, object>("host.os", Environment.OSVersion.ToString()),
            });

    /// <summary>Get OTLP endpoint from environment or default.</summary>
    public static string GetOtlpEndpoint() =>
        Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
        ?? Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_ENDPOINT")
        ?? DefaultOtlpEndpoint;

    public static bool IsOtlpExportEnabled() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_OTLP_ENDPOINT"))
        || IsDevelopment();

    private static bool IsDevelopment() =>
        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development",
            StringComparison.OrdinalIgnoreCase)
        || Debugger.IsAttached;

    private static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var ver = asm.GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.0.0";
    }
}