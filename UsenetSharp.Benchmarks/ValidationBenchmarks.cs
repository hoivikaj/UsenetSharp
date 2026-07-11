using BenchmarkDotNet.Attributes;
using UsenetSharp.Models;

namespace UsenetSharp.Benchmarks;

[MemoryDiagnoser]
public class ValidationBenchmarks
{
    private readonly SegmentId _segmentId = new("benchmark-article@example.invalid");

    [Benchmark(Baseline = true)]
    public bool LegacyStringAndLinq()
    {
        var value = _segmentId.ToString();
        return value.Length is >= 3 and <= 497 &&
               value[0] != '@' &&
               value[^1] != '@' &&
               value.Contains('@') &&
               !value.Contains('<') &&
               !value.Contains('>') &&
               !value.Any(char.IsWhiteSpace) &&
               !value.Any(char.IsControl);
    }

    [Benchmark]
    public bool SpanSinglePass()
    {
        var value = _segmentId.Value;
        if (value.Length is < 3 or > 497 ||
            value[0] == '@' ||
            value[^1] == '@' ||
            !value.Contains('@') ||
            value.Contains('<') ||
            value.Contains('>'))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
