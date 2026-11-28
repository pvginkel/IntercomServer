using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The device routes, under <c>/api/devices</c>, as the tests and the operator by hand call them
/// with a token holding the client role <c>user</c>: the list, and the start and stop of a
/// device's ambient sound. A device is named by its id and answered as an object of <c>id</c>,
/// <c>prefix</c>, <c>microphone</c>, <c>online</c>, <c>format</c> and <c>track</c>. A start the
/// player refuses answers a problem whose <c>detail</c> says why: 404 for a device or track it
/// does not know, and 409 for a device it cannot play on or a track it cannot play. The devices
/// it plays on are those under <c>AMBIENT_TOPIC_PREFIXES</c>.
/// </summary>
public sealed class DeviceApiTests
{
    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_device_route_answers_401_without_a_valid_token_and_403_without_the_user_role()
    {
        await using var harness = await ServerHarness.StartAsync();
        var issuer = harness.Issuer;
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        var routes = new Dictionary<string, Func<string?, Task<HttpResponseMessage>>>
        {
            ["list"] = token => harness.ListDevicesAsync(token),
            ["start"] = token => harness.StartAmbientAsync(token, toilet.DeviceId),
            ["stop"] = token => harness.StopAmbientAsync(token, toilet.DeviceId),
        };
        var tokens = new Dictionary<string, (string? Token, HttpStatusCode Expected)>
        {
            ["no token"] = (null, HttpStatusCode.Unauthorized),
            ["a malformed token"] = ("not-a-token", HttpStatusCode.Unauthorized),
            ["pipeline"] = (issuer.TokenWithRoles("pipeline"), HttpStatusCode.Forbidden),
            ["user on another client"] = (
                issuer.CreateToken(clientRoles: new Dictionary<string, string[]> { ["another-client"] = ["user"] }),
                HttpStatusCode.Forbidden
            ),
            ["user as a realm role"] = (issuer.CreateToken(realmRoles: ["user"]), HttpStatusCode.Forbidden),
        };

        var answers = new Dictionary<string, (HttpStatusCode, string)>();
        foreach (var (route, call) in routes)
        foreach (var (kind, (token, _)) in tokens)
        {
            using var response = await call(token);
            answers[$"{route} with {kind}"] = (response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation));
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        }

