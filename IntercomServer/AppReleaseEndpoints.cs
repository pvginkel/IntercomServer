using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace IntercomServer;

/// <summary>
/// The app's update channel. Jenkins publishes a release to <c>POST /api/app-releases</c>, a
/// multipart form of <c>apk</c> and <c>version_code</c>; the app asks
/// <c>GET /updates?action=version|download</c>, MyDownloadsServer's two-call contract, with no
/// token, so a build whose sign-in is broken can still update itself.
/// </summary>
internal static class AppReleaseEndpoints
{
    private const string ApkContentType = "application/vnd.android.package-archive";

    // Kestrel's default request limit, 30,000,000 bytes, is less than an APK can be. The form's
    // own limit on a file is 128 MiB by default too.
    private const long MaxPublishBytes = 128 * 1024 * 1024;

    public static void MapAppReleaseEndpoints(this IEndpointRouteBuilder endpoints, IEndpointRouteBuilder api)
    {
        endpoints.MapGet(
            "/updates",
            (string? action, AppReleaseStore releases) =>
                action switch
                {
                    "version" => Results.Text(
                        releases.Latest()?.VersionCode.ToString(CultureInfo.InvariantCulture) ?? ""
                    ),
                    "download" when releases.Latest() is { } latest => Results.File(
                        latest.Path,
                        ApkContentType,
                        Path.GetFileName(latest.Path)
                    ),
                    _ => Results.NotFound(),
                }
        );

        api.MapPost("/app-releases", PublishAsync)
            .RequireAuthorization(policy => policy.RequireRole(Roles.Pipeline))
            .WithMetadata(new RequestSizeLimitAttribute(MaxPublishBytes));
    }

    private static async Task<IResult> PublishAsync(
        HttpRequest request,
        AppReleaseStore releases,
        CancellationToken cancellationToken
    )
    {
        if (!request.HasFormContentType)
            return BadRequest("A release is a multipart form of apk and version_code.");

        var form = await request.ReadFormAsync(cancellationToken);

        if (
            !int.TryParse(
                form["version_code"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var versionCode
            )
            || versionCode <= 0
        )
            return BadRequest("version_code must be a positive integer.");

        if (form.Files.GetFile("apk") is not { Length: > 0 } apk)
            return BadRequest("apk must be the release's APK file.");

        await using var stream = apk.OpenReadStream();
        return await releases.PublishAsync(versionCode, stream, cancellationToken)
            ? Results.StatusCode(StatusCodes.Status201Created)
            : Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: $"Release {versionCode} is published already."
            );
    }

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);
}
