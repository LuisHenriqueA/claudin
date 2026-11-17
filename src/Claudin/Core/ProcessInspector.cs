using System.Runtime.InteropServices;
using Claudin.Interop;

namespace Claudin.Core;

public sealed record ProcessEntry(int Pid, int ParentPid, string ExeName);

/// <summary>Leitura de processos de terceiros (substituível nos testes). Nenhum método lança exceção.</summary>
public interface IProcessInspector
{
    IReadOnlyList<ProcessEntry> Snapshot();

    string? GetImagePath(int pid);

    string? GetCommandLine(int pid);
}

public sealed class WindowsProcessInspector : IProcessInspector
{
    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        var result = new List<ProcessEntry>();
        var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == NativeMethods.INVALID_HANDLE_VALUE || snap == IntPtr.Zero)
        {
            return result;
        }
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snap, ref entry))
            {
                return result;
            }
            do
            {
                result.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            }
            while (NativeMethods.Process32NextW(snap, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snap);
        }
        return result;
    }

    public string? GetImagePath(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    public string? GetCommandLine(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var length = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    var status = NativeMethods.NtQueryInformationProcess(
                        h, NativeMethods.ProcessCommandLineInformation, buffer, length, out var needed);
                    if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH && needed > length)
                    {
                        length = needed;
                        continue;
                    }
                    if (status != 0)
                    {
                        return null;
                    }
                    var us = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buffer);
                    return us.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }
}
