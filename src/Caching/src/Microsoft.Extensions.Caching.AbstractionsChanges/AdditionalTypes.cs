// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Caching.Distributed;

/// <summary>TODO</summary>
public interface IDistributedCache<T> where T : class
{
    /// <summary>TODO</summary>
    ValueTask<T?> GetAsync(string key, CancellationToken token = default(CancellationToken));

    /// <summary>TODO</summary>
    Task SetAsync(string key, T value, DistributedCacheEntryOptions? options = null, CancellationToken token = default(CancellationToken));

    /// <summary>TODO</summary>
    Task RefreshAsync(string key, CancellationToken token = default(CancellationToken));

    /// <summary>TODO</summary>
    Task RemoveAsync(string key, CancellationToken token = default(CancellationToken));
}

/// <summary>TODO</summary>
public interface IDistributedCacheSerializer<T> where T : class
{
    /// <summary>TODO</summary>
    void Serialize(T value, IBufferWriter<byte> destination);
    /// <summary>TODO</summary>
    T? Deserialize(in ReadOnlySequence<byte> source);
}

/// <summary>TODO</summary>
public interface IDistributedCacheFactory
{
    /// <summary>TODO</summary>
    IDistributedCache<T> CreateDistributedCache<T>() where T : class;
}

internal sealed class DistributedCacheFactory : IDistributedCacheFactory
{
    private readonly IServiceProvider _services;
    public DistributedCacheFactory(IServiceProvider services)
    {
        _services = services;
    }

    public IDistributedCache<T> CreateDistributedCache<T>() where T : class
    {
        var backend = _services.GetRequiredService<IDistributedCache>();
        var serializer = _services.GetService<IDistributedCacheSerializer<T>>() ?? CreateDefaultSerializer<T>();
        return new DistributedCache<T>(backend, serializer);
    }

    private static IDistributedCacheSerializer<T> CreateDefaultSerializer<T>() where T : class
    {
        if (typeof(T) == typeof(string))
        {
            return (IDistributedCacheSerializer<T>)(object)new Utf8DistributedCacheSerializer();
        }
        // TODO: other special-cased primitives?
        // TODO: any rules on what types should be allowed by default?
        return new SystemJsonDistributedCacheSerializer<T>();
    }

    private sealed class Utf8DistributedCacheSerializer : IDistributedCacheSerializer<string>
    {
        public string? Deserialize(in ReadOnlySequence<byte> source) => Encoding.UTF8.GetString(in source);

        public void Serialize(string value, IBufferWriter<byte> destination) => Encoding.UTF8.GetBytes(value, destination);
    }

    private sealed class SystemJsonDistributedCacheSerializer<T> : IDistributedCacheSerializer<T> where T : class
    {
        private readonly JsonSerializerOptions? _options;
        public SystemJsonDistributedCacheSerializer(JsonSerializerOptions? options = null)
        {
            _options = options;
        }
        public T? Deserialize(in ReadOnlySequence<byte> source)
        {
            var reader = new Utf8JsonReader(source);
            return JsonSerializer.Deserialize<T>(ref reader, _options);
        }

        public void Serialize(T value, IBufferWriter<byte> destination)
        {
            using var writer = new Utf8JsonWriter(destination);
            JsonSerializer.Serialize<T>(writer, value, _options);
        }
    }
}

internal sealed class DistributedCache<T> : IDistributedCache<T> where T : class
{
    private readonly IDistributedCache _backend;
    private readonly IDistributedCacheSerializer<T> _serializer;

    public DistributedCache(IDistributedCache backend, IDistributedCacheSerializer<T> serializer)
    {
        _backend = backend;
        _serializer = serializer;
    }

    public async ValueTask<T?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var bytes = await _backend.GetAsync(key, token);
        if (bytes is null)
        {
            return default;
        }
        var ros = new ReadOnlySequence<byte>(bytes);
        return _serializer.Deserialize(in ros);
    }

    public Task RefreshAsync(string key, CancellationToken token = default)
        => _backend.RefreshAsync(key, token);

    public Task RemoveAsync(string key, CancellationToken token = default)
        => _backend.RemoveAsync(key, token);

    public Task SetAsync(string key, T value, DistributedCacheEntryOptions? options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var buffer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(value, buffer);
        // default here consistent with the default from the similar extension method
        return _backend.SetAsync(key, buffer.WrittenSpan.ToArray(), options ?? new DistributedCacheEntryOptions(), token);
    }
}
