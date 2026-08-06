// Reflection-based test runner: discovers [Test] methods, runs them, prints a
// summary, and exits non-zero on any failure (so it can gate CI / mutation probes).

using System.Reflection;
using Coder.Tests;

var types = Assembly.GetExecutingAssembly().GetTypes();
int passed = 0, failed = 0;
var failures = new List<string>();

foreach (var type in types.OrderBy(t => t.Name))
{
    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
        .OrderBy(m => m.Name)
        .ToArray();
    if (methods.Length == 0) continue;

    Console.WriteLine($"== {type.Name} ==");
    object? instance = type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : null;

    foreach (var m in methods)
    {
        try
        {
            if (m.IsStatic) m.Invoke(null, null);
            else m.Invoke(instance, null);
            passed++;
            Console.WriteLine($"  PASS  {m.Name}");
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            failed++;
            Console.WriteLine($"  FAIL  {m.Name}: {inner.Message}");
            failures.Add($"{type.Name}.{m.Name}: {inner.Message}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  FAIL  {m.Name}: {ex.Message}");
            failures.Add($"{type.Name}.{m.Name}: {ex.Message}");
        }
    }
}

Console.WriteLine($"\n==== {passed} passed, {failed} failed, {passed + failed} total ====");
if (failed > 0)
{
    Console.WriteLine("Failures:");
    foreach (var f in failures) Console.WriteLine("  - " + f);
    Environment.Exit(1);
}
Environment.Exit(0);
