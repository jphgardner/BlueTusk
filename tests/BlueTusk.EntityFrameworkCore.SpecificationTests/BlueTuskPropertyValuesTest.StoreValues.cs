// BlueTusk ports of EF Core 10.0.11 complex-collection store-value tests.
//
// Derived from Entity Framework Core 10.0.11 (test/EFCore.Specification.Tests/PropertyValuesTestBase.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
//
// EF Core skips these four tests (dotnet/efcore#31411) and, as written, they cannot pass on any provider:
// - EntityEntry.GetDatabaseValues() and GetDatabaseValuesAsync() build the store values from scalar columns only, inside
//   EntityEntry, so the returned values never contain complex collections. BlueTusk's GetCompleteDatabaseValues() and
//   GetCompleteDatabaseValuesAsync() read the same scalar values plus every complex collection in one statement; the
//   ports call them instead, and EF_Core_GetDatabaseValues_omits_complex_collection_store_values below pins the EF Core
//   behaviour that makes this necessary.
// - The shared fixture does not seed the School these tests read (its seed line is commented out pending #31411), so
//   the BlueTusk fixture seeds exactly the School that the upstream CreateSchool() builds.
// - Store_values_can_be_cloned reads "Departments" through PropertyValues.Properties, which holds scalar properties
//   only; the port reads it through ComplexCollectionProperties.
// The two already-enabled store-value ToObject cases also use the complete API: seeding School makes their
// previously empty schoolValues branch execute, and the scalar-only EF Core API cannot materialize its collections.
// The private EF Core helpers below are copied unchanged (renamed with a "Ported" prefix) because they cannot be called
// from a derived class.

using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Microsoft.EntityFrameworkCore;

public sealed partial class BlueTuskPropertyValuesTest
{
    [ConditionalFact]
    public override Task Complex_collection_store_values_can_be_accessed_as_a_property_dictionary()
        => PortedTestComplexCollectionPropertyValues(e => Task.FromResult(e.GetCompleteDatabaseValues()!), expectOriginalValues: true);

    [ConditionalFact]
    public override Task Complex_collection_store_values_can_be_accessed_asynchronously_as_a_property_dictionary()
        => PortedTestComplexCollectionPropertyValues(async e => (await e.GetCompleteDatabaseValuesAsync())!, expectOriginalValues: true);

    [ConditionalFact]
    public override Task Store_values_can_be_cloned()
        => PortedStore_values_can_be_cloned_implementation(e => Task.FromResult(e.GetCompleteDatabaseValues()!));

    [ConditionalFact]
    public override Task Store_values_can_be_cloned_asynchronously()
        => PortedStore_values_can_be_cloned_implementation(async e => (await e.GetCompleteDatabaseValuesAsync())!);

    [ConditionalFact]
    public override Task Store_values_can_be_copied_to_object_using_ToObject()
        => PortedStore_values_can_be_copied_to_object_using_ToObject_implementation(e => Task.FromResult(e.GetCompleteDatabaseValues()!));

