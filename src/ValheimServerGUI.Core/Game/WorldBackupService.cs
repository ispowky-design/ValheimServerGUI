using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game;

/// <summary>Where a backup came from: made by VSG (a zip it owns) or by the dedicated server itself.</summary>
public enum WorldBackupKind
{
    /// <summary>A zip VSG wrote under <c>&lt;savedir&gt;/vsg_backups/&lt;World&gt;</c> ("Back up now", or before a restore).</summary>
    Manual,

    /// <summary>A server-made <c>&lt;World&gt;_backup_…</c> folder or file pair next to the world (the <c>-backups</c> rotation).</summary>
    Automatic,
}

/// <summary>One restorable copy of a world.</summary>
/// <param name="Path">The zip (Manual), the backup folder (Automatic, Valheim 1.0+), or the backup's <c>.fwl</c> (Automatic, legacy).</param>
public sealed record WorldBackup(string WorldName, WorldBackupKind Kind, DateTime CreatedAt, long SizeBytes, string Path);

/// <summary>
/// Lists, creates, restores and deletes world backups. Callers make sure no server is running the world before
/// a restore; this service only touches files.
/// </summary>
public interface IWorldBackupService
{
    /// <summary>Every backup of <paramref name="worldName"/> (VSG zips and server backups), newest first.</summary>
    IReadOnlyList<WorldBackup> GetBackups(DirectoryInfo saveFolder, string worldName);

    /// <summary>Whether the world has data on disk that a backup would capture.</summary>
    bool WorldExists(DirectoryInfo saveFolder, string worldName);

    /// <summary>Zips the world's current files into its VSG backup folder and returns the new backup.</summary>
    WorldBackup CreateBackup(DirectoryInfo saveFolder, string worldName);

    /// <summary>
    /// Replaces the world's files with <paramref name="backup"/>. When the world exists, its current files are
    /// zipped first, and that safety backup is returned (null when there was nothing to save).
    /// </summary>
    WorldBackup? RestoreBackup(DirectoryInfo saveFolder, WorldBackup backup);

    /// <summary>Deletes a VSG backup. Server-made backups are left to the server's own rotation.</summary>
    void DeleteBackup(WorldBackup backup);

    /// <summary>The folder VSG writes a world's backups to (it may not exist yet).</summary>
    DirectoryInfo GetBackupFolder(DirectoryInfo saveFolder, string worldName);
}

public sealed class WorldBackupService : IWorldBackupService
{
    /// <summary>The save-data subfolder holding VSG's zips, one folder per world. The server never reads it.</summary>
    public const string BackupFolderName = "vsg_backups";

    private const string ZipTimestampFormat = "yyyyMMdd-HHmmss";
    private static readonly string[] WorldsFolderNames = { "worlds_local", "worlds" };
    private static readonly string[] LegacyExtensions = { ".fwl", ".db" };

    // "<World>_backup_auto-20240205174838" and "<World>_backup_20240604-183113" (the same two schemes
    // ValheimPathExtensions hides from the world list).
    private static readonly Regex ServerBackupName = new(@"^(?<world>.+?)_backup_(?:auto-(?<auto>\d{14})|(?<dated>\d{8}-\d{6}))$");

    private readonly Func<DateTime> _now;

    public WorldBackupService() : this(() => DateTime.Now)
    {
    }

    /// <summary>Test seam: a fixed clock gives predictable zip names.</summary>
    internal WorldBackupService(Func<DateTime> now)
    {
        _now = now;
    }

    public DirectoryInfo GetBackupFolder(DirectoryInfo saveFolder, string worldName)
        => new(Path.Join(saveFolder.FullName, BackupFolderName, worldName));

    public bool WorldExists(DirectoryInfo saveFolder, string worldName)
        => !string.IsNullOrWhiteSpace(worldName) && GetWorldFiles(saveFolder, worldName).Any();

