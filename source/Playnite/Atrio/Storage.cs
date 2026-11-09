using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>An installed copy of a game and the room it takes.</summary>
    public class StorageGame
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }

        /// <summary>Bytes on the disk, 0 while unknown.</summary>
        public long Size { get; set; }
    }

    /// <summary>What Atrio needs to know of a disk, so the sums can be tested without real disks.</summary>
    public class StorageDiskInfo
    {
        /// <summary>"D:\".</summary>
        public string Root { get; set; }
        public string Label { get; set; }
        public long Total { get; set; }
        public long Free { get; set; }
    }

    public class StorageDisk
    {
        public string Root { get; set; }
        public string Label { get; set; }
        public long Total { get; set; }
        public long Free { get; set; }
        public long GamesSize { get; set; }

        /// <summary>Everything else on the disk: Windows, other programs, files.</summary>
        public long OtherSize => Math.Max(0, Total - Free - GamesSize);

        /// <summary>The biggest first.</summary>
        public List<StorageGame> Games { get; set; } = new List<StorageGame>();
    }

    /// <summary>Which disk holds each installed game and how full each disk is, like the storage page of Steam.</summary>
    public static class Storage
    {
        /// <summary>"D:\" for "d:\steam\steamapps\common\Dead Cells"; null when the folder isn't on a lettered disk.</summary>
        public static string GetRoot(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            try
            {
                var root = Path.GetPathRoot(folder.Trim());
                if (root == null || root.Length < 2 || root[1] != ':')
                {
                    return null;
                }

                return char.ToUpperInvariant(root[0]) + ":\\";
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// The disks with games first, the fullest of them first, then the other fixed disks. A game on a disk
        /// that isn't connected right now is left out: there is nothing to free there.
        /// </summary>
        public static List<StorageDisk> Build(IEnumerable<StorageGame> games, IEnumerable<StorageDiskInfo> disks)
        {
            var result = (disks ?? Enumerable.Empty<StorageDiskInfo>()).
                Where(a => a != null && a.Root != null).
                GroupBy(a => a.Root.ToUpperInvariant()).
                Select(a => a.First()).
                Select(a => new StorageDisk
                {
                    Root = a.Root.ToUpperInvariant(),
                    Label = a.Label,
                    Total = Math.Max(0, a.Total),
                    Free = Math.Max(0, Math.Min(a.Free, a.Total))
                }).
                ToList();

            foreach (var game in games ?? Enumerable.Empty<StorageGame>())
            {
                var root = GetRoot(game?.Folder);
                var disk = root == null ? null : result.FirstOrDefault(a => a.Root == root);
                if (disk == null)
                {
                    continue;
                }

                disk.Games.Add(game);
                disk.GamesSize += Math.Max(0, game.Size);
            }

            foreach (var disk in result)
            {
                disk.Games = disk.Games.
                    OrderByDescending(a => a.Size).
                    ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).
                    ToList();

                // Sizes saved long ago can add up to more than what's used: the bar never says more than the disk.
                disk.GamesSize = Math.Min(disk.GamesSize, Math.Max(0, disk.Total - disk.Free));
            }

            return result.
                OrderByDescending(a => a.Games.Count > 0).
                ThenByDescending(a => a.Games.Count > 0 ? a.GamesSize : 0).
                ThenBy(a => a.Root).
                ToList();
        }

        /// <summary>The fixed disks of this computer that are ready, with their room.</summary>
        public static List<StorageDiskInfo> ReadDisks()
        {
            var result = new List<StorageDiskInfo>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    {
                        continue;
                    }

                    result.Add(new StorageDiskInfo
                    {
                        Root = drive.RootDirectory.FullName,
                        Label = drive.VolumeLabel,
                        Total = drive.TotalSize,
                        Free = drive.AvailableFreeSpace
                    });
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // A disk that went away or can't be read is simply not listed.
                }
            }

            return result;
        }
    }
}
