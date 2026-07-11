using BenchmarkDotNet.Attributes;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace UsenetSharp.Benchmarks;

[MemoryDiagnoser]
public class DecodingBenchmarks
{
    [Benchmark]
    public async Task YencStreamReadAsync()
    {
        using var source = new MemoryStream(BenchmarkPayload.YencArticle, writable: false);
        using var yenc = new YencStream(source);
        await yenc.CopyToAsync(Stream.Null);
    }

    [Benchmark]
    public UsenetYencHeader ParseYencHeaders()
    {
        return YencStream.ParseYencHeaders(BenchmarkPayload.Ybegin, BenchmarkPayload.Ypart);
    }
}
