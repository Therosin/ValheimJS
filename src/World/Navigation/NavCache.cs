using System;
using System.IO;

namespace ValheimJS.World.Navigation;

/// <summary>Terrain-only zone cache. Obstacles are always stamped after loading.</summary>
internal static class NavCache
{
    private const int Magic = 0x56534E31;
    private const int Version = 1;

    internal static string? PathFor(string root, long worldUid, int zx, int zy)
    {
        if (worldUid == 0 || string.IsNullOrEmpty(root))
        {
            return null;
        }

        return Path.Combine(root, worldUid.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{zx}_{zy}.bin");
    }

    internal static bool TryLoad(string root, long worldUid, int zx, int zy, out NavZone zone)
    {
        zone = null!;
        var path = PathFor(root, worldUid, zx, zy);
        if (path == null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            {
                return false;
            }

            var cells = reader.ReadInt32();
            var cellSize = reader.ReadSingle();
            if (cells != NavZone.CellsPerSide || Math.Abs(cellSize - NavZone.CellSize) > 0.001f)
            {
                return false;
            }

            if (reader.ReadInt32() != zx || reader.ReadInt32() != zy)
            {
                return false;
            }

            var loaded = new NavZone(zx, zy);
            for (var index = 0; index < loaded.Cells.Length; index++)
            {
                var count = reader.ReadByte();
                if (count > 4)
                {
                    return false;
                }

                var cell = loaded.Cells[index];
                for (var surface = 0; surface < count; surface++)
                {
                    cell.Surfaces.Add(new NavSurface
                    {
                        Y = reader.ReadSingle(),
                        Slope = reader.ReadSingle(),
                        Flags = (NavFlags)reader.ReadByte()
                    });
                }
            }

            zone = loaded;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void Save(string root, long worldUid, NavZone zone)
    {
        var path = PathFor(root, worldUid, zone.Zx, zone.Zy);
        if (path == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(NavZone.CellsPerSide);
            writer.Write(NavZone.CellSize);
            writer.Write(zone.Zx);
            writer.Write(zone.Zy);
            foreach (var cell in zone.Cells)
            {
                var count = (byte)Math.Min(4, cell.Surfaces.Count);
                writer.Write(count);
                for (var index = 0; index < count; index++)
                {
                    var surface = cell.Surfaces[index];
                    writer.Write(surface.Y);
                    writer.Write(surface.Slope);
                    writer.Write((byte)surface.Flags);
                }
            }
        }
        catch (Exception)
        {
            // A cache failure must never prevent local navigation from working.
        }
    }
}
