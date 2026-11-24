using System.Diagnostics;
using System.Reflection;

namespace DeskGrid.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestFactAttribute : Attribute;

public static class AssertEx
{
    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(message ?? $"Expected <{expected}>, got <{actual}>.");
    }

    public static void Near(double expected, double actual, double tolerance = 1e-6, string? message = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(message ?? $"Expected <{expected}> (±{tolerance}), got <{actual}>.");
    }

    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
            throw new InvalidOperationException(message ?? "Expected condition to be false.");
    }
}

public static class Program
{
    public static int Main()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var testMethods = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<TestFactAttribute>() is not null)
            .OrderBy(m => m.DeclaringType?.Name)
            .ThenBy(m => m.Name)
            .ToList();

        var passed = 0;
        var failed = 0;
        var swTotal = Stopwatch.StartNew();

        foreach (var method in testMethods)
        {
            var fullName = $"{method.DeclaringType?.Name}.{method.Name}";
            var sw = Stopwatch.StartNew();
            try
            {
                var instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
                var result = method.Invoke(instance, null);
                if (result is Task task)
                    task.GetAwaiter().GetResult();

                sw.Stop();
                passed++;
                Console.WriteLine($"[PASS] {fullName} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (TargetInvocationException ex)
            {
                sw.Stop();
                failed++;
                Console.Error.WriteLine($"[FAIL] {fullName} ({sw.ElapsedMilliseconds}ms): {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                Console.Error.WriteLine($"[FAIL] {fullName} ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            }
        }

        swTotal.Stop();
        Console.WriteLine($"--- Test Summary: {passed} passed, {failed} failed ({swTotal.ElapsedMilliseconds}ms) ---");
        return failed == 0 ? 0 : 1;
    }
}
