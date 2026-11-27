using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IntercomServer.Tests.Harness;

/// <summary>The track API under <c>/api/ambient-tracks</c>, driven as the app calls it.</summary>
internal static class TrackApi
{
    /// <summary>The path of the track named <paramref name="name"/>, its name URL-encoded.</summary>
    public static string TrackPath(string name) => $"/api/ambient-tracks/{Uri.EscapeDataString(name)}";

    /// <summary>A token holding the client role the track API asks for.</summary>
    public static string UserToken(this ServerHarness harness) => harness.Issuer.TokenWithRoles("user");

    public static Task<HttpResponseMessage> ListTracksAsync(this ServerHarness harness, string? token) =>
        harness.SendAsync(HttpMethod.Get, "/api/ambient-tracks", token);

    /// <summary>Uploads <paramref name="file"/> as the track <paramref name="name"/>, with <see cref="UploadForm"/>.</summary>
    public static Task<HttpResponseMessage> UploadTrackAsync(
        this ServerHarness harness,
        string? token,
        string name,
        byte[] file
    ) => harness.UploadTrackAsync(token, UploadForm(name, new ByteArrayContent(file)));

    public static Task<HttpResponseMessage> UploadTrackAsync(this ServerHarness harness, string? token, HttpContent form) =>
        harness.SendAsync(HttpMethod.Post, "/api/ambient-tracks", token, form);

    /// <summary>An upload as the app sends it: a multipart form of <c>name</c>, then <c>file</c>.</summary>
    public static MultipartFormDataContent UploadForm(string name, HttpContent file) =>
        new() { { new StringContent(name), "name" }, { file, "file", "recording.m4a" } };

    /// <summary>Changes the track named <paramref name="name"/> by <paramref name="change"/>, sent as JSON.</summary>
    public static Task<HttpResponseMessage> ChangeTrackAsync(
        this ServerHarness harness,
        string? token,
        string name,
        object change
    ) => harness.ChangeTrackAsync(token, name, JsonContent.Create(change));

    public static Task<HttpResponseMessage> ChangeTrackAsync(
        this ServerHarness harness,
        string? token,
        string name,
        HttpContent change
    ) => harness.SendAsync(HttpMethod.Patch, TrackPath(name), token, change);

    public static Task<HttpResponseMessage> DeleteTrackAsync(this ServerHarness harness, string? token, string name) =>
        harness.SendAsync(HttpMethod.Delete, TrackPath(name), token);

    public static Task<HttpResponseMessage> GetTrackAudioAsync(
        this ServerHarness harness,
        string? token,
        string name,
        RangeHeaderValue? range = null
    ) => harness.SendAsync(HttpMethod.Get, $"{TrackPath(name)}/audio", token, range: range);

    /// <summary>
    /// The track an answer holds, an object of exactly <c>name</c>, <c>enabled</c> and
    /// <c>duration</c>.
    /// </summary>
    public static (string Name, bool Enabled, double Duration) ReadTrack(JsonNode? answer)
    {
        var track = Assert.IsType<JsonObject>(answer);
        Assert.Equal(["duration", "enabled", "name"], track.Select(property => property.Key).Order(StringComparer.Ordinal));
        return ((string)track["name"]!, (bool)track["enabled"]!, (double)track["duration"]!);
    }

    /// <summary>The <c>detail</c> of a problem the server answered, asserting the answer is one.</summary>
    public static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (string?)problem?["detail"];
    }

    private static async Task<HttpResponseMessage> SendAsync(
        this ServerHarness harness,
        HttpMethod method,
        string path,
        string? token,
        HttpContent? content = null,
        RangeHeaderValue? range = null
    )
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = range;

        return await harness.SendAsync(request);
    }
}
