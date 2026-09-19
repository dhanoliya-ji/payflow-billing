using System.Text.Json;
using StackExchange.Redis;

namespace PayFlow.Infrastructure;

public sealed class RedisCache(IConnectionMultiplexer redis) : ICache
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var value = await redis.GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString());
    }
    public Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default) =>
        redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), expiry);
}
