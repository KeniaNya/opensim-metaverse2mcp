using System.Collections.Concurrent;
using System.Globalization;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const int EventStreamMaxGeneral = 1000;
    private const int EventStreamMaxObject = 2000;
    private const int EventStreamMaxTeleport = 500;
    private const int EventStreamMaxProgress = 1000;
    private const int EventStreamMaxFollow = 1000;
    private const int EventStreamMaxAgentMonitor = 1000;
    private const int EventStreamMaxFriends = 500;
    private const int EventStreamObjectMinIntervalMs = 250;

    private readonly object _eventStreamLock = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamGeneral = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamObject = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamTeleport = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamProgress = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamFollow = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamAgentMonitor = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamFriends = new();
    private readonly SemaphoreSlim _eventStreamSignal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, EventStreamSubscriptionState> _eventSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _objectEventThrottle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ObjectMonitorSpec> _objectMonitors = new(StringComparer.OrdinalIgnoreCase);
    private long _eventStreamSequence;
    private int _eventStreamTrimmedTotal;
    private int _eventStreamTrimmedGeneral;
    private int _eventStreamTrimmedObject;
    private int _eventStreamTrimmedTeleport;
    private int _eventStreamTrimmedProgress;

    public EventStreamSubscriptionResult EventStreamSubscribe(
        string? channels,
        string? eventTypes,
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources)
    {
        var channelSet = ParseEventChannels(channels, out var channelError) ?? DefaultEventChannels();
        if (channelError != null)
        {
            return EventStreamSubscriptionResult.FailResult(channelError);
        }

        var eventTypeSet = ParseEventTypes(eventTypes);
        var filter = ParseEventFilter(radiusMeters, objectIds, objectLocalIds, chatSources, out var filterError);
        if (filterError != null)
        {
            return EventStreamSubscriptionResult.FailResult(filterError);
        }

        var cursor = GetCurrentEventCursor();
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var state = new EventStreamSubscriptionState(
            id,
            channelSet,
            eventTypeSet,
            filter,
            cursor,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        _eventSubscriptions[id] = state;

        return EventStreamSubscriptionResult.OkResult(
            id,
            cursor.ToString(CultureInfo.InvariantCulture),
            channelSet.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            eventTypeSet?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            filter.RadiusMeters,
            filter.ObjectIds?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            filter.ObjectLocalIds?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            filter.ChatSources?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            BuildEventSubscriptionMessage(channelSet));
    }

    public ObjectMonitorResult ObjectMonitorSimOrPrim(
        ulong regionHandle,
        uint primId,
        bool children,
        bool terseUpdateEvent,
        bool fullUpdateEvent)
    {
        if (!terseUpdateEvent && !fullUpdateEvent)
        {
            return ObjectMonitorResult.FailResult("At least one of terseUpdateEvent or fullUpdateEvent must be true.");
        }

        var handle = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var spec = new ObjectMonitorSpec(
            handle,
            regionHandle,
            primId,
            children,
            terseUpdateEvent,
            fullUpdateEvent,
            DateTimeOffset.UtcNow);

        _objectMonitors[handle] = spec;

        return ObjectMonitorResult.OkResult(
            handle,
            spec.RegionHandle,
            spec.PrimId,
            spec.Children,
            spec.TerseUpdateEvent,
            spec.FullUpdateEvent,
            $"Object monitor '{handle}' added. Active monitors: {_objectMonitors.Count}.");
    }

    public BotToolResult CloseObjectMonitor(string? monitorHandle)
    {
        if (string.IsNullOrWhiteSpace(monitorHandle))
        {
            var removed = _objectMonitors.Count;
            _objectMonitors.Clear();
            return BotToolResult.OkResult($"Closed all object monitors ({removed}).");
        }

        var normalizedHandle = monitorHandle.Trim();
        return _objectMonitors.TryRemove(normalizedHandle, out _)
            ? BotToolResult.OkResult($"Closed object monitor '{normalizedHandle}'. Active monitors: {_objectMonitors.Count}.")
            : BotToolResult.Fail($"Object monitor handle '{normalizedHandle}' was not found.");
    }

    public ObjectMonitorListResult ListObjectMonitors()
    {
        var monitors = _objectMonitors.Values
            .OrderBy(spec => spec.CreatedAtUtc)
            .Select(spec => new ObjectMonitorInfo(
                spec.Handle,
                spec.RegionHandle,
                spec.PrimId,
                spec.Children,
                spec.TerseUpdateEvent,
                spec.FullUpdateEvent,
                spec.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)))
            .ToArray();

        return ObjectMonitorListResult.OkResult(monitors, $"Returned {monitors.Length} object monitor(s).");
    }

    public BotToolResult EventStreamUnsubscribe(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return BotToolResult.Fail("subscriptionId is required.");
        }

        return _eventSubscriptions.TryRemove(subscriptionId.Trim(), out _)
            ? BotToolResult.OkResult("Subscription removed.")
            : BotToolResult.Fail("Subscription was not found.");
    }

    public async Task<EventStreamPollResult> EventStreamPollAsync(
        string? subscriptionId,
        string? cursor,
        string? channels,
        string? eventTypes,
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources,
        int maxResults,
        int waitMs,
        CancellationToken cancellationToken)
    {
        var effectiveMax = Math.Clamp(maxResults, 1, 500);
        var effectiveWaitMs = Math.Clamp(waitMs, 0, 30000);

        EventStreamSubscriptionState? subscription = null;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            _eventSubscriptions.TryGetValue(subscriptionId.Trim(), out subscription);
            if (subscription == null)
            {
                return EventStreamPollResult.FailResult("Subscription was not found.");
            }
        }

        var channelSet = ParseEventChannels(channels, out var channelError)
            ?? subscription?.Channels
            ?? DefaultEventChannels();
        if (channelError != null)
        {
            return EventStreamPollResult.FailResult(channelError);
        }

        var eventTypeSet = ParseEventTypes(eventTypes) ?? subscription?.EventTypes;
        var hasFilterOverride = HasAnyEventFilterInput(radiusMeters, objectIds, objectLocalIds, chatSources);
        var overrideFilter = ParseEventFilter(radiusMeters, objectIds, objectLocalIds, chatSources, out var filterError);
        if (filterError != null)
        {
            return EventStreamPollResult.FailResult(filterError);
        }

        var effectiveFilter = hasFilterOverride
            ? overrideFilter
            : (subscription?.Filter ?? EventStreamFilterSpec.None);
        if (effectiveFilter.RadiusMeters.HasValue && !TryGetCurrentSimPosition(out var _))
        {
            return EventStreamPollResult.FailResult("radiusMeters filtering requires a connected bot with current position.");
        }

        var afterId = ParseEventCursor(cursor, out var cursorError);
        if (cursorError != null)
        {
            return EventStreamPollResult.FailResult(cursorError);
        }

        if (afterId == null)
        {
            afterId = subscription?.Cursor ?? 0L;
        }

        var started = DateTimeOffset.UtcNow;
        EventStreamReadSnapshot snapshot;
        while (true)
        {
            snapshot = ReadEventSnapshot(afterId.Value, channelSet, eventTypeSet, effectiveFilter, effectiveMax);
            if (snapshot.Events.Count > 0 || effectiveWaitMs == 0)
            {
                break;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var remainingMs = effectiveWaitMs - (int)elapsed.TotalMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            try
            {
                var signaled = await _eventStreamSignal.WaitAsync(Math.Min(remainingMs, 2000), cancellationToken).ConfigureAwait(false);
                if (!signaled)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (subscription != null)
        {
            _eventSubscriptions[subscription.Id] = subscription with
            {
                Cursor = snapshot.NextCursor,
                LastAccessAtUtc = DateTimeOffset.UtcNow
            };
        }

        var message = snapshot.CursorTrimmed
            ? "Some older events were trimmed before this cursor; returned all currently retained matching events."
            : $"Returned {snapshot.Events.Count} event(s).";

        return EventStreamPollResult.OkResult(
            snapshot.Events,
            snapshot.NextCursor.ToString(CultureInfo.InvariantCulture),
            snapshot.TrimmedTotal,
            snapshot.TrimmedGeneral,
            snapshot.TrimmedObject,
            snapshot.TrimmedTeleport,
            snapshot.CursorTrimmed,
            message);
    }

    public EventStreamHistoryResult EventStreamHistory(
        string? channels,
        string? eventTypes,
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources,
        int lastSeconds,
        int maxResults)
    {
        var channelSet = ParseEventChannels(channels, out var channelError) ?? DefaultEventChannels();
        if (channelError != null)
        {
            return EventStreamHistoryResult.FailResult(channelError);
        }

        var eventTypeSet = ParseEventTypes(eventTypes);
        var filter = ParseEventFilter(radiusMeters, objectIds, objectLocalIds, chatSources, out var filterError);
        if (filterError != null)
        {
            return EventStreamHistoryResult.FailResult(filterError);
        }

        if (filter.RadiusMeters.HasValue && !TryGetCurrentSimPosition(out var _))
        {
            return EventStreamHistoryResult.FailResult("radiusMeters filtering requires a connected bot with current position.");
        }

        var effectiveWindowSeconds = Math.Clamp(lastSeconds, 1, 1800);
        var effectiveMax = Math.Clamp(maxResults, 1, 500);
        var notBeforeUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(effectiveWindowSeconds);
        var snapshot = ReadEventSnapshot(0, channelSet, eventTypeSet, filter, effectiveMax, notBeforeUtc);

        return EventStreamHistoryResult.OkResult(
            snapshot.Events,
            snapshot.NextCursor.ToString(CultureInfo.InvariantCulture),
            effectiveWindowSeconds,
            snapshot.TrimmedTotal,
            snapshot.TrimmedGeneral,
            snapshot.TrimmedObject,
            snapshot.TrimmedTeleport,
            $"Returned {snapshot.Events.Count} event(s) from the last {effectiveWindowSeconds} second(s).");
    }

    public EventStreamStatsResult EventStreamStats()
    {
        lock (_eventStreamLock)
        {
            return EventStreamStatsResult.OkResult(
                _eventStreamGeneral.Count,
                _eventStreamObject.Count,
                _eventStreamTeleport.Count,
                _eventStreamProgress.Count,
                _eventStreamTrimmedTotal,
                _eventStreamTrimmedGeneral,
                _eventStreamTrimmedObject,
                _eventStreamTrimmedTeleport,
                _eventStreamTrimmedProgress,
                _eventSubscriptions.Count,
                _eventStreamSequence.ToString(CultureInfo.InvariantCulture),
                "Current event stream buffer stats.");
        }
    }
    
    public void EmitInventoryImportProgressEvent(string handle, string message, int progressPercent)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Inventory import is in progress." : message.Trim();
        var clampedPercent = Math.Clamp(progressPercent, 0, 100);

        EmitRuntimeEvent(
            "progress",
            "inventory.import.message",
            "inventory.import",
            normalizedMessage,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["progressPercent"] = clampedPercent.ToString(CultureInfo.InvariantCulture)
            });
    }

    public void EmitRegionImportProgressEvent(string handle, string message, int progressPercent)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Region import is in progress." : message.Trim();
        var clampedPercent = Math.Clamp(progressPercent, 0, 100);

        EmitRuntimeEvent(
            "progress",
            "region.import.message",
            "region.import",
            normalizedMessage,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["progressPercent"] = clampedPercent.ToString(CultureInfo.InvariantCulture)
            });
    }

    public void EmitInventoryImportCompleteEvent(string handle, bool success, string message)
    {
        _botTaskManager.TryReportCompletion(handle, success, message);

        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? (success ? "Inventory import completed." : "Inventory import failed.")
            : message.Trim();

        EmitRuntimeEvent(
            "progress",
            "inventory.import.complete",
            "inventory.import",
            normalizedMessage,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["success"] = success ? "true" : "false"
            });
    }

    public void EmitRegionImportCompleteEvent(string handle, bool success, string message)
    {
        _botTaskManager.TryReportCompletion(handle, success, message);

        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? (success ? "Region import completed." : "Region import failed.")
            : message.Trim();

        EmitRuntimeEvent(
            "progress",
            "region.import.complete",
            "region.import",
            normalizedMessage,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["success"] = success ? "true" : "false"
            });
    }

    public void EmitInventoryListProgressEvent(string handle, string message, int progressPercent)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Inventory list is in progress." : message.Trim();
        var clampedPercent = Math.Clamp(progressPercent, 0, 100);

        EmitRuntimeEvent(
            "progress",
            "inventory.list.message",
            "inventory.list",
            normalizedMessage,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["progressPercent"] = clampedPercent.ToString(CultureInfo.InvariantCulture)
            });
    }

    public void EmitInventoryListCompleteEvent(string handle, string resultHandle, bool success, string message)
    {
        _botTaskManager.TryReportCompletion(handle, success, message);

        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? (success ? "Inventory list completed." : "Inventory list failed.")
            : message.Trim();

        EmitRuntimeEvent(
            "progress",
            "inventory.list.complete",
            "inventory.list",
            normalizedMessage,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["resultHandle"] = resultHandle,
                ["success"] = success ? "true" : "false"
            });
    }

    public void EmitInventoryListCompleteEvent(string handle, bool success, string message)
        => EmitInventoryListCompleteEvent(handle, string.Empty, success, message);

    private void EmitRuntimeEvent(
        string channel,
        string eventType,
        string source,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        var normalizedChannel = NormalizeEventChannel(channel);
        var normalizedType = string.IsNullOrWhiteSpace(eventType) ? "unknown" : eventType.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();

        RuntimeEventInfo envelope;
        lock (_eventStreamLock)
        {
            var eventId = ++_eventStreamSequence;
            envelope = new RuntimeEventInfo(
                eventId,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                normalizedChannel,
                normalizedType,
                normalizedSource,
                string.IsNullOrWhiteSpace(message) ? normalizedType : message.Trim(),
                attributes ?? new Dictionary<string, string?>());

            var queue = GetEventQueueByChannel(normalizedChannel);
            queue.Enqueue(envelope);
            TrimEventQueueIfNeeded(normalizedChannel, queue);
        }

        // Wake long-poll listeners waiting for new events.
        _eventStreamSignal.Release();
    }

    internal void EmitAgentMonitorRuntimeEvent(
        string eventType,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        EmitRuntimeEvent("agentMonitor", eventType, "agentLocator", message, attributes);
    }

    private void OnWorldObjectUpdateForEventStream(object? sender, PrimEventArgs e)
    {
        EmitObjectUpdateEvent(
            e.Prim,
            e.Simulator,
            "object.updated",
            "object.updated",
            isTerseUpdate: false,
            objectId => $"Object update for {objectId}.");
    }
    
    private void OnTerseWorldObjectUpdateForEventStream(object? sender, TerseObjectUpdateEventArgs e)
    {
        EmitObjectUpdateEvent(
            e.Prim,
            e.Simulator,
            "object.terse.updated",
            "object.terse.updated",
            isTerseUpdate: true,
            objectId => $"Terse object update for {objectId}.");
    }

    private void EmitObjectUpdateEvent(
        Primitive? prim,
        Simulator? simulator,
        string throttleKey,
        string eventType,
        bool isTerseUpdate,
        Func<UUID, string> messageFactory)
    {
        if (prim == null)
        {
            return;
        }

        var objectId = prim.ID;
        if (objectId == UUID.Zero || !ShouldEmitObjectUpdateEvent(prim, simulator, isTerseUpdate) || !TryAcquireObjectEventThrottle(throttleKey))
        {
            return;
        }

        var localPosition = prim.Position;
        var worldPosition = ResolvePrimWorldPosition(prim, simulator);

        EmitRuntimeEvent(
            "object",
            eventType,
            "opensim",
            messageFactory(objectId),
            new Dictionary<string, string?>
            {
                ["objectId"] = objectId.ToString(),
                ["localId"] = prim.LocalID.ToString(CultureInfo.InvariantCulture),
                ["parentLocalId"] = prim.ParentID.ToString(CultureInfo.InvariantCulture),
                ["name"] = string.IsNullOrWhiteSpace(prim.Properties?.Name) ? null : prim.Properties?.Name,
                ["simulator"] = simulator?.Name,
                ["position"] = FormatVector3(worldPosition),
                ["positionWorld"] = FormatVector3(worldPosition),
                ["positionLocal"] = FormatVector3(localPosition)
            });
    }

    private string BuildEventSubscriptionMessage(HashSet<string> channels)
    {
        if (!channels.Contains("object"))
        {
            return "Subscription created.";
        }

        return _objectMonitors.Count == 0
            ? "Subscription created. Note: object events are disabled by default; use ObjectMonitorSimOrPrim to enable monitored object updates."
            : $"Subscription created. Object monitor gating is active with {_objectMonitors.Count} monitor(s).";
    }

    private bool ShouldEmitObjectUpdateEvent(Primitive prim, Simulator? simulator, bool isTerseUpdate)
    {
        if (_objectMonitors.IsEmpty)
        {
            return false;
        }

        var regionHandle = simulator?.Handle ?? prim.RegionHandle;
        foreach (var spec in _objectMonitors.Values)
        {
            if (spec.RegionHandle != 0 && spec.RegionHandle != regionHandle)
            {
                continue;
            }

            if (isTerseUpdate ? !spec.TerseUpdateEvent : !spec.FullUpdateEvent)
            {
                continue;
            }

            if (!IsObjectMonitorPrimMatch(spec, prim, simulator))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsObjectMonitorPrimMatch(ObjectMonitorSpec spec, Primitive prim, Simulator? simulator)
    {
        if (spec.PrimId == 0)
        {
            return true;
        }

        if (prim.LocalID == spec.PrimId)
        {
            return true;
        }

        if (!spec.Children || prim.ParentID == 0)
        {
            return false;
        }

        var ancestorLocalId = prim.ParentID;
        if (ancestorLocalId == spec.PrimId)
        {
            return true;
        }

        if (simulator == null)
        {
            return false;
        }

        var visited = new HashSet<uint>();
        var depth = 0;
        while (ancestorLocalId != 0
               && depth < 16
               && visited.Add(ancestorLocalId)
               && simulator.ObjectsPrimitives.TryGetValue(ancestorLocalId, out var ancestor))
        {
            if (ancestor.LocalID == spec.PrimId)
            {
                return true;
            }

            ancestorLocalId = ancestor.ParentID;
            depth++;
        }

        return false;
    }

    private bool TryAcquireObjectEventThrottle(string throttleKey)
    {
        var now = DateTimeOffset.UtcNow;
        if (_objectEventThrottle.TryGetValue(throttleKey, out var lastSeen)
            && (now - lastSeen).TotalMilliseconds < EventStreamObjectMinIntervalMs)
        {
            return false;
        }

        _objectEventThrottle[throttleKey] = now;
        if (_objectEventThrottle.Count > 8000)
        {
            var cutoff = now - TimeSpan.FromMinutes(5);
            foreach (var item in _objectEventThrottle)
            {
                if (item.Value < cutoff)
                {
                    _objectEventThrottle.TryRemove(item.Key, out _);
                }
            }
        }

        return true;
    }

    private static string FormatVector3(Vector3 value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value.X:0.###},{value.Y:0.###},{value.Z:0.###}");
    }

    private static Vector3 ResolvePrimWorldPosition(Primitive prim, Simulator? simulator)
    {
        if (prim.ParentID == 0 || simulator == null)
        {
            return prim.Position;
        }

        var worldPosition = prim.Position;
        var parentLocalId = prim.ParentID;
        var visited = new HashSet<uint>();
        var depth = 0;

        // Child prim positions are local offsets; walk parent chain to map into simulator space.
        while (parentLocalId != 0
               && depth < 16
               && visited.Add(parentLocalId)
               && simulator.ObjectsPrimitives.TryGetValue(parentLocalId, out var parent))
        {
            worldPosition = (worldPosition * parent.Rotation) + parent.Position;
            parentLocalId = parent.ParentID;
            depth++;
        }

        return worldPosition;
    }

    private static string NormalizeEventChannel(string channel)
    {
        var normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "object" => "object",
            "teleport" => "teleport",
            "progress" => "progress",
            "follow" => "follow",
            "agentmonitor" => "agentMonitor",
            "friends" => "friends",
            _ => "general"
        };
    }

    private static HashSet<string> DefaultEventChannels()
        => new(new[] { "general", "object", "teleport", "progress", "follow", "agentMonitor", "friends" }, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string>? ParseEventTypes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw
            .Split(new[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return parsed.Count == 0 ? null : parsed;
    }

    private static HashSet<string>? ParseEventChannels(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var requested = raw
            .Split(new[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .ToArray();

        if (requested.Length == 0)
        {
            return null;
        }

        if (requested.Any(value => value == "all"))
        {
            return DefaultEventChannels();
        }

        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in requested)
        {
            if (channel == "general" || channel == "object" || channel == "teleport" || channel == "progress" || channel == "follow" || channel == "agentmonitor" || channel == "friends")
            {
                accepted.Add(channel == "agentmonitor" ? "agentMonitor" : channel);
                continue;
            }

            error = "channels must use: general, object, teleport, progress, follow, agentMonitor, friends, or all.";
            return null;
        }

        if (accepted.Count == 0)
        {
            error = "At least one valid channel is required.";
            return null;
        }

        return accepted;
    }

    private static long? ParseEventCursor(string? cursor, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!long.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            error = "cursor must be a non-negative integer string.";
            return null;
        }

        return parsed;
    }

    private long GetCurrentEventCursor()
    {
        lock (_eventStreamLock)
        {
            return _eventStreamSequence;
        }
    }

    private EventStreamReadSnapshot ReadEventSnapshot(
        long afterId,
        HashSet<string> channels,
        HashSet<string>? eventTypes,
        EventStreamFilterSpec filter,
        int maxResults,
        DateTimeOffset? notBeforeUtc = null)
    {
        lock (_eventStreamLock)
        {
            var origin = filter.RadiusMeters.HasValue && TryGetCurrentSimPosition(out var currentPosition)
                ? currentPosition
                : (Vector3?)null;
            var matchingAll = EnumerateChannelEvents(channels)
                .OrderBy(e => e.EventId)
                .Where(e => channels.Contains(e.Channel))
                .Where(e => e.EventId > afterId)
                .Where(e => EventTypeMatches(e.EventType, eventTypes))
                .Where(e => EventMatchesFilter(e, filter, origin, notBeforeUtc))
                .ToList();

            var matching = notBeforeUtc.HasValue
                ? (matchingAll.Count > maxResults
                    ? matchingAll.Skip(matchingAll.Count - maxResults).ToList()
                    : matchingAll)
                : matchingAll.Take(maxResults).ToList();

            var nextCursor = matching.Count > 0 ? matching[^1].EventId : afterId;
            var oldestMatchingRetainedId = matchingAll
                .Select(e => e.EventId)
                .DefaultIfEmpty(0)
                .Min();
            var cursorTrimmed = oldestMatchingRetainedId > 0 && afterId > 0 && afterId < oldestMatchingRetainedId;

            return new EventStreamReadSnapshot(
                matching,
                nextCursor,
                cursorTrimmed,
                _eventStreamTrimmedTotal,
                _eventStreamTrimmedGeneral,
                _eventStreamTrimmedObject,
                _eventStreamTrimmedTeleport);
        }
    }

    private IEnumerable<RuntimeEventInfo> EnumerateChannelEvents(HashSet<string> channels)
    {
        if (channels.Contains("general"))
        {
            foreach (var item in _eventStreamGeneral)
            {
                yield return item;
            }
        }

        if (channels.Contains("object"))
        {
            foreach (var item in _eventStreamObject)
            {
                yield return item;
            }
        }

        if (channels.Contains("teleport"))
        {
            foreach (var item in _eventStreamTeleport)
            {
                yield return item;
            }
        }

        if (channels.Contains("progress"))
        {
            foreach (var item in _eventStreamProgress)
            {
                yield return item;
            }
        }

        if (channels.Contains("follow"))
        {
            foreach (var item in _eventStreamFollow)
            {
                yield return item;
            }
        }

        if (channels.Contains("agentMonitor"))
        {
            foreach (var item in _eventStreamAgentMonitor)
            {
                yield return item;
            }
        }

        if (channels.Contains("friends"))
        {
            foreach (var item in _eventStreamFriends)
            {
                yield return item;
            }
        }
    }

    private static bool EventTypeMatches(string eventType, HashSet<string>? filters)
    {
        return filters == null || filters.Count == 0 || filters.Contains(eventType);
    }

    private static bool EventMatchesFilter(
        RuntimeEventInfo eventInfo,
        EventStreamFilterSpec filter,
        Vector3? origin,
        DateTimeOffset? notBeforeUtc)
    {
        if (notBeforeUtc.HasValue
            && TryParseEventTimestamp(eventInfo.TimestampUtc, out var parsedTs)
            && parsedTs < notBeforeUtc.Value)
        {
            return false;
        }

        if (filter.ObjectIds != null && filter.ObjectIds.Count > 0)
        {
            if (!eventInfo.Attributes.TryGetValue("objectId", out var objectId)
                || string.IsNullOrWhiteSpace(objectId)
                || !filter.ObjectIds.Contains(objectId.Trim()))
            {
                return false;
            }
        }

        if (filter.ObjectLocalIds != null && filter.ObjectLocalIds.Count > 0)
        {
            if (!eventInfo.Attributes.TryGetValue("localId", out var localId)
                || string.IsNullOrWhiteSpace(localId)
                || !filter.ObjectLocalIds.Contains(localId.Trim()))
            {
                return false;
            }
        }

        if (filter.ChatSources != null && filter.ChatSources.Count > 0)
        {
            var fromAgentId = eventInfo.Attributes.TryGetValue("fromAgentId", out var sourceAgent) ? sourceAgent : null;
            var fromName = eventInfo.Attributes.TryGetValue("fromName", out var sourceName) ? sourceName : null;
            var sourceType = eventInfo.Attributes.TryGetValue("sourceType", out var sourceKind) ? sourceKind : null;

            var sourceMatched = filter.ChatSources.Contains(eventInfo.Source)
                || (!string.IsNullOrWhiteSpace(fromAgentId) && filter.ChatSources.Contains(fromAgentId.Trim()))
                || (!string.IsNullOrWhiteSpace(fromName) && filter.ChatSources.Contains(fromName.Trim()))
                || (!string.IsNullOrWhiteSpace(sourceType) && filter.ChatSources.Contains(sourceType.Trim()));
            if (!sourceMatched)
            {
                return false;
            }
        }

        if (!filter.RadiusMeters.HasValue)
        {
            return true;
        }

        if (!origin.HasValue)
        {
            return false;
        }

        if (!TryParseEventPosition(eventInfo, out var eventPosition))
        {
            return false;
        }

        var distance = Vector3.Distance(origin.Value, eventPosition);
        return distance <= filter.RadiusMeters.Value;
    }

    private static bool TryParseEventTimestamp(string raw, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static bool TryParseEventPosition(RuntimeEventInfo eventInfo, out Vector3 position)
    {
        position = Vector3.Zero;
        if (!eventInfo.Attributes.TryGetValue("position", out var rawPosition)
            || string.IsNullOrWhiteSpace(rawPosition))
        {
            eventInfo.Attributes.TryGetValue("targetPosition", out rawPosition);
        }

        if (string.IsNullOrWhiteSpace(rawPosition))
        {
            return false;
        }

        var parts = rawPosition.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        return true;
    }

    private static bool HasAnyEventFilterInput(float? radiusMeters, string? objectIds, string? objectLocalIds, string? chatSources)
    {
        return radiusMeters.HasValue
            || !string.IsNullOrWhiteSpace(objectIds)
            || !string.IsNullOrWhiteSpace(objectLocalIds)
            || !string.IsNullOrWhiteSpace(chatSources);
    }

    private EventStreamFilterSpec ParseEventFilter(
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources,
        out string? error)
    {
        error = null;

        float? normalizedRadius = null;
        if (radiusMeters.HasValue)
        {
            if (radiusMeters.Value <= 0f)
            {
                error = "radiusMeters must be greater than 0 when provided.";
                return EventStreamFilterSpec.None;
            }

            normalizedRadius = radiusMeters.Value;
        }

        var parsedObjectIds = ParseDelimitedSet(objectIds);
        if (parsedObjectIds != null)
        {
            foreach (var objectId in parsedObjectIds)
            {
                if (!UUID.TryParse(objectId, out _))
                {
                    error = "objectIds must contain valid UUID values.";
                    return EventStreamFilterSpec.None;
                }
            }
        }

        HashSet<string>? parsedLocalIds = null;
        var localIdTokens = ParseDelimitedSet(objectLocalIds);
        if (localIdTokens != null)
        {
            parsedLocalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in localIdTokens)
            {
                if (!uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLocalId))
                {
                    error = "objectLocalIds must contain unsigned integer values.";
                    return EventStreamFilterSpec.None;
                }

                parsedLocalIds.Add(parsedLocalId.ToString(CultureInfo.InvariantCulture));
            }
        }

        var parsedChatSources = ParseDelimitedSet(chatSources);
        return new EventStreamFilterSpec(normalizedRadius, parsedObjectIds, parsedLocalIds, parsedChatSources);
    }

    private static HashSet<string>? ParseDelimitedSet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw
            .Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return parsed.Count == 0 ? null : parsed;
    }

    private bool TryGetCurrentSimPosition(out Vector3 position)
    {
        position = Vector3.Zero;
        var client = _client;
        if (!_connected || client == null)
        {
            return false;
        }

        position = client.Self.SimPosition;
        return true;
    }

    private Queue<RuntimeEventInfo> GetEventQueueByChannel(string channel)
    {
        return channel switch
        {
            "object" => _eventStreamObject,
            "teleport" => _eventStreamTeleport,
            "progress" => _eventStreamProgress,
            "follow" => _eventStreamFollow,
            "agentMonitor" => _eventStreamAgentMonitor,
            "friends" => _eventStreamFriends,
            _ => _eventStreamGeneral,
        };
    }

    private void TrimEventQueueIfNeeded(string channel, Queue<RuntimeEventInfo> queue)
    {
        var limit = channel switch
        {
            "object" => EventStreamMaxObject,
            "teleport" => EventStreamMaxTeleport,
            "progress" => EventStreamMaxProgress,
            "follow" => EventStreamMaxFollow,
            "agentMonitor" => EventStreamMaxAgentMonitor,
            "friends" => EventStreamMaxFriends,
            _ => EventStreamMaxGeneral,
        };

        while (queue.Count > limit)
        {
            queue.Dequeue();
            _eventStreamTrimmedTotal++;
            switch (channel)
            {
                case "object":
                    _eventStreamTrimmedObject++;
                    break;
                case "teleport":
                    _eventStreamTrimmedTeleport++;
                    break;
                case "progress":
                    _eventStreamTrimmedProgress++;
                    break;
                case "follow":
                    _eventStreamTrimmedGeneral++;
                    break;
                default:
                    _eventStreamTrimmedGeneral++;
                    break;
            }
        }
    }

    private sealed record EventStreamReadSnapshot(
        IReadOnlyList<RuntimeEventInfo> Events,
        long NextCursor,
        bool CursorTrimmed,
        int TrimmedTotal,
        int TrimmedGeneral,
        int TrimmedObject,
        int TrimmedTeleport);

    private sealed record EventStreamSubscriptionState(
        string Id,
        HashSet<string> Channels,
        HashSet<string>? EventTypes,
        EventStreamFilterSpec Filter,
        long Cursor,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastAccessAtUtc);

    private sealed record ObjectMonitorSpec(
        string Handle,
        ulong RegionHandle,
        uint PrimId,
        bool Children,
        bool TerseUpdateEvent,
        bool FullUpdateEvent,
        DateTimeOffset CreatedAtUtc);
}

