// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.OutputCaching.Benchmark;

[MemoryDiagnoser, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory), CategoriesColumn]
public class EndToEndBenchmarks
{
    [Params(10, 1000, (64 * 1024) + 17, (256 * 1024) + 17)]
    public int PayloadLength { get; set; } = 1024; // default for simple runs

    private byte[] _payloadOversized = Array.Empty<byte>();
    private string Key = "";
    private IOutputCacheStore _store = null!;

    private static readonly OutputCacheOptions _options = new();
    private static readonly Action _noop = () => { };

    private static readonly string[] _tags = Array.Empty<string>();
    private static HeaderDictionary _headers = null!;

    private ReadOnlyMemory<byte> Payload => new(_payloadOversized, 0, PayloadLength);

    [GlobalCleanup]
    public void Cleanup()
    {
        var arr = _payloadOversized;
        _payloadOversized = Array.Empty<byte>();
        if (arr.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(arr);
        }
        _store = null!;
        _headers = null!;
    }

    [GlobalSetup]
    public async Task InitAsync()
    {
        Key = Guid.NewGuid().ToString();
        _store = new DummyStore(Key);
        _payloadOversized = ArrayPool<byte>.Shared.Rent(PayloadLength);
        Random.Shared.NextBytes(_payloadOversized);
        // some random headers from ms.com
        _headers = new HeaderDictionary
        {
            ContentLength = PayloadLength,
            ["X-Rtag"] = "AEM_PROD_Marketing",
            ["X-Vhost"] = "publish_microsoft_s",
        };
        IHeaderDictionary headers = _headers;
        headers.ContentType = "text/html;charset=utf-8";
        headers.Vary = "Accept-Encoding";
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "SAMEORIGIN";
        headers.RequestId = Key;

        // store, fetch, validate (for each impl)
        await StreamSync();
        await ReadAsync(true);

        await StreamAsync();
        await ReadAsync(true);

        await WriterAsync();
        await ReadAsync(true);
    }

    static void WriteInRandomChunks(ReadOnlySpan<byte> value, Stream destination)
    {
        var rand = Random.Shared;
        while (!value.IsEmpty)
        {
            var bytes = Math.Min(rand.Next(4, 1024), value.Length);
            destination.Write(value.Slice(0, bytes));
            value = value.Slice(bytes);
        }
        destination.Flush();
    }

    static Task WriteInRandomChunks(ReadOnlyMemory<byte> source, PipeWriter destination, CancellationToken cancellationToken)
    {
        var value = source.Span;
        var rand = Random.Shared;
        while (!value.IsEmpty)
        {
            var bytes = Math.Min(rand.Next(4, 1024), value.Length);
            var span = destination.GetSpan(bytes);
            bytes = Math.Min(bytes, span.Length);
            value.Slice(0, bytes).CopyTo(span);
            destination.Advance(bytes);
            value = value.Slice(bytes);
        }
        return destination.FlushAsync(cancellationToken).AsTask();
    }

    static async Task WriteInRandomChunksAsync(ReadOnlyMemory<byte> value, Stream destination, CancellationToken cancellationToken)
    {
        var rand = Random.Shared;
        while (!value.IsEmpty)
        {
            var bytes = Math.Min(rand.Next(4, 1024), value.Length);
            await destination.WriteAsync(value.Slice(0, bytes), cancellationToken);
            value = value.Slice(bytes);
        }
        await destination.FlushAsync(cancellationToken);
    }

    [Benchmark(Description = "StreamSync"), BenchmarkCategory("Write")]
    public async Task StreamSync()
    {
        CachedResponseBody body;
        using (var oc = new OutputCacheStream(Stream.Null, _options.MaximumBodySize, StreamUtilities.BodySegmentSize, _noop))
        {
            WriteInRandomChunks(Payload.Span, oc);
            body = oc.GetCachedResponseBody();
        }
        var entry = new OutputCacheEntry
        {
            Created = DateTimeOffset.UtcNow,
            StatusCode = StatusCodes.Status200OK,
            Headers = _headers,
            Tags = _tags,
            Body = body,
        };
        await OutputCacheEntryFormatter.StoreAsync(Key, entry, _options.DefaultExpirationTimeSpan, _store, CancellationToken.None);
    }

    [Benchmark(Description = "StreamAsync"), BenchmarkCategory("Write")]
    public async Task StreamAsync()
    {
        CachedResponseBody body;
        using (var oc = new OutputCacheStream(Stream.Null, _options.MaximumBodySize, StreamUtilities.BodySegmentSize, _noop))
        {
            await WriteInRandomChunksAsync(Payload, oc, CancellationToken.None);
            body = oc.GetCachedResponseBody();
        }
        var entry = new OutputCacheEntry
        {
            Created = DateTimeOffset.UtcNow,
            StatusCode = StatusCodes.Status200OK,
            Headers = _headers,
            Tags = _tags,
            Body = body,
        };

        await OutputCacheEntryFormatter.StoreAsync(Key, entry, _options.DefaultExpirationTimeSpan, _store, CancellationToken.None);
    }