        Assert.Equal(
            routes.Keys.SelectMany(route => tokens.Select(token => ($"{route} with {token.Key}", (token.Value.Expected, ""))))
                .ToDictionary(),
            answers
        );
        // Started, it would be playing.
        await harness.PlayAmbientAsync(toilet);
        Assert.Equal(0, (await toilet.NextPacketAsync()).Index);
    }

    [Fact]
    public async Task The_list_answers_each_device_by_id_with_its_prefix_microphone_online_format_and_track_sorted_by_id()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var stateOnly = await harness.AddDeviceAsync("0x0a0002");
        await stateOnly.PublishStateAsync();
        var playing = await harness.AddToiletAsync("0x0b0001");
        var offline = await harness.AddDeviceAsync("0x0b0002", DeviceApi.ToiletPrefix);
        await offline.PublishStateAsync(online: false);
        await offline.PublishConfigurationAsync(DeviceApi.ToiletConfiguration(offline));
        var stereo = await harness.AddDeviceAsync("0x0b0003", DeviceApi.ToiletPrefix);
        var stereoConfiguration = DeviceApi.ToiletConfiguration(stereo);
        stereoConfiguration["audio_formats"]!["out"]!["channel_layout"] = "stereo";
        await stereo.PublishConfigurationAsync(stereoConfiguration);
        await stereo.PublishStateAsync();
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        await harness.PlayAmbientAsync(playing);
        await harness.AwaitDeviceAsync(intercom.DeviceId, device => device.Online);
        await harness.AwaitDeviceAsync(stateOnly.DeviceId, device => device.Online);
        await harness.AwaitDeviceAsync(offline.DeviceId, device => device.Format != null);
        await harness.AwaitDeviceAsync(stereo.DeviceId, device => device.Online);

        using var response = await harness.ListDevicesAsync(harness.UserToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var listed = Assert.IsType<JsonArray>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancellation)));
        const string intercomFormat = """{"channel_layout":"mono","sample_rate":16000,"bit_rate":16}""";
        Assert.Equal(
            [
                new ListedDevice("0x0a0001", "intercom/client", true, true, intercomFormat, null),
                new ListedDevice("0x0a0002", "intercom/client", false, true, null, null),
                new ListedDevice("0x0b0001", "toilet_automation", false, true, DeviceApi.ToiletFormatAnswer, "Rain"),
                new ListedDevice("0x0b0002", "toilet_automation", false, false, DeviceApi.ToiletFormatAnswer, null),
                new ListedDevice("0x0b0003", "toilet_automation", false, true, null, null),
            ],
            listed.Select(DeviceApi.ReadDevice)
        );
    }

    [Fact]
    public async Task An_unknown_device_or_track_answers_404_and_a_stop_with_nothing_playing_204()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        var token = harness.UserToken();

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var (call, send) in new Dictionary<string, Func<Task<HttpResponseMessage>>>
                 {
                     ["start on an unknown device"] = () => harness.StartAmbientAsync(token, "0x0f0001"),
                     ["stop on an unknown device"] = () => harness.StopAmbientAsync(token, "0x0f0001"),
                     ["start of an unknown track"] = () => harness.StartAmbientAsync(token, toilet.DeviceId, "Snow"),
                 })
        {
            using var response = await send();
            answers[call] = (response.StatusCode, await TrackApi.ReadProblemDetailAsync(response));
        }

        Assert.Equal(
            new Dictionary<string, (HttpStatusCode, string?)>
            {
                ["start on an unknown device"] = (HttpStatusCode.NotFound, "There is no device '0x0f0001'."),
                ["stop on an unknown device"] = (HttpStatusCode.NotFound, "There is no device '0x0f0001'."),
                ["start of an unknown track"] = (HttpStatusCode.NotFound, "There is no track named 'Snow'."),
            },
            answers
        );
        using (var stop = await harness.StopAmbientAsync(token, toilet.DeviceId))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            Assert.Empty(await stop.Content.ReadAsByteArrayAsync(Cancellation));
        }

        await harness.PlayAmbientAsync(toilet);
        Assert.Equal(0, (await toilet.NextPacketAsync()).Index);
    }

    [Fact]
    public async Task A_start_on_a_device_the_player_cannot_play_on_answers_409_and_sends_it_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        var offline = await harness.AddDeviceAsync("0x0b0001", DeviceApi.ToiletPrefix);
        await offline.PublishStateAsync(online: false);
        await offline.PublishConfigurationAsync(DeviceApi.ToiletConfiguration(offline));
        await harness.AwaitDeviceAsync(offline.DeviceId, device => device.Format != null);
        var unconfigured = await harness.AddDeviceAsync("0x0b0002", DeviceApi.ToiletPrefix);
        var noEndpoint = await harness.AddDeviceAsync("0x0b0003", DeviceApi.ToiletPrefix);
        var badEndpoint = await harness.AddDeviceAsync("0x0b0004", DeviceApi.ToiletPrefix);
        var stereo = await harness.AddDeviceAsync("0x0b0005", DeviceApi.ToiletPrefix);
        var tooFast = await harness.AddDeviceAsync("0x0b0006", DeviceApi.ToiletPrefix);
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var configurations = new Dictionary<TestDevice, Action<JsonObject>>
        {
            [noEndpoint] = configuration => configuration.Remove("endpoint"),
            [badEndpoint] = configuration => configuration["endpoint"] = "nowhere",
            [stereo] = configuration => configuration["audio_formats"]!["out"]!["channel_layout"] = "stereo",
            [tooFast] = configuration => configuration["audio_formats"]!["out"]!["sample_rate"] = 384000,
        };
        foreach (var (device, edit) in configurations)
        {
            var configuration = DeviceApi.ToiletConfiguration(device);
            edit(configuration);
            await device.PublishConfigurationAsync(configuration);
        }
        await intercom.PublishConfigurationAsync();
        foreach (var device in (TestDevice[])[unconfigured, noEndpoint, badEndpoint, stereo, tooFast, intercom])
        {
            await device.PublishStateAsync();
            await harness.AwaitDeviceAsync(device.DeviceId, listed => listed.Online);
        }
        var refused = (TestDevice[])[offline, unconfigured, noEndpoint, badEndpoint, stereo, tooFast, intercom];

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var device in refused)
        {
            using var response = await harness.StartAmbientAsync(harness.UserToken(), device.DeviceId);
            answers[device.DeviceId] = (response.StatusCode, await TrackApi.ReadProblemDetailAsync(response));
        }

        const string noFormat =
            "advertises no audio format the server can produce: mono 16-bit PCM at 8 to 192 kHz.";
        Assert.Equal(
            new Dictionary<string, (HttpStatusCode, string?)>
            {
                ["0x0b0001"] = (HttpStatusCode.Conflict, "Device 0x0b0001 is offline."),
                ["0x0b0002"] = (
                    HttpStatusCode.Conflict,
                    "Device 0x0b0002 advertises no audio endpoint the server can send to."
                ),
                ["0x0b0003"] = (
                    HttpStatusCode.Conflict,
                    "Device 0x0b0003 advertises no audio endpoint the server can send to."
                ),
                ["0x0b0004"] = (
                    HttpStatusCode.Conflict,
                    "Device 0x0b0004 advertises no audio endpoint the server can send to."
                ),
                ["0x0b0005"] = (HttpStatusCode.Conflict, $"Device 0x0b0005 {noFormat}"),
                ["0x0b0006"] = (HttpStatusCode.Conflict, $"Device 0x0b0006 {noFormat}"),
                ["0x0a0001"] = (
                    HttpStatusCode.Conflict,
                    "Device 0x0a0001 is under 'intercom/client', which AMBIENT_TOPIC_PREFIXES does not name."
                ),
            },
            answers
        );

        // A device the player can play on gets its sound, and by then the refused ones would have theirs.
        var control = await harness.AddToiletAsync("0x0b0007");
        await harness.PlayAmbientAsync(control);
        await control.NextPacketsAsync(10);
        Assert.All(refused, device => Assert.Equal(0, device.ReceivedPacketCount));
    }

    [Fact]
    public async Task A_start_of_a_track_that_does_not_decode_or_decodes_to_no_audio_answers_409_and_sends_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        Assert.NotNull((await harness.UploadAsync("Broken", Tracks.UndecodableWav())).Track);
        Assert.NotNull((await harness.UploadAsync("Empty", await Tracks.EmptyFlacAsync())).Track);
        var token = harness.UserToken();

        using (var broken = await harness.StartAmbientAsync(token, toilet.DeviceId, "Broken"))
        {
            Assert.Equal(HttpStatusCode.Conflict, broken.StatusCode);
            Assert.StartsWith(
                "FFmpeg could not decode Broken: it exited with code ",
                await TrackApi.ReadProblemDetailAsync(broken)
            );
        }

        using (var empty = await harness.StartAmbientAsync(token, toilet.DeviceId, "Empty"))
        {
            Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
            // An FFmpeg that fails an input it decodes nothing of says so in its own words.
            var detail = await TrackApi.ReadProblemDetailAsync(empty);
            Assert.True(
                detail == "The track 'Empty' decodes to no audio."
                    || detail!.StartsWith("FFmpeg could not decode Empty: ", StringComparison.Ordinal),
                $"The start answered {detail}"
            );
        }

        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        await harness.PlayAmbientAsync(toilet, "Rain");
        Assert.Equal(0, (await toilet.NextPacketAsync()).Index);
    }

    [Fact]
    public async Task A_start_has_no_body_or_a_JSON_object_with_an_optional_track_and_any_other_answers_400()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        var token = harness.UserToken();

        foreach (var body in (HttpContent[])
                 [
                     new StringContent("Rain", Encoding.UTF8, "text/plain"),
                     new StringContent("""{"track":""", Encoding.UTF8, "application/json"),
                     new StringContent("[1]", Encoding.UTF8, "application/json"),
                     new StringContent("null", Encoding.UTF8, "application/json"),
                     new StringContent("""{"track":5}""", Encoding.UTF8, "application/json"),
                 ])
        {
            using var response = await harness.StartAmbientAsync(token, toilet.DeviceId, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                "A start has no body, or a JSON object with an optional track.",
                await TrackApi.ReadProblemDetailAsync(response)
            );
        }
        Assert.Equal(0, toilet.ReceivedPacketCount);

        foreach (var body in (HttpContent?[])[JsonContent.Create(new { }), null])
        {
            using var response = await harness.StartAmbientAsync(token, toilet.DeviceId, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                new ListedDevice("0x0b0001", "toilet_automation", false, true, DeviceApi.ToiletFormatAnswer, "Rain"),
                DeviceApi.ReadDevice(JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancellation)))
            );
            await harness.EndAmbientAsync(toilet);
        }
    }

    [Fact]
    public async Task The_setting_names_the_prefixes_the_player_plays_on_each_trimmed()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_TOPIC_PREFIXES"] = " intercom/client , toilet_automation " }
        );
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        await harness.AwaitDeviceAsync(intercom.DeviceId, device => device.Online);
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));

        await harness.PlayAmbientAsync(intercom);
        await harness.PlayAmbientAsync(toilet);

        // An intercom is sent its sound in its own format: 20 ms at 16 kHz in one packet.
        Assert.Equal(640, (await intercom.NextPacketAsync()).Payload.Length);
        Assert.Equal(960, (await toilet.NextPacketAsync()).Payload.Length);
    }

    [Theory]
    [InlineData("AMBIENT_TOPIC_PREFIXES", "kitchen", "AMBIENT_TOPIC_PREFIXES holds 'kitchen', which is not one of DEVICES_TOPIC_PREFIXES")]
    [InlineData("AMBIENT_TOPIC_PREFIXES", "toilet_automation,", "AMBIENT_TOPIC_PREFIXES holds '', which is not one of DEVICES_TOPIC_PREFIXES")]
    [InlineData("AMBIENT_TOPIC_PREFIXES", "toilet_automation/kitchen", "AMBIENT_TOPIC_PREFIXES holds 'toilet_automation/kitchen', which is not one of DEVICES_TOPIC_PREFIXES")]
    [InlineData("AMBIENT_TOPIC_PREFIXES", "intercom/server", "AMBIENT_TOPIC_PREFIXES holds 'intercom/server', which is not one of DEVICES_TOPIC_PREFIXES")]
    [InlineData("AMBIENT_LOUDNESS_LUFS", "NaN", "AMBIENT_LOUDNESS_LUFS must be a loudness in LUFS.")]
    [InlineData("AMBIENT_LOUDNESS_LUFS", "-Infinity", "AMBIENT_LOUDNESS_LUFS must be a loudness in LUFS.")]
    [InlineData("AMBIENT_FADE_MS", "-1", "AMBIENT_FADE_MS must be 0 or more milliseconds.")]
    public async Task A_setting_the_player_cannot_play_by_stops_the_start(string setting, string value, string message)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(new Dictionary<string, string?> { [setting] = value })
        );

        Assert.StartsWith(message, error.Message);
    }
}
