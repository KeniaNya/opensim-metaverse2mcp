using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const int SignalsDiscoveryChannel = -44448888;
    private const string SignalsProtocolVersion = "1";
    private const string SignalsIahPrefix = "_SIGNALS_:";
    private static readonly TimeSpan SignalsCacheMaxAge = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan SignalsCachePruneInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SignalsChatDescriptionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SignalsHttpDescriptionTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<UUID, SignalDiscoveryCacheEntry> _signalsDiscoveryBySender = new();
    private readonly ConcurrentDictionary<UUID, PendingSignalsDescriptionRequest> _pendingSignalsDescriptionBySender = new();
    private readonly HttpClient _signalsHttpClient = new();
    private readonly object _signalsDiscoveryScheduleLock = new();
    private readonly Random _signalsRandom = new();
    private int _signalsMaintenanceLoopStarted;
    private DateTimeOffset _signalsNextDiscoveryBroadcastUtc = DateTimeOffset.MinValue;
    private ulong _signalsDiscoveryRegionHandle;

    private void StartSignalsMaintenanceLoop()
    {
        if (Interlocked.Exchange(ref _signalsMaintenanceLoopStarted, 1) == 1)
        {
            return;
        }

        _signalsHttpClient.Timeout = SignalsHttpDescriptionTimeout;
        ResetSignalsDiscoverySchedule(clearCache: false);
        _ = Task.Run(() => RunSignalsMaintenanceLoopAsync(_lifecycleCts.Token));
    }

    private async Task RunSignalsMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PruneSignalsCache(DateTimeOffset.UtcNow);
            TryBroadcastSignalsDiscoveryAyt();

            try
            {
                await Task.Delay(SignalsCachePruneInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void PruneSignalsCache(DateTimeOffset nowUtc)
    {
        foreach (var pair in _signalsDiscoveryBySender)
        {
            if (nowUtc - pair.Value.LastHeardUtc > SignalsCacheMaxAge)
            {
                _signalsDiscoveryBySender.TryRemove(pair.Key, out _);
            }
        }
    }

    private void ResetSignalsDiscoverySchedule(bool clearCache)
    {
        if (clearCache)
        {
            _signalsDiscoveryBySender.Clear();
        }

        lock (_signalsDiscoveryScheduleLock)
        {
            _signalsDiscoveryRegionHandle = 0UL;
            _signalsNextDiscoveryBroadcastUtc = DateTimeOffset.UtcNow;
        }
    }

    private void NotifySignalsRegionChanged()
    {
        ResetSignalsDiscoverySchedule(clearCache: true);
        TryBroadcastSignalsDiscoveryAyt();
    }

    private void TryBroadcastSignalsDiscoveryAyt()
    {
        var client = _client;
        if (!_connected || client == null)
        {
            return;
        }

        var currentSim = client.Network.CurrentSim;
        if (currentSim == null)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var shouldBroadcast = false;
        lock (_signalsDiscoveryScheduleLock)
        {
            if (_signalsDiscoveryRegionHandle != currentSim.Handle)
            {
                shouldBroadcast = true;
            }
            else if (nowUtc >= _signalsNextDiscoveryBroadcastUtc)
            {
                shouldBroadcast = true;
            }

            if (shouldBroadcast)
            {
                _signalsDiscoveryRegionHandle = currentSim.Handle;
                _signalsNextDiscoveryBroadcastUtc = nowUtc + ComputeNextSignalsDiscoveryBroadcastDelay();
            }
        }

        if (!shouldBroadcast)
        {
            return;
        }

        var payload = $"{SignalsProtocolVersion}|AYT";
        try
        {
            client.Self.Chat(payload, SignalsDiscoveryChannel, ChatType.Normal);
            Console.WriteLine($"[signals] sent AYT discovery broadcast on channel {SignalsDiscoveryChannel}: {payload}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[signals] failed to send AYT discovery broadcast: {ex.Message}");
        }
    }

    private TimeSpan ComputeNextSignalsDiscoveryBroadcastDelay()
    {
        var maxAgeSeconds = SignalsCacheMaxAge.TotalSeconds;
        var adjustmentWindowSeconds = maxAgeSeconds * 0.15;
        var floorSeconds = maxAgeSeconds - adjustmentWindowSeconds;
        double randomizedReduction;
        lock (_signalsDiscoveryScheduleLock)
        {
            randomizedReduction = _signalsRandom.NextDouble() * adjustmentWindowSeconds;
        }

        return TimeSpan.FromSeconds(Math.Max(5d, floorSeconds - randomizedReduction));
    }

    private void TryProcessSignalsIngress(GridClient client, ChatEventArgs e, string text)
    {
        if (e.SourceType != ChatSourceType.Object || e.SourceID == UUID.Zero)
        {
            return;
        }

        // Chat-based description fetch waits for the next object response from the requested sender.
        if (_pendingSignalsDescriptionBySender.TryGetValue(e.SourceID, out var pending))
        {
            pending.Response.TrySetResult(text);
        }
    }

    private bool TryProcessSignalsInstantIngress(GridClient client, UUID sourceId, string fromName, Vector3 position, string text)
    {
        if (sourceId == UUID.Zero || sourceId == client.Self.AgentID)
        {
            return false;
        }

        if (!text.StartsWith(SignalsIahPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseSignalsPrefixedIahPayload(text, out var protocolVersion, out var uri))
        {
            Console.WriteLine($"[signals] ignored malformed prefixed IAH IM from {fromName} [{sourceId}].");
            return true;
        }

        UpsertSignalsDiscoveryEntry(
            client,
            sourceId,
            UUID.Zero,
            fromName,
            position,
            client.Network.CurrentSim,
            protocolVersion,
            uri);

        Console.WriteLine($"[signals] received prefixed IAH IM from {fromName} [{sourceId}] uri={uri}");
        return true;
    }

    private void UpsertSignalsDiscoveryEntry(
        GridClient client,
        UUID sourceId,
        UUID ownerId,
        string fromName,
        Vector3 position,
        Simulator? simulator,
        string protocolVersion,
        string uri)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var objectName = fromName;
        var objectDescription = string.Empty;
        var resolvedOwnerId = ownerId;
        var resolvedPosition = position;
        var regionName = simulator?.Name ?? client.Network.CurrentSim?.Name ?? string.Empty;
        var regionHandle = simulator?.Handle ?? client.Network.CurrentSim?.Handle ?? 0UL;

        var matched = FindPrimInKnownSims(client, sourceId);
        if (matched != null)
        {
            var (sim, prim) = matched.Value;
            var properties = prim.Properties;
            objectName = !string.IsNullOrWhiteSpace(properties?.Name) ? properties.Name : objectName;
            objectDescription = properties?.Description ?? objectDescription;
            resolvedOwnerId = properties != null && properties.OwnerID != UUID.Zero
                ? properties.OwnerID
                : (prim.OwnerID != UUID.Zero ? prim.OwnerID : resolvedOwnerId);
            resolvedPosition = prim.Position;
            regionName = sim.Name;
            regionHandle = sim.Handle;
        }

        _signalsDiscoveryBySender.AddOrUpdate(
            sourceId,
            _ => new SignalDiscoveryCacheEntry
            {
                SenderId = sourceId,
                ProtocolVersion = protocolVersion,
                Uri = uri,
                LastHeardUtc = nowUtc,
                RegionName = regionName,
                RegionHandle = regionHandle,
                Position = resolvedPosition,
                OwnerId = resolvedOwnerId,
                ObjectName = objectName,
                ObjectDescription = objectDescription
            },
            (_, existing) =>
            {
                existing.ProtocolVersion = protocolVersion;
                existing.Uri = uri;
                existing.LastHeardUtc = nowUtc;
                existing.RegionName = regionName;
                existing.RegionHandle = regionHandle;
                existing.Position = resolvedPosition;
                existing.OwnerId = resolvedOwnerId;
                existing.ObjectName = objectName;
                existing.ObjectDescription = objectDescription;
                return existing;
            });
    }

    private static (Simulator Sim, Primitive Prim)? FindPrimInKnownSims(GridClient client, UUID objectId)
    {
        var sims = client.Network.Simulators.ToArray();
        foreach (var sim in sims)
        {
            var prim = sim.ObjectsPrimitives.Values.FirstOrDefault(candidate => candidate.ID == objectId);
            if (prim != null)
            {
                return (sim, prim);
            }
        }

        return null;
    }

    private static bool TryParseSignalsIahPayload(string text, out string protocolVersion, out string uri)
    {
        protocolVersion = string.Empty;
        uri = string.Empty;

        var raw = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var parsedUri))
        {
            return false;
        }

        var scheme = parsedUri.Scheme.ToLowerInvariant();
        if (scheme != "http" && scheme != "https" && scheme != "chat")
        {
            return false;
        }

        if (scheme == "chat")
        {
            var chatChannelText = parsedUri.Host;
            if (string.IsNullOrWhiteSpace(chatChannelText)
                || !int.TryParse(chatChannelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        protocolVersion = parts[0].Trim();
        uri = parts[1].Trim();
        return true;
    }

    private static bool TryParseSignalsPrefixedIahPayload(string text, out string protocolVersion, out string uri)
    {
        protocolVersion = string.Empty;
        uri = string.Empty;

        var raw = text?.Trim() ?? string.Empty;
        if (!raw.StartsWith(SignalsIahPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = raw.Substring(SignalsIahPrefix.Length).Trim();
        return TryParseSignalsIahPayload(payload, out protocolVersion, out uri);
    }

    public async Task<DataToolResult> SignalsNearbyAsync(string? agentUuid, float? radius, CancellationToken cancellationToken)
    {
        UUID? ownerFilter = null;
        if (!string.IsNullOrWhiteSpace(agentUuid))
        {
            if (!UUID.TryParse(agentUuid.Trim(), out var parsedOwner) || parsedOwner == UUID.Zero)
            {
                return DataToolResult.FailResult("agentUuid must be a valid non-zero UUID when provided.");
            }

            ownerFilter = parsedOwner;
        }

        float? radiusFilter = null;
        if (radius.HasValue)
        {
            if (radius.Value <= 0f)
            {
                return DataToolResult.FailResult("radius must be greater than 0 when provided.");
            }

            radiusFilter = Math.Clamp(radius.Value, 0.1f, 4096f);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            PruneSignalsCache(DateTimeOffset.UtcNow);

            var currentSim = client.Network.CurrentSim;
            if (currentSim == null)
            {
                return Task.FromResult(DataToolResult.FailResult("No current simulator available."));
            }

            var botPosition = client.Self.SimPosition;
            var rows = _signalsDiscoveryBySender.Values
                .Where(entry => entry.RegionHandle != 0UL
                    ? entry.RegionHandle == currentSim.Handle
                    : string.Equals(entry.RegionName, currentSim.Name, StringComparison.OrdinalIgnoreCase))
                .Where(entry => !ownerFilter.HasValue || entry.OwnerId == ownerFilter.Value)
                .Select(entry =>
                {
                    var distance = Vector3.Distance(botPosition, entry.Position);
                    return new SignalNearbyObjectInfo(
                        SenderId: entry.SenderId.ToString(),
                        ProtocolVersion: entry.ProtocolVersion,
                        Uri: entry.Uri,
                        ObjectName: entry.ObjectName,
                        ObjectDescription: entry.ObjectDescription,
                        OwnerId: entry.OwnerId == UUID.Zero ? null : entry.OwnerId.ToString(),
                        RegionName: entry.RegionName,
                        RegionHandle: entry.RegionHandle,
                        PositionX: entry.Position.X,
                        PositionY: entry.Position.Y,
                        PositionZ: entry.Position.Z,
                        DistanceMeters: distance,
                        LastHeardUtc: entry.LastHeardUtc.UtcDateTime.ToString("O"));
                })
                .Where(entry => !radiusFilter.HasValue || entry.DistanceMeters <= radiusFilter.Value)
                .OrderBy(entry => entry.DistanceMeters)
                .ThenBy(entry => entry.ObjectName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SenderId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var payload = new
            {
                simulator = currentSim.Name,
                discoveryChannel = SignalsDiscoveryChannel,
                cacheMaxAgeSeconds = (int)SignalsCacheMaxAge.TotalSeconds,
                filters = new
                {
                    agentUuid = ownerFilter?.ToString(),
                    radius = radiusFilter
                },
                returned = rows.Length,
                objects = rows
            };

            var message = rows.Length == 0
                ? "No signal-capable objects matched the requested filters in the current simulator."
                : $"Found {rows.Length} signal-capable object(s).";

            return Task.FromResult(DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> SignalsDescriptionAsync(string objectSenderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objectSenderId))
        {
            return DataToolResult.FailResult("objectSenderId must be a valid non-zero UUID.");
        }

        if (!UUID.TryParse(objectSenderId.Trim(), out var senderId) || senderId == UUID.Zero)
        {
            return DataToolResult.FailResult("objectSenderId must be a valid non-zero UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            PruneSignalsCache(DateTimeOffset.UtcNow);

            if (!_signalsDiscoveryBySender.TryGetValue(senderId, out var cached))
            {
                return DataToolResult.FailResult("No cached Signals discovery entry found for objectSenderId. Call signals_nearby first.");
            }

            if (!Uri.TryCreate(cached.Uri, UriKind.Absolute, out var uri))
            {
                return DataToolResult.FailResult("Cached signal URI is invalid.");
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme == "http" || scheme == "https")
            {
                return await FetchSignalsDescriptionOverHttpAsync(senderId, cached, uri, token).ConfigureAwait(false);
            }

            if (scheme == "chat")
            {
                return await FetchSignalsDescriptionOverChatAsync(client, senderId, cached, uri, token).ConfigureAwait(false);
            }

            return DataToolResult.FailResult($"Unsupported signals URI scheme '{uri.Scheme}'.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DataToolResult> FetchSignalsDescriptionOverHttpAsync(
        UUID senderId,
        SignalDiscoveryCacheEntry cached,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(SignalsHttpDescriptionTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _signalsHttpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return DataToolResult.FailResult($"HTTP request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            var payload = new
            {
                senderId = senderId.ToString(),
                protocolVersion = cached.ProtocolVersion,
                uri = cached.Uri,
                transport = uri.Scheme.ToLowerInvariant(),
                contentType = response.Content.Headers.ContentType?.ToString(),
                fetchedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                lastHeardUtc = cached.LastHeardUtc.UtcDateTime.ToString("O"),
                content
            };

            return DataToolResult.OkResult("Retrieved Signals description over HTTP.", JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DataToolResult.FailResult("Timed out while retrieving Signals description over HTTP.");
        }
    }

    private async Task<DataToolResult> FetchSignalsDescriptionOverChatAsync(
        GridClient client,
        UUID senderId,
        SignalDiscoveryCacheEntry cached,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var chatChannelText = uri.Host;
        if (!int.TryParse(chatChannelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatChannel))
        {
            return DataToolResult.FailResult("chat:// URI host must be an integer channel number.");
        }

        var requestResource = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(requestResource))
        {
            requestResource = "SIGNALS.json";
        }

        var pending = new PendingSignalsDescriptionRequest(requestResource, chatChannel);
        if (!_pendingSignalsDescriptionBySender.TryAdd(senderId, pending))
        {
            return DataToolResult.FailResult("A Signals chat description request is already in-flight for this object sender.");
        }

        try
        {
            client.Self.Chat(requestResource, chatChannel, ChatType.Normal);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(SignalsChatDescriptionTimeout);
            var responseText = await pending.Response.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);

            var payload = new
            {
                senderId = senderId.ToString(),
                protocolVersion = cached.ProtocolVersion,
                uri = cached.Uri,
                transport = "chat",
                channel = chatChannel,
                request = requestResource,
                fetchedAtUtc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
                lastHeardUtc = cached.LastHeardUtc.UtcDateTime.ToString("O"),
                content = responseText
            };

            return DataToolResult.OkResult("Retrieved Signals description over chat.", JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DataToolResult.FailResult("Timed out waiting for Signals chat description response.");
        }
        finally
        {
            _pendingSignalsDescriptionBySender.TryRemove(senderId, out _);
        }
    }

}

internal sealed class SignalDiscoveryCacheEntry
{
    public UUID SenderId { get; set; } = UUID.Zero;
    public string ProtocolVersion { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public UUID OwnerId { get; set; } = UUID.Zero;
    public string? ObjectName { get; set; }
    public string? ObjectDescription { get; set; }
    public string RegionName { get; set; } = string.Empty;
    public ulong RegionHandle { get; set; }
    public Vector3 Position { get; set; } = Vector3.Zero;
    public DateTimeOffset LastHeardUtc { get; set; } = DateTimeOffset.MinValue;
}

internal sealed record SignalNearbyObjectInfo(
    string SenderId,
    string ProtocolVersion,
    string Uri,
    string? ObjectName,
    string? ObjectDescription,
    string? OwnerId,
    string RegionName,
    ulong RegionHandle,
    float PositionX,
    float PositionY,
    float PositionZ,
    float DistanceMeters,
    string LastHeardUtc);

internal sealed class PendingSignalsDescriptionRequest
{
    public PendingSignalsDescriptionRequest(string requestResource, int channel)
    {
        RequestResource = requestResource;
        Channel = channel;
    }

    public string RequestResource { get; }

    public int Channel { get; }

    public TaskCompletionSource<string> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}