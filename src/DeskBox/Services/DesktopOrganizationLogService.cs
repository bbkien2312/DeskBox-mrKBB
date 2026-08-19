using System.Text;
using DeskBox.Models;

namespace DeskBox.Services;

public enum DesktopOrganizationLogLevel
{
    Info,
    Ok,
    Warning,
    Error
}

/// <summary>
/// Writes a small, dedicated, tab-separated log for desktop organization.
/// The general DeskBox.log remains useful for application-wide diagnostics;
/// this file is intended to make one scan/organize run easy to inspect.
/// </summary>
public sealed class DesktopOrganizationLogService
{
    private static readonly object WriteGate = new();

    public DesktopOrganizationLogService(string? logFilePath = null)
    {
        LogFilePath = string.IsNullOrWhiteSpace(logFilePath)
            ? Path.Combine(DeskBoxDataPathService.Current.LogDirectory, "desktop-organization.log")
            : Path.GetFullPath(logFilePath);
    }

    public string LogFilePath { get; }

    public void Info(string eventName, string message, DesktopOrganizationFileSnapshot? item = null) =>
        Write(DesktopOrganizationLogLevel.Info, eventName, message, item);

    public void Ok(string eventName, string message, DesktopOrganizationFileSnapshot? item = null) =>
        Write(DesktopOrganizationLogLevel.Ok, eventName, message, item);

    public void Warning(string eventName, string message, DesktopOrganizationFileSnapshot? item = null) =>
        Write(DesktopOrganizationLogLevel.Warning, eventName, message, item);

    public void Error(string eventName, string message, DesktopOrganizationFileSnapshot? item = null) =>
        Write(DesktopOrganizationLogLevel.Error, eventName, message, item);

    public void Write(
        DesktopOrganizationLogLevel level,
        string eventName,
        string message,
        DesktopOrganizationFileSnapshot? item = null)
    {
        string sourcePath = item?.SourcePath ?? string.Empty;
        string line = string.Join(
            '\t',
            DateTimeOffset.Now.ToString("O"),
            level.ToString().ToUpperInvariant(),
            Clean(eventName),
            Clean(message),
            Clean(sourcePath));

        try
        {
            string? directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (WriteGate)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never prevent the user from organizing files.
        }
    }

    private static string Clean(string? value) =>
        (value ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
