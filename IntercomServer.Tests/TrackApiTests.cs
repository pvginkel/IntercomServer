using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The track API, under <c>/api/ambient-tracks</c>, as the app calls it with a token holding the
/// client role <c>user</c>: the list, the upload, a change, the delete and the audio, over the
/// track store's real files and the real ffprobe. A track is answered as an object of
/// <c>name</c>, <c>state</c>, one of <c>ambient</c>, <c>occasional</c> and <c>disabled</c>, and
/// <c>duration</c> in seconds; what the store refuses answers a problem whose <c>detail</c> says
/// why: 400, 404, 409 or 413.
/// </summary>
public sealed class TrackApiTests
{
    private static readonly TimeSpan Length = TimeSpan.FromSeconds(3);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_track_route_answers_401_without_a_valid_token_and_403_without_the_user_role()
    {
        await using var harness = await ServerHarness.StartAsync();
        var issuer = harness.Issuer;
        var recording = await Tracks.RecordingAsync("m4a", Length);
        await harness.UploadAsync("Rain", recording);
        var routes = new Dictionary<string, Func<string?, Task<HttpResponseMessage>>>
        {
            ["list"] = token => harness.ListTracksAsync(token),
            ["upload"] = token => harness.UploadTrackAsync(token, "Snow", recording),
            ["change"] = token => harness.ChangeTrackAsync(token, "Rain", new { state = "disabled" }),
            ["delete"] = token => harness.DeleteTrackAsync(token, "Rain"),
            ["audio"] = token => harness.GetTrackAudioAsync(token, "Rain"),
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
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles("disabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task The_list_answers_each_track_by_name_state_and_duration_in_seconds_sorted_by_name()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();

        using var none = await harness.ListTracksAsync(token);
        foreach (var (name, seconds) in new[] { ("cherry", 3), ("Banana", 2), ("apple", 1) })
        {
            using var uploaded = await harness.UploadTrackAsync(
                token,
                name,
                await Tracks.RecordingAsync("wav", TimeSpan.FromSeconds(seconds))
            );
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }
        using var disabled = await harness.ChangeTrackAsync(token, "Banana", new { state = "disabled" });
        using var occasional = await harness.ChangeTrackAsync(token, "cherry", new { state = "occasional" });
        using var three = await harness.ListTracksAsync(token);

        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal("[]", await none.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, occasional.StatusCode);
        Assert.Equal(HttpStatusCode.OK, three.StatusCode);
        Assert.Equal("application/json", three.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            """[{"name":"apple","state":"ambient","duration":1},{"name":"Banana","state":"disabled","duration":2},{"name":"cherry","state":"occasional","duration":3}]""",
            await three.Content.ReadAsStringAsync(Cancellation)
        );
    }

    [Fact]
    public async Task An_upload_answers_201_with_the_track_now_an_Ambient_file_of_its_name()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);

        using var response = await harness.UploadTrackAsync(harness.UserToken(), "Rain on the skylight", recording);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("/api/ambient-tracks/Rain%20on%20the%20skylight", response.Headers.Location?.OriginalString);
        var (name, state, duration) = TrackApi.ReadTrack(await response.Content.ReadFromJsonAsync<JsonNode>(Cancellation));
        Assert.Equal("Rain on the skylight", name);
        Assert.Equal("ambient", state);
        AssertLasts(Length, duration);
        Assert.Equal(["Rain on the skylight.m4a"], harness.AmbientFiles("enabled"));
        Assert.Equal(
            recording,
            await File.ReadAllBytesAsync(
                Path.Combine(harness.AmbientDirectory(), "enabled", "Rain on the skylight.m4a"),
                Cancellation
            )
        );
    }

