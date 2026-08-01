using BlueTusk.EntityFrameworkCore.Graphs.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#pragma warning disable EF1001 // Provider design-time code consumes provider infrastructure metadata.

namespace BlueTusk.EntityFrameworkCore.Design.Internal;

internal sealed class BlueTuskAnnotationCodeGenerator(
    AnnotationCodeGeneratorDependencies dependencies)
    : AnnotationCodeGenerator(dependencies)
{
    protected override MethodCallCodeFragment? GenerateFluentApi(
        IModel model,
        IAnnotation annotation)
    {
        if (annotation.Name == BlueTuskPropertyGraphMetadata.AnnotationName &&
            annotation.Value is string serializedDefinitions)
        {
            return new MethodCallCodeFragment(
                nameof(BlueTuskPropertyGraphModelBuilderExtensions.HasBlueTuskPropertyGraphs),
                serializedDefinitions);
        }

        return base.GenerateFluentApi(model, annotation);
    }
}

#pragma warning restore EF1001
