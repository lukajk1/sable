using System.Diagnostics;

namespace Sable.Diagnostics;

/// <summary>Collects named timings for --bench and prints them as one table.</summary>
internal sealed class Benchmark
{
    private readonly List<(string Name, double Ms, string Note)> rows = new();

    public double Time(string name, Action action, string note = "")
    {
        var watch = Stopwatch.StartNew();
        action();
        double ms = watch.Elapsed.TotalMilliseconds;
        rows.Add((name, ms, note));
        return ms;
    }

    public void Add(string name, double ms, string note = "") => rows.Add((name, ms, note));

    public void Print(string header)
    {
        Console.WriteLine($"[bench] {header}");
        foreach (var (name, ms, note) in rows)
            Console.WriteLine($"[bench]   {name,-44} {ms,10:0.00} ms  {note}");
    }
}