internal sealed record EventStreamFilterSpec(
    float? RadiusMeters,
    HashSet<string>? ObjectIds,
    HashSet<string>? ObjectLocalIds,
    HashSet<string>? ChatSources)
{
    public static EventStreamFilterSpec None { get; } = new(null, null, null, null);
}

internal sealed record RuntimeEventInfo(
    long EventId,
    string TimestampUtc,
    string Channel,
    string EventType,
    string Source,
    string Message,
    IReadOnlyDictionary<string, string?> Attributes);

internal sealed record EventStreamSubscriptionResult(
    bool Ok,
    string Message,
    string? SubscriptionId,
    string? Cursor,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string>? EventTypes,
    float? RadiusMeters,
    IReadOnlyList<string>? ObjectIds,
    IReadOnlyList<string>? ObjectLocalIds,
    IReadOnlyList<string>? ChatSources)
{
    public static EventStreamSubscriptionResult OkResult(
        string subscriptionId,
        string cursor,
        IReadOnlyList<string> channels,
        IReadOnlyList<string>? eventTypes,
        float? radiusMeters,
        IReadOnlyList<string>? objectIds,
        IReadOnlyList<string>? objectLocalIds,
        IReadOnlyList<string>? chatSources,
        string message)
        => new(true, message, subscriptionId, cursor, channels, eventTypes, radiusMeters, objectIds, objectLocalIds, chatSources);

    public static EventStreamSubscriptionResult FailResult(string message)
        => new(false, message, null, null, Array.Empty<string>(), null, null, null, null, null);
}