    [Fact]
    public async Task A_name_that_breaks_the_rules_or_a_file_that_is_not_audio_answers_400_and_writes_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        var uploads = new Dictionary<string, (string Name, byte[] File, string Detail)>
        {
            ["an empty name"] = ("", recording, "A track's name cannot be empty."),
            ["a slash"] = ("Rain/Snow", recording, "A track's name cannot hold '/'."),
            ["a leading space"] = (" Rain", recording, "A track's name cannot start or end with a space or a dot."),
            ["81 characters"] = (
                new string('a', TrackStore.MaxNameLength + 1),
                recording,
                "A track's name is at most 80 characters, and this one has more."
            ),
            ["5,000 characters"] = (
                new string('a', 5000),
                recording,
                "A track's name is at most 80 characters, and this one has more."
            ),
            ["noise"] = (
                "Rain",
                Tracks.Noise(),
                "The file is not audio the server can play: ffprobe cannot read it: Invalid data found when processing input."
            ),
            ["a video"] = (
                "Rain",
                await Tracks.VideoAsync(),
                "The file is not audio the server can play: it holds no audio stream."
            ),
        };

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var (kind, (name, file, _)) in uploads)
        {
            using var response = await harness.UploadTrackAsync(token, name, file);
            answers[kind] = (response.StatusCode, await TrackApi.ReadProblemDetailAsync(response));
        }
        var longest = new string('a', TrackStore.MaxNameLength);
        using var taken = await harness.UploadTrackAsync(token, longest, recording);

        Assert.Equal(
            uploads.ToDictionary(upload => upload.Key, upload => (HttpStatusCode.BadRequest, (string?)upload.Value.Detail)),
            answers
        );
        Assert.Equal(HttpStatusCode.Created, taken.StatusCode);
        Assert.Equal([$"{longest}.m4a"], harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task An_upload_that_is_not_a_form_of_name_then_file_answers_400()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        var forms = new Dictionary<string, HttpContent?>
        {
            ["no body"] = null,
            ["JSON"] = new StringContent("""{"name":"Rain"}""", Encoding.UTF8, "application/json"),
            ["a URL-encoded form"] = new FormUrlEncodedContent([new("name", "Rain")]),
            ["a file alone"] = new MultipartFormDataContent { { new ByteArrayContent(recording), "file", "rain.m4a" } },
            ["a name alone"] = new MultipartFormDataContent { { new StringContent("Rain"), "name" } },
            ["a file, then a name"] = new MultipartFormDataContent
            {
                { new ByteArrayContent(recording), "file", "rain.m4a" },
                { new StringContent("Rain"), "name" },
            },
            ["another field first"] = new MultipartFormDataContent
            {
                { new StringContent("Rain"), "title" },
                { new StringContent("Rain"), "name" },
                { new ByteArrayContent(recording), "file", "rain.m4a" },
            },
            ["a name, then another field"] = new MultipartFormDataContent
            {
                { new StringContent("Rain"), "name" },
                { new ByteArrayContent(recording), "audio", "rain.m4a" },
            },
        };

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var (kind, form) in forms)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ambient-tracks") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await harness.SendAsync(request);
            answers[kind] = (response.StatusCode, await TrackApi.ReadProblemDetailAsync(response));
        }

        Assert.Equal(
            forms.Keys.ToDictionary(
                kind => kind,
                _ => (HttpStatusCode.BadRequest, (string?)"An upload is a multipart form of name, then file.")
            ),
            answers
        );
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task An_upload_of_a_name_a_track_in_any_folder_has_ignoring_case_answers_409()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var first = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("m4a", Length));
        using var disabled = await harness.ChangeTrackAsync(token, "Rain", new { state = "disabled" });

        using var response = await harness.UploadTrackAsync(token, "RAIN", await Tracks.RecordingAsync("wav", Length));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("The name 'RAIN' is taken, by disabled/Rain.m4a.", await TrackApi.ReadProblemDetailAsync(response));
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("disabled"));
    }

    [Fact]
    public async Task An_upload_lasting_longer_than_the_limit_answers_413()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["TRACK_MAX_SECONDS"] = "2" }
        );

        using var response = await harness.UploadTrackAsync(
            harness.UserToken(),
            "Rain",
            await Tracks.RecordingAsync("wav", Length)
        );

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("A track lasts at most 2 s, and this one lasts 3 s.", await TrackApi.ReadProblemDetailAsync(response));
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task The_designs_50_MB_upload_gets_through_unless_the_limit_is_set()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("wav", TimeSpan.FromSeconds(580));

        using var response = await harness.UploadTrackAsync(harness.UserToken(), "Rain", recording);

        Assert.InRange(recording.Length, 50_000_001, 50 * 1024 * 1024);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["Rain.wav"], harness.AmbientFiles("enabled"));
    }

    [Fact]
    public async Task An_upload_as_large_as_the_size_limit_gets_through_and_one_a_byte_larger_answers_413()
    {
        // Larger than the default limit, so the setting is what lets it through.
        var recording = await Tracks.RecordingAsync("wav", TimeSpan.FromSeconds(660));
        Assert.True(recording.Length > 50 * 1024 * 1024);

        await using (
            var asLarge = await ServerHarness.StartAsync(
                new Dictionary<string, string?> { ["TRACK_MAX_BYTES"] = recording.Length.ToString() }
            )
        )
        {
            using var response = await asLarge.UploadTrackAsync(asLarge.UserToken(), "Rain", recording);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(["Rain.wav"], asLarge.AmbientFiles("enabled"));
        }

        await using (
            var smaller = await ServerHarness.StartAsync(
                new Dictionary<string, string?> { ["TRACK_MAX_BYTES"] = (recording.Length - 1).ToString() }
            )
        )
        {
            using var response = await smaller.UploadTrackAsync(smaller.UserToken(), "Rain", recording);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal(
                $"A track's file holds at most {recording.Length - 1} bytes, and this one holds more.",
                await TrackApi.ReadProblemDetailAsync(response)
            );
            Assert.Empty(smaller.AmbientFiles("enabled"));
            Assert.Empty(smaller.AmbientFiles(".incoming"));
        }
    }

    [Fact]
    public async Task An_upload_past_the_size_limit_is_refused_before_all_of_it_has_arrived()
    {
        const int maxBytes = 64 * 1024;
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["TRACK_MAX_BYTES"] = maxBytes.ToString() }
        );
        var dropped = Channel.CreateUnbounded<FileSystemEventArgs>();
        using var watcher = new FileSystemWatcher(Path.Combine(harness.AmbientDirectory(), ".incoming"))
        {
            NotifyFilter = NotifyFilters.FileName,
        };
        watcher.Deleted += (_, e) => dropped.Writer.TryWrite(e);
        watcher.EnableRaisingEvents = true;
        // Held well past the limit, so what the server needs to refuse it has been sent, and its tail has not.
        var arriving = new HeldStream(new byte[maxBytes + 512 * 1024], holdAt: maxBytes + 256 * 1024);

        var uploading = harness.UploadTrackAsync(
            harness.UserToken(),
            TrackApi.UploadForm("Rain", new StreamContent(arriving))
        );
        await Timeouts.NextAsync(dropped.Reader, "the server to delete the staged upload");
        arriving.Release();
        using var response = await uploading;

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            $"A track's file holds at most {maxBytes} bytes, and this one holds more.",
            await TrackApi.ReadProblemDetailAsync(response)
        );
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task An_upload_far_past_the_size_limit_answers_413()
    {
        const int maxBytes = 64 * 1024;
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["TRACK_MAX_BYTES"] = maxBytes.ToString() }
        );

        using var response = await harness.UploadTrackAsync(harness.UserToken(), "Rain", new byte[16 * 1024 * 1024]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            $"A track's file holds at most {maxBytes} bytes, and this one holds more.",
            await TrackApi.ReadProblemDetailAsync(response)
        );
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task A_change_renames_the_track_and_moves_it_between_any_two_states_and_answers_it()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var uploaded = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("wav", Length));

        using var renamed = await harness.ChangeTrackAsync(token, "Rain", new { name = "Rain on the skylight" });
        var afterRename = Placed(harness);
        using var both = await harness.ChangeTrackAsync(token, "rain on the skylight", new { name = "Rain", state = "occasional" });
        var afterBoth = Placed(harness);
        // With the change before, each of the six moves from one state to another, once.
        var moves = new List<(string State, string Folder, (string, string, double) Answer, string[] Placed)>();
        foreach (var (state, folder) in new[]
                 {
                     ("disabled", "disabled"),
                     ("ambient", "enabled"),
                     ("disabled", "disabled"),
                     ("occasional", "occasional"),
                     ("ambient", "enabled"),
                 })
        {
            using var moved = await harness.ChangeTrackAsync(token, "Rain", new { state });
            moves.Add((state, folder, await ReadTrackAsync(moved), Placed(harness)));
        }
        using var nothing = await harness.ChangeTrackAsync(token, "Rain", new { });

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal(("Rain on the skylight", "ambient", 3.0), await ReadTrackAsync(renamed));
        Assert.Equal(["enabled/Rain on the skylight.wav"], afterRename);
        Assert.Equal(("Rain", "occasional", 3.0), await ReadTrackAsync(both));
        Assert.Equal(["occasional/Rain.wav"], afterBoth);
        Assert.All(
            moves,
            move =>
            {
                Assert.Equal(("Rain", move.State, 3.0), move.Answer);
                Assert.Equal([$"{move.Folder}/Rain.wav"], move.Placed);
            }
        );
        Assert.Equal(("Rain", "ambient", 3.0), await ReadTrackAsync(nothing));
        Assert.Equal(["enabled/Rain.wav"], Placed(harness));
    }

    [Fact]
    public async Task A_change_to_a_name_taken_answers_409_and_to_one_breaking_the_rules_400()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var rain = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("m4a", Length));
        using var snow = await harness.UploadTrackAsync(token, "Snow", await Tracks.RecordingAsync("wav", Length));

        using var taken = await harness.ChangeTrackAsync(token, "Rain", new { name = "SNOW", state = "disabled" });
        using var broken = await harness.ChangeTrackAsync(token, "Rain", new { name = "Rain?", state = "occasional" });

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("The name 'SNOW' is taken, by enabled/Snow.wav.", await TrackApi.ReadProblemDetailAsync(taken));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("A track's name cannot hold '?'.", await TrackApi.ReadProblemDetailAsync(broken));
        Assert.Equal(["enabled/Rain.m4a", "enabled/Snow.wav"], Placed(harness));
    }

    [Fact]
    public async Task A_change_that_is_not_a_JSON_object_of_name_and_state_answers_400()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var uploaded = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("m4a", Length));
        var changes = new Dictionary<string, HttpContent>
        {
            ["plain text"] = new StringContent("""{"state":"disabled"}""", Encoding.UTF8, "text/plain"),
            ["no JSON"] = new StringContent("", Encoding.UTF8, "application/json"),
            ["broken JSON"] = new StringContent("""{"state":""", Encoding.UTF8, "application/json"),
            ["null"] = new StringContent("null", Encoding.UTF8, "application/json"),
            ["an array"] = new StringContent("""["disabled"]""", Encoding.UTF8, "application/json"),
            ["state as a boolean"] = new StringContent("""{"state":false}""", Encoding.UTF8, "application/json"),
            ["state as a number"] = new StringContent("""{"state":2}""", Encoding.UTF8, "application/json"),
            ["name as a number"] = new StringContent("""{"name":5}""", Encoding.UTF8, "application/json"),
        };

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var (kind, change) in changes)
        {
            using var response = await harness.ChangeTrackAsync(token, "Rain", change);
            answers[kind] = (response.StatusCode, await TrackApi.ReadProblemDetailAsync(response));
        }

        Assert.Equal(
            changes.Keys.ToDictionary(
                kind => kind,
                _ => (HttpStatusCode.BadRequest, (string?)"A change is a JSON object of name, state or both.")
            ),
            answers
        );
        Assert.Equal(["enabled/Rain.m4a"], Placed(harness));
    }

    [Fact]
    public async Task A_change_to_a_state_other_than_ambient_occasional_or_disabled_answers_400_and_changes_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var uploaded = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("m4a", Length));
        var changes = new Dictionary<string, object>
        {
            ["enabled"] = new { state = "enabled" },
            ["a code in another case"] = new { state = "Occasional" },
            ["a code padded"] = new { state = " disabled" },
            ["an empty code"] = new { state = "" },
            ["another word"] = new { state = "random" },
            ["with a rename"] = new { name = "Snow", state = "on" },
        };

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var (kind, change) in changes)
        {
            using var response = await harness.ChangeTrackAsync(token, "Rain", change);
            answers[kind] = (response.StatusCode, await TrackApi.ReadProblemDetailAsync(response));
        }

        Assert.Equal(
            changes.Keys.ToDictionary(
                kind => kind,
                _ => (HttpStatusCode.BadRequest, (string?)"A track's state is ambient, occasional or disabled.")
            ),
            answers
        );
        Assert.Equal(["enabled/Rain.m4a"], Placed(harness));
    }

    [Fact]
    public async Task A_delete_answers_204_and_removes_the_file()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var uploaded = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("m4a", Length));

        using var deleted = await harness.DeleteTrackAsync(token, "Rain");
        using var list = await harness.ListTracksAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("", await deleted.Content.ReadAsStringAsync(Cancellation));
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Equal("[]", await list.Content.ReadAsStringAsync(Cancellation));
    }

    [Fact]
    public async Task A_change_delete_or_audio_of_a_name_no_track_has_answers_404()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        using var uploaded = await harness.UploadTrackAsync(token, "Rain", await Tracks.RecordingAsync("m4a", Length));

        using var changed = await harness.ChangeTrackAsync(token, "Hail", new { state = "disabled" });
        using var deleted = await harness.DeleteTrackAsync(token, "Hail");
        using var audio = await harness.GetTrackAudioAsync(token, "Hail");

        foreach (var response in new[] { changed, deleted, audio })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("There is no track named 'Hail'.", await TrackApi.ReadProblemDetailAsync(response));
        }
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
    }

    [Theory]
    [InlineData("m4a", "audio/mp4")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("wav", "audio/wav")]
    [InlineData("webm", "video/webm")]
    public async Task The_audio_answers_the_file_and_a_range_of_it_206(string format, string contentType)
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        var recording = await Tracks.RecordingAsync(format, Length);
        using var uploaded = await harness.UploadTrackAsync(token, "Rain", recording);
        // A disabled track is played on the phone too.
        using var uploadedSnow = await harness.UploadTrackAsync(token, "Snow", recording);
        using var disabledSnow = await harness.ChangeTrackAsync(token, "Snow", new { state = "disabled" });

        using var whole = await harness.GetTrackAudioAsync(token, "Rain");
        using var part = await harness.GetTrackAudioAsync(token, "Rain", new RangeHeaderValue(100, 199));
        using var disabled = await harness.GetTrackAudioAsync(token, "Snow");

        Assert.Equal(["Snow." + format], harness.AmbientFiles("disabled"));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(contentType, disabled.Content.Headers.ContentType?.MediaType);
        Assert.Equal(recording, await disabled.Content.ReadAsByteArrayAsync(Cancellation));
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(contentType, whole.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["bytes"], whole.Headers.AcceptRanges);
        Assert.Equal(recording, await whole.Content.ReadAsByteArrayAsync(Cancellation));
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(100, 199, recording.Length), part.Content.Headers.ContentRange);
        Assert.Equal(recording[100..200], await part.Content.ReadAsByteArrayAsync(Cancellation));
    }

    [Fact]
    public async Task A_track_is_named_in_the_path_by_its_name_URL_encoded_in_any_case()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.UserToken();
        const string name = "Rain #2 + 50% — één";
        var recording = await Tracks.RecordingAsync("m4a", Length);

        using var uploaded = await harness.UploadTrackAsync(token, name, recording);
        using var audio = await harness.GetTrackAudioAsync(token, name.ToUpperInvariant());
        using var disabled = await harness.ChangeTrackAsync(token, name, new { state = "disabled" });
        var afterDisable = harness.AmbientFiles("disabled");
        using var deleted = await harness.DeleteTrackAsync(token, name.ToLowerInvariant());

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal(
            "/api/ambient-tracks/Rain%20%232%20%2B%2050%25%20%E2%80%94%20%C3%A9%C3%A9n",
            uploaded.Headers.Location?.OriginalString
        );
        Assert.Equal(recording, await audio.Content.ReadAsByteArrayAsync(Cancellation));
        Assert.Equal(name, (await ReadTrackAsync(disabled)).Name);
        Assert.Equal([$"{name}.m4a"], afterDisable);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(harness.AmbientFiles("disabled"));
    }

    private static async Task<(string Name, string State, double Duration)> ReadTrackAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return TrackApi.ReadTrack(await response.Content.ReadFromJsonAsync<JsonNode>(Cancellation));
    }

    // Every file of the three track folders, as its folder and its name.
    private static string[] Placed(ServerHarness harness) =>
        new[] { "enabled", "occasional", "disabled" }
            .SelectMany(folder => harness.AmbientFiles(folder).Select(file => $"{folder}/{file}"))
            .ToArray();

    private static void AssertLasts(TimeSpan expected, double seconds) =>
        Assert.InRange(seconds, expected.TotalSeconds - 0.1, expected.TotalSeconds + 0.1);
}