    [Benchmark(Description = "BodyWriter"), BenchmarkCategory("Write")]
    public async Task WriterAsync()
    {
        CachedResponseBody body;
        using (var oc = new OutputCacheStream(Stream.Null, _options.MaximumBodySize, StreamUtilities.BodySegmentSize, _noop))
        {
            var pipe = PipeWriter.Create(oc, new StreamPipeWriterOptions(leaveOpen: true));
            await WriteInRandomChunks(Payload, pipe, CancellationToken.None);
            body = oc.GetCachedResponseBody();
        }
        var entry = new OutputCacheEntry
        {
            Created = DateTimeOffset.UtcNow,
            StatusCode = StatusCodes.Status200OK,
            Headers = _headers,
            Tags = _tags,
            Body = body,
        };

        await OutputCacheEntryFormatter.StoreAsync(Key, entry, _options.DefaultExpirationTimeSpan, _store, CancellationToken.None);
    }

    [Benchmark, BenchmarkCategory("Read")]
    public Task ReadAsync() => ReadAsync(false);

    private async Task ReadAsync(bool validate)
    {
        static void ThrowNotFound() => throw new KeyNotFoundException();

        var entry = await OutputCacheEntryFormatter.GetAsync(Key, _store, CancellationToken.None);
        if (validate)
        {
            Validate(entry!);
        }
        if (entry is null)
        {
            ThrowNotFound();
        }
    }

    private void Validate(OutputCacheEntry value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var body = value.Body;
        if (body is null || body.Segments is null)
        {
            throw new InvalidOperationException("No segments");
        }
        var len = body.Segments.Sum(x => x.Length);
        if (len != PayloadLength || body.Length != PayloadLength)
        {
            throw new InvalidOperationException("Invalid payload length");
        }

        if (body.Segments.Count == 1)
        {
            if (!Payload.Span.SequenceEqual(body.Segments[0]))
            {
                throw new InvalidOperationException("Invalid payload");
            }
        }
        else
        {
            var oversized = ArrayPool<byte>.Shared.Rent(PayloadLength);
            int offset = 0;
            foreach (var segment in body.Segments)
            {
                segment.CopyTo(oversized, offset);
                offset += segment.Length;
            }
            if (!Payload.Span.SequenceEqual(new(oversized, 0, PayloadLength)))
            {
                throw new InvalidOperationException("Invalid payload");
            }

            ArrayPool<byte>.Shared.Return(oversized);
        }

        if (value.Headers is null)
        {
            throw new InvalidOperationException("Missing headers");
        }
        if (value.Headers.Count != _headers.Count)
        {
            throw new InvalidOperationException("Incorrect header count");
        }
        foreach (var header in _headers)
        {
            if (!value.Headers.TryGetValue(header.Key, out var vals) || vals != header.Value)
            {
                throw new InvalidOperationException("Invalid header: " + header.Key);
            }
        }
    }

    sealed class DummyStore : IOutputCacheStore
    {
        private readonly string _key;
        private byte[]? _payload;
        public DummyStore(string key) => _key = key;

        ValueTask IOutputCacheStore.EvictByTagAsync(string tag, CancellationToken cancellationToken) => default;

        ValueTask<byte[]?> IOutputCacheStore.GetAsync(string key, CancellationToken cancellationToken)
        {
            if (key != _key)
            {
                Throw();
            }
            return new(_payload);
        }

        ValueTask IOutputCacheStore.SetAsync(string key, byte[]? value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            if (key != _key)
            {
                Throw();
            }
            _payload = value;
            return default;
        }

        static void Throw() => throw new InvalidOperationException("Incorrect key");
    }

    internal sealed class NullPipeWriter : PipeWriter, IDisposable
    {
        public void Dispose()
        {
            var arr = _buffer;
            _buffer = null!;
            if (arr is not null)
            {
                ArrayPool<byte>.Shared.Return(arr);
            }
        }
        byte[] _buffer;
        public NullPipeWriter(int size) => _buffer = ArrayPool<byte>.Shared.Rent(size);
        public override void Advance(int bytes) { }
        public override Span<byte> GetSpan(int sizeHint = 0) => _buffer;
        public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer;
        public override void Complete(Exception? exception = null) { }
        public override void CancelPendingFlush() { }
        public override ValueTask CompleteAsync(Exception? exception = null) => default;
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => default;
    }
}
