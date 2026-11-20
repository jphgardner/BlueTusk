using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace BlueTusk.Compatibility;

// Source-linked so compatibility helpers never become part of a package's public API.
internal static class BlueTuskGuard
{
    public static void NotNull([NotNull] object? value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value, parameterName);
#else
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }
#endif
    }

    public static void NotNullOrWhiteSpace([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NET8_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
#else
        NotNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", parameterName);
        }
#endif
    }

    public static void NotNullOrEmpty([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NET7_0_OR_GREATER
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
#else
        NotNull(value, parameterName);
        if (value.Length == 0)
        {
            throw new ArgumentException("The value cannot be an empty string.", parameterName);
        }
#endif
    }

    public static void NotDisposed([DoesNotReturnIf(true)] bool condition, object instance)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(condition, instance);
#else
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
#endif
    }

    public static void NotLessThan<T>(T value, T minimum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (Compare(value, minimum) < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be greater than or equal to {minimum}.");
        }
    }

    public static void NotGreaterThan<T>(T value, T maximum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (Compare(value, maximum) > 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be less than or equal to {maximum}.");
        }
    }

    public static void NotNegative<T>(T value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : struct, IComparable<T> =>
        NotLessThan(value, default, parameterName);

    public static void NotNegativeOrZero<T>(T value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : struct, IComparable<T> =>
        NotLessThanOrEqual(value, default, parameterName);

    public static void NotGreaterThanOrEqual<T>(T value, T maximum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (!IsNaN(value) && !IsNaN(maximum) && Compare(value, maximum) >= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be less than {maximum}.");
        }
    }

    public static void NotLessThanOrEqual<T>(T value, T minimum, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (!IsNaN(value) && !IsNaN(minimum) && Compare(value, minimum) <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be greater than {minimum}.");
        }
    }

    public static void NotZero<T>(T value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : struct, IComparable<T>
    {
        if (EqualityComparer<T>.Default.Equals(value, default))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be non-zero.");
        }
    }

    public static void Equal<T>(T value, T expected, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (IsNaN(value) || IsNaN(expected) || !EqualityComparer<T>.Default.Equals(value, expected))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be equal to {expected}.");
        }
    }

    private static bool IsNaN<T>(T value) => value is double d && double.IsNaN(d) || value is float f && float.IsNaN(f);

    private static int Compare<T>(T left, T right) where T : IComparable<T> =>
        IsNaN(left) || IsNaN(right) ? 0 : left.CompareTo(right);
}
