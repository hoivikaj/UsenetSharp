using System.Text;

namespace UsenetSharp.Benchmarks;

internal static class BenchmarkPayload
{
    public static readonly byte[] Decoded = CreateDecodedPayload(256 * 1024);
    public static readonly byte[] YencArticle = CreateYencArticle(Decoded);
    public static readonly byte[] Ybegin =
        "=ybegin part=2 total=10 line=128 size=262144 name=benchmark.bin"u8.ToArray();
    public static readonly byte[] Ypart = "=ypart begin=65537 end=131072"u8.ToArray();

    private static byte[] CreateDecodedPayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index * 31);
        }

        return payload;
    }

    private static byte[] CreateYencArticle(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.Length * 2);
        WriteAscii(stream, $"=ybegin line=128 size={payload.Length} name=benchmark.bin\r\n");

        var column = 0;
        foreach (var value in payload)
        {
            var encoded = unchecked((byte)(value + 42));
            var escaped = encoded is 0 or 10 or 13 or 61;
            var encodedLength = escaped ? 2 : 1;
            if (column + encodedLength > 128)
            {
                WriteAscii(stream, "\r\n");
                column = 0;
            }

            if (escaped)
            {
                stream.WriteByte((byte)'=');
                stream.WriteByte(unchecked((byte)(encoded + 64)));
            }
            else
            {
                stream.WriteByte(encoded);
            }

            column += encodedLength;
        }

        WriteAscii(stream, $"\r\n=yend size={payload.Length}\r\n");
        return stream.ToArray();
    }

    private static void WriteAscii(Stream stream, string value)
    {
        stream.Write(Encoding.ASCII.GetBytes(value));
    }
}
