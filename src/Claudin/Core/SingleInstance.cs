using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Claudin.Core;

/// <summary>
/// Instância única do Claudin: um mutex por sessão do Windows e um named pipe restrito ao usuário atual.
/// A segunda execução manda uma mensagem para a primeira (que vem para a frente) e encerra.
/// O nome depende da pasta do Claudin, então uma configuração de teste (CLAUDIN_HOME) roda em paralelo.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ActivateMessage = "activate";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    public static string NameFor(string home)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(home.Trim().TrimEnd('\\', '/').ToLowerInvariant()));
        return "Claudin-" + Convert.ToHexString(hash)[..16];
    }

    /// <summary>Tenta ser a instância principal. Nulo = já existe outra.</summary>
    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, name);
    }

    /// <summary>Escuta mensagens de outras execuções. O callback roda numa thread do pool.</summary>
    public void StartServer(Action<string> onMessage)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(token);
                    if (line is not null)
                    {
                        onMessage(line.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Pipe de instância única: {ex.Message}");
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, token);
    }

    /// <summary>Envia uma mensagem para a instância principal. False se não conseguiu.</summary>
    public static bool SendToExisting(string name, string message, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(message);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não foi possível falar com a instância principal: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // liberado por outra thread; o Dispose resolve
        }
        _mutex.Dispose();
        _cts.Dispose();
    }
}
