using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Jobs;

namespace SchemaDiff.Core.Tests;

public class JobConfigurationTests
{
    private const string Source = "Server=dev01;Integrated Security=true;TrustServerCertificate=true";
    private const string Target = "Server=prod01;Integrated Security=true;TrustServerCertificate=true";

    private static string Database(string connectionString) => new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    [Fact]
    public void Defaults_and_a_shared_database_name_build_both_sides()
    {
        var configuration = new JobConfiguration
        {
            Defaults = new ConnectionDefaults { Source = Source, Target = Target },
            Jobs = [new JobDefinition { Database = "EDW" }],
        };

        var job = Assert.Single(configuration.Resolve());

        Assert.Equal("EDW", job.Name);
        Assert.Equal("EDW", Database(job.SourceConnectionString));
        Assert.Equal("dev01.EDW", job.SourceLabel);
        Assert.Equal("prod01.EDW", job.TargetLabel);
    }

    [Fact]
    public void Different_database_names_per_side_are_supported()
    {
        var configuration = new JobConfiguration
        {
            Defaults = new ConnectionDefaults { Source = Source, Target = Target },
            Jobs = [new JobDefinition { Name = "renamed", SourceDatabase = "Sales_v2", TargetDatabase = "Sales" }],
        };

        var job = Assert.Single(configuration.Resolve());

        Assert.Equal("Sales_v2", Database(job.SourceConnectionString));
        Assert.Equal("Sales", Database(job.TargetConnectionString));
    }

    [Fact]
    public void A_job_without_a_source_or_default_is_rejected()
    {
        var configuration = new JobConfiguration { Jobs = [new JobDefinition { Database = "EDW", Target = Target }] };

        var ex = Assert.Throws<InvalidDataException>(() => configuration.Resolve());
        Assert.Contains("jobs[0]", ex.Message);
    }

    [Fact]
    public void Duplicate_job_names_are_rejected()
    {
        var configuration = new JobConfiguration
        {
            Defaults = new ConnectionDefaults { Source = Source, Target = Target },
            Jobs = [new JobDefinition { Database = "EDW" }, new JobDefinition { Database = "edw" }],
        };

        Assert.Throws<InvalidDataException>(() => configuration.Resolve());
    }

    [Fact]
    public void An_empty_job_list_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => new JobConfiguration().Resolve());
    }

    [Fact]
    public async Task Jobs_file_allows_comments_trailing_commas_and_any_casing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              // shared connections
              "Defaults": { "source": "{{Source}}", "TARGET": "{{Target}}", },
              "jobs": [ { "database": "EDWSTG" }, { "database": "EDW" }, ],
            }
            """);
        try
        {
            var jobs = await JobConfiguration.LoadAsync(path);
            Assert.Equal(["EDWSTG", "EDW"], jobs.Select(j => j.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Template_file_is_valid_and_loads_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jobs-{Guid.NewGuid():N}.json");
        try
        {
            await JobConfiguration.WriteTemplateAsync(path);
            var jobs = await JobConfiguration.LoadAsync(path);
            Assert.Equal(4, jobs.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Labels_never_show_credentials()
    {
        var job = new ComparisonJob("x",
            "Server=dev01;Database=EDW;User ID=deployer;Password=S3cret!",
            "Server=prod01;Database=EDW;User ID=deployer;Password=S3cret!");

        Assert.DoesNotContain("S3cret", job.SourceLabel);
        Assert.DoesNotContain("deployer", job.TargetLabel);
        Assert.Equal("(invalid connection string)", new ComparisonJob("x", "Server=a;Unknown Keyword=1", "y").SourceLabel);
    }
}
