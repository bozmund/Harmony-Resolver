using System.Runtime.CompilerServices;
using Harmony.Resolver.Downloader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

LoadDotEnvFile();
LoadUserSecretsFallback();

var options = DownloaderOptions.FromEnvironment();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new Auth0TokenProvider(new HttpClient(), options));
builder.Services.AddSingleton(sp => new ResolverWorkerClient(
    new HttpClient { BaseAddress = new Uri(options.ResolverBaseUrl), Timeout = TimeSpan.FromMinutes(10) },
    sp.GetRequiredService<Auth0TokenProvider>()));
builder.Services.AddSingleton<YtDlpDownloader>();
builder.Services.AddHostedService<DownloaderWorker>();

await builder.Build().RunAsync();

// Loads a local .env (next to the built executable, the working directory, or — during development —
// the project source directory) so operators can drop credentials in a file instead of exporting them.
static void LoadDotEnvFile([CallerFilePath] string sourceFilePath = "")
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, ".env"),
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(Path.GetDirectoryName(sourceFilePath) ?? ".", ".env")
    };
    var envPath = candidates.FirstOrDefault(File.Exists);
    if (envPath is null) return;
    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex < 0) continue;
        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim().Trim('"');
        if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
    }
}

// Fallback for values supplied via `dotnet user-secrets` during local development.
static void LoadUserSecretsFallback()
{
    var configuration = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).Build();
    foreach (var key in new[]
             {
                 "RESOLVER_BASE_URL", "AUTH0_DOMAIN", "AUTH0_CLIENT_ID", "AUTH0_CLIENT_SECRET", "AUTH0_AUDIENCE",
                 "DOWNLOADER_POLL_SECONDS", "DOWNLOADER_MAX_PARALLEL", "DOWNLOADER_MAX_DURATION_MINUTES",
                 "YTDLP_PATH", "DOWNLOAD_TIMEOUT_SECONDS",
                 "RABBITMQ_URI", "RABBITMQ_QUEUE", "RABBITMQ_CERT_SHA256"
             })
    {
        if (Environment.GetEnvironmentVariable(key) is null && configuration[key] is { } value)
            Environment.SetEnvironmentVariable(key, value);
    }
}

public partial class Program;
