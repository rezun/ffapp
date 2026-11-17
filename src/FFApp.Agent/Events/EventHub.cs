using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FFApp.Agent.Events;

/// <summary>
/// Fans server-sent events out to connected clients. Job events go only to the job's owner. If a
/// slow client falls behind, its oldest events are dropped; clients reconcile through
/// <c>GET /jobs</c> after reconnecting, so no state is lost.
/// </summary>
public sealed class EventHub(TimeProvider timeProvider)
{
    private static readonly int SubscriberCapacity = 1_000;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    public static readonly string HeartbeatEvent = "heartbeat";

    private readonly Lock _lock = new();
    private readonly List<Subscriber> _subscribers = [];

    private sealed record Subscriber(string ClientId, Channel<SseItem<string>> Channel);

    public int SubscriberCount
    {
        get { lock (_lock) return _subscribers.Count; }
    }

    public void PublishTo(string clientId, string eventType, string json)
    {
        foreach (var subscriber in Snapshot())
        {
            if (string.Equals(subscriber.ClientId, clientId, StringComparison.Ordinal))
                subscriber.Channel.Writer.TryWrite(new SseItem<string>(json, eventType));
        }
    }

    public void PublishToAll(string eventType, string json)
    {
        foreach (var subscriber in Snapshot())
            subscriber.Channel.Writer.TryWrite(new SseItem<string>(json, eventType));
    }

    /// <summary>Publishes an event whose payload differs per client, such as queue information.</summary>
    public void PublishPerClient(string eventType, Func<string, string> payloadFor)
    {
        ArgumentNullException.ThrowIfNull(payloadFor);
        foreach (var subscriber in Snapshot())
            subscriber.Channel.Writer.TryWrite(new SseItem<string>(payloadFor(subscriber.ClientId), eventType));
    }

    /// <summary>
    /// Streams events for one client until the request ends. <paramref name="initial"/> is sent
    /// first so a fresh connection starts with the current state; heartbeats let the client notice
    /// a dead connection.
    /// </summary>
    public async IAsyncEnumerable<SseItem<string>> StreamAsync(
        string clientId, IEnumerable<SseItem<string>> initial, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<SseItem<string>>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var subscriber = new Subscriber(clientId, channel);
        lock (_lock)
            _subscribers.Add(subscriber);
        try
        {
            foreach (var item in initial)
                yield return item;

            using var heartbeat = new PeriodicTimer(HeartbeatInterval, timeProvider);
            var heartbeatTask = heartbeat.WaitForNextTickAsync(ct).AsTask();
            Task<bool>? readTask = null;
            while (true)
            {
                readTask ??= channel.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(readTask, heartbeatTask) == heartbeatTask)
                {
                    if (!await heartbeatTask)
                        yield break;
                    heartbeatTask = heartbeat.WaitForNextTickAsync(ct).AsTask();
                    yield return new SseItem<string>("{}", HeartbeatEvent);
                    continue;
                }

                if (!await readTask)
                    yield break;
                readTask = null;
                while (channel.Reader.TryRead(out var item))
                    yield return item;
            }
        }
        finally
        {
            lock (_lock)
                _subscribers.Remove(subscriber);
        }
    }

    private Subscriber[] Snapshot()
    {
        lock (_lock)
            return [.. _subscribers];
    }
}
