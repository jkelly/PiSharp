// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/testing/assertions.ts.
// Upstream adapts a Vitest/Jest `expect` (toBe, toEqual, toMatchObject, toBeTruthy, toBeGreaterThan, rejects.toThrow). .NET runners have no
// common expect, so the adapter takes the runner's failure instead: the exception an assertion throws. toEqual's structural equality is
// sequences element by element and Equals otherwise; toMatchObject compares the properties of the expected object, recursively.
using System.Collections;
using System.Reflection;

namespace PiSharp.Durable.Testing;

/// <summary>Builds <see cref="IConformanceAssertions"/> (upstream <c>createExpectAssertions</c>).</summary>
public static class ConformanceAssertions
{
    /// <summary>Assertions that fail by throwing <paramref name="failure"/>'s exception for the failure message.</summary>
    public static IConformanceAssertions Create(Func<string, Exception> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new Assertions(failure);
    }

    /// <summary>Whether two values are structurally equal: sequences (not strings) element by element, everything else by Equals.</summary>
    public static bool DeepEquals(object? actual, object? expected)
    {
        if (ReferenceEquals(actual, expected)) return true;
        if (actual is null || expected is null) return false;
        if (actual is not string && expected is not string && actual is IEnumerable left && expected is IEnumerable right)
        {
            var leftItems = left.Cast<object?>().ToList();
            var rightItems = right.Cast<object?>().ToList();
            return leftItems.Count == rightItems.Count && leftItems.Zip(rightItems).All(pair => DeepEquals(pair.First, pair.Second));
        }
        if (IsNumber(actual) && IsNumber(expected)) return Convert.ToDouble(actual, System.Globalization.CultureInfo.InvariantCulture) ==
            Convert.ToDouble(expected, System.Globalization.CultureInfo.InvariantCulture);
        return actual.Equals(expected);
    }

    /// <summary>Whether every public property of <paramref name="expected"/> deeply matches the property of the same name of
    /// <paramref name="actual"/>; nested anonymous objects match partially too.</summary>
    public static bool PartialDeepEquals(object? actual, object expected)
    {
        if (actual is null) return false;
        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var actualProperty = actual.GetType().GetProperty(property.Name, BindingFlags.Public | BindingFlags.Instance);
            if (actualProperty is null) return false;
            var want = property.GetValue(expected);
            var have = actualProperty.GetValue(actual);
            var matched = want is not null && IsAnonymous(want.GetType()) ? PartialDeepEquals(have, want) : DeepEquals(have, want);
            if (!matched) return false;
        }
        return true;
    }

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool IsAnonymous(Type type) => type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    private static string Show(object? value) => value switch
    {
        null => "null",
        string text => '"' + text + '"',
        IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Show)) + "]",
        _ => value.ToString() ?? "",
    };

    private sealed class Assertions(Func<string, Exception> failure) : IConformanceAssertions
    {
        private Exception Fail(string? message, string detail) => failure(message is null ? detail : $"{message}: {detail}");

        public void Ok(bool value, string? message = null)
        {
            if (!value) throw Fail(message, "expected a truthy value");
        }

        public void StrictEqual<T>(T actual, T expected, string? message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw Fail(message, $"expected <{Show(expected)}>, actual <{Show(actual)}>");
        }

        public void DeepEqual(object? actual, object? expected, string? message = null)
        {
            if (!DeepEquals(actual, expected)) throw Fail(message, $"expected <{Show(expected)}>, actual <{Show(actual)}>");
        }

        public void PartialDeepEqual(object? actual, object expected, string? message = null)
        {
            if (!PartialDeepEquals(actual, expected)) throw Fail(message, $"expected <{actual}> to match <{expected}>");
        }

        public void GreaterThan(double actual, double expected, string? message = null)
        {
            if (!(actual > expected)) throw Fail(message, $"expected {actual} to be greater than {expected}");
        }

        public async Task RejectsAsync(Func<Task> operation, string messageIncludes)
        {
            try { await operation().ConfigureAwait(false); }
            catch (Exception error)
            {
                if (!error.Message.Contains(messageIncludes, StringComparison.Ordinal))
                    throw Fail(null, $"expected a rejection containing \"{messageIncludes}\", got \"{error.Message}\"");
                return;
            }
            throw Fail(null, $"expected a rejection containing \"{messageIncludes}\"");
        }
    }
}
