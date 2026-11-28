using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IntercomServer.Utils.Audio;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The device routes under <c>/api/devices</c>, driven as the tests and the operator by hand call
/// them, and the ToiletAutomation devices the ambient player plays on.
/// </summary>
internal static class DeviceApi
{
    /// <summary>The prefix the ToiletAutomation devices publish under, the ambient player's by default.</summary>
    public const string ToiletPrefix = "toilet_automation";

    /// <summary>The format a ToiletAutomation device plays: mono 48 kHz 16-bit.</summary>
    public static readonly AudioFormat ToiletFormat = new(AudioChannelLayout.Mono, 48000, 16);

    /// <summary>The <c>format</c> a device playing <see cref="ToiletFormat"/> is answered with.</summary>
    public const string ToiletFormatAnswer = """{"channel_layout":"mono","sample_rate":48000,"bit_rate":16}""";

    public static string AmbientPath(string deviceId) => $"/api/devices/{Uri.EscapeDataString(deviceId)}/ambient";

    public static Task<HttpResponseMessage> ListDevicesAsync(this ServerHarness harness, string? token) =>
        harness.SendAsync(HttpMethod.Get, "/api/devices", token);

    /// <summary>
    /// Starts ambient sound on the device <paramref name="deviceId"/>: the track named
    /// <paramref name="track"/>, sent as a JSON object, or, with none, a start with no body.
    /// </summary>
    public static Task<HttpResponseMessage> StartAmbientAsync(
        this ServerHarness harness,
        string? token,
        string deviceId,
        string? track = null
    ) => harness.StartAmbientAsync(token, deviceId, track == null ? null : JsonContent.Create(new { track }));

    public static Task<HttpResponseMessage> StartAmbientAsync(
        this ServerHarness harness,
        string? token,
        string deviceId,
        HttpContent? body
    ) => harness.SendAsync(HttpMethod.Put, AmbientPath(deviceId), token, body);

    public static Task<HttpResponseMessage> StopAmbientAsync(this ServerHarness harness, string? token, string deviceId) =>
        harness.SendAsync(HttpMethod.Delete, AmbientPath(deviceId), token);

    /// <summary>
    /// Starts ambient sound on <paramref name="device"/> as <see cref="StartAmbientAsync(ServerHarness, string?, string, string?)"/>
    /// does, asserts its sound began, and returns the track it plays.
    /// </summary>
    public static async Task<string> PlayAmbientAsync(this ServerHarness harness, TestDevice device, string? track = null)
    {
        using var response = await harness.StartAmbientAsync(harness.UserToken(), device.DeviceId, track);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"The start on device {device.DeviceId} answered {(int)response.StatusCode}: {body}"
        );

        var answer = ReadDevice(JsonNode.Parse(body));
        Assert.Equal(device.DeviceId, answer.Id);
        return answer.Track!;
    }

    /// <summary>Stops the ambient sound on <paramref name="device"/>, asserting the stop answered 204 once it had ended.</summary>
    public static async Task EndAmbientAsync(this ServerHarness harness, TestDevice device)
    {
        using var response = await harness.StopAmbientAsync(harness.UserToken(), device.DeviceId);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// The device <paramref name="deviceId"/> as the list answers it, once that answer meets
    /// <paramref name="until"/>: the test publishes as the device does, and the server reads the
    /// broker's messages beside the web host's requests, so a route sees them only once it has.
    /// </summary>
    public static async Task<ListedDevice> AwaitDeviceAsync(
        this ServerHarness harness,
        string deviceId,
        Func<ListedDevice, bool> until
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeouts.Observe);

        ListedDevice? last = null;
        try
        {
            while (true)
            {
                using (var response = await harness.ListDevicesAsync(harness.UserToken()))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var devices = Assert.IsType<JsonArray>(
                        JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))
                    );
                    last = devices.Select(ReadDevice).SingleOrDefault(device => device.Id == deviceId);
                    if (last != null && until(last))
                        return last;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Waited {Timeouts.Observe.TotalSeconds:0} s for device {deviceId} to be listed as the test expects; it was last listed as {last?.ToString() ?? "nothing"}."
            );
        }
    }

    /// <summary>
    /// Connects a device as a ToiletAutomation unit is: under <see cref="ToiletPrefix"/>, with
    /// no microphone, playing <see cref="ToiletFormat"/>. It comes online, and the server has
    /// read it once this returns.
    /// </summary>
    public static async Task<TestDevice> AddToiletAsync(this ServerHarness harness, string deviceId)
    {
        var toilet = await harness.AddDeviceAsync(deviceId, ToiletPrefix);
        await toilet.PublishConfigurationAsync(ToiletConfiguration(toilet));
        await toilet.PublishStateAsync();
        await harness.AwaitDeviceAsync(deviceId, device => device.Online);
        return toilet;
    }

    /// <summary>The configuration a ToiletAutomation unit publishes: no <c>in</c> format, and <see cref="ToiletFormat"/> out.</summary>
    public static JsonObject ToiletConfiguration(TestDevice toilet)
    {
        var configuration = toilet.ConfigurationWithoutMicrophone();
        configuration["audio_formats"]!["out"]!["sample_rate"] = ToiletFormat.SampleRate;
        return configuration;
    }

    /// <summary>
    /// The device an answer holds, an object of exactly <c>id</c>, <c>prefix</c>,
    /// <c>microphone</c>, <c>online</c>, <c>format</c> and <c>track</c>; its format as JSON text.
    /// </summary>
    public static ListedDevice ReadDevice(JsonNode? answer)
    {
        var device = Assert.IsType<JsonObject>(answer);
        Assert.Equal(
            ["format", "id", "microphone", "online", "prefix", "track"],
            device.Select(property => property.Key).Order(StringComparer.Ordinal)
        );
        return new ListedDevice(
            (string)device["id"]!,
            (string)device["prefix"]!,
            (bool)device["microphone"]!,
            (bool)device["online"]!,
            device["format"]?.ToJsonString(),
            (string?)device["track"]
        );
    }
}

/// <summary>A device as the device routes answer it, its <paramref name="Format"/> as JSON text.</summary>
internal sealed record ListedDevice(
    string Id,
    string Prefix,
    bool Microphone,
    bool Online,
    string? Format,
    string? Track
);
