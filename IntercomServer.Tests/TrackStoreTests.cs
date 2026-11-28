using System.Threading.Channels;
using IntercomServer.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// The track store: the ambient tracks are files under <c>&lt;DATA_DIR&gt;/ambient</c>, in the
/// folder of their state, <c>enabled</c> for Ambient, <c>occasional</c> and <c>disabled</c>, each
/// named by its file name without its extension, unique across the three folders ignoring case
/// and extension. An upload is written under <c>.incoming</c>, probed with ffprobe and only then
/// renamed into <c>enabled/</c>, an Ambient track; rename, a change of state and delete move or
/// remove the file. The files are real ones FFmpeg makes, and ffprobe reads them.
/// </summary>
public sealed class TrackStoreTests
{
    private static readonly TimeSpan Length = TimeSpan.FromSeconds(3);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("m4a")]
    [InlineData("mp3")]
    [InlineData("wav")]
    [InlineData("webm")]
    public async Task An_upload_is_an_Ambient_track_in_a_file_of_its_name_and_its_formats_extension(string format)
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync(format, Length);

        var result = await harness.UploadAsync("Rain on the skylight", recording);

        var track = Assert.IsType<Track>(result.Track);
        Assert.Equal("Rain on the skylight", track.Name);
        Assert.Equal(TrackState.Ambient, track.State);
        AssertLasts(Length, track.Duration);
        Assert.Equal([$"Rain on the skylight.{format}"], harness.AmbientFiles("enabled"));
        Assert.Equal(recording, await File.ReadAllBytesAsync(track.Path, Cancellation));
        Assert.Equal(
            Path.Combine(harness.AmbientDirectory(), "enabled", $"Rain on the skylight.{format}"),
            track.Path
        );
        Assert.Equal(track, Assert.Single(await harness.TrackStore().ListAsync(Cancellation)));
    }

    [Fact]
    public async Task An_upload_is_written_under_incoming_and_is_no_track_until_it_is_renamed_into_enabled()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        var arriving = new HeldStream(recording, recording.Length / 2);

        var uploading = harness.UploadAsync("Rain", arriving);
        await arriving.Held.WaitAsync(Timeouts.Observe, Cancellation);

        Assert.Single(harness.AmbientFiles(".incoming"));
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(await harness.TrackStore().ListAsync(Cancellation));

        arriving.Release();
        var result = await uploading;

        Assert.NotNull(result.Track);
        Assert.Empty(harness.AmbientFiles(".incoming"));
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
        Assert.Equal(recording, await File.ReadAllBytesAsync(result.Track.Path, Cancellation));
    }

    [Fact]
    public async Task An_upload_is_renamed_from_incoming_into_enabled()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        var arrivals = Channel.CreateUnbounded<FileSystemEventArgs>();
        using var watcher = new FileSystemWatcher(harness.AmbientDirectory())
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
        };
        watcher.Created += (_, e) => arrivals.Writer.TryWrite(e);
        watcher.Renamed += (_, e) => arrivals.Writer.TryWrite(e);
        watcher.EnableRaisingEvents = true;

        var result = await harness.UploadAsync("Rain", recording);
        var track = Assert.IsType<Track>(result.Track);
        FileSystemEventArgs arrival;
        do
            arrival = await Timeouts.NextAsync(arrivals.Reader, "Rain.m4a to appear in enabled/");
        while (arrival.FullPath != track.Path);

        var renamed = Assert.IsType<RenamedEventArgs>(arrival);
        Assert.Equal(Path.Combine(harness.AmbientDirectory(), ".incoming"), Path.GetDirectoryName(renamed.OldFullPath));
    }

    public static TheoryData<string> BrokenNames =>
    [
        "",
        new string('a', TrackStore.MaxNameLength + 1),
        "Rain/Snow",
        "Rain\\Snow",
        "Rain:Snow",
        "Rain*",
        "Rain?",
        "\"Rain\"",
        "<Rain>",
        "Rain|Snow",
        "Rain\u0001",
        "Rain\nSnow",
        "Rain\u007f",
        " Rain",
        "Rain ",
        ".Rain",
        "Rain.",
        ".",
        "..",
    ];

    [Fact]
    public async Task A_name_that_breaks_the_rules_is_refused_and_nothing_is_written()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("wav", Length);

        foreach (var name in BrokenNames)
        {
            var result = await harness.UploadAsync(name, recording);

            Assert.True(
                result.Refusal == TrackRefusal.InvalidName,
                $"'{name}' was {(result.Track != null ? "taken" : result.Refusal.ToString())}."
            );
            Assert.Null(result.Track);
        }

        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task A_name_within_the_rules_is_taken_as_it_is()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("wav", Length);
        string[] names =
        [
            new('a', TrackStore.MaxNameLength),
            "R",
            "Rain 2.0",
            "Kinderliedjes ♫ (live, '24)",
            "Regen op het dakraam & wind",
        ];

        foreach (var name in names)
        {
            var result = await harness.UploadAsync(name, recording);

            Assert.True(result.Track?.Name == name, $"'{name}' was refused: {result.Detail}");
        }

        Assert.Equal(
            names.Select(name => $"{name}.wav").Order(StringComparer.Ordinal),
            harness.AmbientFiles("enabled")
        );
        Assert.Equal(
            names.Order(StringComparer.OrdinalIgnoreCase),
            (await harness.TrackStore().ListAsync(Cancellation)).Select(track => track.Name)
        );
    }

    [Fact]
    public async Task A_name_a_track_in_any_folder_has_ignoring_case_and_extension_is_refused()
    {
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));
        await harness.UploadAsync("Snow", await Tracks.RecordingAsync("wav", Length));
        await harness.UploadAsync("Hail", await Tracks.RecordingAsync("mp3", Length));
        await harness.TrackStore().UpdateAsync("Snow", null, TrackState.Disabled, Cancellation);
        await harness.TrackStore().UpdateAsync("Hail", null, TrackState.Occasional, Cancellation);

        var ambientTaken = await harness.UploadAsync("RAIN", await Tracks.RecordingAsync("mp3", Length));
        var disabledTaken = await harness.UploadAsync("snow", await Tracks.RecordingAsync("m4a", Length));
        var occasionalTaken = await harness.UploadAsync("hAIL", await Tracks.RecordingAsync("wav", Length));

        Assert.Equal(TrackRefusal.NameTaken, ambientTaken.Refusal);
        Assert.Equal("The name 'RAIN' is taken, by enabled/Rain.m4a.", ambientTaken.Detail);
        Assert.Equal(TrackRefusal.NameTaken, disabledTaken.Refusal);
        Assert.Equal(TrackRefusal.NameTaken, occasionalTaken.Refusal);
        Assert.Equal("The name 'hAIL' is taken, by occasional/Hail.mp3.", occasionalTaken.Detail);
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
        Assert.Equal(["Snow.wav"], harness.AmbientFiles("disabled"));
        Assert.Equal(["Hail.mp3"], harness.AmbientFiles("occasional"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Theory]
    [InlineData("noise", "ffprobe cannot read it: ")]
    [InlineData("nothing", "ffprobe cannot read it: ")]
    [InlineData("video", "it holds no audio stream")]
    public async Task A_file_that_is_not_audio_is_refused_and_nothing_is_written(string file, string reason)
    {
        await using var harness = await ServerHarness.StartAsync();
        var content = file switch
        {
            "noise" => Tracks.Noise(),
            "nothing" => [],
            _ => await Tracks.VideoAsync(),
        };

        var result = await harness.UploadAsync("Rain", content);

        Assert.Equal(TrackRefusal.NotAudio, result.Refusal);
        Assert.StartsWith($"The file is not audio the server can play: {reason}", result.Detail);
        Assert.DoesNotContain(harness.AmbientDirectory(), result.Detail);
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task A_file_lasting_longer_than_the_limit_is_refused_and_one_lasting_as_long_is_taken()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["TRACK_MAX_SECONDS"] = "2" }
        );

        var asLong = await harness.UploadAsync("Rain", await Tracks.RecordingAsync("wav", TimeSpan.FromSeconds(2)));
        var longer = await harness.UploadAsync("Snow", await Tracks.RecordingAsync("wav", Length));

        Assert.NotNull(asLong.Track);
        Assert.Equal(TrackRefusal.TooLong, longer.Refusal);
        Assert.Equal("A track lasts at most 2 s, and this one lasts 3 s.", longer.Detail);
        Assert.Equal(["Rain.wav"], harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_refused_and_one_as_large_is_taken()
    {
        var asLarge = await Tracks.RecordingAsync("wav", TimeSpan.FromSeconds(2));
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["TRACK_MAX_BYTES"] = asLarge.Length.ToString() }
        );

        var taken = await harness.UploadAsync("Rain", asLarge);
        var larger = await harness.UploadAsync("Snow", await Tracks.RecordingAsync("wav", Length));

        Assert.NotNull(taken.Track);
        Assert.Equal(TrackRefusal.TooLarge, larger.Refusal);
        Assert.Equal($"A track's file holds at most {asLarge.Length} bytes, and this one holds more.", larger.Detail);
        Assert.Equal(["Rain.wav"], harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task The_upload_limits_are_15_minutes_and_50_MiB_unless_set()
    {
        await using var harness = await ServerHarness.StartAsync();

        var limits = harness.Services.GetRequiredService<TrackConfiguration>();

        Assert.Equal(TimeSpan.FromMinutes(15), limits.MaxDuration);
        Assert.Equal(50 * 1024 * 1024, limits.MaxBytes);
    }

    [Theory]
    [InlineData("TRACK_MAX_SECONDS", "0", "TRACK_MAX_SECONDS must be a positive number of seconds.")]
    [InlineData("TRACK_MAX_BYTES", "-1", "TRACK_MAX_BYTES must be a positive number of bytes.")]
    public async Task A_limit_that_lets_no_track_through_stops_the_start(string setting, string value, string message)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(new Dictionary<string, string?> { [setting] = value })
        );

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData(nameof(TrackState.Ambient))]
    [InlineData(nameof(TrackState.Occasional))]
    [InlineData(nameof(TrackState.Disabled))]
    public async Task A_rename_renames_the_file_in_its_folder(string stateName)
    {
        var state = Enum.Parse<TrackState>(stateName);
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        await harness.UploadAsync("Rain", recording);
        await harness.TrackStore().UpdateAsync("Rain", null, state, Cancellation);
        var folder = Folder(state);

        var result = await harness.TrackStore().UpdateAsync("Rain", "Rain on the skylight", null, Cancellation);

        var track = Assert.IsType<Track>(result.Track);
        Assert.Equal("Rain on the skylight", track.Name);
        Assert.Equal(state, track.State);
        Assert.Equal(Path.Combine(harness.AmbientDirectory(), folder, "Rain on the skylight.m4a"), track.Path);
        Assert.Equal(["Rain on the skylight.m4a"], harness.AmbientFiles(folder));
        Assert.Equal(recording, await File.ReadAllBytesAsync(track.Path, Cancellation));
        Assert.Equal(track, Assert.Single(await harness.TrackStore().ListAsync(Cancellation)));
        Assert.Null(await harness.TrackStore().OpenAsync("Rain", Cancellation));
    }

    [Fact]
    public async Task A_rename_to_a_name_taken_or_breaking_the_rules_is_refused_and_changes_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));
        await harness.UploadAsync("Snow", await Tracks.RecordingAsync("wav", Length));
        await harness.TrackStore().UpdateAsync("Snow", null, TrackState.Disabled, Cancellation);

        var taken = await harness.TrackStore().UpdateAsync("Rain", "SNOW", TrackState.Disabled, Cancellation);
        var broken = await harness.TrackStore().UpdateAsync("Rain", "Rain/Snow", TrackState.Occasional, Cancellation);

        Assert.Equal(TrackRefusal.NameTaken, taken.Refusal);
        Assert.Equal("The name 'SNOW' is taken, by disabled/Snow.wav.", taken.Detail);
        Assert.Equal(TrackRefusal.InvalidName, broken.Refusal);
        Assert.Equal("A track's name cannot hold '/'.", broken.Detail);
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
        Assert.Equal(["Snow.wav"], harness.AmbientFiles("disabled"));
        Assert.Empty(harness.AmbientFiles("occasional"));
    }

    [Fact]
    public async Task A_rename_to_another_case_of_its_own_name_is_taken()
    {
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));

        var result = await harness.TrackStore().UpdateAsync("Rain", "RAIN", null, Cancellation);

        Assert.Equal("RAIN", result.Track?.Name);
        Assert.Equal(["RAIN.m4a"], harness.AmbientFiles("enabled"));
    }

    [Fact]
    public async Task A_change_of_state_moves_the_file_between_any_two_of_the_three_folders()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        await harness.UploadAsync("Rain", recording);
        var store = harness.TrackStore();
        // Each of the six moves from one state to another, once.
        TrackState[] states =
        [
            TrackState.Occasional,
            TrackState.Disabled,
            TrackState.Ambient,
            TrackState.Disabled,
            TrackState.Occasional,
            TrackState.Ambient,
        ];

        foreach (var state in states)
        {
            var moved = await store.UpdateAsync("Rain", null, state, Cancellation);
            var placed = Placed(harness);
            var listed = await store.ListAsync(Cancellation);
            var again = await store.UpdateAsync("Rain", null, state, Cancellation);

            Assert.Equal(state, moved.Track?.State);
            Assert.Equal(Path.Combine(harness.AmbientDirectory(), Folder(state), "Rain.m4a"), moved.Track?.Path);
            Assert.Equal([$"{Folder(state)}/Rain.m4a"], placed);
            Assert.Equal(moved.Track, Assert.Single(listed));
            Assert.Equal(moved.Track, again.Track);
            Assert.Equal(recording, await File.ReadAllBytesAsync(moved.Track!.Path, Cancellation));
        }
    }

    [Fact]
    public async Task A_change_renames_and_disables_at_once()
    {
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));

        var result = await harness.TrackStore().UpdateAsync("Rain", "Rain on the skylight", TrackState.Disabled, Cancellation);

        Assert.Equal(("Rain on the skylight", TrackState.Disabled), (result.Track?.Name, result.Track?.State));
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Equal(["Rain on the skylight.m4a"], harness.AmbientFiles("disabled"));
    }

    [Theory]
    [InlineData(nameof(TrackState.Ambient))]
    [InlineData(nameof(TrackState.Occasional))]
    [InlineData(nameof(TrackState.Disabled))]
    public async Task Deleting_removes_the_file(string stateName)
    {
        var state = Enum.Parse<TrackState>(stateName);
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));
        var snow = await harness.UploadAsync("Snow", await Tracks.RecordingAsync("wav", Length));
        await harness.TrackStore().UpdateAsync("Rain", null, state, Cancellation);

        var result = await harness.TrackStore().DeleteAsync("Rain", Cancellation);

        Assert.Equal("Rain", result.Track?.Name);
        Assert.Equal(state, result.Track?.State);
        Assert.False(File.Exists(result.Track!.Path));
        Assert.Equal(["Snow.wav"], harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles("occasional"));
        Assert.Empty(harness.AmbientFiles("disabled"));
        Assert.Equal(snow.Track, Assert.Single(await harness.TrackStore().ListAsync(Cancellation)));
    }

    [Fact]
    public async Task A_change_or_delete_of_a_name_no_track_has_is_refused()
    {
        await using var harness = await ServerHarness.StartAsync();
        await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));

        var changed = await harness.TrackStore().UpdateAsync("Snow", "Hail", TrackState.Disabled, Cancellation);
        var deleted = await harness.TrackStore().DeleteAsync("Snow", Cancellation);

        Assert.Equal(TrackRefusal.NotFound, changed.Refusal);
        Assert.Equal("There is no track named 'Snow'.", changed.Detail);
        Assert.Equal(TrackRefusal.NotFound, deleted.Refusal);
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
    }

    [Fact]
    public async Task A_track_is_found_changed_and_deleted_by_its_name_in_any_case()
    {
        await using var harness = await ServerHarness.StartAsync();
        var rain = await harness.UploadAsync("Rain", await Tracks.RecordingAsync("m4a", Length));
        await harness.UploadAsync("Snow", await Tracks.RecordingAsync("wav", Length));

        var found = Assert.NotNull(await harness.TrackStore().OpenAsync("rAIN", Cancellation));
        await using var audio = found.Audio;
        var disabled = await harness.TrackStore().UpdateAsync("rain", null, TrackState.Disabled, Cancellation);
        var deleted = await harness.TrackStore().DeleteAsync("SNOW", Cancellation);

        Assert.Equal(rain.Track, found.Track);
        Assert.Equal("Rain", disabled.Track?.Name);
        Assert.Equal("Snow", deleted.Track?.Name);
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("disabled"));
        Assert.Empty(harness.AmbientFiles("enabled"));
    }

    [Fact]
    public async Task An_opened_track_reads_on_as_it_was_opened_once_it_is_renamed_disabled_and_deleted()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        await harness.UploadAsync("Rain", recording);

        var opened = Assert.NotNull(await harness.TrackStore().OpenAsync("Rain", Cancellation));
        await using var audio = opened.Audio;
        var changed = await harness.TrackStore().UpdateAsync("Rain", "Snow", TrackState.Disabled, Cancellation);
        var deleted = await harness.TrackStore().DeleteAsync("Snow", Cancellation);
        using var read = new MemoryStream();
        await audio.CopyToAsync(read, Cancellation);

        Assert.Equal(Path.Combine(harness.AmbientDirectory(), "enabled", "Rain.m4a"), opened.Track.Path);
        Assert.NotNull(changed.Track);
        Assert.NotNull(deleted.Track);
        Assert.Empty(harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles("disabled"));
        Assert.Equal(recording, read.ToArray());
    }

    [Fact]
    public async Task The_list_holds_the_three_folders_sorted_by_name_ignoring_case()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("wav", Length);
        await harness.UploadAsync("cherry", recording);
        await harness.UploadAsync("Banana", recording);
        await harness.UploadAsync("apple", recording);
        await harness.UploadAsync("date", recording);
        await harness.TrackStore().UpdateAsync("Banana", null, TrackState.Disabled, Cancellation);
        await harness.TrackStore().UpdateAsync("cherry", null, TrackState.Occasional, Cancellation);

        var tracks = await harness.TrackStore().ListAsync(Cancellation);

        Assert.Equal(
            [
                ("apple", TrackState.Ambient),
                ("Banana", TrackState.Disabled),
                ("cherry", TrackState.Occasional),
                ("date", TrackState.Ambient),
            ],
            tracks.Select(track => (track.Name, track.State))
        );
    }

    [Fact]
    public async Task A_duration_is_ffprobes_kept_until_the_file_is_modified()
    {
        await using var harness = await ServerHarness.StartAsync();
        var result = await harness.UploadAsync("Rain", await Tracks.RecordingAsync("wav", TimeSpan.FromSeconds(2)));
        var path = result.Track!.Path;
        var modified = File.GetLastWriteTimeUtc(path);

        await File.WriteAllBytesAsync(path, await Tracks.RecordingAsync("wav", Length), Cancellation);
        File.SetLastWriteTimeUtc(path, modified);
        var unmodified = await harness.TrackStore().ListAsync(Cancellation);
        File.SetLastWriteTimeUtc(path, modified + TimeSpan.FromSeconds(1));
        var rewritten = await harness.TrackStore().ListAsync(Cancellation);

        AssertLasts(TimeSpan.FromSeconds(2), Assert.Single(unmodified).Duration);
        AssertLasts(Length, Assert.Single(rewritten).Duration);
    }

    [Fact]
    public async Task A_file_in_the_folders_is_a_track_only_with_audio_ffprobe_reads_and_a_name_of_its_own_within_the_rules()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        var enabled = Path.Combine(harness.AmbientDirectory(), "enabled");
        var occasional = Path.Combine(harness.AmbientDirectory(), "occasional");
        var disabled = Path.Combine(harness.AmbientDirectory(), "disabled");
        await File.WriteAllBytesAsync(Path.Combine(enabled, "Rain.m4a"), recording, Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(occasional, "Hail.m4a"), recording, Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(disabled, "Snow.m4a"), recording, Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(enabled, "Notes.txt"), Tracks.Noise(), Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(occasional, "Readme.txt"), Tracks.Noise(), Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(disabled, "Video.mp4"), await Tracks.VideoAsync(), Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(enabled, ".Hidden.m4a"), recording, Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(enabled, "Twin.m4a"), recording, Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(disabled, "twin.wav"), await Tracks.RecordingAsync("wav", Length), Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(occasional, "Sleet.m4a"), recording, Cancellation);
        await File.WriteAllBytesAsync(Path.Combine(enabled, "SLEET.wav"), await Tracks.RecordingAsync("wav", Length), Cancellation);

        var tracks = await harness.TrackStore().ListAsync(Cancellation);
        var twin = await harness.TrackStore().OpenAsync("Twin", Cancellation);
        var sleet = await harness.TrackStore().OpenAsync("Sleet", Cancellation);
        var notes = await harness.TrackStore().DeleteAsync("Notes", Cancellation);
        var taken = await harness.UploadAsync("notes", recording);
        var readmeTaken = await harness.UploadAsync("README", recording);

        Assert.Equal(
            [("Hail", TrackState.Occasional), ("Rain", TrackState.Ambient), ("Snow", TrackState.Disabled)],
            tracks.Select(track => (track.Name, track.State))
        );
        Assert.Null(twin);
        Assert.Null(sleet);
        Assert.Equal(TrackRefusal.NotFound, notes.Refusal);
        // Its name is taken on disk, track or not.
        Assert.Equal(TrackRefusal.NameTaken, taken.Refusal);
        Assert.Equal(TrackRefusal.NameTaken, readmeTaken.Refusal);
        Assert.Equal([".Hidden.m4a", "Notes.txt", "Rain.m4a", "SLEET.wav", "Twin.m4a"], harness.AmbientFiles("enabled"));
        Assert.Equal(["Hail.m4a", "Readme.txt", "Sleet.m4a"], harness.AmbientFiles("occasional"));
    }

    [Fact]
    public async Task Of_uploads_of_one_name_at_once_one_is_taken()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => harness.UploadAsync("Rain", recording)));

        Assert.Single(results, result => result.Track != null);
        Assert.All(
            results.Where(result => result.Track == null),
            result => Assert.Equal(TrackRefusal.NameTaken, result.Refusal)
        );
        Assert.Equal(["Rain.m4a"], harness.AmbientFiles("enabled"));
        Assert.Empty(harness.AmbientFiles(".incoming"));
    }

    [Fact]
    public async Task An_upload_an_earlier_process_left_under_incoming_is_deleted_at_the_start()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("intercom-server-tests-").FullName;
        try
        {
            var incoming = Directory.CreateDirectory(Path.Combine(dataDirectory, "ambient", ".incoming")).FullName;
            var enabled = Directory.CreateDirectory(Path.Combine(dataDirectory, "ambient", "enabled")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(incoming, "unfinished"), Tracks.Noise(), Cancellation);
            await File.WriteAllBytesAsync(
                Path.Combine(enabled, "Rain.m4a"),
                await Tracks.RecordingAsync("m4a", Length),
                Cancellation
            );

            await using var harness = await ServerHarness.StartAsync(
                new Dictionary<string, string?> { ["DATA_DIR"] = dataDirectory }
            );

            Assert.Empty(Directory.GetFiles(incoming));
            Assert.Equal(["Rain"], (await harness.TrackStore().ListAsync(Cancellation)).Select(track => track.Name));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task The_start_creates_the_occasional_folder_beside_a_volumes_enabled_and_disabled_ones_and_moves_no_file()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("intercom-server-tests-").FullName;
        try
        {
            var enabled = Directory.CreateDirectory(Path.Combine(dataDirectory, "ambient", "enabled")).FullName;
            var disabled = Directory.CreateDirectory(Path.Combine(dataDirectory, "ambient", "disabled")).FullName;
            var recording = await Tracks.RecordingAsync("m4a", Length);
            await File.WriteAllBytesAsync(Path.Combine(enabled, "Rain.m4a"), recording, Cancellation);
            await File.WriteAllBytesAsync(Path.Combine(disabled, "Snow.m4a"), recording, Cancellation);

            await using var harness = await ServerHarness.StartAsync(
                new Dictionary<string, string?> { ["DATA_DIR"] = dataDirectory }
            );

            Assert.Equal(
                [("Rain", TrackState.Ambient), ("Snow", TrackState.Disabled)],
                (await harness.TrackStore().ListAsync(Cancellation)).Select(track => (track.Name, track.State))
            );
            Assert.Equal([Path.Combine(enabled, "Rain.m4a")], Directory.GetFiles(enabled));
            Assert.Empty(Directory.GetFiles(Path.Combine(dataDirectory, "ambient", "occasional")));
            Assert.Equal([Path.Combine(disabled, "Snow.m4a")], Directory.GetFiles(disabled));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    // The web host takes requests before the store's start runs.
    [Fact]
    public async Task The_start_leaves_an_upload_of_its_own_process_in_progress()
    {
        await using var harness = await ServerHarness.StartAsync();
        var recording = await Tracks.RecordingAsync("m4a", Length);
        var arriving = new HeldStream(recording, recording.Length / 2);
        var uploading = harness.UploadAsync("Rain", arriving);
        await arriving.Held.WaitAsync(Timeouts.Observe, Cancellation);

        await harness.TrackStore().StartAsync(Cancellation);
        arriving.Release();
        var result = await uploading;

        Assert.Equal("Rain", result.Track?.Name);
        Assert.Equal(recording, await File.ReadAllBytesAsync(result.Track!.Path, Cancellation));
    }

    [Fact]
    public async Task A_server_without_ffprobe_stops_at_its_start_saying_ffprobe_is_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-ffprobe-{Guid.NewGuid():N}", "ffprobe");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(services: services => services.AddSingleton(new AudioProbe(missing)))
        );

        Assert.StartsWith("ffprobe is missing: ", error.Message);
        Assert.Contains($"'{missing}' could not be started", error.Message);
    }

    // Every file of the three folders, as its folder and its name.
    private static string[] Placed(ServerHarness harness) =>
        Enum.GetValues<TrackState>()
            .SelectMany(state => harness.AmbientFiles(Folder(state)).Select(file => $"{Folder(state)}/{file}"))
            .ToArray();

    private static string Folder(TrackState state) =>
        state switch
        {
            TrackState.Ambient => "enabled",
            TrackState.Occasional => "occasional",
            TrackState.Disabled => "disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    // ffprobe reads an encoder's padding as part of a file's length, tens of milliseconds.
    private static void AssertLasts(TimeSpan expected, TimeSpan duration) =>
        Assert.InRange(duration, expected - TimeSpan.FromSeconds(0.1), expected + TimeSpan.FromSeconds(0.1));
}
