using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SixLabors.ImageSharp.Web.Processors;
using Umbraco.Community.Imaging.ImageSharp.Configuration;

namespace Umbraco.Community.Imaging.ImageSharp;

/// <summary>
/// Bounds the number of images processed concurrently.
/// </summary>
/// <remarks>
/// The imaging middleware only de-duplicates concurrent requests for the same URL, so a page of
/// distinct thumbnails decodes every source at full resolution in parallel. Peak memory is then
/// the number of concurrent requests multiplied by the size of a decoded source, which on a host
/// with a hard memory limit is enough to have the process killed. Requests over the limit wait
/// here instead.
/// </remarks>
public sealed class ImageProcessingThrottleMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SemaphoreSlim _semaphore;
    private readonly HashSet<string> _commands;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessingThrottleMiddleware" /> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="imagingMemorySettings">The imaging memory settings.</param>
    /// <param name="processors">The registered image processors, used to recognise processing requests.</param>
    public ImageProcessingThrottleMiddleware(
        RequestDelegate next,
        IOptions<ImagingMemorySettings> imagingMemorySettings,
        IEnumerable<IImageWebProcessor> processors)
    {
        _next = next;

        var maximumConcurrentProcessing = imagingMemorySettings.Value.ResolveMaximumConcurrentProcessing(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Environment.ProcessorCount);

        _semaphore = new SemaphoreSlim(maximumConcurrentProcessing, maximumConcurrentProcessing);
        _commands = new HashSet<string>(processors.SelectMany(x => x.Commands), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Executes the middleware.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>A <see cref="Task" /> representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsProcessingRequest(context.Request))
        {
            await _next(context);
            return;
        }

        await _semaphore.WaitAsync(context.RequestAborted);
        try
        {
            await _next(context);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private bool IsProcessingRequest(HttpRequest request)
    {
        // The image provider resolves a file, so anything without an extension cannot reach it.
        // Without this an unrelated request that happens to carry a "width" would queue here too.
        if (!Path.HasExtension(request.Path.Value))
        {
            return false;
        }

        foreach (KeyValuePair<string, StringValues> query in request.Query)
        {
            if (_commands.Contains(query.Key))
            {
                return true;
            }
        }

        return false;
    }
}
