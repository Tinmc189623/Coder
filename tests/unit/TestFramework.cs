// Minimal zero-dependency test framework.
// No NuGet packages: just a [Test] marker, an Assert surface, and a reflection
// runner (see Program.cs). Keeps the suite runnable fully offline against BCL.

using System.Reflection;

namespace Coder.Tests;

/// <summary>Marks a parameterless instance method as a test case.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

/// <summary>Thrown when an assertion fails; the runner reports the message.</summary>
public sealed class TestFailureException : Exception
{
    public TestFailureException(string message) : base(message) { }
}

public static class Assert
{
    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!Equals(expected, actual))
            throw new TestFailureException($"{message ?? "Assert.Equal failed"} — expected: {expected}, actual: {actual}");
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (Equals(notExpected, actual))
            throw new TestFailureException($"{message ?? "Assert.NotEqual failed"} — value was unexpectedly equal to: {actual}");
    }

    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new TestFailureException(message ?? "Assert.True failed — expected true");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new TestFailureException(message ?? "Assert.False failed — expected false");
    }

    public static void Contains(string haystack, string needle, string? message = null)
    {
        if (haystack == null || needle == null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new TestFailureException($"{message ?? "Assert.Contains failed"} — substring not found");
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    public static int CountOccurrences(string haystack, string needle)
    {
        if (haystack == null || needle == null || needle.Length == 0) return 0;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
