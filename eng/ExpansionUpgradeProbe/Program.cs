using BlueTusk.UpgradeProbe;

// This source is compiled twice per family, once against each immutable source tree. The
// processes share only PostgreSQL (and, for Edge, one SQLite file) plus small state files.
// Each phase writes its report only after every assertion passed; any failure exits non-zero.
if (args.Length != 4 || args[0] is not ("seed" or "upgrade" or "rollback"))
{
    throw new ArgumentException("Expected phase (seed, upgrade or rollback), schema base, state path and report path.");
}

await using var context = new ProbeContext(args[0], args[1], args[2], args[3]);
await FamilyProbe.RunAsync(context);
await context.CompleteAsync();
