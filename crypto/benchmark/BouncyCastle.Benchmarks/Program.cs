using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Org.BouncyCastle.Crypto.Digests;

BenchmarkRunner.Run<Blake2bDigestBenchmark>();

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net60)]
[HtmlExporter]
public class Blake2bDigestBenchmark
{
    private readonly Blake2bDigest _blake2b = new Blake2bDigest();
    private byte[] _data = Array.Empty<byte>();

    [GlobalSetup]
    public void Setup()
    {
        Random rnd = new Random(42);
        _data = new byte[Size];
        rnd.NextBytes(_data);
    }

    [Params((int)3,
   (int)1E+2,  // 100 bytes
   (int)1E+3,  // 1 000 bytes = 1 KB
   (int)1E+4  // 10 000 bytes = 10 KB
   )]
    public int Size { get; set; }

    [Benchmark]
    public byte[] GetHash()
    {
        _blake2b.BlockUpdate(_data);
        var digest = new byte[64];
        _blake2b.DoFinal(digest);
        return digest;
    }
}