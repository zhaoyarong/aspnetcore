Initial aim:

- make it easier to work with typed data in caches

Intended usage:

- make sure there is a backend distributed cache implementation
- take `IDistributedCache<T>` instead of `IDistributedCache` for your chosen `T`
- serialization of `T`:
  - optionally register some serializer factories (`IDistributedCacheSerializer`) which get a chance to claim all candidate types
  - optionally register some type-specific serializers (`IDistributedCacheSerializer<T>`) which always win over anything else
  - otherwise, the system-text json serializer is used (or UTF8 for `string`)

Key topics:

- is this configuration OK? rich enough?
- AOT: what's our story there? can we do anything interesting?
- API shape; naming is hard!

Additional things planned for consideration:

- look at the `IDistributedCache` API shape - currently `byte[]`-based
- - look at two-tier cache, for example redis "server-assisted client-side caching" (https://redis.io/docs/manual/client-side-caching/)

Full example (lines with ** are key):

``` c#
using Microsoft.Extensions.Caching.Distributed;
using ProtoBuf;
using System.Runtime.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

builder.Services.AddTypedCache(); // ** register typed distributed cache subsystem 
builder.Services.AddProtoBufNetTypedCache(); // ** register for [ProtoContract] cache types (would be in 3rd-party lib)
builder.Services.AddDataContractTypedCache(); // ** register for [DataContract] cache types

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();

app.MapGet("/jget", async (IDistributedCache<JsonModel> cache) => {
    var val = await cache.GetAsync("json");
    return $"cached value: {val?.Value}";
});
app.MapGet("/jset", async (IDistributedCache<JsonModel> cache) =>
{
    var guid = Guid.NewGuid();
    await cache.SetAsync("json", new JsonModel { Value = guid });
    return $"updated cached value to: {guid}";
});

app.MapGet("/pbget", async (IDistributedCache<ProtoBufModel> cache) => {
    var val = await cache.GetAsync("pbn");
    return $"cached value: {val?.Value}";
});
app.MapGet("/pbset", async (IDistributedCache<ProtoBufModel> cache) =>
{
    var guid = Guid.NewGuid();
    await cache.SetAsync("pbn", new ProtoBufModel { Value = guid });
    return $"updated cached value to: {guid}";
});

app.MapGet("/dcget", async (IDistributedCache<WcfModel> cache) => {
    var val = await cache.GetAsync("wcf");
    return $"cached value: {val?.Value}";
});
app.MapGet("/dcset", async (IDistributedCache<WcfModel> cache) =>
{
    var guid = Guid.NewGuid();
    await cache.SetAsync("wcf", new WcfModel { Value = guid });
    return $"updated cached value to: {guid}";
});

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();

// models, here showing json (fallback), protobuf-net and data-contract (WCF-style),
// all working in harmony

class JsonModel
{
    public required Guid Value { get; set; }
}

[ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
class ProtoBufModel
{
    [ProtoMember(1, DataFormat = DataFormat.FixedSize)]
    public required Guid Value { get; set; }
}

[DataContract]
class WcfModel
{
    [DataMember]
    public required Guid Value { get; set; }
}
```
