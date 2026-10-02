using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class ComplexTypeConstructorBindingTests
{
    [Fact]
    public void Readonly_struct_taking_nested_complex_values_is_materialized_by_member_assignment()
    {
        using var context = new StructContext();
        var activity = context.Model.FindEntityType(typeof(Venue))!.FindComplexProperty(nameof(Venue.Activity))!.ComplexType;

        Assert.IsType<DefaultValueBinding>(activity.ConstructorBinding);
        Assert.NotNull(activity.FindComplexProperty(nameof(Activity.Champions)));
        Assert.NotNull(activity.FindProperty(nameof(Activity.Name)));
    }

    [Fact]
    public void Reference_complex_type_with_unbindable_constructor_is_still_rejected()
    {
        using var context = new ClassContext();

        var exception = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("No suitable constructor was found", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StructContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseBlueTusk("Host=localhost;Database=unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Venue>(venue => venue.ComplexProperty(
                v => v.Activity,
                activity => activity.ComplexProperty(a => a.Champions)));
    }

    private sealed class ClassContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseBlueTusk("Host=localhost;Database=unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ClassVenue>(venue => venue.ComplexProperty(
                v => v.Activity,
                activity => activity.ComplexProperty(a => a.Champions)));
    }

    private sealed class Venue
    {
        public int Id { get; set; }

        public Activity Activity { get; set; }
    }

    private readonly struct Activity(string name, Team champions)
    {
        public readonly string Name = name;

        public readonly Team Champions = champions;
    }

    private readonly struct Team(string name)
    {
        public readonly string Name = name;
    }

    private sealed class ClassVenue
    {
        public int Id { get; set; }

        public ClassActivity Activity { get; set; } = null!;
    }

    private sealed class ClassActivity(string name, Team champions)
    {
        public string Name { get; } = name;

        public Team Champions { get; } = champions;
    }
}
