using System.Text.Json;
using Microsoft.Win32;

namespace Claudin.Core;

/// <summary>Acesso a um valor do registro (substituível nos testes).</summary>
public interface IRegistryValueStore
{
    string Location { get; }

    string? Get(string name);

    void Set(string name, string value);

    void Delete(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public sealed class RunKeyStore : IRegistryValueStore
{
    private const string SubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string Location => @"HKCU\" + SubKey;

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SubKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Valor original de uma entrada de registro, salvo antes da primeira alteração (regra 3 do plano).</summary>
public sealed record RegistryBackupEntry(string Location, string Name, bool Existed, string? Value, DateTime SavedAt);

/// <summary>
/// "Iniciar com o Windows": um valor "Claudin" na chave Run do usuário. Antes da primeira alteração, o valor original
/// (ou a ausência dele) é salvo em registry-backup.json, e RestoreOriginal() o devolve (botão nas configurações e
/// "Claudin.exe --restore-registry", usado na desinstalação).
/// </summary>
public sealed class StartupRegistration(IRegistryValueStore store, string backupFile, string valueName = "Claudin")
{
    public const string BackgroundArgument = "--background";

    public bool IsEnabled(string executablePath) =>
        string.Equals(store.Get(valueName), CommandFor(executablePath), StringComparison.OrdinalIgnoreCase);

    public bool HasBackup => File.Exists(backupFile);

    public static string CommandFor(string executablePath) => $"\"{executablePath}\" {BackgroundArgument}";

    public void Enable(string executablePath)
    {
        SaveBackupIfMissing();
        store.Set(valueName, CommandFor(executablePath));
        Log.Info($"Início com o Windows ativado ({store.Location})");
    }

    public void Disable()
    {
        SaveBackupIfMissing();
        store.Delete(valueName);
        Log.Info($"Início com o Windows desativado ({store.Location})");
    }

    /// <summary>Devolve a entrada ao estado anterior ao Claudin e apaga o backup. False se não havia backup.</summary>
    public bool RestoreOriginal()
    {
        if (!HasBackup)
        {
            return false;
        }
        RegistryBackupEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<RegistryBackupEntry>(File.ReadAllText(backupFile), ConfigService.JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Error("Backup do registro ilegível; nada foi alterado", ex);
            return false;
        }
        if (entry is null || string.IsNullOrWhiteSpace(entry.Name))
        {
            Log.Warn("Backup do registro incompleto; nada foi alterado");
            return false;
        }
        if (entry.Existed && entry.Value is not null)
        {
            store.Set(entry.Name, entry.Value);
        }
        else
        {
            store.Delete(entry.Name);
        }
        File.Delete(backupFile);
        Log.Info($"Registro restaurado ao original ({entry.Location}\\{entry.Name})");
        return true;
    }

    private void SaveBackupIfMissing()
    {
        if (HasBackup)
        {
            return;
        }
        var current = store.Get(valueName);
        var entry = new RegistryBackupEntry(store.Location, valueName, current is not null, current, DateTime.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, JsonSerializer.Serialize(entry, ConfigService.JsonOptions));
    }
}
