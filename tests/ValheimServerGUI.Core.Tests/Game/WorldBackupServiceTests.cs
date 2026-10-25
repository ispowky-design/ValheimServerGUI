using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ValheimServerGUI.Game;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game;

/// <summary>
/// World backups against a temp save folder: listing VSG zips and both server backup schemes (folder and legacy
/// file pair), zipping a world, restoring each kind (with the safety backup first), and refusing zips that
/// reach outside the world.
/// </summary>
public class WorldBackupServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 20, 15, 30);

    private readonly DirectoryInfo _saveFolder;
    private readonly string _worldsLocal;
    private readonly WorldBackupService _service = new(() => Now);

    public WorldBackupServiceTests()
    {
        _saveFolder = new DirectoryInfo(Path.Join(Path.GetTempPath(), "vsg_backup_test_" + Guid.NewGuid().ToString("N")));
        _worldsLocal = Path.Join(_saveFolder.FullName, "worlds_local");
        Directory.CreateDirectory(_worldsLocal);
    }

    public void Dispose()
    {
        if (_saveFolder.Exists) _saveFolder.Delete(true);
    }

    private string WriteFolderWorld(string name, string content)
    {
        var dir = Directory.CreateDirectory(Path.Join(_worldsLocal, name));
        File.WriteAllText(Path.Join(dir.FullName, "_main.1.fwl2"), content);
        File.WriteAllText(Path.Join(dir.FullName, "_main.1.db2"), content + "-db");
        var chunks = Directory.CreateDirectory(Path.Join(dir.FullName, "chunks"));
        File.WriteAllText(Path.Join(chunks.FullName, "0_0.chunk"), content + "-chunk");
        return dir.FullName;
    }

    private void WriteLegacyWorld(string name, string content)
    {
        File.WriteAllText(Path.Join(_worldsLocal, name + ".fwl"), content);
        File.WriteAllText(Path.Join(_worldsLocal, name + ".db"), content + "-db");
    }

    private string ReadWorldFile(params string[] parts) => File.ReadAllText(Path.Join(new[] { _worldsLocal }.Concat(parts).ToArray()));

    [Fact]
    public void WorldExists_IsTrueForEitherFormat_AndFalseForNothing()
    {
        WriteFolderWorld("Folder", "a");
        WriteLegacyWorld("Legacy", "b");

        Assert.True(_service.WorldExists(_saveFolder, "Folder"));
        Assert.True(_service.WorldExists(_saveFolder, "Legacy"));
        Assert.False(_service.WorldExists(_saveFolder, "Missing"));
        Assert.False(_service.WorldExists(_saveFolder, ""));
    }

    // A cache-only folder (no .fwl2) is not a world, so there is nothing to back up.
    [Fact]
    public void WorldExists_IgnoresAFolderWithoutASave()
    {
        var dir = Directory.CreateDirectory(Path.Join(_worldsLocal, "Cache"));
        File.WriteAllText(Path.Join(dir.FullName, "cacheMinimap_1"), "x");

        Assert.False(_service.WorldExists(_saveFolder, "Cache"));
    }

    [Fact]
    public void CreateBackup_ZipsEveryWorldFileUnderItsSaveFolderPath()
    {
        WriteFolderWorld("Viking", "v1");

        var backup = _service.CreateBackup(_saveFolder, "Viking");

        Assert.Equal(WorldBackupKind.Manual, backup.Kind);
        Assert.Equal(Now, backup.CreatedAt);
        Assert.Equal(Path.Join(_saveFolder.FullName, "vsg_backups", "Viking", "Viking_20261025-201530.zip"), backup.Path);
        using var zip = ZipFile.OpenRead(backup.Path);
        Assert.Equal(
            new[] { "worlds_local/Viking/_main.1.db2", "worlds_local/Viking/_main.1.fwl2", "worlds_local/Viking/chunks/0_0.chunk" },
            zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.False(File.Exists(backup.Path + ".partial"));
    }

    [Fact]
    public void CreateBackup_TwiceInTheSameSecond_KeepsBoth()
    {
        WriteLegacyWorld("Viking", "v1");

        var first = _service.CreateBackup(_saveFolder, "Viking");
        var second = _service.CreateBackup(_saveFolder, "Viking");

        Assert.NotEqual(first.Path, second.Path);
        Assert.EndsWith("Viking_20261025-201530_2.zip", second.Path);
        Assert.All(_service.GetBackups(_saveFolder, "Viking"), b => Assert.Equal(Now, b.CreatedAt));
    }

    [Fact]
    public void CreateBackup_OfAMissingWorld_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => _service.CreateBackup(_saveFolder, "Missing"));
        Assert.False(Directory.Exists(Path.Join(_saveFolder.FullName, "vsg_backups", "Missing")));
    }

    [Fact]
    public void GetBackups_ListsZipsAndBothServerSchemes_NewestFirst_ForThisWorldOnly()
    {
        WriteFolderWorld("Viking", "now");
        WriteFolderWorld("Viking_backup_auto-20261020120000", "auto");
        WriteLegacyWorld("Viking_backup_20261018-090000", "dated");
        WriteFolderWorld("Other_backup_auto-20261024120000", "other");
        _service.CreateBackup(_saveFolder, "Viking");

        var backups = _service.GetBackups(_saveFolder, "Viking");

        Assert.Equal(
            new[] { (WorldBackupKind.Manual, Now), (WorldBackupKind.Automatic, new DateTime(2026, 10, 20, 12, 0, 0)), (WorldBackupKind.Automatic, new DateTime(2026, 10, 18, 9, 0, 0)) },
            backups.Select(b => (b.Kind, b.CreatedAt)));
        Assert.All(backups, b => Assert.True(b.SizeBytes > 0));
    }

    [Fact]
    public void RestoreBackup_FromZip_ReplacesTheWorld_AndSavesTheCurrentStateFirst()
    {
        var worldDir = WriteFolderWorld("Viking", "old");
        var backup = _service.CreateBackup(_saveFolder, "Viking");
        Directory.Delete(worldDir, true);
        WriteFolderWorld("Viking", "new");
        File.WriteAllText(Path.Join(worldDir, "_main.2.fwl2"), "newer save");

        var safety = new WorldBackupService(() => Now.AddMinutes(5)).RestoreBackup(_saveFolder, backup);

        Assert.Equal("old", ReadWorldFile("Viking", "_main.1.fwl2"));
        Assert.Equal("old-chunk", ReadWorldFile("Viking", "chunks", "0_0.chunk"));
        Assert.False(File.Exists(Path.Join(worldDir, "_main.2.fwl2")));

        Assert.NotNull(safety);
        using var zip = ZipFile.OpenRead(safety!.Path);
        Assert.Contains(zip.Entries, e => e.FullName == "worlds_local/Viking/_main.2.fwl2");
        Assert.Empty(Directory.GetDirectories(Path.Join(_saveFolder.FullName, "vsg_backups"), ".restore-*"));
    }

    [Fact]
    public void RestoreBackup_FromServerFolderBackup_CopiesItIntoTheWorldFolder_AndKeepsTheBackup()
    {
        WriteFolderWorld("Viking", "current");
        var backupDir = WriteFolderWorld("Viking_backup_auto-20261020120000", "auto");
        var backup = _service.GetBackups(_saveFolder, "Viking").Single();

        _service.RestoreBackup(_saveFolder, backup);

        Assert.Equal("auto", ReadWorldFile("Viking", "_main.1.fwl2"));
        Assert.Equal("auto-chunk", ReadWorldFile("Viking", "chunks", "0_0.chunk"));
        Assert.True(Directory.Exists(backupDir));
    }

    [Fact]
    public void RestoreBackup_FromLegacyServerBackup_RenamesThePairToTheWorld()
    {
        WriteLegacyWorld("Viking", "current");
        WriteLegacyWorld("Viking_backup_auto-20261020120000", "auto");
        var backup = _service.GetBackups(_saveFolder, "Viking").Single();

        _service.RestoreBackup(_saveFolder, backup);

        Assert.Equal("auto", ReadWorldFile("Viking.fwl"));
        Assert.Equal("auto-db", ReadWorldFile("Viking.db"));
        Assert.True(File.Exists(Path.Join(_worldsLocal, "Viking_backup_auto-20261020120000.fwl")));
    }

    // A world that was deleted can be brought back; there is nothing to save first.
    [Fact]
    public void RestoreBackup_OfADeletedWorld_RecreatesIt_WithoutASafetyBackup()
    {
        var worldDir = WriteFolderWorld("Viking", "old");
        var backup = _service.CreateBackup(_saveFolder, "Viking");
        Directory.Delete(worldDir, true);

        var safety = _service.RestoreBackup(_saveFolder, backup);

        Assert.Null(safety);
        Assert.Equal("old", ReadWorldFile("Viking", "_main.1.fwl2"));
    }

    [Theory]
    [InlineData("adminlist.txt")]
    [InlineData("worlds_local/Other/_main.1.fwl2")]
    [InlineData("worlds_local/Viking/../../evil.txt")]
    [InlineData("worlds_local/Other.db")]
    public void RestoreBackup_RefusesAZipWithForeignEntries_AndLeavesTheWorldAlone(string entryName)
    {
        WriteFolderWorld("Viking", "current");
        var zipDir = Directory.CreateDirectory(Path.Join(_saveFolder.FullName, "vsg_backups", "Viking"));
        var zipPath = Path.Join(zipDir.FullName, "Viking_20261001-000000.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("worlds_local/Viking/_main.1.fwl2").Open())) writer.Write("backup");
            using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open())) writer.Write("x");
        }
        var backup = _service.GetBackups(_saveFolder, "Viking").Single();

        Assert.Throws<InvalidDataException>(() => _service.RestoreBackup(_saveFolder, backup));

        Assert.Equal("current", ReadWorldFile("Viking", "_main.1.fwl2"));
        Assert.Single(_service.GetBackups(_saveFolder, "Viking"));
    }

    [Fact]
    public void DeleteBackup_RemovesAZip_ButRefusesServerBackups()
    {
        WriteFolderWorld("Viking", "current");
        WriteFolderWorld("Viking_backup_auto-20261020120000", "auto");
        var manual = _service.CreateBackup(_saveFolder, "Viking");
        var automatic = _service.GetBackups(_saveFolder, "Viking").Single(b => b.Kind == WorldBackupKind.Automatic);

        _service.DeleteBackup(manual);
        Assert.Throws<InvalidOperationException>(() => _service.DeleteBackup(automatic));

        Assert.False(File.Exists(manual.Path));
        Assert.True(Directory.Exists(automatic.Path));
    }

    // VSG's backup folder sits beside the worlds folders, so it never shows up as a world.
    [Fact]
    public void BackupFolder_IsNotListedAsAWorld()
    {
        WriteFolderWorld("Viking", "current");
        _service.CreateBackup(_saveFolder, "Viking");

        Assert.Equal(new[] { "Viking" }, _saveFolder.GetWorldNames());
    }
}
