using System;
using System.Collections.Generic;
using System.Linq;
using ValheimJS.Utils;

namespace ValheimJS.Core;

/// <summary>
/// Inventory is owned by the client that owns the player; a dedicated server never holds it.
/// Clients therefore publish a snapshot, and server-side reads answer from that mirror.
/// Writes stay commands (see <see cref="PlayerAccess"/>) and the client republishes afterwards.
/// </summary>
internal static class InventorySnapshot
{
    internal const string Channel = "valheimjs:player-state";

    private static readonly Dictionary<long, string> Mirror = new();
    private static bool _subscribed;

    /// <summary>Server-side consumer: stores what each owning client publishes.</summary>
    internal static void Install()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Networking.RpcChannel.Received += Handle;
        Networking.RpcChannel.Listen(Channel);
        Plugin.Logger.LogInfo($"inventory mirror listening on '{Channel}'");
    }

    private static void Handle(string channel, long sender, string payload)
    {
        if (channel != Channel)
        {
            return;
        }

        // The routed sender is the authenticated peer id; the payload prefix is only a fallback
        // (a client's own GetPlayerID() is not guaranteed to equal it).
        var separator = payload.IndexOf('|');
        var json = separator >= 0 ? payload.Substring(separator + 1) : payload;

        var uid = sender;
        if (uid == 0L && separator > 0)
        {
            long.TryParse(payload.Substring(0, separator), out uid);
        }

        if (uid == 0L)
        {
            return;
        }

        Store(uid, json);
        PlayerAccess.MarkReady(uid);
    }

    internal static void Store(long uid, string json)
    {
        if (uid == 0L)
        {
            return;
        }

        Mirror[uid] = json;
    }

    internal static string? Read(long uid) => Mirror.TryGetValue(uid, out var json) ? json : null;

    internal static void Forget(long uid) => Mirror.Remove(uid);

    /// <summary>Runs where the player component exists and serialises its inventory.</summary>
    internal static string Build(Player player)
    {
        var inventory = player.GetInventory();
        if (inventory == null)
        {
            return "[]";
        }

        var items = inventory.GetAllItems() ?? new List<ItemDrop.ItemData>();
        var entries = items.Select(item =>
            "{" +
            $"\"name\":{Json.Quote(item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name)}," +
            $"\"amount\":{item.m_stack}," +
            $"\"quality\":{item.m_quality}," +
            $"\"slot\":{{\"x\":{item.m_gridPos.x},\"y\":{item.m_gridPos.y}}}" +
            "}");

        return "[" + string.Join(",", entries) + "]";
    }

    internal static int Count(string json, string itemName)
    {
        var marker = "\"name\":" + Json.Quote(itemName);
        var total = 0;
        var index = 0;
        while ((index = json.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var amountIndex = json.IndexOf("\"amount\":", index, StringComparison.Ordinal);
            if (amountIndex < 0)
            {
                break;
            }

            var end = json.IndexOfAny(new[] { ',', '}' }, amountIndex + 9);
            var text = end < 0 ? "0" : json.Substring(amountIndex + 9, end - amountIndex - 9);
            if (int.TryParse(text, out var amount))
            {
                total += amount;
            }

            index = end < 0 ? json.Length : end;
        }

        return total;
    }
}