    [ConditionalFact]
    public override Task Store_values_can_be_copied_to_object_using_ToObject_asynchronously()
        => PortedStore_values_can_be_copied_to_object_using_ToObject_implementation(async e => (await e.GetCompleteDatabaseValuesAsync())!);

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EF_Core_GetDatabaseValues_omits_complex_collection_store_values(bool async)
    {
        using var context = CreateContext();
        var school = PortedCreateSchool();
        context.Set<School>().Attach(school);
        var entry = context.Entry(school);

        var values = async ? await entry.GetDatabaseValuesAsync() : entry.GetDatabaseValues();

        Assert.NotNull(values);
        Assert.Equal("Test School", values["Name"]);
        Assert.Null(values[entry.Metadata.FindComplexProperty(nameof(School.Departments))!]);
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_store_values_are_null_for_an_entity_not_in_the_store(bool async)
    {
        using var context = CreateContext();
        var school = PortedCreateSchool();
        school.Id = 77;
        context.Set<School>().Attach(school);
        var entry = context.Entry(school);

        Assert.Null(async ? await entry.GetCompleteDatabaseValuesAsync() : entry.GetCompleteDatabaseValues());
    }

    [ConditionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_store_values_match_EF_Core_store_values_without_complex_collections(bool async)
    {
        using var context = CreateContext();
        var building = context.Set<Building>().Single(b => b.Name == "Building One");
        building.Name = "Building One Prime";
        var entry = context.Entry(building);

        var expected = async ? await entry.GetDatabaseValuesAsync() : entry.GetDatabaseValues();
        var actual = async ? await entry.GetCompleteDatabaseValuesAsync() : entry.GetCompleteDatabaseValues();

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Properties, actual.Properties);
        foreach (var property in expected.Properties)
        {
            Assert.Equal(expected[property], actual[property]);
        }
    }

    [ConditionalFact]
    public async Task Complete_store_values_read_complex_collections_changed_in_the_store()
    {
        using var context = CreateContext();
        var school = PortedCreateSchool();
        context.Set<School>().Attach(school);
        var entry = context.Entry(school);

        var changed = PortedCreateSchool();
        changed.Departments.RemoveAt(1);
        changed.Departments[0].Courses.Add(new Course { Name = "Compilers", Credits = 5 });
        await StoreDepartmentsAsync(changed);
        try
        {
            var values = await entry.GetCompleteDatabaseValuesAsync();

            var departments = (IList<Department>)values!["Departments"]!;
            var department = Assert.Single(departments);
            Assert.Equal("Computer Science", department.Name);
            Assert.Equal(["Data Structures", "Algorithms", "Compilers"], department.Courses.Select(course => course.Name));
            Assert.Equal(2, school.Departments.Count);
            Assert.Equal(2, school.Departments[0].Courses.Count);
        }
        finally
        {
            await StoreDepartmentsAsync(PortedCreateSchool());
        }
    }

    private async Task StoreDepartmentsAsync(School school)
    {
        using var writer = Fixture.CreateContext();
        writer.Set<School>().Attach(school);
        writer.Entry(school).ComplexCollection(s => s.Departments).IsModified = true;
        await writer.SaveChangesAsync();
    }

    private async Task PortedTestComplexCollectionPropertyValues(
        Func<EntityEntry<School>, Task<PropertyValues>> getPropertyValues,
        bool expectOriginalValues)
    {
        using var context = CreateContext();
        //var school = context.Set<School>().Single(s => s.Name == "Test School");

        // Complex collection query support. Issue #31411
        var school = PortedCreateSchool();
        context.Set<School>().Attach(school);

        var originalDepartments = school.Departments.ToList();
        var originalFirstDepartmentCourses = school.Departments.First().Courses.ToList();

        school.Departments.Clear();
        school.Departments.Add(
            new Department
            {
                Name = "Modified Department",
                Building = "Modified Building",
                Courses =
                [
                    new Course { Name = "Modified Course 1", Credits = 4 },
                    new Course { Name = "Modified Course 2", Credits = 5 }
                ]
            });

        var entry = context.Entry(school);
        var values = await getPropertyValues(entry);

        Assert.Equal("Test School", values["Name"]);
        Assert.Equal("Test School", values[entry.Property(e => e.Name).Metadata]);
        Assert.Equal(1, values[entry.Property(e => e.Id).Metadata]);

        var departmentsComplexProperty = entry.Metadata.FindComplexProperty(nameof(School.Departments))!;
        if (expectOriginalValues)
        {
            Assert.Equal("Test School", values["Name"]);
            var departments = (IList<Department>)values["Departments"]!;
            var departmentsViaComplexProperty = (IList<Department>)values[departmentsComplexProperty]!;
            Assert.Equal(2, departments.Count);
            Assert.Equal(2, departmentsViaComplexProperty.Count);

            var dept1 = departments[0];
            var dept1Object = departmentsViaComplexProperty[0];
            Assert.Equal("Computer Science", dept1.Name);
            Assert.Equal("Building A", dept1.Building);
            Assert.Equal("Computer Science", dept1Object.Name);
            Assert.Equal("Building A", dept1Object.Building);

            var department1Courses = dept1.Courses;
            Assert.Equal(2, department1Courses.Count);

            Assert.Equal("Data Structures", department1Courses[0].Name);
            Assert.Equal(3, department1Courses[0].Credits);
            Assert.Equal("Data Structures", department1Courses[0].Name);
            Assert.Equal(3, department1Courses[0].Credits);

            Assert.Equal("Algorithms", department1Courses[1].Name);
            Assert.Equal(4, department1Courses[1].Credits);
            Assert.Equal("Algorithms", department1Courses[1].Name);
            Assert.Equal(4, department1Courses[1].Credits);

            var dept2 = departments[1];
            Assert.Equal("Mathematics", dept2.Name);
            Assert.Equal("Building B", dept2.Building);
            Assert.Equal("Mathematics", dept2.Name);
            Assert.Equal("Building B", dept2.Building);

            var department2Courses = dept2.Courses;
            Assert.Equal(2, department2Courses.Count);
            Assert.Equal("Calculus I", department2Courses[0].Name);
            Assert.Equal(4, department2Courses[0].Credits);
            Assert.Equal("Linear Algebra", department2Courses[1].Name);
            Assert.Equal(3, department2Courses[1].Credits);
        }
        else
        {
            Assert.Equal("Test School", values["Name"]);
            var departments = (IList<Department>)values["Departments"]!;
            var departmentsViaComplexProperty = (IList<Department>)values[departmentsComplexProperty]!;

            Assert.Single(departments);
            Assert.Single(departmentsViaComplexProperty);
            var dept = departments[0];
            var deptViaComplexProperty = departmentsViaComplexProperty[0];
            Assert.Equal("Modified Department", dept.Name);
            Assert.Equal("Modified Building", dept.Building);
            Assert.Equal("Modified Department", deptViaComplexProperty.Name);
            Assert.Equal("Modified Building", deptViaComplexProperty.Building);

            Assert.Equal("Modified Department", dept.Name);
            Assert.Equal("Modified Building", dept.Building);

            var courses = dept.Courses;
            Assert.Equal(2, courses.Count);

            Assert.Equal("Modified Course 1", courses[0].Name);
            Assert.Equal(4, courses[0].Credits);
            Assert.Equal("Modified Course 1", courses[0].Name);
            Assert.Equal(4, courses[0].Credits);

            Assert.Equal("Modified Course 2", courses[1].Name);
            Assert.Equal(5, courses[1].Credits);
            Assert.Equal("Modified Course 2", courses[1].Name);
            Assert.Equal(5, courses[1].Credits);
        }
    }

    private async Task PortedStore_values_can_be_copied_to_object_using_ToObject_implementation(
        Func<EntityEntry, Task<PropertyValues>> getPropertyValues)
    {
        using var context = CreateContext();
        var building = context.Set<Building>().Single(b => b.Name == "Building One");

        building.Name = "Building One Prime";
        building.Value = 1500001m;
        context.Entry(building).Property("Shadow1").CurrentValue = 12;
        context.Entry(building).Property("Shadow2").CurrentValue = "Pine Walk";

        var values = await getPropertyValues(context.Entry(building));
        var copy = (Building)values.ToObject();

        Assert.Equal("Building One", copy.Name);
        Assert.Equal(1500000m, copy.Value);
        Assert.Equal(building.BuildingId, copy.BuildingId);
        Assert.True(copy.CreatedCalled);
        Assert.True(copy.InitializingCalled);
        Assert.True(copy.InitializedCalled);

        if (context.Model.FindEntityType(typeof(School)) != null)
        {
            var school = PortedCreateSchool();
            context.Set<School>().Attach(school);
            school.Name = "Modified School";
            school.Departments[0].Name = "Modified Department";
            school.Departments[0].Courses[0].Name = "Modified Course";
            school.Departments[0].Courses[0].Credits = 999;

            var schoolValues = await getPropertyValues(context.Entry(school));
            Assert.NotNull(schoolValues);
            var schoolCopy = (School)schoolValues.ToObject();

            Assert.Equal("Test School", schoolCopy.Name);
            Assert.Equal(school.Id, schoolCopy.Id);
            Assert.Equal(2, schoolCopy.Departments.Count);
            Assert.Equal("Computer Science", schoolCopy.Departments[0].Name);
            Assert.Equal(2, schoolCopy.Departments[0].Courses.Count);
            Assert.Equal("Data Structures", schoolCopy.Departments[0].Courses[0].Name);
            Assert.Equal(3, schoolCopy.Departments[0].Courses[0].Credits);
        }
    }

    private async Task PortedStore_values_can_be_cloned_implementation(
        Func<EntityEntry, Task<PropertyValues>> getPropertyValues)
    {
        using var context = CreateContext();
        var building = context.Set<Building>().Single(b => b.Name == "Building One");

        building.Name = "Building One Prime";
        building.Value = 1500001m;
        context.Entry(building).Property("Shadow1").CurrentValue = 12;
        context.Entry(building).Property("Shadow2").CurrentValue = "Pine Walk";

        var values = await getPropertyValues(context.Entry(building));
        var clone = values.Clone();

        Assert.NotSame(values, clone);
        Assert.Equal("Building One", clone["Name"]);
        Assert.Equal(1500000m, clone["Value"]);
        Assert.Equal(11, clone["Shadow1"]);
        Assert.Equal("Meadow Drive", clone["Shadow2"]);

        values["Name"] = "Modified";
        Assert.Equal("Building One", clone["Name"]);

        if (context.Model.FindEntityType(typeof(School)) != null)
        {
            //var school = context.Set<School>().First();

            // Complex collection query support. Issue #31411
            var school = PortedCreateSchool();
            context.Set<School>().Attach(school);
            school.Name = "Modified School";
            school.Departments[0].Name = "Modified Department";
            school.Departments[0].Courses[0].Name = "Modified Course";
            school.Departments[0].Courses[0].Credits = 999;

            var schoolValues = await getPropertyValues(context.Entry(school));
            var schoolClone = schoolValues.Clone();

            Assert.NotSame(schoolValues, schoolClone);
            Assert.Equal("Test School", schoolClone["Name"]);
            Assert.Equal(school.Id, schoolClone["Id"]);

            schoolValues["Name"] = "Further Modified";
            Assert.Equal("Test School", schoolClone["Name"]);

            // Ported: upstream reads this through schoolValues.Properties, which holds scalar properties only.
            var departmentsProperty = schoolValues.ComplexCollectionProperties.Single(p => p.Name == "Departments");
            var originalDepts = schoolValues[departmentsProperty];
            var clonedDepts = schoolClone[departmentsProperty];
            Assert.NotSame(originalDepts, clonedDepts);

            var storedSchool = (School)schoolClone.ToObject();
            Assert.Equal("Test School", storedSchool.Name);
            Assert.Equal(2, storedSchool.Departments.Count);
            Assert.Equal("Computer Science", storedSchool.Departments[0].Name);
            Assert.Equal("Data Structures", storedSchool.Departments[0].Courses[0].Name);
            Assert.Equal(3, storedSchool.Departments[0].Courses[0].Credits);

            schoolValues[departmentsProperty] = new List<Department>
            {
                new() { Name = "Replaced Department", Building = "Replaced Building", Courses = [] }
            };
            Assert.Equal("Computer Science", ((IList<Department>)schoolClone[departmentsProperty]!)[0].Name);
        }
    }

    private static School PortedCreateSchool()
        => new()
        {
            Id = 1,
            Name = "Test School",
            Departments =
            [
                new Department
                {
                    Name = "Computer Science",
                    Building = "Building A",
                    Courses =
                    [
                        new Course { Name = "Data Structures", Credits = 3 },
                        new Course { Name = "Algorithms", Credits = 4 }
                    ]
                },
                new Department
                {
                    Name = "Mathematics",
                    Building = "Building B",
                    Courses =
                    [
                        new Course { Name = "Calculus I", Credits = 4 },
                        new Course { Name = "Linear Algebra", Credits = 3 }
                    ]
                }
            ]
        };
}
