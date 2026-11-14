using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimJS.Entities;

/// <summary>One persisted inventory item: prefab hash plus the native fields worth keeping.</summary>
internal sealed class NpcItemRecord
{
    internal int PrefabHash;
    internal int Stack;
    internal int Quality;
    internal float Durability;
    internal int X;
    internal int Y;
}

/// <summary>One equipped item, identified by its prefab hash and the slot it was saved in.</summary>
internal sealed class NpcEquipmentRecord
{
    internal string Slot = "";
    internal int PrefabHash;
    internal int X;
    internal int Y;
}

/// <summary>
/// Versioned, forward-compatible encoding of an NPC inventory and its equipment.
/// </summary>
/// <remarks>
/// Format: <c>1;I,hash,stack,quality,durability,x,y;...;E,slot,hash,x,y;...</c>.
/// Unknown record types are ignored; malformed known records are skipped and counted so the
/// caller can report them instead of losing data silently.
/// </remarks>
internal sealed class NpcInventoryPayload
{
    internal const int Version = 1;

    internal List<NpcItemRecord> Items { get; } = new();

    internal List<NpcEquipmentRecord> Equipment { get; } = new();

    /// <summary>Number of records that could not be parsed.</summary>
    internal int Skipped { get; private set; }

    internal string Encode()
    {
        var builder = new StringBuilder();
        builder.Append(Version.ToString(CultureInfo.InvariantCulture));
        foreach (var item in Items)
        {
            builder.Append(";I,")
                .Append(item.PrefabHash.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Stack.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Quality.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Durability.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Y.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var entry in Equipment)
        {
            builder.Append(";E,")
                .Append(entry.Slot).Append(',')
                .Append(entry.PrefabHash.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.Y.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    internal static bool TryDecode(string? text, out NpcInventoryPayload payload, out string error)
    {
        payload = new NpcInventoryPayload();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty payload";
            return false;
        }

        var parts = text.Split(';');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ||
            version != Version)
        {
            error = $"unsupported payload version '{parts[0]}'";
            return false;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            var fields = parts[index].Split(',');
            if (fields.Length == 0)
            {
                continue;
            }

            if (fields[0] == "I")
            {
                if (fields.Length < 7 ||
                    !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash) ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stack) ||
                    !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) ||
                    !float.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var durability) ||
                    !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                    !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
                    hash == 0 || stack <= 0)
                {
                    payload.Skipped++;
                    continue;
                }

                payload.Items.Add(new NpcItemRecord
                {
                    PrefabHash = hash,
                    Stack = stack,
                    Quality = quality,
                    Durability = durability,
                    X = x,
                    Y = y
                });
                continue;
            }

            if (fields[0] == "E")
            {
                if (fields.Length < 5 ||
                    string.IsNullOrWhiteSpace(fields[1]) ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash) ||
                    !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                    !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
                    hash == 0)
                {
                    payload.Skipped++;
                    continue;
                }

                payload.Equipment.Add(new NpcEquipmentRecord
                {
                    Slot = fields[1],
                    PrefabHash = hash,
                    X = x,
                    Y = y
                });
            }
        }

        return true;
    }
}
