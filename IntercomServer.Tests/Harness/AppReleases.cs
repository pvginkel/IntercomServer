using System.Net.Http.Headers;

namespace IntercomServer.Tests.Harness;

/// <summary>The update channel, driven as Jenkins publishes to it and the app asks it.</summary>
internal static class AppReleases
{
    /// <summary>The server's folder of releases.</summary>
    public static string UpdatesDirectory(this ServerHarness harness) =>
        Path.Combine(harness.DataDirectory, "updates");

    /// <summary>The names of the files in <see cref="UpdatesDirectory"/>, sorted; none when it is missing.</summary>
    public static string[] UpdatesFiles(this ServerHarness harness) =>
        Directory.Exists(harness.UpdatesDirectory())
            ? Directory
                .GetFiles(harness.UpdatesDirectory())
                .Select(path => Path.GetFileName(path))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>
    /// Publishes <paramref name="apk"/> as Jenkins does, a multipart form of <c>apk</c> and
    /// <c>version_code</c> to <c>POST /api/app-releases</c>, with <paramref name="token"/> as
    /// its bearer token when there is one.
    /// </summary>
    public static Task<HttpResponseMessage> PublishAsync(
        this ServerHarness harness,
        string? token,
        string versionCode,
        byte[] apk
    )
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(apk), "apk", "app-release.apk" },
            { new StringContent(versionCode), "version_code" },
        };

        return harness.PostAsync(token, form);
    }

    /// <summary>Posts <paramref name="content"/> to <c>POST /api/app-releases</c>.</summary>
    public static async Task<HttpResponseMessage> PostAsync(
        this ServerHarness harness,
        string? token,
        HttpContent content
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/app-releases") { Content = content };
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await harness.SendAsync(request);
    }

    /// <summary>Asks <c>GET /updates</c> with <paramref name="query"/>, as the app does, with no token.</summary>
    public static async Task<HttpResponseMessage> GetUpdatesAsync(this ServerHarness harness, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/updates{query}");
        return await harness.SendAsync(request);
    }

    /// <summary>An APK's stand-in: <paramref name="length"/> random bytes.</summary>
    public static byte[] Apk(int length = 4096)
    {
        var apk = new byte[length];
        Random.Shared.NextBytes(apk);
        return apk;
    }
}
