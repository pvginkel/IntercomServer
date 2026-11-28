using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using IntercomServer.Utils.Audio;

namespace IntercomServer;

/// <summary>
/// A device as the API answers it: its id, the topic prefix it was found under, whether it has a
/// microphone, whether it is online, the format the server sends it, or null when it advertises
/// none the server can produce, and the ambient track it plays, or null.
/// </summary>
internal sealed record DeviceAnswer(
    string Id,
    string Prefix,
    bool Microphone,
    bool Online,
    FormatAnswer? Format,
    string? Track
)
{
    public static DeviceAnswer Of(Device device, string? track) =>
        new(
            device.DeviceId,
            device.TopicPrefix,
            device.HasMicrophone,
            device.State?.Online == true,
            device.OutputFormat is { } format ? FormatAnswer.Of(format) : null,
            track
        );
}

/// <summary>An audio format, named as a device advertises one in its <c>audio_formats</c>.</summary>
internal sealed record FormatAnswer(
    [property: JsonPropertyName("channel_layout")] string ChannelLayout,
    [property: JsonPropertyName("sample_rate")] int SampleRate,
    [property: JsonPropertyName("bit_rate")] int BitRate
)
{
    public static FormatAnswer Of(AudioFormat format) =>
        new(format.ChannelLayout.ToString().ToLowerInvariant(), format.SampleRate, format.BitRate);
}

/// <summary>A start of ambient sound: the track to play, or none for a pick at random.</summary>
internal sealed record AmbientStart(string? Track);

/// <summary>
/// The devices, under <c>/api/devices</c>, for the client role <see cref="Roles.User"/>, and the
/// ambient sound of each, which <see cref="AmbientPlayer"/> plays. A device is named in the path
/// by its device id, and answered as a <see cref="DeviceAnswer"/>. What the player refuses answers
/// <c>application/problem+json</c> whose <c>detail</c> is the player's sentence.
/// </summary>
internal static class DeviceEndpoints
{
    private const string DevicesPath = "/devices";

    private const string StartShape = "A start has no body, or a JSON object with an optional track.";

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder api)
    {
        var devices = api.MapGroup(DevicesPath).RequireAuthorization(policy => policy.RequireRole(Roles.User));

        devices.MapGet("/", List);
        devices.MapPut("/{deviceId}/ambient", StartAsync);
        devices.MapDelete("/{deviceId}/ambient", StopAsync);
    }

    private static IResult List(DeviceManager devices, AmbientPlayer player) =>
        Results.Ok(
            devices
                .GetAll()
                .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
                .Select(device => DeviceAnswer.Of(device, player.Playing(device)))
                .ToList()
        );

    private static async Task<IResult> StartAsync(
        string deviceId,
        HttpRequest request,
        DeviceManager devices,
        AmbientPlayer player,
        CancellationToken cancellationToken
    )
    {
        string? track = null;
        if (request.HttpContext.Features.GetRequiredFeature<IHttpRequestBodyDetectionFeature>().CanHaveBody)
        {
            if (!request.HasJsonContentType())
                return BadRequest(StartShape);

            AmbientStart? start;
            try
            {
                start = await request.ReadFromJsonAsync<AmbientStart>(cancellationToken);
            }
            catch (JsonException)
            {
                return BadRequest(StartShape);
            }

            if (start == null)
                return BadRequest(StartShape);

            track = start.Track;
        }

        if (!devices.TryGet(deviceId, out var device))
            return NoSuchDevice(deviceId);

        var result = await player.StartAsync(device, track, cancellationToken);
        return result.Track is { } playing ? Results.Ok(DeviceAnswer.Of(device, playing)) : Refused(result);
    }

    private static async Task<IResult> StopAsync(string deviceId, DeviceManager devices, AmbientPlayer player)
    {
        if (!devices.TryGet(deviceId, out var device))
            return NoSuchDevice(deviceId);

        await player.StopAsync(device);
        return Results.NoContent();
    }

    private static IResult NoSuchDevice(string deviceId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: $"There is no device '{deviceId}'.");

    private static IResult Refused(AmbientResult result) =>
        Results.Problem(
            statusCode: result.Refusal switch
            {
                AmbientRefusal.NoSuchTrack => StatusCodes.Status404NotFound,
                AmbientRefusal.CannotPlay => StatusCodes.Status409Conflict,
                _ => throw new UnreachableException($"The player refused with {result.Refusal}."),
            },
            detail: result.Detail
        );

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: detail);
}
