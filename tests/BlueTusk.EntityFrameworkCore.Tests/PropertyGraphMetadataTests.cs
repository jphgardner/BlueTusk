using BlueTusk.EntityFrameworkCore.Design.Internal;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable EF1001 // Tests intentionally exercise provider design-time infrastructure.

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PropertyGraphMetadataTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Model_builder_records_typed_graph_metadata_and_generates_guarded_quoted_DDL()
    {
        using var context = CreateContext<QuotedGraphContext>();

        var graph = Assert.Single(context.Model.GetBlueTuskPropertyGraphs());
        Assert.Equal("Social \"Graph", graph.Name);
        Assert.Equal("Graph Schema", graph.Schema);
        var vertex = Assert.Single(
            graph.ElementTables,
            element => element.Kind == BlueTuskGraphElementKind.Vertex);
        Assert.Equal("People Vertex", vertex.Alias);
        Assert.Equal("People \"Table", vertex.Table);
        Assert.Equal(["Person Id"], vertex.KeyColumns);
        var label = Assert.Single(vertex.Labels);
        Assert.Equal("Person \"Label", label.Name);
        Assert.Equal(["Id", "Name"], label.Properties.Select(property => property.Name));

        var edge = Assert.Single(
            graph.ElementTables,
            element => element.Kind == BlueTuskGraphElementKind.Edge);
        Assert.Equal("People Vertex", edge.Source?.VertexTableAlias);
        Assert.Equal(["From Id"], edge.Source?.EdgeKeyColumns);
        Assert.Equal(["Person Id"], edge.Source?.VertexKeyColumns);

        var sql = context.Database.GenerateCreateScript();
        Assert.Contains("current_setting('server_version_num')::integer < 190000", sql, StringComparison.Ordinal);
        Assert.Contains("to_regclass('information_schema.property_graphs')", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE PROPERTY GRAPH \"Graph Schema\".\"Social \"\"Graph\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Graph Schema\".\"People \"\"Table\" AS \"People Vertex\"", sql, StringComparison.Ordinal);
        Assert.Contains("LABEL \"Person \"\"Label\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Person Id\" AS \"Id\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_differ_emits_create_alter_replace_and_drop_graph_operations()
    {
        using var oldContext = CreateContext<OldGraphContext>();
        using var renamedContext = CreateContext<RenamedGraphContext>();
        var differ = renamedContext.GetService<IMigrationsModelDiffer>();
        var oldModel = oldContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var renamedModel = renamedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var rename = Assert.Single(
            differ.GetDifferences(oldModel, renamedModel)
                .OfType<AlterBlueTuskPropertyGraphOperation>());
        Assert.Equal("social", rename.Name);
        Assert.Equal("renamed_social", rename.NewName);

        var create = Assert.Single(
            differ.GetDifferences(null, oldModel)
                .OfType<CreateBlueTuskPropertyGraphOperation>());
        Assert.Equal("social", create.Definition.Name);
        var drop = Assert.Single(
            differ.GetDifferences(oldModel, null)
                .OfType<DropBlueTuskPropertyGraphOperation>());
        Assert.Equal("social", drop.Name);

        using var changedContext = CreateContext<ChangedGraphContext>();
        var changedModel = changedContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var changes = differ.GetDifferences(oldModel, changedModel);
        Assert.Single(changes.OfType<DropBlueTuskPropertyGraphOperation>());
        Assert.Single(changes.OfType<CreateBlueTuskPropertyGraphOperation>());
    }

    [Fact]
    public void Design_time_generator_scaffolds_property_graph_operations()
    {
        using var context = CreateContext<OldGraphContext>();
        var definition = Assert.Single(context.Model.GetBlueTuskPropertyGraphs());
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddEntityFrameworkBlueTusk();
        new BlueTuskDesignTimeServices().ConfigureDesignTimeServices(services);
        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<ICSharpMigrationOperationGenerator>();
        var builder = new IndentedStringBuilder();

        generator.Generate(
            "migrationBuilder",
            [
                new CreateBlueTuskPropertyGraphOperation { Definition = definition },
                new AlterBlueTuskPropertyGraphOperation
                {
                    Name = "social",
                    Schema = "graphs",
                    NewName = "renamed_social",
                    NewSchema = "archive",
                },
                new DropBlueTuskPropertyGraphOperation { Name = "renamed_social", Schema = "archive" },
            ],
            builder);

        var code = builder.ToString();
        Assert.Contains("migrationBuilder.CreateBlueTuskPropertyGraph(", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AlterBlueTuskPropertyGraph(\"social\"", code, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropBlueTuskPropertyGraph(\"renamed_social\"", code, StringComparison.Ordinal);
    }

    private static TContext CreateContext<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static void ConfigureModel(ModelBuilder modelBuilder, string graphName, bool includeNameProperty)
    {
        modelBuilder.Entity<Person>(entity =>
        {
            entity.ToTable("people", "graphs");
            entity.Property(person => person.Id).HasColumnName("id");
            entity.Property(person => person.Name).HasColumnName("name");
        });
        modelBuilder.Entity<Friendship>(entity =>
        {
            entity.ToTable("friendships", "graphs");
            entity.Property(friendship => friendship.Id).HasColumnName("id");
            entity.Property(friendship => friendship.FromPersonId).HasColumnName("from_id");
            entity.Property(friendship => friendship.ToPersonId).HasColumnName("to_id");
        });
        modelBuilder.HasBlueTuskPropertyGraph(
            graphName,
            graph =>
            {
                graph.Vertex<Person>("people", vertex =>
                {
                    vertex.HasLabel("person").HasKey(person => person.Id);
                    if (includeNameProperty)
                    {
                        vertex.Properties(person => new { person.Id, person.Name });
                    }
                    else
                    {
                        vertex.Properties(person => person.Id);
                    }
                });
                graph.Edge<Friendship>("friendships", edge => edge
                    .HasLabel("knows")
                    .HasKey(friendship => friendship.Id)
                    .HasSource<Person>(friendship => friendship.FromPersonId, person => person.Id)
                    .HasDestination<Person>(friendship => friendship.ToPersonId, person => person.Id));
            },
            schema: "graphs");
    }

    private sealed class QuotedGraphContext(DbContextOptions<QuotedGraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.ToTable("People \"Table", "Graph Schema");
                entity.Property(person => person.Id).HasColumnName("Person Id");
                entity.Property(person => person.Name).HasColumnName("Display \"Name");
            });
            modelBuilder.Entity<Friendship>(entity =>
            {
                entity.ToTable("Friend \"Edges", "Graph Schema");
                entity.Property(friendship => friendship.Id).HasColumnName("Edge Id");
                entity.Property(friendship => friendship.FromPersonId).HasColumnName("From Id");
                entity.Property(friendship => friendship.ToPersonId).HasColumnName("To Id");
            });
            modelBuilder.HasBlueTuskPropertyGraph(
                "Social \"Graph",
                graph =>
                {
                    graph.Vertex<Person>("People Vertex", vertex => vertex
                        .HasLabel("Person \"Label")
                        .HasKey(person => person.Id)
                        .Properties(person => new { person.Id, person.Name }));
                    graph.Edge<Friendship>("Friend Edge", edge => edge
                        .HasLabel("Knows \"Label")
                        .HasKey(friendship => friendship.Id)
                        .HasSource<Person>(friendship => friendship.FromPersonId, person => person.Id)
                        .HasDestination<Person>(friendship => friendship.ToPersonId, person => person.Id));
                },
                schema: "Graph Schema");
        }
    }

    private sealed class OldGraphContext(DbContextOptions<OldGraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureModel(modelBuilder, "social", includeNameProperty: true);
    }

    private sealed class RenamedGraphContext(DbContextOptions<RenamedGraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureModel(modelBuilder, "renamed_social", includeNameProperty: true);
    }

    private sealed class ChangedGraphContext(DbContextOptions<ChangedGraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            ConfigureModel(modelBuilder, "social", includeNameProperty: false);
    }

    private sealed class Person
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class Friendship
    {
        public int Id { get; set; }

        public int FromPersonId { get; set; }

        public int ToPersonId { get; set; }
    }
}

#pragma warning restore EF1001
