using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Downlism.Core.Ingest;

/// <summary>
/// The channel between the native messaging host and the running app.
/// </summary>
/// <remarks>
/// A named pipe rather than a loopback HTTP port: a port prompts the firewall, can collide
/// with whatever else is listening, and would let any local process queue downloads. The pipe
/// is created with an ACL naming only the current user.
/// </remarks>
public static class IngestPipe
{
    public const string Name = "Downlism.Ingest";
    private const int MaximumMessageBytes = 1024 * 1024;

    /// <param name="name">
    /// Overridden only by tests. They must not share the production name: a running app owns
    /// it, and a client would connect to that instead of to the server under test.
    /// </param>
    public static NamedPipeServerStream CreateServer(string? name = null)
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine the current user.");

        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        // Not PipeOptions.CurrentUserOnly: that flag asks the framework to apply its own
        // owner-only ACL and is rejected outright when a PipeSecurity is supplied. The rule
        // above already limits the pipe to this user, and the client still verifies the
        // server's owner through its own CurrentUserOnly.
        return NamedPipeServerStreamAcl.Create(
            name ?? Name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    public static NamedPipeClientStream CreateClient(string? name = null) =>
        new(".", name ?? Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>Writes a length-prefixed UTF-8 JSON message.</summary>
    public static async Task WriteAsync<T>(Stream stream, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a length-prefixed UTF-8 JSON message, or null at end of stream.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var prefix = new byte[4];
        if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        // A hostile or broken peer must not be able to ask for a gigabyte-sized allocation.
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException($"Invalid message length {length}.");

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false)) return null;

        return JsonSerializer.Deserialize(Encoding.UTF8.GetString(payload), typeInfo);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }
}
