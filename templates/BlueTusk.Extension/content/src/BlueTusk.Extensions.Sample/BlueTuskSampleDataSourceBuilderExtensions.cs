using BlueTusk.Data;

namespace BlueTusk.Extensions.Sample;

public static class BlueTuskSampleDataSourceBuilderExtensions
{
    public static BlueTuskDataSourceBuilder UseSample(
        this BlueTuskDataSourceBuilder builder,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UsePlugin(new SamplePlugin(schema));
    }
}
