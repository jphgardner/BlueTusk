using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set BLUETUSK_CONNECTION_STRING to a PostgreSQL database.");
    return 1;
}

await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();
await using (var command = new BlueTuskCommand(
    """
    CREATE TEMP TABLE graph_people (id int PRIMARY KEY, name text NOT NULL);
    CREATE TEMP TABLE graph_knows (
        id int PRIMARY KEY,
        source_id int NOT NULL REFERENCES graph_people (id),
        destination_id int NOT NULL REFERENCES graph_people (id));
    CREATE INDEX ON graph_knows (source_id);
    CREATE INDEX ON graph_knows (destination_id);
    INSERT INTO graph_people VALUES (1, 'Ada'), (2, 'Grace');
    INSERT INTO graph_knows VALUES (1, 1, 2);
    """, connection))
{
    _ = await command.ExecuteNonQueryAsync();
}

var options = new DbContextOptionsBuilder<PeopleContext>().UseBlueTusk(connection).Options;
await using var db = new PeopleContext(options);
var sourceId = 1;
var friends = await db.PropertyGraph("people")
    .Match(pattern => pattern
        .Vertex<Person>("source", person => person.Id == sourceId)
        .Outgoing<Knows>("knows")
        .Vertex<Person>("target"))
    .Select<Friend>(projection => projection
        .Property<Person, string>("source", person => person.Name, row => row.Source)
        .Property<Person, string>("target", person => person.Name, row => row.Target))
    .ToListAsync();
foreach (var friend in friends)
{
    Console.WriteLine($"{friend.Source} knows {friend.Target}");
}

return 0;

internal sealed class PeopleContext(DbContextOptions<PeopleContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Person>(entity =>
        {
            entity.ToTable("graph_people", "pg_temp");
            entity.Property(person => person.Id).HasColumnName("id");
            entity.Property(person => person.Name).HasColumnName("name");
        });
        modelBuilder.Entity<Knows>(entity =>
        {
            entity.ToTable("graph_knows", "pg_temp");
            entity.Property(edge => edge.Id).HasColumnName("id");
            entity.Property(edge => edge.SourceId).HasColumnName("source_id");
            entity.Property(edge => edge.DestinationId).HasColumnName("destination_id");
        });
        modelBuilder.HasPropertyGraph("people", graph => graph
            .Vertex<Person>("people", vertex => vertex.HasLabel("person")
                .HasKey(person => person.Id)
                .Properties(person => new { person.Id, person.Name }))
            .Edge<Knows>("knows", edge => edge.HasLabel("knows")
                .HasKey(relationship => relationship.Id)
                .HasSource<Person>(relationship => relationship.SourceId, person => person.Id)
                .HasDestination<Person>(relationship => relationship.DestinationId, person => person.Id)));
    }
}

internal sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class Knows
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public int DestinationId { get; set; }
}

internal sealed class Friend
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
}
