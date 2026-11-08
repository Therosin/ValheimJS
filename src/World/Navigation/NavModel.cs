using System;
using System.Collections.Generic;

namespace ValheimJS.World.Navigation;

[Flags]
internal enum NavFlags : byte
{
    None = 0,
    Walk = 1,
    Stair = 2,
    Indoor = 4
}

internal sealed class NavSurface
{
    public float Y;
    public float Slope;
    public NavFlags Flags;
}

internal sealed class NavCell
{
    public readonly List<NavSurface> Surfaces = new(2);
    public bool Blocked;
}

/// <summary>
/// One 64x64 m zone of the navigation volume. Cell (0,0) is the south-west cell; its centre is
/// at world (Zx*64 - 31.5, Zy*64 - 31.5), matching <c>ZoneSystem.GetZone</c>.
/// </summary>
internal sealed class NavZone
{
    public const int CellsPerSide = 64;
    public const float CellSize = 1f;

    public readonly int Zx;
    public readonly int Zy;
    public readonly NavCell[] Cells = new NavCell[CellsPerSide * CellsPerSide];

    public NavZone(int zx, int zy)
    {
        Zx = zx;
        Zy = zy;
        for (var index = 0; index < Cells.Length; index++)
        {
            Cells[index] = new NavCell();
        }
    }

    public static float WorldX(int globalCellX) => globalCellX - 31.5f;

    public static float WorldZ(int globalCellZ) => globalCellZ - 31.5f;

    public float CellWorldX(int cellX) => WorldX(Zx * CellsPerSide + cellX);

    public float CellWorldZ(int cellZ) => WorldZ(Zy * CellsPerSide + cellZ);

    public int GlobalX(int cellX) => Zx * CellsPerSide + cellX;

    public int GlobalZ(int cellZ) => Zy * CellsPerSide + cellZ;

    public NavCell Cell(int cellX, int cellZ) => Cells[cellZ * CellsPerSide + cellX];

    public bool TryGetCell(float worldX, float worldZ, out NavCell cell, out int cellX, out int cellZ)
    {
        var globalX = (int)Math.Floor(worldX + 32f);
        var globalZ = (int)Math.Floor(worldZ + 32f);
        return TryGetGlobalCell(globalX, globalZ, out cell, out cellX, out cellZ);
    }

    public bool TryGetGlobalCell(int globalX, int globalZ, out NavCell cell, out int cellX, out int cellZ)
    {
        cellX = globalX - Zx * CellsPerSide;
        cellZ = globalZ - Zy * CellsPerSide;
        if (cellX < 0 || cellX >= CellsPerSide || cellZ < 0 || cellZ >= CellsPerSide)
        {
            cell = null!;
            return false;
        }

        cell = Cells[cellZ * CellsPerSide + cellX];
        return true;
    }

    public void AddSurface(int cellX, int cellZ, float y, NavFlags flags, float slope = 0f)
    {
        if (cellX < 0 || cellX >= CellsPerSide || cellZ < 0 || cellZ >= CellsPerSide)
        {
            return;
        }

        var cell = Cells[cellZ * CellsPerSide + cellX];
        foreach (var surface in cell.Surfaces)
        {
            if (Math.Abs(surface.Y - y) <= 0.25f)
            {
                surface.Flags |= flags;
                return;
            }
        }

        if (cell.Surfaces.Count >= 4)
        {
            return;
        }

        cell.Surfaces.Add(new NavSurface { Y = y, Slope = slope, Flags = flags });
        if ((flags & (NavFlags.Stair | NavFlags.Indoor)) != 0)
        {
            cell.Blocked = false;
        }
    }

    public void AddSurfaceAtWorld(float worldX, float worldZ, float y, NavFlags flags, float slope = 0f)
    {
        if (TryGetCell(worldX, worldZ, out _, out var cellX, out var cellZ))
        {
            AddSurface(cellX, cellZ, y, flags, slope);
        }
    }

    public void BlockAtWorld(float worldX, float worldZ, float radius)
    {
        var minX = (int)Math.Floor(worldX - radius + 32f);
        var maxX = (int)Math.Floor(worldX + radius + 32f);
        var minZ = (int)Math.Floor(worldZ - radius + 32f);
        var maxZ = (int)Math.Floor(worldZ + radius + 32f);
        for (var globalZ = minZ; globalZ <= maxZ; globalZ++)
        {
            for (var globalX = minX; globalX <= maxX; globalX++)
            {
                if (!TryGetGlobalCell(globalX, globalZ, out var cell, out _, out _))
                {
                    continue;
                }

                var centreX = WorldX(globalX);
                var centreZ = WorldZ(globalZ);
                var dx = centreX - worldX;
                var dz = centreZ - worldZ;
                if (dx * dx + dz * dz <= radius * radius)
                {
                    cell.Blocked = true;
                    cell.Surfaces.Clear();
                }
            }
        }
    }

    public void ClearBlockedAtWorld(float worldX, float worldZ, float radius)
    {
        var minX = (int)Math.Floor(worldX - radius + 32f);
        var maxX = (int)Math.Floor(worldX + radius + 32f);
        var minZ = (int)Math.Floor(worldZ - radius + 32f);
        var maxZ = (int)Math.Floor(worldZ + radius + 32f);
        for (var globalZ = minZ; globalZ <= maxZ; globalZ++)
        {
            for (var globalX = minX; globalX <= maxX; globalX++)
            {
                if (!TryGetGlobalCell(globalX, globalZ, out var cell, out _, out _))
                {
                    continue;
                }

                var dx = WorldX(globalX) - worldX;
                var dz = WorldZ(globalZ) - worldZ;
                if (dx * dx + dz * dz <= radius * radius)
                {
                    cell.Blocked = false;
                }
            }
        }
    }
}

internal interface ITerrainSource
{
    bool TryHeight(float x, float z, out float y);
}

internal interface IObstacleSource
{
    void Stamp(NavZone zone);
}
