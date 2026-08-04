using System.Globalization;
using ImagingHarness;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Web.Caching;
using SixLabors.ImageSharp.Web.DependencyInjection;
using SixLabors.ImageSharp.Web.Middleware;
using SixLabors.ImageSharp.Web.Providers;
using Umbraco.Community.Imaging.ImageSharp.Configuration;

// Aliased rather than imported: the package namespace ends in ".ImageSharp", so importing it would
// make the bare name "Configuration" ambiguous with SixLabors.ImageSharp.Configuration.
using ImageProcessingThrottleMiddleware = Umbraco.Community.Imaging.ImageSharp.ImageProcessingThrottleMiddleware;

string mediaRoot = Environment.GetEnvironmentVariable("HARNESS_MEDIA_ROOT") ?? "/media";
string cacheRoot = Environment.GetEnvironmentVariable("HARNESS_CACHE_ROOT") ?? "/cache";

// ---------------------------------------------------------------------------
// Mode: generate source media, then exit. Run once against a mounted volume so
// the measured run starts from a clean baseline.
// ---------------------------------------------------------------------------
if (args.Length > 0 && args[0] == "generate")
{
    int count = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 40;
    int generateWidth = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 4000;
    int generateHeight = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 3000;
    GenerateMedia(mediaRoot, count, generateWidth, generateHeight);
    return;
}

// ---------------------------------------------------------------------------
// HARNESS_FIX=1 applies what the package applies: the pool cap from
// ImagingMemorySettings plus ImageProcessingThrottleMiddleware. Both come from
// the package's own sources, so this measures the shipped code.
//   HARNESS_POOL_MB          - override MaximumPoolSizeMegabytes (0 = derive)
//   HARNESS_MAX_CONCURRENCY  - override MaximumConcurrentProcessing (0 = derive)
// The allocator has to be set before anything allocates through Configuration.Default.
// ---------------------------------------------------------------------------
bool applyFix = Environment.GetEnvironmentVariable("HARNESS_FIX") == "1";

var settings = new ImagingMemorySettings
{
    MaximumPoolSizeMegabytes = ReadInt("HARNESS_POOL_MB", 0),
    MaximumConcurrentProcessing = ReadInt("HARNESS_MAX_CONCURRENCY", 0),
};

if (applyFix)
{
    long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    Configuration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
    {
        MaximumPoolSizeMegabytes = settings.ResolveMaximumPoolSizeMegabytes(available),
    });

    Console.WriteLine(
        $"[fix] poolMB={settings.ResolveMaximumPoolSizeMegabytes(available)} " +
        $"maxConcurrent={settings.ResolveMaximumConcurrentProcessing(available, Environment.ProcessorCount)} " +
        $"availableMB={available / 1048576} cpus={Environment.ProcessorCount}");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = mediaRoot,
});

builder.Logging.SetMinimumLevel(LogLevel.Warning);

// --- Mirrors Umbraco's AddUmbracoImageSharp() -------------------------------
builder.Services.AddSingleton(Configuration.Default);

builder.Services.AddImageSharp()
    .ClearProviders()
    .AddProvider<WebRootImageProvider>()
    .AddProcessor<CropWebProcessor>();

builder.Services.Configure<ImageSharpMiddlewareOptions>(options =>
{
    options.Configuration = Configuration.Default;
    options.BrowserMaxAge = TimeSpan.FromDays(7);
    options.CacheMaxAge = TimeSpan.FromDays(365);
    options.CacheHashLength = 12;

    // Umbraco's max width/height guard is omitted: CommandCollection differs between the two
    // ImageSharp majors, every width requested here is far below the limit so the guard would
    // never fire, and it has no bearing on the memory behaviour being measured.
    options.OnPrepareResponseAsync = context =>
    {
        if (context.Request.Query.ContainsKey("rnd"))
        {
            ResponseHeaders headers = context.Response.GetTypedHeaders();
            CacheControlHeaderValue cacheControl = headers.CacheControl ?? new CacheControlHeaderValue { Public = true };
            cacheControl.MustRevalidate = false;
            cacheControl.Extensions.Add(new NameValueHeaderValue("immutable"));
            headers.CacheControl = cacheControl;
        }

        return Task.CompletedTask;
    };
});

builder.Services.Configure<PhysicalFileSystemCacheOptions>(options =>
{
    options.CacheFolder = cacheRoot;
    options.CacheFolderDepth = 8;
});

if (applyFix)
{
    builder.Services.AddSingleton(Options.Create(settings));
}

WebApplication app = builder.Build();

if (applyFix)
{
    app.UseMiddleware<ImageProcessingThrottleMiddleware>();
}

app.UseImageSharp();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok("ok"));
app.MapGet("/stats", (string? label) => Text(Diagnostics.Snapshot(label ?? "sample")));

// Full blocking gen2 collection, which is what triggers ImageSharp's pool trimming.
app.MapGet("/gc", () =>
{
    for (int i = 0; i < 3; i++)
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    return Text(Diagnostics.Snapshot("after-gc"));
});

// Ask glibc to return free heap pages to the OS. If RSS drops here but not after /gc, the
// retained memory is held by the C allocator rather than by ImageSharp.
app.MapGet("/malloc-trim", () =>
{
    int? result = Diagnostics.MallocTrim();
    Dictionary<string, object?> snapshot = Diagnostics.Snapshot("after-malloc-trim");
    snapshot["mallocTrimResult"] = result;
    return Text(snapshot);
});

app.Run();

static int ReadInt(string name, int fallback)
    => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : fallback;

static IResult Text(Dictionary<string, object?> snapshot)
    => Results.Text(
        string.Join('\n', snapshot.Select(kvp => $"{kvp.Key}={kvp.Value}")),
        "text/plain");

static void GenerateMedia(string root, int count, int width, int height)
{
    Directory.CreateDirectory(root);
    var random = new Random(20260804);

    for (int i = 0; i < count; i++)
    {
        string path = Path.Combine(root, $"img{i:D3}.jpg");
        if (File.Exists(path))
        {
            continue;
        }

        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    // Noise keeps the encoded files realistically large (~9 MB at 4000x3000).
                    row[x] = new Rgb24((byte)(x ^ y), (byte)random.Next(256), (byte)(y + i));
                }
            }
        });

        image.Save(path, new JpegEncoder { Quality = 90 });
        Console.WriteLine($"generated {path} ({new FileInfo(path).Length / 1024} KB)");
    }
}
