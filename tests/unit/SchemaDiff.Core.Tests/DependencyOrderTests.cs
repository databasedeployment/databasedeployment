using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Core.Tests.TestFactory;

namespace SchemaDiff.Core.Tests;

public class DependencyOrderTests
{
    private static List<ObjectKey> Order(
        IReadOnlyCollection<ObjectKey> nodes, Dictionary<ObjectKey, List<ObjectKey>> references, out bool hadCycle) =>
        DependencyOrder.Order(nodes, references, ObjectKeyComparer.CaseInsensitive, out hadCycle);

    [Fact]
    public void Referenced_objects_come_before_the_objects_that_use_them()
    {
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(ObjectKeyComparer.CaseInsensitive)
        {
            [View("ActiveCustomers")] = [Table("Customer")],
            [Proc("ListActive")] = [View("ActiveCustomers")],
        };

        var ordered = Order([Proc("ListActive"), View("ActiveCustomers"), Table("Customer")], references, out var cycle);

        Assert.False(cycle);
        Assert.Equal([Table("Customer"), View("ActiveCustomers"), Proc("ListActive")], ordered);
    }

    [Fact]
    public void Independent_objects_keep_a_stable_kind_and_name_order()
    {
        var ordered = Order([View("B"), Table("Z"), Table("A")], [], out _);

        Assert.Equal([Table("A"), Table("Z"), View("B")], ordered);
    }

    [Fact]
    public void References_to_objects_outside_the_set_are_ignored()
    {
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(ObjectKeyComparer.CaseInsensitive)
        {
            [View("V")] = [Table("AlreadyOnTarget")],
        };

        Assert.Equal([View("V")], Order([View("V")], references, out _));
    }

    [Fact]
    public void A_cycle_is_reported_and_every_object_is_still_returned()
    {
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(ObjectKeyComparer.CaseInsensitive)
        {
            [Table("A")] = [Table("B")],
            [Table("B")] = [Table("A")],
        };

        var ordered = Order([Table("A"), Table("B")], references, out var cycle);

        Assert.True(cycle);
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Matching_ignores_name_casing()
    {
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(ObjectKeyComparer.CaseInsensitive)
        {
            [View("v")] = [Table("CUSTOMER")],
        };

        var ordered = Order([View("V"), Table("Customer")], references, out _);

        Assert.Equal(Table("Customer"), ordered[0]);
    }

    [Fact]
    public void Deployment_sections_run_from_types_to_settings()
    {
        var sections = Enum.GetValues<DeploymentSection>();

        Assert.Equal(DeploymentSection.Schemas, sections.First());
        Assert.True(DeploymentSection.Types < DeploymentSection.Tables);
        Assert.True(DeploymentSection.Tables < DeploymentSection.Modules);
        Assert.True(DeploymentSection.Modules < DeploymentSection.Security);
        Assert.Equal(DeploymentSection.Settings, sections.Last());
    }
}
