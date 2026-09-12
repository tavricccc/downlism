using Downlism.Core.Ingest;
using Xunit;

namespace Downlism.Tests;

/// <summary>
/// Covers the channel the browser handover runs on.
/// </summary>
/// <remarks>
/// Worth testing directly because a failure here is invisible: the listener runs on a
/// background task, the host reports only "Downlism is not running", and every other part of
/// the app keeps working normally.
/// </remarks>
public sealed class IngestPipeTests
{
    /// <summary>
    /// A name of its own per test. The production name may be owned by a Downlism running on
    /// this machine, and a client asking for it would reach that app instead of the server
    /// under test.
    /// </summary>
    private static string UniqueName() => $"Downlism.Ingest.Test.{Guid.NewGuid():N}";

    [Fact]
    public void ServerCanBeCreated()
    {
        // A pipe the app cannot even open means no download is ever handed over, no matter
        // what the browser or the host do.
        using var server = IngestPipe.CreateServer(UniqueName());
        Assert.NotNull(server);
    }

    [Fact]
    public async Task CarriesAMessageAndItsReply()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var name = UniqueName();

        await using var server = IngestPipe.CreateServer(name);
        var listening = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cancellation.Token);

            var received = await IngestPipe.ReadAsync(
                server, IngestJsonContext.Default.IngestMessage, cancellation.Token);

            await IngestPipe.WriteAsync(
                server,
                received is null ? IngestReply.Rejected("empty") : IngestReply.Ok(),
                IngestJsonContext.Default.IngestReply,
                cancellation.Token);

            return received;
        }, cancellation.Token);

        await using var client = IngestPipe.CreateClient(name);
        await client.ConnectAsync(5000, cancellation.Token);

        await IngestPipe.WriteAsync(
            client,
            new IngestMessage { Url = "https://example.com/a.zip", FileName = "a.zip" },
            IngestJsonContext.Default.IngestMessage,
            cancellation.Token);

        var reply = await IngestPipe.ReadAsync(client, IngestJsonContext.Default.IngestReply, cancellation.Token);
        var received = await listening;

        Assert.NotNull(received);
        Assert.Equal("https://example.com/a.zip", received.Url);
        Assert.Equal("a.zip", received.FileName);
        Assert.NotNull(reply);
        Assert.True(reply.Accepted);
    }
}
