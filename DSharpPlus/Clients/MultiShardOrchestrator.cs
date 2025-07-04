using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using DSharpPlus.Entities;
using DSharpPlus.Net;
using DSharpPlus.Net.Abstractions;
using DSharpPlus.Net.Gateway;
using DSharpPlus.Net.Gateway.Compression;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DSharpPlus.Clients;

/// <summary>
/// Orchestrates multiple shards within this process.
/// </summary>
public sealed class MultiShardOrchestrator : IShardOrchestrator
{
    private IGatewayClient[]? shards;
    private readonly DiscordRestApiClient apiClient;
    private readonly ShardingOptions options;
    private readonly IServiceProvider serviceProvider;
    private readonly IPayloadDecompressor decompressor;

    private uint shardCount;
    private uint stride;
    private uint totalShards;

    /// <inheritdoc/>
    public bool AllShardsConnected => this.shards?.All(shard => shard.IsConnected) == true;

    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    /// <remarks>
    /// This value may be inaccurate before startup. It is guaranteed to be correct by the time the first SessionCreated event
    /// is fired.
    /// </remarks>
    public int TotalShardCount => (int)this.totalShards;

    /// <summary>
    /// <inheritdoc/>
    /// </summary>
    /// <remarks>
    /// This value may be inaccurate before startup. It is guaranteed to be correct by the time the first SessionCreated event
    /// is fired.
    /// </remarks>
    public int ConnectedShardCount => (int)this.shardCount;

    public MultiShardOrchestrator
    (
        IServiceProvider serviceProvider, 
        IOptions<ShardingOptions> options,
        DiscordRestApiClientFactory apiClientFactory,
        IPayloadDecompressor decompressor
    )
    {
        this.apiClient = apiClientFactory.GetCurrentApplicationClient();
        this.options = options.Value;
        this.serviceProvider = serviceProvider;
        this.decompressor = decompressor;
    }

    /// <inheritdoc/>
    public async ValueTask StartAsync(DiscordActivity? activity, DiscordUserStatus? status, DateTimeOffset? idleSince)
    {
        uint startShards, totalShards, stride;
        GatewayInfo info = await this.apiClient.GetGatewayInfoAsync();

        if (this.options.ShardCount is null)
        {
            startShards = totalShards = (uint)info.ShardCount;
            stride = 0;
        }
        else
        {
            totalShards = this.options.TotalShards == 0 ? this.options.ShardCount.Value : this.options.TotalShards;
            startShards = this.options.ShardCount.Value;
            stride = this.options.Stride;

            if (stride != 0 && totalShards == 0)
            {
                throw new ArgumentOutOfRangeException
                (
                    paramName: "options",
                    message: "The sharded client was set up for multi-process sharding but did not specify a total shard count."
                );
            }
        }

        this.stride = stride;
        this.shardCount = startShards;
        this.totalShards = totalShards;

        QueryUriBuilder gwuri = new(info.Url);

        gwuri.AddParameter("v", "10")
             .AddParameter("encoding", "json");

        if (this.decompressor.IsTransportCompression)
        {
            gwuri.AddParameter("compress", this.decompressor.Name);
        }

        this.shards = new IGatewayClient[startShards];

        // create all shard instances before starting any of them
        for (int i = 0; i < startShards; i++)
        {
            this.shards[i] = this.serviceProvider.GetRequiredService<IGatewayClient>();
        }

        for (int i = 0; i < startShards; i += info.SessionBucket.MaxConcurrency)
        {
            DateTimeOffset startTime = DateTimeOffset.UtcNow;

            for (int j = i; j < i + info.SessionBucket.MaxConcurrency && j < startShards; j++)
            {
                await this.shards[j].ConnectAsync
                (
                    gwuri.Build(),
                    activity,
                    status,
                    idleSince,
                    new ShardInfo
                    {
                        ShardCount = (int)totalShards,
                        ShardId = (int)stride + j
                    }
                );
            }

            TimeSpan diff = DateTimeOffset.UtcNow - startTime;

            if (diff < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromSeconds(5) - diff);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask StopAsync()
    {
        foreach (IGatewayClient client in this.shards)
        {
            await client.DisconnectAsync();
        }
    }

    /// <inheritdoc/>
    public bool IsConnected(int shardId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardId, (int)this.stride);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shardId, (int)this.stride + this.shardCount);

        return this.shards[shardId - this.stride].IsConnected;
    }

    /// <inheritdoc/>
    public bool IsConnected(ulong guildId)
    {
        uint shardId = GetShardIdForGuildId(guildId);
        return IsConnected(shardId);
    }

    /// <inheritdoc/>
    public TimeSpan GetConnectionLatency(int shardId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardId, (int)this.stride);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shardId, (int)this.stride + this.shardCount);

        return this.shards[shardId - this.stride].Ping;
    }

    /// <inheritdoc/>
    public TimeSpan GetConnectionLatency(ulong guildId)
    {
        uint shardId = GetShardIdForGuildId(guildId);
        return GetConnectionLatency(shardId);
    }

    private uint GetShardIdForGuildId(ulong guildId)
        => (uint)((guildId >> 22) % this.totalShards);

    /// <inheritdoc/>
    public async ValueTask ReconnectAsync()
    {
        // don't parallelize this, we can't start shards too wildly out of order
        foreach(IGatewayClient shard in this.shards)
        {
            await shard.ReconnectAsync();
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendOutboundEventAsync(byte[] payload, ulong guildId)
    {
        if (guildId == 0)
        {
            await this.shards[0].WriteAsync(payload);
        }

        uint shardId = GetShardIdForGuildId(guildId);

        ArgumentOutOfRangeException.ThrowIfLessThan(shardId, this.stride);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shardId, this.stride + this.shardCount);

        await this.shards[shardId].WriteAsync(payload);
    }

    /// <inheritdoc/>
    public async ValueTask BroadcastOutboundEventAsync(byte[] payload)
    {
        if (!this.AllShardsConnected)
        {
            throw new InvalidOperationException("Broadcast is only possible when all shards are connected");
        }

        await Parallel.ForEachAsync(this.shards, async (shard, _) => await shard.WriteAsync(payload));
    }

    /// <inheritdoc/>
    public IEnumerable<int> GetShardIds()
    {
        if (this.stride == 0 || this.totalShards == 0)
        {
            // No striding, use linear IDs
            uint count = this.shardCount == default ? 1 : this.shardCount;
            for (int i = 0; i < count; i++)
            {
                yield return i;
            }
        }
        else
        {
            // Strided IDs
            uint count = this.shardCount == default ? 1 : this.shardCount;
            for (int i = 0; i < count; i++)
            {
                int shardId = (int)(i * this.stride);
                if (shardId < this.totalShards)
                {
                    yield return shardId;
                }
            }
        }
    }
}