internal sealed record EventStreamPollResult(
    bool Ok,
    string Message,
    string NextCursor,
    int TrimmedTotal,
    int TrimmedGeneral,
    int TrimmedObject,
    int TrimmedTeleport,
    bool CursorTrimmed,
    IReadOnlyList<RuntimeEventInfo> Events)
{
    public static EventStreamPollResult OkResult(
        IReadOnlyList<RuntimeEventInfo> events,
        string nextCursor,
        int trimmedTotal,
        int trimmedGeneral,
        int trimmedObject,
        int trimmedTeleport,
        bool cursorTrimmed,
        string message)
        => new(true, message, nextCursor, trimmedTotal, trimmedGeneral, trimmedObject, trimmedTeleport, cursorTrimmed, events);

    public static EventStreamPollResult FailResult(string message)
        => new(false, message, "0", 0, 0, 0, 0, false, Array.Empty<RuntimeEventInfo>());
}

internal sealed record EventStreamStatsResult(
    bool Ok,
    string Message,
    int GeneralBufferCount,
    int ObjectBufferCount,
    int TeleportBufferCount,
    int ProgressBufferCount,
    int TrimmedTotal,
    int TrimmedGeneral,
    int TrimmedObject,
    int TrimmedTeleport,
    int TrimmedProgress,
    int SubscriptionCount,
    string CurrentCursor)
{
    public static EventStreamStatsResult OkResult(
        int generalBufferCount,
        int objectBufferCount,
        int teleportBufferCount,
        int progressBufferCount,
        int trimmedTotal,
        int trimmedGeneral,
        int trimmedObject,
        int trimmedTeleport,
        int trimmedProgress,
        int subscriptionCount,
        string currentCursor,
        string message)
        => new(
            true,
            message,
            generalBufferCount,
            objectBufferCount,
            teleportBufferCount,
            progressBufferCount,
            trimmedTotal,
            trimmedGeneral,
            trimmedObject,
            trimmedTeleport,
            trimmedProgress,
            subscriptionCount,
            currentCursor);

    public static EventStreamStatsResult FailResult(string message)
        => new(false, message, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "0");
}

