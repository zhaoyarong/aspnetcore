// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
public interface IDistributedCacheSerializerFactory
{
    /// <summary>TODO</summary>
    IDistributedCacheSerializer<T>? TryCreateSerializer<T>(IServiceProvider services) where T : class;
}

/// <summary>TODO</summary>
public interface IDistributedCacheSerializer<T> where T : class
{
    /// <summary>TODO</summary>
    void Serialize(T value, IBufferWriter<byte> destination);
    /// <summary>TODO</summary>
    T? Deserialize(in ReadOnlySequence<byte> source);
}

internal sealed class Utf8DistributedCacheSerializer : IDistributedCacheSerializer<string>
{
    public string? Deserialize(in ReadOnlySequence<byte> source) => Encoding.UTF8.GetString(in source);

    public void Serialize(string value, IBufferWriter<byte> destination) => Encoding.UTF8.GetBytes(value, destination);
}

[RequiresDynamicCode("..."), RequiresUnreferencedCode("...")]
internal sealed class SystemJsonDistributedCacheSerializer<T> : IDistributedCacheSerializer<T> where T : class
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

internal sealed class DistributedCache<T> : IDistributedCache<T> where T : class
{
    private readonly IDistributedCache _backend;
    private readonly IDistributedCacheSerializer<T> _serializer;
    private readonly ILogger<IDistributedCache> _logger;

    [RequiresDynamicCode("..."), RequiresUnreferencedCode("...")]
    public DistributedCache(IDistributedCache backend, IServiceProvider services, ILogger<IDistributedCache> logger)
    {
        _logger = logger;
        var serializer = services.GetService<IDistributedCacheSerializer<T>>();
        if (serializer is null)
        {
            // try all factories
            foreach (var factory in services.GetServices<IDistributedCacheSerializerFactory>())
            {
                serializer = factory.TryCreateSerializer<T>(services);
                if (serializer is null)
                {
                    logger.LogInformation($"DC {typeof(T).Name} factory {factory.GetType().Name} rejected type");
                }
                else
                {
                    logger.LogInformation($"DC {typeof(T).Name} factory {factory.GetType().Name} accepted type");
                    break;
                }
            }
        }

        _backend = backend;
        _serializer = serializer ?? CreateDefaultSerializer(logger);

        logger.LogInformation($"DC {typeof(T).Name} using {_serializer.GetType().Name}");
    }

    [RequiresDynamicCode("..."), RequiresUnreferencedCode("...")]
    private static IDistributedCacheSerializer<T> CreateDefaultSerializer(ILogger<IDistributedCache> logger)
    {
        logger.LogInformation($"DC {typeof(T).Name} using fallback");
        if (typeof(T) == typeof(string))
        {
            return (IDistributedCacheSerializer<T>)(object)new Utf8DistributedCacheSerializer();
        }
        return new SystemJsonDistributedCacheSerializer<T>();
    }

    public async ValueTask<T?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        try
        {
            var bytes = await _backend.GetAsync(key, token);
            if (bytes is null)
            {
                return default;
            }
            _logger.LogInformation($"{key} >> {bytes.Length} bytes");
            var ros = new ReadOnlySequence<byte>(bytes);

            return _serializer.Deserialize(in ros);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read cache");
            return null;
        }
    }

    public Task RefreshAsync(string key, CancellationToken token = default)
        => _backend.RefreshAsync(key, token);

    public Task RemoveAsync(string key, CancellationToken token = default)
        => _backend.RemoveAsync(key, token);

    public async Task SetAsync(string key, T value, DistributedCacheEntryOptions? options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            _serializer.Serialize(value, buffer);
            // default here consistent with the default from the similar extension method
            _logger.LogInformation($"{key} << {buffer.WrittenCount} bytes");
            await _backend.SetAsync(key, buffer.WrittenSpan.ToArray(), options ?? new DistributedCacheEntryOptions(), token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to write cache");
        }
    }
}
