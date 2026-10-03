using AppDelivery.Application;
using AppDelivery.Infrastructure;
using Microsoft.Extensions.Options;
using Serilog;

// ============================================================================
// The delivery worker.
//
// Its own process, and this is the one that matters most: it was a hosted
// service inside the API, which meant deploying an endpoint change killed
// whatever four-gigabyte delivery happened to be running. It also meant the
// API could not be scaled out without multiplying delivery workers with it.
//
// Nothing about the work changed in moving it. It consumes a queue and writes
// to a table, neither of which care where it runs.
//
// It has to be Windows: IntuneWinAppUtil.exe, signtool and the staged folder
// all are. It also has to share a work folder with the crawler, which writes
// downloaded installers into it.
//
// Scale by running more of these, each with its own disk. Nothing coordinates
// them — the queue already does.
//
// The only HTTP it serves is /health, on an internal interface. Not behind
// nginx: there is nothing here a caller should reach from outside.
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(Health.Url(builder.Configuration, 5048));

builder.Logging.ClearProviders();

var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "app-worker")
    .Enrich.WithProperty("Host", Environment.MachineName)
    .WriteTo.Console(
        outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File("logs/appworker-.txt",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        buffered: true,
        flushToDiskInterval: TimeSpan.FromSeconds(10))
    .CreateLogger();

Log.Logger = logger;
builder.Logging.AddSerilog(logger);

// What this host is for, in three lines: it reads the tables, it publishes to
// Intune, and it fetches, packages and signs. It does not queue deliveries —
// that is the API and the scheduler.
builder.Services.AddAppDeliveryStores(builder.Configuration);
builder.Services.AddAppDeliveryIntune(builder.Configuration);
builder.Services.AddAppDeliveryBuilding(builder.Configuration);

// The queue itself, but not Deliveries: this host reads the queue and does
// not write to it. Asking for a delivery carries a duplicate check that
// belongs where somebody asks — the API and the scheduler — and a worker
// able to queue work for itself is a worker that can loop.
builder.Services.AddSingleton<JobQueue>();

builder.Services.AddHostedService<DeliveryWorker>();

// A delivery that succeeds clears up after itself. One that fails keeps
// everything, because that is the only evidence — and then keeps it forever.
// This removes those once they are old enough that nobody will open them.
builder.Services.AddHostedService<Housekeeping>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Is this worker able to work?
//
// Running and unable to work looks identical to healthy from outside, and it
// is the state worth catching before a delivery does. A worker with no
// packaging tool fails every job at the packaging step, forty minutes in.
// ---------------------------------------------------------------------------
app.MapGet("/health", (IConfiguration configuration, IScriptSigner signer) =>
{
    var checks = new List<Check>
    {
        Health.FileThere(
            "packaging tool",
            configuration["Packaging:IntuneWinAppUtil"],
            "Install it from github.com/microsoft/Microsoft-Win32-Content-Prep-Tool " +
            "and set Packaging:IntuneWinAppUtil."),

        Health.FolderWritable("work folder", configuration["Packaging:WorkFolder"]),

        // Not a failure: most tenants run unsigned, because the wrapper is
        // already inside a package only Intune can open.
        new Check(
            "signing",
            true,
            signer.Enabled ? $"on, with {signer.Describe()}" : "off"),
    };

    var (status, body) = Health.Report("app-worker", checks);
    return Results.Json(body, statusCode: status);
});

Log.Information("The delivery worker is starting on {Host}", Environment.MachineName);

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "The delivery worker stopped unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
