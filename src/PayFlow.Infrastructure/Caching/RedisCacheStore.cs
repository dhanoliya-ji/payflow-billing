using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PayFlow.Application.Abstractions;
using StackExchange.Redis;

namespace PayFlow.Infrastructure.Caching;

/// <summary>
/// Redis-backed cache.
/// <para>
/// Every operation swallows Redis failures and degrades to a miss. A cache is an
/// optimisation; a billing API that starts returning 500s because a cache node is
/// rebooting has turned an optimisation into a dependency.
/// </para>
/// </summary>
public sealed class RedisCacheStore(IConnectionMultiplexer redis, ILogger<RedisCacheStore> logger) : ICacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value.ToString(), SerializerOptions);
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            logger.LogWarning(ex, "Cache read failed for {Key}; treating it as a miss", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value, SerializerOptions), expiry);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Cache write failed for {Key}; continuing uncached", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Cache eviction failed for {Key}", key);
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan expiry,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await GetAsync<T>(key, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(cancellationToken);
        await SetAsync(key, value, expiry, cancellationToken);
        return value;
    }
}

/// <summary>
/// Process-local cache used when no Redis connection is configured, so the application
/// runs with one less moving part in development and in tests. Not shared between
/// instances, which is fine for the short-lived analytics snapshots it holds.
/// </summary>
public sealed class InMemoryCacheStore(IClock clock) : ICacheStore
{
    private readonly ConcurrentDictionary<string, (object? Value, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.Ordinal);

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > clock.UtcNow)
            {
                return Task.FromResult((T?)entry.Value);
            }

            _entries.TryRemove(key, out _);
        }

        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        _entries[key] = (value, clock.UtcNow + expiry);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan expiry,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await GetAsync<T>(key, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var value = await factory(cancellationToken);
        await SetAsync(key, value, expiry, cancellationToken);
        return value;
    }
}
