namespace SchemaDiff.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Core_assembly_is_loadable()
    {
        Assert.Equal("SchemaDiff.Core", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
