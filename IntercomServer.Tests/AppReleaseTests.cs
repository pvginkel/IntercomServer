using System.Net;
using System.Text;
using System.Threading.Channels;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The update channel: Jenkins publishes a release to <c>POST /api/app-releases</c>, which keeps
/// the newest three in <c>&lt;DATA_DIR&gt;/updates</c>, and the app asks
/// <c>GET /updates?action=version|download</c> as MyDownloadsServer answers it: the highest
/// version code as plain text, empty with no release, and that release's APK.
/// </summary>
public sealed class AppReleaseTests
{
    [Fact]
    public async Task With_no_release_the_version_is_empty_and_there_is_nothing_to_download()
    {
        await using var harness = await ServerHarness.StartAsync();

        using var version = await harness.GetUpdatesAsync("?action=version");
        using var download = await harness.GetUpdatesAsync("?action=download");

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal("text/plain", version.Content.Headers.ContentType?.MediaType);
        Assert.Equal("", await version.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task A_published_release_is_offered_and_downloaded()
    {
        await using var harness = await ServerHarness.StartAsync();
        var apk = AppReleases.Apk();

        using var published = await harness.PublishAsync(harness.Issuer.TokenWithRoles("pipeline"), "7", apk);
        using var version = await harness.GetUpdatesAsync("?action=version");
        using var download = await harness.GetUpdatesAsync("?action=download");

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal("", await published.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal("7", await version.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/vnd.android.package-archive", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("7.apk", download.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(apk, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["7.apk"], harness.UpdatesFiles());
        Assert.Equal(
            apk,
            await File.ReadAllBytesAsync(
                Path.Combine(harness.UpdatesDirectory(), "7.apk"),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task A_release_is_written_to_a_temporary_file_in_the_folder_and_renamed_into_place()
    {
        await using var harness = await ServerHarness.StartAsync();
        Directory.CreateDirectory(harness.UpdatesDirectory());
        var arrivals = Channel.CreateUnbounded<FileSystemEventArgs>();
        using var watcher = new FileSystemWatcher(harness.UpdatesDirectory()) { NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, e) => arrivals.Writer.TryWrite(e);
        watcher.Renamed += (_, e) => arrivals.Writer.TryWrite(e);
        watcher.EnableRaisingEvents = true;

        using var published = await harness.PublishAsync(harness.Issuer.TokenWithRoles("pipeline"), "7", AppReleases.Apk());
        FileSystemEventArgs arrival;
        do
            arrival = await Timeouts.NextAsync(arrivals.Reader, "7.apk to appear in the folder");
        while (arrival.Name != "7.apk");

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var renamed = Assert.IsType<RenamedEventArgs>(arrival);
        Assert.Matches(@"^\..+\.tmp$", renamed.OldName);
    }

    [Fact]
    public async Task A_version_code_published_already_answers_409_and_keeps_the_first()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.Issuer.TokenWithRoles("pipeline");
        var first = AppReleases.Apk();

        using var firstPublish = await harness.PublishAsync(token, "7", first);
        using var secondPublish = await harness.PublishAsync(token, "7", AppReleases.Apk());
        using var download = await harness.GetUpdatesAsync("?action=download");

        Assert.Equal(HttpStatusCode.Created, firstPublish.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondPublish.StatusCode);
        Assert.Equal("application/problem+json", secondPublish.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "\"detail\":\"Release 7 is published already.\"",
            await secondPublish.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(first, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["7.apk"], harness.UpdatesFiles());
    }

    [Fact]
    public async Task The_newest_three_by_version_code_are_kept_and_the_highest_is_offered()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.Issuer.TokenWithRoles("pipeline");
        var apks = new Dictionary<string, byte[]>();

        foreach (var versionCode in new[] { "5", "6", "10", "7" })
        {
            apks[versionCode] = AppReleases.Apk();
            using var published = await harness.PublishAsync(token, versionCode, apks[versionCode]);
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        }

        using var version = await harness.GetUpdatesAsync("?action=version");
        using var download = await harness.GetUpdatesAsync("?action=download");

        Assert.Equal(["10.apk", "6.apk", "7.apk"], harness.UpdatesFiles());
        Assert.Equal("10", await version.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(apks["10"], await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_action_other_than_version_or_download_answers_404()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var published = await harness.PublishAsync(harness.Issuer.TokenWithRoles("pipeline"), "7", AppReleases.Apk());

        var answers = new Dictionary<string, HttpStatusCode>();
        foreach (var query in new[] { "", "?action=", "?action=Version", "?action=list", "?version" })
        {
            using var response = await harness.GetUpdatesAsync(query);
            answers[query] = response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.NotFound, answer.Value));
    }

    [Fact]
    public async Task A_release_past_Kestrels_default_request_limit_publishes()
    {
        await using var harness = await ServerHarness.StartAsync();
        var apk = AppReleases.Apk(31_000_000);

        using var published = await harness.PublishAsync(harness.Issuer.TokenWithRoles("pipeline"), "7", apk);
        using var download = await harness.GetUpdatesAsync("?action=download");

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal(apk, await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_publish_that_is_not_a_release_answers_400()
    {
        await using var harness = await ServerHarness.StartAsync();
        var token = harness.Issuer.TokenWithRoles("pipeline");
        var forms = new Dictionary<string, HttpContent>
        {
            ["no version_code"] = new MultipartFormDataContent
            {
                { new ByteArrayContent(AppReleases.Apk()), "apk", "app-release.apk" },
            },
            ["no apk"] = new MultipartFormDataContent { { new StringContent("7"), "version_code" } },
            ["an empty apk"] = new MultipartFormDataContent
            {
                { new ByteArrayContent([]), "apk", "app-release.apk" },
                { new StringContent("7"), "version_code" },
            },
            ["JSON"] = new StringContent("{\"version_code\":7}", Encoding.UTF8, "application/json"),
        };
        foreach (var versionCode in new[] { "abc", "0", "-7", "+7", "7.0", " 7", "99999999999" })
        {
            forms[$"version_code {versionCode}"] = new MultipartFormDataContent
            {
                { new ByteArrayContent(AppReleases.Apk()), "apk", "app-release.apk" },
                { new StringContent(versionCode), "version_code" },
            };
        }

        var answers = new Dictionary<string, (HttpStatusCode, string?)>();
        foreach (var (kind, form) in forms)
        {
            using var response = await harness.PostAsync(token, form);
            answers[kind] = (response.StatusCode, response.Content.Headers.ContentType?.MediaType);
        }

        Assert.Equal(
            forms.Keys.ToDictionary(kind => kind, _ => (HttpStatusCode.BadRequest, (string?)"application/problem+json")),
            answers
        );
        Assert.Empty(harness.UpdatesFiles());
    }

    [Fact]
    public async Task Files_in_the_folder_not_named_as_releases_are_not_offered()
    {
        await using var harness = await ServerHarness.StartAsync();
        Directory.CreateDirectory(harness.UpdatesDirectory());
        foreach (var name in new[] { "4.apk", "9.apk.part", "beta.apk", "0.apk", "012.apk", "11.APK", "notes.txt" })
            await File.WriteAllBytesAsync(
                Path.Combine(harness.UpdatesDirectory(), name),
                AppReleases.Apk(),
                TestContext.Current.CancellationToken
            );

        using var version = await harness.GetUpdatesAsync("?action=version");

        Assert.Equal("4", await version.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_publish_removes_what_an_unfinished_one_left()
    {
        await using var harness = await ServerHarness.StartAsync();
        Directory.CreateDirectory(harness.UpdatesDirectory());
        await File.WriteAllBytesAsync(
            Path.Combine(harness.UpdatesDirectory(), ".0123456789abcdef.tmp"),
            AppReleases.Apk(),
            TestContext.Current.CancellationToken
        );

        using var published = await harness.PublishAsync(harness.Issuer.TokenWithRoles("pipeline"), "7", AppReleases.Apk());

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal(["7.apk"], harness.UpdatesFiles());
    }
}
