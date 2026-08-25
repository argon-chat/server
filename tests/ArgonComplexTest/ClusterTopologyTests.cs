namespace ArgonComplexTest;

using Argon.Entities;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The cluster the fixture built is the cluster the run asked for.
/// </summary>
/// <remarks>
/// <para>Every other CockroachDB assertion in this suite is written against a topology it takes on
/// trust. That trust is worth one test, because the failure is silent in the direction that matters:
/// a fixture that quietly stood up one region would make the placement assertions pass for the wrong
/// reason on a run meant to prove them, and a fixture that quietly gave every node the same zone
/// would make <c>SURVIVE ZONE FAILURE</c> a sentence nobody had tested.</para>
///
/// <para>Asserted against <see cref="TestEnvironmentOptions.DatabaseTopology"/> rather than against
/// constants, so it holds for both shapes — the default three regions of one node, and the
/// <c>2x3</c> a two-region deployment really looks like.</para>
/// </remarks>
[TestFixture]
public class ClusterTopologyTests : TestBase
{
    private static void OnlyOnCockroach()
        => Assume.That(TestEnvironmentOptions.DatabaseKind, Is.EqualTo(TestDatabaseKind.Cockroach),
            "there is no cluster topology to speak of on PostgreSQL");

    private async Task<List<(string Region, int Zones)>> ClusterRegionsAsync(CancellationToken ct)
    {
        // Through the server's own context factory rather than a connection string of our own: it is
        // the connection the suite actually uses, so a topology this proves is the topology every other
        // assertion is written against.
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        var connection = db.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();

        // From the CLUSTER, not the database: this is a question about what the nodes were started
        // with, and a region only reaches the database because a node advertised it first.
        command.CommandText = "SHOW REGIONS FROM CLUSTER";

        var regions = new List<(string, int)>();

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            regions.Add((reader.GetString(0), reader.GetFieldValue<string[]>(1).Length));

        return regions;
    }

    [Test, CancelAfter(120_000)]
    public async Task The_cluster_has_the_regions_and_zones_the_run_asked_for(CancellationToken ct = default)
    {
        OnlyOnCockroach();

        var (regions, perRegion) = TestEnvironmentOptions.DatabaseTopology;
        var actual               = await ClusterRegionsAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(actual.Select(entry => entry.Region),
                Is.EquivalentTo(TestEnvironmentOptions.DatabaseRegions),
                $"the run asked for {regions} region(s) and the cluster reports "
              + string.Join(", ", actual.Select(entry => entry.Region)));

            foreach (var (region, zones) in actual)
                Assert.That(zones, Is.EqualTo(perRegion),
                    $"{region} has {zones} zone(s) and the run asked for {perRegion}; below two, "
                  + "SURVIVE ZONE FAILURE is a goal the cluster cannot meet");
        });
    }
}
