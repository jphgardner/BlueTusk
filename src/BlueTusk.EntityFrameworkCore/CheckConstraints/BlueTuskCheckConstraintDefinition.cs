namespace BlueTusk.EntityFrameworkCore.CheckConstraints;

/// <summary>A PostgreSQL table CHECK constraint discovered during database scaffolding.</summary>
public sealed record BlueTuskCheckConstraintDefinition(
    string Name,
    string Sql,
    bool IsNotValid,
    bool NoInherit,
    bool IsNotEnforced);
