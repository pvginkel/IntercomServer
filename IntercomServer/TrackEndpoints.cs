using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace IntercomServer;

/// <summary>A track as the API answers it, its <paramref name="Duration"/> in seconds.</summary>
internal sealed record TrackAnswer(string Name, bool Enabled, double Duration)
{
    public static TrackAnswer Of(Track track) => new(track.Name, track.Enabled, track.Duration.TotalSeconds);
}

/// <summary>A change of a track: its new name, enabled or not, or both; either left out stays as it is.</summary>
internal sealed record TrackChange(string? Name, bool? Enabled);

/// <summary>
/// The ambient tracks of <see cref="TrackStore"/>, under <c>/api/ambient-tracks</c>, for the
/// client role <see cref="Roles.User"/>. A track is named in the path by its name, URL-encoded,
/// and answered as a <see cref="TrackAnswer"/>. What the store refuses answers
/// <c>application/problem+json</c> whose <c>detail</c> is the store's sentence.
/// </summary>
internal static class TrackEndpoints
{
    private const string TracksPath = "/ambient-tracks";

    private const string UploadShape = "An upload is a multipart form of name, then file.";

    private const string ChangeShape = "A change is a JSON object of name, enabled or both.";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapTrackEndpoints(this IEndpointRouteBuilder api)
    {
        var tracks = api.MapGroup(TracksPath).RequireAuthorization(policy => policy.RequireRole(Roles.User));

        tracks.MapGet("/", ListAsync);
        // No request limit: the route reads a bounded part of an upload, its file no further than
        // the store's limit, and Kestrel reads on through the rest of a refused one, so the client
        // gets its 413. Past a request limit, Kestrel closes the connection instead.
        tracks.MapPost("/", UploadAsync).WithMetadata(new DisableRequestSizeLimitAttribute());
        tracks.MapPatch("/{name}", UpdateAsync);
        tracks.MapDelete("/{name}", DeleteAsync);
        tracks.MapGet("/{name}/audio", AudioAsync);
    }

    private static async Task<IResult> ListAsync(TrackStore tracks, CancellationToken cancellationToken) =>
        Results.Ok((await tracks.ListAsync(cancellationToken)).Select(TrackAnswer.Of).ToList());

    // The form is read as it arrives, so the file streams into the store, which stops reading it
    // once it holds more than the limit.
    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        TrackStore tracks,
        CancellationToken cancellationToken
    )
    {
        if (Boundary(request) is not { } boundary)
            return BadRequest(UploadShape);

        var form = new MultipartReader(boundary, request.Body);

        if (await form.ReadNextSectionAsync(cancellationToken) is not { } nameSection || FieldName(nameSection) != "name")
            return BadRequest(UploadShape);

        if (await ReadNameAsync(nameSection.Body, cancellationToken) is not { } name)
            return BadRequest(
                $"A track's name is at most {TrackStore.MaxNameLength} characters, and this one has more."
            );

        if (await form.ReadNextSectionAsync(cancellationToken) is not { } fileSection || FieldName(fileSection) != "file")
            return BadRequest(UploadShape);

        var result = await tracks.UploadAsync(name, fileSection.Body, cancellationToken);
        return result.Track is { } track
            ? Results.Created($"/api{TracksPath}/{Uri.EscapeDataString(track.Name)}", TrackAnswer.Of(track))
            : Refused(result);
    }

    private static async Task<IResult> UpdateAsync(
        string name,
        HttpRequest request,
        TrackStore tracks,
        CancellationToken cancellationToken
    )
    {
        if (!request.HasJsonContentType())
            return BadRequest(ChangeShape);

        TrackChange? change;
        try
        {
            change = await request.ReadFromJsonAsync<TrackChange>(cancellationToken);
        }
        catch (JsonException)
        {
            return BadRequest(ChangeShape);
        }

        if (change == null)
            return BadRequest(ChangeShape);

        var result = await tracks.UpdateAsync(name, change.Name, change.Enabled, cancellationToken);
        return result.Track is { } track ? Results.Ok(TrackAnswer.Of(track)) : Refused(result);
    }

    private static async Task<IResult> DeleteAsync(string name, TrackStore tracks, CancellationToken cancellationToken)
    {
        var result = await tracks.DeleteAsync(name, cancellationToken);
        return result.Track != null ? Results.NoContent() : Refused(result);
    }

    private static async Task<IResult> AudioAsync(string name, TrackStore tracks, CancellationToken cancellationToken)
    {
        if (await tracks.OpenAsync(name, cancellationToken) is not { } opened)
            return Refused(TrackStore.NotFound(name));

        return Results.File(
            opened.Audio,
            ContentTypes.TryGetContentType(opened.Track.Path, out var contentType)
                ? contentType
                : "application/octet-stream",
            enableRangeProcessing: true
        );
    }

    // The boundary of a multipart/form-data body; null for any other body.
    private static string? Boundary(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
        && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
        && HeaderUtilities.RemoveQuotes(type.Boundary) is { Length: > 0 } boundary
            ? boundary.Value
            : null;

    private static string? FieldName(MultipartSection section) =>
        section.GetContentDispositionHeader() is { } disposition
        && disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            ? HeaderUtilities.RemoveQuotes(disposition.Name).Value
            : null;

    // Null for a name longer than the rules allow, read no further than one character past it.
    private static async Task<string?> ReadNameAsync(Stream body, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        var name = new char[TrackStore.MaxNameLength + 1];
        var read = await reader.ReadBlockAsync(name, cancellationToken);
        return read > TrackStore.MaxNameLength ? null : new string(name, 0, read);
    }

    private static IResult Refused(TrackResult result) =>
        Results.Problem(
            statusCode: result.Refusal switch
            {
                TrackRefusal.InvalidName or TrackRefusal.NotAudio => StatusCodes.Status400BadRequest,
                TrackRefusal.NotFound => StatusCodes.Status404NotFound,
                TrackRefusal.NameTaken => StatusCodes.Status409Conflict,
                TrackRefusal.TooLong or TrackRefusal.TooLarge => StatusCodes.Status413PayloadTooLarge,
                _ => throw new UnreachableException($"The store refused with {result.Refusal}."),
            },
            detail: result.Detail
        );

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);
}
