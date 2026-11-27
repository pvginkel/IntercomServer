using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The server sends audio in real time, because the devices' jitter buffers are small. It keeps
/// a clock of how much audio it has sent: a chunk that goes out late is made up by sending the
/// next ones sooner, unless the source starved for longer than 60 ms, after which the clock
/// starts again from now. A built-in sound never starves, so the late chunk and the stall are
/// shown on the one source that does, the ChatGPT bridge's live stream (<see cref="LivePlayback"/>).
/// Each sits well clear of 60 ms, since a loaded machine stretches both. The class runs alone,
/// after the suite's other tests, so their servers do not stretch it too.
/// </summary>
[Collection(nameof(PacingTests))]
public sealed class PacingTests
{
    [Fact]
    public async Task A_sound_is_paced_to_real_time()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        // A second of the doorbell, well within its length.
        var packets = await device.NextPacketsAsync(50);

        var audio = TimeSpan.FromSeconds(
            (double)packets.SkipLast(1).Sum(p => p.Payload.Length) / BuiltInSounds.BytesPerSecond
        );
        Assert.InRange(
            packets[^1].ArrivedAfter(packets[0]),
            audio * 0.9,
            audio + TimeSpan.FromMilliseconds(250)
        );
    }

    [Fact]
    public async Task A_chunk_that_comes_late_is_made_up()
    {
        const int rounds = 20;
        const int chunksPerRound = 5;

        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await using var stream = LivePlayback.Start(harness, device);

        var packets = new List<AudioPacket>();
        stream.Append(chunksPerRound);
        for (var round = 1; ; round++)
        {
            packets.AddRange(await device.NextPacketsAsync(chunksPerRound));
            if (round == rounds)
                break;

            // The stream asks for the next chunk a chunk's time after the round's last went
            // out, and gets it about a chunk's time after that: late, but well short of a stall.
            await Task.Delay(2 * LivePlayback.ChunkLength, TestContext.Current.CancellationToken);
            stream.Append(chunksPerRound);
        }
        await stream.CompleteAsync();

        // A round's first chunk goes out late and the rest are made up, so a round's last goes
        // out on time, measured from the first round's last. Had each late round reset the
        // clock instead, each would end about 20 ms after the one before it, the stream 380 ms
        // late in all; half that is the tolerance either way.
        var onTime = packets[chunksPerRound - 1];
        var audio = (rounds - 1) * chunksPerRound * LivePlayback.ChunkLength;
        Assert.InRange(
            packets[^1].ArrivedAfter(onTime),
            audio - TimeSpan.FromMilliseconds(190),
            audio + TimeSpan.FromMilliseconds(190)
        );
    }

    [Fact]
    public async Task After_a_stall_the_clock_starts_again_instead_of_catching_up()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await using var stream = LivePlayback.Start(harness, device);

        stream.Append(10);
        await device.NextPacketsAsync(10);
        // The stream waits on its source for a second: a stall, far past 60 ms.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        stream.Append(25);
        var afterStall = await device.NextPacketsAsync(25);
        await stream.CompleteAsync();

        // Paced from the stall's end, the 25 chunks take 480 ms. Caught up instead, a second
        // behind, they would all go out at once.
        var took = afterStall[^1].ArrivedAfter(afterStall[0]);
        Assert.True(
            took >= 12 * LivePlayback.ChunkLength,
            $"The 25 chunks after the stall went out in {took.TotalMilliseconds:0} ms."
        );
    }
}

[CollectionDefinition(nameof(PacingTests), DisableParallelization = true)]
public sealed class PacingTestsCollection;