internal sealed record EventStreamHistoryResult(
    bool Ok,
    string Message,
    string NextCursor,
    int WindowSeconds,
    int TrimmedTotal,
    int TrimmedGeneral,
    int TrimmedObject,
    int TrimmedTeleport,
    IReadOnlyList<RuntimeEventInfo> Events)
{
    public static EventStreamHistoryResult OkResult(
        IReadOnlyList<RuntimeEventInfo> events,
        string nextCursor,
        int windowSeconds,
        int trimmedTotal,
        int trimmedGeneral,
        int trimmedObject,
        int trimmedTeleport,
        string message)
        => new(true, message, nextCursor, windowSeconds, trimmedTotal, trimmedGeneral, trimmedObject, trimmedTeleport, events);

    public static EventStreamHistoryResult FailResult(string message)
        => new(false, message, "0", 0, 0, 0, 0, 0, Array.Empty<RuntimeEventInfo>());
}

internal sealed record ObjectMonitorResult(
    bool Ok,
    string Message,
    string? MonitorHandle,
    ulong RegionHandle,
    uint PrimId,
    bool Children,
    bool TerseUpdateEvent,
    bool FullUpdateEvent)
{
    public static ObjectMonitorResult OkResult(
        string monitorHandle,
        ulong regionHandle,
        uint primId,
        bool children,
        bool terseUpdateEvent,
        bool fullUpdateEvent,
        string message)
        => new(true, message, monitorHandle, regionHandle, primId, children, terseUpdateEvent, fullUpdateEvent);

    public static ObjectMonitorResult FailResult(string message)
        => new(false, message, null, 0UL, 0U, false, false, false);
}

internal sealed record ObjectMonitorInfo(
    string MonitorHandle,
    ulong RegionHandle,
    uint PrimId,
    bool Children,
    bool TerseUpdateEvent,
    bool FullUpdateEvent,
    string CreatedAtUtc);

internal sealed record ObjectMonitorListResult(
    bool Ok,
    string Message,
    int Count,
    IReadOnlyList<ObjectMonitorInfo> Monitors)
{
    public static ObjectMonitorListResult OkResult(
        IReadOnlyList<ObjectMonitorInfo> monitors,
        string message)
        => new(true, message, monitors.Count, monitors);

    public static ObjectMonitorListResult FailResult(string message)
        => new(false, message, 0, Array.Empty<ObjectMonitorInfo>());
}
