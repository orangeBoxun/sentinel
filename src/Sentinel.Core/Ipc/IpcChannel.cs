using System.IO.Pipes;
using System.Text;

namespace Sentinel.Core.Ipc;

/// <summary>
/// One request, one response, one line of JSON each way. Deliberately simple: the traffic is a config
/// read every few seconds plus the occasional privileged request, and a line protocol is easy to
/// reason about when something goes wrong at 2am.
/// </summary>
public static class IpcChannel
{
    private const int MaxMessageBytes = 1 << 20;

    public static async Task WriteAsync(Stream stream, string payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.Replace("\r", "").Replace("\n", " ") + "\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<string?> ReadAsync(Stream stream, CancellationToken token)
    {
        var collected = new MemoryStream();
        var chunk = new byte[4096];

        while (collected.Length < MaxMessageBytes)
        {
            var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0)
                break;   // peer closed

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                collected.Write(chunk, 0, newline);
                return Encoding.UTF8.GetString(collected.ToArray());
            }

            collected.Write(chunk, 0, read);
        }

        return collected.Length > 0 ? Encoding.UTF8.GetString(collected.ToArray()) : null;
    }

    public static async Task<IpcResponse> SendAsync(string pipeName, IpcRequest request, CancellationToken token)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, token).ConfigureAwait(false);

            await WriteAsync(client, Ipc.Serialize(request), token).ConfigureAwait(false);
            var line = await ReadAsync(client, token).ConfigureAwait(false);

            return line is null
                ? IpcResponse.Unreachable("service closed the connection")
                : Ipc.Deserialize<IpcResponse>(line) ?? IpcResponse.Unreachable("unreadable reply");
        }
        catch (Exception ex)
        {
            return IpcResponse.Unreachable(ex.Message);
        }
    }
}
