using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp.Memory;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Community.Imaging.ImageSharp.Configuration;

// The package's own namespace ends in ".ImageSharp" and contains a "Configuration" namespace, so
// both of these have to be disambiguated from it explicitly.
using ImageSharpComposer = Umbraco.Cms.Imaging.ImageSharp.ImageSharpComposer;
using ImageSharpConfiguration = SixLabors.ImageSharp.Configuration;

namespace Umbraco.Community.Imaging.ImageSharp;

/// <summary>
/// Applies memory limits to ImageSharp image processing.
/// </summary>
/// <remarks>
/// Composes before <see cref="ImageSharpComposer" /> so the throttle is added to the pre-pipeline
/// ahead of <c>UseImageSharp()</c>. Registered after it, the imaging middleware would already have
/// served the response before the throttle was ever reached.
/// </remarks>
[ComposeBefore(typeof(ImageSharpComposer))]
public sealed class ImagingMemoryComposer : IComposer
{
    /// <summary>
    /// The configuration section the settings are bound from.
    /// </summary>
    public const string ConfigurationSection = "Umbraco:CMS:Imaging:Memory";

    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        IConfigurationSection section = builder.Config.GetSection(ConfigurationSection);
        ImagingMemorySettings settings = section.Get<ImagingMemorySettings>() ?? new ImagingMemorySettings();

        builder.Services.Configure<ImagingMemorySettings>(section);

        if (!settings.Enabled)
        {
            return;
        }

        // ImageSharp sizes its unmanaged pool against the available memory and releases it only on a
        // gen2 collection, so on a memory constrained host it sits at rest well above what the site
        // needs. Configuration.Default is the instance Umbraco registers, so this reaches every
        // caller including the ones that use the static default directly.
        ImageSharpConfiguration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            MaximumPoolSizeMegabytes = settings.ResolveMaximumPoolSizeMegabytes(
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
        });

        builder.Services.Configure<UmbracoPipelineOptions>(options =>
        {
            options.AddFilter(new UmbracoPipelineFilter(nameof(ImagingMemoryComposer))
            {
                PrePipeline = prePipeline => prePipeline.UseMiddleware<ImageProcessingThrottleMiddleware>(),
            });
        });
    }
}
