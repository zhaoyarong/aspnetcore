// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class DistributedCacheExtensions
{
    public static void AddTypedCache(this IServiceCollection services)
    {
        services.TryAdd(ServiceDescriptor.Singleton<IDistributedCacheSerializer<string>>(new Utf8DistributedCacheSerializer()));
        services.TryAdd(ServiceDescriptor.Singleton(typeof(IDistributedCache<>), typeof(DistributedCache<>)));
    }

    [RequiresUnreferencedCode("..."), RequiresDynamicCode("...")]
    public static void AddDataContractTypedCache(this IServiceCollection services)
    {
        AddTypedCache(services);
        services.Add(ServiceDescriptor.Singleton<IDistributedCacheSerializerFactory, DataContractDistributedCacheSerializerFactory>());
    }
}

[RequiresUnreferencedCode("..."), RequiresDynamicCode("...")]
internal sealed class DataContractDistributedCacheSerializerFactory : IDistributedCacheSerializerFactory
{
    public IDistributedCacheSerializer<T>? TryCreateSerializer<T>(IServiceProvider services) where T : class
    {
        if (Attribute.IsDefined(typeof(T), typeof(DataContractAttribute)))
        {
            return new DataContractDistributedCacheSerializer<T>();
        }
        return null;
    }

    [RequiresUnreferencedCode("..."), RequiresDynamicCode("...")]
    private sealed class DataContractDistributedCacheSerializer<T> : IDistributedCacheSerializer<T> where T : class
    {
        private readonly DataContractSerializer _serializer = new(typeof(T));

        public T? Deserialize(in ReadOnlySequence<byte> source)
        {
            Stream s;
            byte[]? leased = null;
            if (source.IsEmpty)
            {
                s = Stream.Null;
            }
            else if (source.IsSingleSegment && MemoryMarshal.TryGetArray(source.First, out var segment))
            {
                s = new MemoryStream(segment.Array!, segment.Offset, segment.Count);
            }
            else
            {
                var len = checked((int)source.Length);
                leased = ArrayPool<byte>.Shared.Rent(len);
                source.CopyTo(leased);
                s = new MemoryStream(leased, 0, len);
            }
            var result = (T?)_serializer.ReadObject(s);
            if (leased is not null)
            {
                ArrayPool<byte>.Shared.Return(leased);
            }
            s.Dispose();
            return result;
        }

        public void Serialize(T value, IBufferWriter<byte> destination)
        {
            using var ms = new MemoryStream(); // TODO - custom pass-thru stream
            _serializer.WriteObject(ms, value);
            var payload = new ReadOnlySpan<byte>(ms.GetBuffer(), 0, checked((int)ms.Length));
            destination.Write(payload);
        }
    }
}

// these would be in protobuf-net
//public static class ProtoBufNetDistributedCacheExtensions
//{
//    public static void AddProtoBufNetTypedCache(this IServiceCollection services)
//    {
//        DistributedCacheExtensions.AddTypedCache(services);
//        services.Add(ServiceDescriptor.Singleton<IDistributedCacheSerializerFactory, ProtoBufNetDistributedCacheSerializerFactory>());
//    }
//}

//internal sealed class ProtoBufNetDistributedCacheSerializerFactory : IDistributedCacheSerializerFactory
//{
//    public IDistributedCacheSerializer<T>? TryCreateSerializer<T>(IServiceProvider services) where T : class
//    {
//        if (Attribute.IsDefined(typeof(T), typeof(ProtoContractAttribute)))
//        {
//            return new ProtoBufNetDistributedCacheSerializer<T>();
//        }
//        return null;
//    }

//    private sealed class ProtoBufNetDistributedCacheSerializer<T> : IDistributedCacheSerializer<T> where T : class
//    {
//        public T? Deserialize(in ReadOnlySequence<byte> source) => Serializer.Deserialize<T>(source);

//        public void Serialize(T value, IBufferWriter<byte> destination) => Serializer.Serialize<T>(destination, value);
//    }
//}
