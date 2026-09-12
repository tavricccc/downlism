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
    [Fact]
    public void ServerCanBeCreated()
    {
        // A pipe the app cannot even open means no download is ever handed over, no matter
        // what the browser or the host do.
        using var server = IngestPipe.CreateServer();
        Assert.NotNull(server);
    }

    [Fact]
    public async Task CarriesAMessageAndItsReply()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var server = IngestPipe.CreateServer();
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

        await using var client = IngestPipe.CreateClient();
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
