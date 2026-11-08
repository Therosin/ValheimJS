using UnityEngine;
using ValheimJS.Debugging;

namespace ValheimJS.World.Navigation;

/// <summary>
/// Visualises the loaded navigation volume with sparse crosses: walkable cells green, stairs
/// yellow, blocked cells red. Uses the shared <see cref="DebugDraw"/> line system.
/// </summary>
internal sealed class NavDebugRenderer : MonoBehaviour
{
    private const float CellsRadius = 24f;
    private static readonly Color WalkColor = new(0.15f, 0.85f, 0.35f, 0.7f);
    private static readonly Color StairColor = new(0.95f, 0.8f, 0.15f, 0.85f);
    private static readonly Color BlockedColor = new(0.9f, 0.2f, 0.15f, 0.8f);

    internal static bool Enabled;

    private void Update()
    {
        if (!Enabled)
        {
            return;
        }

        var player = Player.m_localPlayer;
        var volume = WorldManager.Instance?.NavVolume;
        if (player == null || volume == null)
        {
            return;
        }

        var origin = player.transform.position;
        var zone = ZoneSystem.GetZone(origin);
        volume.TryGetZone(zone.x, zone.y, out _);
        volume.BuildPendingZone();

        foreach (var navZone in volume.Zones)
        {
            var zoneX = navZone.CellWorldX(NavZone.CellsPerSide / 2);
            var zoneZ = navZone.CellWorldZ(NavZone.CellsPerSide / 2);
            if ((zoneX - origin.x) * (zoneX - origin.x) + (zoneZ - origin.z) * (zoneZ - origin.z) > 80f * 80f)
            {
                continue;
            }

            for (var cellZ = 0; cellZ < NavZone.CellsPerSide; cellZ += 2)
            {
                for (var cellX = 0; cellX < NavZone.CellsPerSide; cellX += 2)
                {
                    var cell = navZone.Cell(cellX, cellZ);
                    var x = navZone.CellWorldX(cellX);
                    var z = navZone.CellWorldZ(cellZ);
                    if ((x - origin.x) * (x - origin.x) + (z - origin.z) * (z - origin.z) > CellsRadius * CellsRadius)
                    {
                        continue;
                    }

                    if (cell.Blocked)
                    {
                        var y = origin.y + 0.12f;
                        DrawCross(x, y, z, 0.22f, BlockedColor);
                        continue;
                    }

                    if (cell.Surfaces.Count == 0)
                    {
                        continue;
                    }

                    var surface = cell.Surfaces[0];
                    for (var index = 1; index < cell.Surfaces.Count; index++)
                    {
                        if ((cell.Surfaces[index].Flags & NavFlags.Walk) != 0)
                        {
                            surface = cell.Surfaces[index];
                            break;
                        }
                    }

                    var surfaceY = surface.Y + 0.08f;
                    if ((surface.Flags & NavFlags.Stair) != 0)
                    {
                        DrawCross(x, surfaceY, z, 0.18f, StairColor);
                        WorldDraw.Line(new Vector3(x, surfaceY, z), new Vector3(x, surfaceY + 0.35f, z), StairColor, 0.05f);
                    }
                    else
                    {
                        DrawCross(x, surfaceY, z, 0.1f, WalkColor);
                    }
                }
            }
        }
    }

    private static void DrawCross(float x, float y, float z, float size, Color color)
    {
        WorldDraw.Line(new Vector3(x - size, y, z), new Vector3(x + size, y, z), color, 0.05f);
        WorldDraw.Line(new Vector3(x, y, z - size), new Vector3(x, y, z + size), color, 0.05f);
    }
}
