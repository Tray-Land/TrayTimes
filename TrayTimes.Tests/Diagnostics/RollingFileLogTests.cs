using TrayTimes.Diagnostics;
using TrayTimes.Tests.Transit;

namespace TrayTimes.Tests.Diagnostics;

[TestClass]
public sealed class RollingFileLogTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), "TrayTimesLogTests-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public void Constructor_InvalidArguments_Rejects()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentException>(() => new RollingFileLog(" "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RollingFileLog("x.log", maxFileBytes: 0));
    }

    [TestMethod]
    public void Write_FirstLine_CreatesMissingFolderAndTimestampsTheLine()
    {
        // Arrange
        RollingFileLog log = new(Path.Combine(_directory, "logs", "a.log"), timeProvider: new RequestTestClock());

        // Act
        log.Write("hello");

        // Assert
        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.HasCount(1, lines);
        Assert.AreEqual("2026-10-06 20:00:00.000 hello", lines[0]);
    }

    [TestMethod]
    public void Write_MessageWithLineBreaks_StaysOneLine()
    {
        // Arrange
        RollingFileLog log = new(Path.Combine(_directory, "a.log"));

        // Act
        log.Write("one\r\ntwo\nthree");

        // Assert
        Assert.HasCount(1, File.ReadAllLines(log.FilePath));
    }

    [TestMethod]
    public void Write_PastTheLimit_ArchivesTheFullFileAndStartsANewOne()
    {
        // Arrange
        RollingFileLog log = new(Path.Combine(_directory, "a.log"), maxFileBytes: 200);

        // Act
        for (int i = 0; i < 20; i++)
        {
            log.Write($"line {i:D2}");
        }

        // Assert
        Assert.IsTrue(File.Exists(log.ArchivePath));
        Assert.IsLessThanOrEqualTo(200, new FileInfo(log.FilePath).Length);
        Assert.IsLessThanOrEqualTo(200, new FileInfo(log.ArchivePath).Length);
        Assert.AreEqual("line 19", File.ReadAllLines(log.FilePath)[^1][24..]);
    }

    [TestMethod]
    public void Write_ExistingLargeFile_ArchivesItBeforeAppending()
    {
        // Arrange
        string path = Path.Combine(_directory, "a.log");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, new string('x', 500));
        RollingFileLog log = new(path, maxFileBytes: 200);

        // Act
        log.Write("fresh");

        // Assert
        Assert.AreEqual(500, new FileInfo(log.ArchivePath).Length);
        Assert.HasCount(1, File.ReadAllLines(path));
    }

    [TestMethod]
    public void Write_UnwritablePath_DoesNotThrow()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        RollingFileLog log = new(_directory);

        // Act & Assert
        log.Write("dropped");
    }
}
