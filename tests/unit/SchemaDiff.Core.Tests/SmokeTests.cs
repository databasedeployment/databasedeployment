namespace SchemaDiff.Core.Tests;

// Placeholder so CI has a test to run until the first WBS activities add real ones.
public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.Equal("SchemaDiff.Core", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