    public IReadOnlyList<WorldBackup> GetBackups(DirectoryInfo saveFolder, string worldName)
    {
        var backups = new List<WorldBackup>();
        if (string.IsNullOrWhiteSpace(worldName)) return backups;

        var backupFolder = GetBackupFolder(saveFolder, worldName);
        if (backupFolder.Exists)
        {
            foreach (var zip in backupFolder.GetFiles("*.zip"))
            {
                backups.Add(new WorldBackup(worldName, WorldBackupKind.Manual, ParseZipTime(zip, worldName), zip.Length, zip.FullName));
            }
        }

        foreach (var folder in ExistingWorldsFolders(saveFolder))
        {
            // Valheim 1.0+: a backup is a folder holding the world's *.fwl2 save.
            foreach (var dir in folder.GetDirectories())
            {
                if (!TryParseServerBackup(dir.Name, worldName, out var time) || !dir.GetFiles("*.fwl2").Any()) continue;
                var size = dir.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                backups.Add(new WorldBackup(worldName, WorldBackupKind.Automatic, time, size, dir.FullName));
            }

            // Legacy: a "<backup>.fwl" + "<backup>.db" pair; the .fwl identifies it.
            foreach (var fwl in folder.GetFiles("*.fwl"))
            {
                var name = Path.GetFileNameWithoutExtension(fwl.Name);
                if (!TryParseServerBackup(name, worldName, out var time)) continue;
                var db = new FileInfo(Path.Join(folder.FullName, name + ".db"));
                backups.Add(new WorldBackup(worldName, WorldBackupKind.Automatic, time, fwl.Length + (db.Exists ? db.Length : 0), fwl.FullName));
            }
        }

        return backups.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public WorldBackup CreateBackup(DirectoryInfo saveFolder, string worldName)
    {
        var files = GetWorldFiles(saveFolder, worldName).ToList();
        if (files.Count == 0)
            throw new FileNotFoundException(string.Format(Strings.Backups_Error_WorldNotFound, worldName));

        var backupFolder = GetBackupFolder(saveFolder, worldName);
        backupFolder.Create();

        var createdAt = _now();
        var zipPath = NextFreeZipPath(backupFolder, worldName, createdAt);

        // Written under a temporary name and moved into place, so a failed or interrupted backup never leaves a
        // half-written zip that looks like a real one.
        var partial = zipPath + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var entryName = Path.GetRelativePath(saveFolder.FullName, file.FullName).Replace('\\', '/');
                    // FileShare.ReadWrite: a running server may hold its save files open.
                    using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                    source.CopyTo(entry);
                }
            }

