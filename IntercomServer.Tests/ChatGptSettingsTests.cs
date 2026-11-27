using IntercomServer.ChatGpt;
using IntercomServer.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// The ChatGPT bridge's number settings are read once, at the start, as numbers of the invariant
/// culture: a value that is not one stops the start, rather than running on the default.
/// </summary>
public sealed class ChatGptSettingsTests
{
    [Fact]
    public async Task The_number_settings_are_read_as_set()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?>
            {
                ["CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS"] = "120",
                ["CHATGPT_MIC_GATE_THRESHOLD"] = "0.02",
                ["CHATGPT_MIC_GATE_ATTACK_MS"] = "70",
                ["CHATGPT_MIC_GATE_HOLD_MS"] = "-1",
                ["CHATGPT_MIC_GATE_PREROLL_MS"] = "90",
            }
        );

        var configuration = harness.Services.GetRequiredService<ChatGptConfiguration>();

        Assert.Equal(
            (120, 0.02, 70, -1, 90),
            (
                configuration.CloseOutTimeoutSeconds,
                configuration.MicGateThreshold,
                configuration.MicGateAttackMs,
                configuration.MicGateHoldMs,
                configuration.MicGatePrerollMs
            )
        );
    }

    [Theory]
    [InlineData("CHATGPT_CLOSE_OUT_TIMEOUT_SECONDS", "30s")]
    [InlineData("CHATGPT_MIC_GATE_THRESHOLD", "0,02")]
    [InlineData("CHATGPT_MIC_GATE_ATTACK_MS", "60.5")]
    [InlineData("CHATGPT_MIC_GATE_HOLD_MS", "four seconds")]
    [InlineData("CHATGPT_MIC_GATE_PREROLL_MS", "80ms")]
    public async Task A_number_setting_that_does_not_parse_stops_the_start(string setting, string value)
    {
        await Assert.ThrowsAsync<FormatException>(() =>
            ServerHarness.StartAsync(new Dictionary<string, string?> { [setting] = value })
        );
    }
}
