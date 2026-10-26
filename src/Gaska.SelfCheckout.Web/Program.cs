using Gaska.SelfCheckout.Application;
using Gaska.SelfCheckout.Devices.Polcard;
using Gaska.SelfCheckout.Devices.Posnet;
using Gaska.SelfCheckout.Infrastructure;
using Gaska.SelfCheckout.Infrastructure.Erp;
using Gaska.SelfCheckout.Infrastructure.Settings;
using Gaska.SelfCheckout.Web.Components;
using Gaska.SelfCheckout.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
// Static web assets also when running without publishing (dotnet run in Production).
builder.WebHost.UseStaticWebAssets();

// Environment credentials (connection string, XL operator) – kept out of the repository.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
// Workstation settings edited in the configuration screen – applied live (IOptionsMonitor).
builder.Configuration.AddJsonFile(StationSettingsStore.FilePath, optional: true, reloadOnChange: true);

var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
var certificationLog = new CertificationLogSink(Path.Combine(logDir, "polcard", "cert"));
builder.Services.AddSingleton(certificationLog);
builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    // application log
    .WriteTo.Logger(l => l
        .Filter.ByExcluding(e => e.Properties.TryGetValue("SourceContext", out var sc) && sc.ToString().Contains(Gaska.SelfCheckout.Devices.Polcard.DependencyInjection.ProtocolLogCategory))
        .WriteTo.File(Path.Combine(logDir, "kasa-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 90,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"))
    // full ECR↔POS message log (required by Fiserv for the integration certification)
    .WriteTo.Logger(l => l
        .Filter.ByIncludingOnly(e => e.Properties.TryGetValue("SourceContext", out var sc) && sc.ToString().Contains(Gaska.SelfCheckout.Devices.Polcard.DependencyInjection.ProtocolLogCategory))
        .MinimumLevel.Verbose()
        .WriteTo.File(Path.Combine(logDir, "polcard", "ecr-pos-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 365,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Message:lj}{NewLine}{Exception}"))
    // Fiserv certification: the messages of one test case in a file of their own (TerminalTestRunner)
    .WriteTo.Logger(l => l
        .Filter.ByIncludingOnly(e => e.Properties.ContainsKey(CertificationLogSink.Property)
            && e.Properties.TryGetValue("SourceContext", out var sc) && sc.ToString().Contains(Gaska.SelfCheckout.Devices.Polcard.DependencyInjection.ProtocolLogCategory))
        .MinimumLevel.Verbose()
        .WriteTo.Sink(certificationLog)));

builder.Host.UseWindowsService(o => o.ServiceName = "GaskaKasaSCO");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

builder.Services
    .AddKasaApplication(builder.Configuration)
    .AddKasaInfrastructure(builder.Configuration)
    .AddComarchXl(builder.Configuration)
    .AddPolcardTerminal(builder.Configuration)
    .AddPosnetPrinter(builder.Configuration);

builder.Services.AddScoped<KioskSession>();
builder.Services.AddSingleton<ProductImageService>();
builder.Services.AddScoped<ServiceOperations>();
builder.Services.AddSingleton<TerminalTestRunner>();
builder.Services.AddHostedService<DeviceWarmupService>();
builder.Services.AddSingleton<KioskHealthMonitor>();
builder.Services.AddSingleton<MaintenanceMode>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KioskHealthMonitor>());

var app = builder.Build();

// last words of the process (native crashes inside XL API end the process without a managed exception)
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    app.Logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception – the application is terminating");
AppDomain.CurrentDomain.ProcessExit += (_, _) => app.Logger.LogInformation("Process exit (code {Code})", Environment.ExitCode);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
// product photo thumbnails (404 = no photo – the card shows a placeholder)
app.MapGet("/img/product/{id:int}", async (int id, ProductImageService images, HttpContext http, CancellationToken ct) =>
{
    var thumbnail = await images.GetThumbnailAsync(id, ct);
    if (thumbnail is null)
        return Results.NotFound();
    http.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.File(thumbnail, "image/jpeg");
});
// category icons (404 = no icon – the tile shows the default icon)
app.MapGet("/img/category/{id:int}", async (int id, ProductImageService images, HttpContext http, CancellationToken ct) =>
{
    var icon = await images.GetCategoryIconAsync(id, ct);
    if (icon is null)
        return Results.NotFound();
    http.Response.Headers.CacheControl = "public, max-age=3600";
    return Results.File(icon.Data, ProductImageService.ContentType(icon.Extension));
});

// update window for the deployment script – this machine only (Deploy-Kiosk.ps1)
var maintenance = app.MapGroup("/maintenance").AddEndpointFilter(async (context, next) =>
    context.HttpContext.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) ? await next(context) : Results.NotFound());
maintenance.MapGet("/", (MaintenanceMode mode) => Results.Ok(mode.Snapshot()));
maintenance.MapPost("/", (MaintenanceMode mode, int? minutes) =>
{
    mode.Request(TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 24 * 60)));
    return Results.Ok(mode.Snapshot());
}).DisableAntiforgery();
maintenance.MapDelete("/", (MaintenanceMode mode) =>
{
    mode.Cancel();
    return Results.Ok(mode.Snapshot());
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
