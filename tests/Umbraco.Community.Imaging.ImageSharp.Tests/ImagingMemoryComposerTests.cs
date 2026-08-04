using System.Reflection;
using NUnit.Framework;
using Umbraco.Cms.Core.Composing;
using ImageSharpComposer = Umbraco.Cms.Imaging.ImageSharp.ImageSharpComposer;

namespace Umbraco.Community.Imaging.ImageSharp.Tests;

[TestFixture]
public class ImagingMemoryComposerTests
{
    [Test]
    public void ComposesBeforeTheUmbracoImageSharpComposer()
    {
        // Umbraco runs pipeline filters in the order they were added, so composing after
        // ImageSharpComposer would register the throttle behind UseImageSharp(), where the imaging
        // middleware has already served the response and the throttle is never reached.
        ComposeBeforeAttribute[] attributes = typeof(ImagingMemoryComposer)
            .GetCustomAttributes<ComposeBeforeAttribute>()
            .ToArray();

        Assert.That(attributes, Has.Length.EqualTo(1));
        Assert.That(attributes[0].RequiringType, Is.EqualTo(typeof(ImageSharpComposer)));
    }

    [Test]
    public void IsDiscoverableAsAComposer()
        => Assert.That(typeof(IComposer).IsAssignableFrom(typeof(ImagingMemoryComposer)), Is.True);

    [Test]
    public void BindsFromTheSameSectionAsTheProposedCmsSetting()
        => Assert.That(ImagingMemoryComposer.ConfigurationSection, Is.EqualTo("Umbraco:CMS:Imaging:Memory"));
}