            File.Move(partial, zipPath);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }

        return new WorldBackup(worldName, WorldBackupKind.Manual, createdAt, new FileInfo(zipPath).Length, zipPath);
    }

    public WorldBackup? RestoreBackup(DirectoryInfo saveFolder, WorldBackup backup)
    {
        var worldName = backup.WorldName;

        // Stage the backup's files first: a backup that cannot be read must leave the current world untouched.
        var staging = new DirectoryInfo(Path.Join(saveFolder.FullName, BackupFolderName, ".restore-" + Guid.NewGuid().ToString("N")));
        try
        {
            switch (backup.Kind)
            {
                case WorldBackupKind.Manual:
                    ExtractZip(backup.Path, staging, worldName);
                    break;
                case WorldBackupKind.Automatic when Directory.Exists(backup.Path):
                    StageServerFolderBackup(new DirectoryInfo(backup.Path), saveFolder, staging, worldName);
                    break;
                case WorldBackupKind.Automatic when File.Exists(backup.Path):
                    StageServerLegacyBackup(new FileInfo(backup.Path), saveFolder, staging, worldName);
                    break;
                default:
                    throw new FileNotFoundException(string.Format(Strings.Backups_Error_BackupNotFound, backup.Path));
            }

            var stagedFiles = staging.Exists ? staging.GetFiles("*", SearchOption.AllDirectories) : Array.Empty<FileInfo>();
            if (stagedFiles.Length == 0)
                throw new InvalidDataException(string.Format(Strings.Backups_Error_BackupEmpty, backup.Path));

            var safety = WorldExists(saveFolder, worldName) ? CreateBackup(saveFolder, worldName) : null;

            foreach (var file in GetWorldFiles(saveFolder, worldName).ToList()) file.Delete();
            foreach (var folder in WorldsFolderNames)
            {
                var worldDir = new DirectoryInfo(Path.Join(saveFolder.FullName, folder, worldName));
                if (worldDir.Exists) worldDir.Delete(true);
            }

            foreach (var file in stagedFiles)
            {
                var dest = new FileInfo(Path.Join(saveFolder.FullName, Path.GetRelativePath(staging.FullName, file.FullName)));
                dest.Directory?.Create();
                file.MoveTo(dest.FullName, true);
            }

            return safety;
        }
        finally
        {
            if (staging.Exists) staging.Delete(true);
        }
    }

    public void DeleteBackup(WorldBackup backup)
    {
        if (backup.Kind != WorldBackupKind.Manual)
            throw new InvalidOperationException(Strings.Backups_Error_DeleteAutomatic);

        if (File.Exists(backup.Path)) File.Delete(backup.Path);
    }

    #region Helpers

    /// <summary>
    /// The files that make up the world, in both formats and both worlds folders: everything under
    /// <c>&lt;folder&gt;/&lt;World&gt;/</c> (Valheim 1.0+) plus <c>&lt;folder&gt;/&lt;World&gt;.fwl/.db</c> (legacy).
    /// </summary>
    private static IEnumerable<FileInfo> GetWorldFiles(DirectoryInfo saveFolder, string worldName)
    {
        foreach (var folder in ExistingWorldsFolders(saveFolder))
        {
            var worldDir = new DirectoryInfo(Path.Join(folder.FullName, worldName));
            if (worldDir.Exists && worldDir.GetFiles("*.fwl2").Any())
            {
                foreach (var file in worldDir.GetFiles("*", SearchOption.AllDirectories)) yield return file;
            }

            foreach (var ext in LegacyExtensions)
            {
                var file = new FileInfo(Path.Join(folder.FullName, worldName + ext));
                if (file.Exists) yield return file;
            }
        }
    }

    private static IEnumerable<DirectoryInfo> ExistingWorldsFolders(DirectoryInfo saveFolder)
        => WorldsFolderNames
            .Select(name => new DirectoryInfo(Path.Join(saveFolder.FullName, name)))
            .Where(dir => dir.Exists);

    private static bool TryParseServerBackup(string name, string worldName, out DateTime createdAt)
    {
        createdAt = default;
        var match = ServerBackupName.Match(name);
        if (!match.Success || !string.Equals(match.Groups["world"].Value, worldName, StringComparison.OrdinalIgnoreCase)) return false;

        return match.Groups["auto"].Success
            ? DateTime.TryParseExact(match.Groups["auto"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out createdAt)
            : DateTime.TryParseExact(match.Groups["dated"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out createdAt);
    }

    private static DateTime ParseZipTime(FileInfo zip, string worldName)
    {
        var stamp = Path.GetFileNameWithoutExtension(zip.Name);
        var prefix = worldName + "_";
        if (stamp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            stamp = stamp[prefix.Length..];
            // A same-second collision gets a "_2" suffix; the time is still the stamp before it.
            var suffix = stamp.IndexOf('_');
            if (suffix > 0) stamp = stamp[..suffix];
            if (DateTime.TryParseExact(stamp, ZipTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return time;
        }

        return zip.LastWriteTime;
    }

    private static string NextFreeZipPath(DirectoryInfo backupFolder, string worldName, DateTime createdAt)
    {
        var stamp = createdAt.ToString(ZipTimestampFormat, CultureInfo.InvariantCulture);
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? $"{worldName}_{stamp}.zip" : $"{worldName}_{stamp}_{i}.zip";
            var path = Path.Join(backupFolder.FullName, name);
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>
    /// Extracts a VSG zip into <paramref name="staging"/>. Every entry must belong to the world (its folder or its
    /// legacy file pair, in a worlds folder): anything else means the zip is not a backup of this world.
    /// </summary>
    private static void ExtractZip(string zipPath, DirectoryInfo staging, string worldName)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException(string.Format(Strings.Backups_Error_BackupNotFound, zipPath));

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // a directory entry

            if (!IsWorldEntry(entry.FullName, worldName))
                throw new InvalidDataException(string.Format(Strings.Backups_Error_ForeignEntry, entry.FullName, worldName));

            var dest = new FileInfo(Path.Join(staging.FullName, entry.FullName));
            dest.Directory?.Create();
            entry.ExtractToFile(dest.FullName, true);
        }
    }

    private static bool IsWorldEntry(string entryName, string worldName)
    {
        var parts = entryName.Split('/');
        if (parts.Length < 2 || !WorldsFolderNames.Contains(parts[0])) return false;
        if (parts.Any(p => p is "" or "." or "..")) return false;

        if (parts.Length == 2)
            return LegacyExtensions.Any(ext => string.Equals(parts[1], worldName + ext, StringComparison.OrdinalIgnoreCase));

        return string.Equals(parts[1], worldName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A server backup folder <c>&lt;folder&gt;/&lt;World&gt;_backup_…/</c> restores to <c>&lt;folder&gt;/&lt;World&gt;/</c>.</summary>
    private static void StageServerFolderBackup(DirectoryInfo source, DirectoryInfo saveFolder, DirectoryInfo staging, string worldName)
    {
        var folderName = Path.GetRelativePath(saveFolder.FullName, source.Parent!.FullName);
        var target = Path.Join(staging.FullName, folderName, worldName);
        foreach (var file in source.GetFiles("*", SearchOption.AllDirectories))
        {
            var dest = new FileInfo(Path.Join(target, Path.GetRelativePath(source.FullName, file.FullName)));
            dest.Directory?.Create();
            file.CopyTo(dest.FullName, true);
        }
    }

    /// <summary>A legacy server backup <c>&lt;World&gt;_backup_….fwl/.db</c> restores to <c>&lt;World&gt;.fwl/.db</c>.</summary>
    private static void StageServerLegacyBackup(FileInfo fwl, DirectoryInfo saveFolder, DirectoryInfo staging, string worldName)
    {
        var folder = fwl.Directory!;
        var target = Directory.CreateDirectory(Path.Join(staging.FullName, Path.GetRelativePath(saveFolder.FullName, folder.FullName)));
        var backupName = Path.GetFileNameWithoutExtension(fwl.Name);
        foreach (var ext in LegacyExtensions)
        {
            var file = new FileInfo(Path.Join(folder.FullName, backupName + ext));
            if (file.Exists) file.CopyTo(Path.Join(target.FullName, worldName + ext), true);
        }
    }

    #endregion
}
