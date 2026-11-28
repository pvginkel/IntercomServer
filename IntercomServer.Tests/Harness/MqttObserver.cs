using System.Text.Json.Nodes;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// A client of the test's own, subscribed as Home Assistant is, that records what arrives on its
/// topics: first what the broker retains there, then each message as it is published. The broker
/// delivers one publisher's messages in the order it published them.
/// </summary>
internal sealed class MqttObserver : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly Channel<ObservedMessage> _messages = Channel.CreateUnbounded<ObservedMessage>();

    private MqttObserver(IMqttClient client)
    {
        _client = client;
    }

    public static async Task<MqttObserver> SubscribeAsync(TestBroker broker, params string[] topicFilters)
    {
        var client = await broker.ConnectAsync($"observer:{Guid.NewGuid():N}");
        var observer = new MqttObserver(client);

        client.ApplicationMessageReceivedAsync += e =>
        {
            observer._messages.Writer.TryWrite(
                new ObservedMessage(
                    e.ApplicationMessage.Topic,
                    e.ApplicationMessage.ConvertPayloadToString() ?? "",
                    e.ApplicationMessage.Retain
                )
            );
            return Task.CompletedTask;
        };

        var subscription = new MqttClientSubscribeOptionsBuilder();
        foreach (var topicFilter in topicFilters)
            subscription.WithTopicFilter(filter =>
                filter.WithTopic(topicFilter).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            );

        using var timeout = new CancellationTokenSource(Timeouts.Observe);
        await client.SubscribeAsync(subscription.Build(), timeout.Token);

        return observer;
    }

    public Task<ObservedMessage> NextAsync() => Timeouts.NextAsync(_messages.Reader, "a message on the observed topics");

    /// <summary>The next message on <paramref name="topic"/>, passing over those on the other topics.</summary>
    public async Task<ObservedMessage> NextOnAsync(string topic)
    {
        while (true)
        {
            var message = await NextAsync();
            if (message.Topic == topic)
                return message;
        }
    }

    /// <summary>Takes the messages that have arrived and not been taken yet, without waiting.</summary>
    public List<ObservedMessage> TakeArrived()
    {
        var messages = new List<ObservedMessage>();
        while (_messages.Reader.TryRead(out var message))
            messages.Add(message);
        return messages;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisconnectAsync();
        _client.Dispose();
    }
}

/// <summary>
/// A message an <see cref="MqttObserver"/> received: an empty <paramref name="Payload"/> is one
/// that removes what the broker retains, and <paramref name="Retained"/> marks one the broker
/// retained before the subscription.
/// </summary>
internal sealed record ObservedMessage(string Topic, string Payload, bool Retained)
{
    /// <summary>
    /// Asserts the message is on <paramref name="topic"/>, with a JSON payload equal to
    /// <paramref name="json"/> whatever its key order and spacing.
    /// </summary>
    public void AssertIs(string topic, JsonNode json)
    {
        Assert.Equal(topic, Topic);
        Assert.True(
            Payload.Length > 0 && JsonNode.DeepEquals(json, JsonNode.Parse(Payload)),
            $"Expected {topic} {json.ToJsonString()}, got {Payload}."
        );
    }

    public void AssertIs(string topic, string payload)
    {
        Assert.Equal(topic, Topic);
        Assert.Equal(payload, Payload);
    }
}
