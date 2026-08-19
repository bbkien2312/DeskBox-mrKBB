using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopOrganizationLogServiceTests
{
    [Fact]
    public void Write_CreatesTimestampedLevelledReadableLog()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "desktop-organization.log");
        try
        {
            var logger = new DesktopOrganizationLogService(path);
            logger.Info("ScanStarted", "includeManagedWidgetItems=true");
            logger.Warning("SourceChangedAfterScan", "name=baby; expectedSize=10");
            logger.Ok("ExecuteCompleted", "items=2; targets=1");

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("\tINFO\tScanStarted\t", lines[0], StringComparison.Ordinal);
            Assert.Contains("\tWARNING\tSourceChangedAfterScan\t", lines[1], StringComparison.Ordinal);
            Assert.Contains("\tOK\tExecuteCompleted\t", lines[2], StringComparison.Ordinal);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", lines[0]);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
