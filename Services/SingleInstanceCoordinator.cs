using System.IO.Pipes;
using System.Text.Json;

namespace TapRoom.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\TapRoom.SingleInstance";
    private const string PipeName = "TapRoom.SingleInstance.Args";

    private readonly Action<string[]> _argsReceived;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listenerTask;

    public SingleInstanceCoordinator(Action<string[]> argsReceived)
    {
        _argsReceived = argsReceived;
    }

    public static bool TryAcquire(out Mutex mutex)
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
            return true;

        mutex.Dispose();
        return false;
    }

    public static bool TryForwardArguments(string[] args, int timeoutMilliseconds = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMilliseconds);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(JsonSerializer.Serialize(args));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Start()
    {
        _listenerTask = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_stop.Token);

                using var reader = new StreamReader(pipe);
                var payload = await reader.ReadToEndAsync(_stop.Token);
                var args = JsonSerializer.Deserialize<string[]>(payload) ?? Array.Empty<string>();
                _argsReceived(args);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep the listener alive. A bad IPC payload should not break TapRoom.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listenerTask?.Wait(500); } catch { }
        _stop.Dispose();
    }
}
