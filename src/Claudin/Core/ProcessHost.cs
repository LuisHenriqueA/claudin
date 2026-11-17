using System.ComponentModel;
using System.Diagnostics;

namespace Claudin.Core;

/// <summary>Handle de um processo de terceiro. Nenhum membro lança exceção.</summary>
public interface IProcessHandle : IDisposable
{
    int Pid { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Encerra o processo e seus filhos. Só chamar por pedido explícito do usuário.</summary>
    bool KillTree();
}

/// <summary>Inicia e abre processos (substituível nos testes).</summary>
public interface IProcessHost
{
    /// <summary>Inicia o executável. Lança exceção se não conseguir.</summary>
    IProcessHandle Launch(string executable, string arguments);

    /// <summary>Abre um processo existente; nulo se ele não existe mais.</summary>
    IProcessHandle? Open(int pid);
}

public sealed class WindowsProcessHost : IProcessHost
{
    public IProcessHandle Launch(string executable, string arguments)
    {
        var psi = new ProcessStartInfo(executable, arguments)
        {
            // Fase 0: sem shell; sem console herdado (o Electron escreve avisos no stdout/stderr).
            // Sem redirecionar a saída: um pipe preso ao Claudin poderia travar ou derrubar o Claude quando o Claudin fechar.
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start não retornou processo.");
        return new WindowsProcessHandle(process);
    }

    public IProcessHandle? Open(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            try
            {
                // Mantém um handle aberto: evita confundir o processo com outro que reaproveite o mesmo PID.
                _ = process.SafeHandle;
            }
            catch (Win32Exception)
            {
                // Sem acesso total: segue com consultas pontuais.
            }
            return new WindowsProcessHandle(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private sealed class WindowsProcessHandle(Process process) : IProcessHandle
    {
        public int Pid { get; } = process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
                catch (Win32Exception)
                {
                    return false;
                }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return process.HasExited ? process.ExitCode : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    return null;
                }
            }
        }

        public bool KillTree()
        {
            try
            {
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                Log.Error($"Falha ao encerrar o processo {Pid}", ex);
                return false;
            }
        }

        public void Dispose() => process.Dispose();
    }
}
