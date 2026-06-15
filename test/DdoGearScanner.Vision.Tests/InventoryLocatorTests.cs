using DdoGearScanner.Vision;
using Xunit;

namespace DdoGearScanner.Vision.Tests;

public class InventoryLocatorTests
{
    [Fact]
    public void EmbeddedRagdollTemplateLoads()
    {
        // Verifies the rag-doll PNG is embedded and decodable, so the single-file exe is self-contained.
        InventoryLocator? loc = InventoryLocator.TryLoadEmbedded();
        Assert.NotNull(loc);
        Assert.True(loc!.TemplateWidth > 0 && loc.TemplateHeight > 0);
    }
}
