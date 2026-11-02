using System;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Per-plugin document storage. <b>Scoped</b>: each call is bound to the calling plugin, so a
/// plugin can only ever reach its own <c>db</c> folder.
/// <para>
/// The C# layer is deliberately dumb — ids in, JSON text out — because the document shape
/// belongs to scripts. The ergonomic object API lives in the SDK (<c>ctx.db</c>).
/// </para>
/// </summary>
[JsBinding("Database", JsBindPhase.Engine, order: 25)]
internal static class DatabaseBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("db.get", new Func<string, string, string, string?>(Get), JsReturnKind.Json, scoped: true);
        context.Add("db.has", new Func<string, string, string, bool>(Has), JsReturnKind.Raw, scoped: true);
        context.Add("db.all", new Func<string, string, string>(All), JsReturnKind.Json, scoped: true);
        context.Add("db.keys", new Func<string, string, string>(Keys), JsReturnKind.Json, scoped: true);
        context.Add("db.count", new Func<string, string, int>(Count), JsReturnKind.Raw, scoped: true);
        context.Add("db.put", new Func<string, string, string, string, bool>(Put), JsReturnKind.Raw, scoped: true);
        context.Add("db.delete", new Func<string, string, string, bool>(Delete), JsReturnKind.Raw, scoped: true);
        context.Add("db.clear", new Func<string, string, int>(Clear), JsReturnKind.Raw, scoped: true);
        context.Add("db.collections", new Func<string, string>(Collections), JsReturnKind.Json, scoped: true);
    }

    private static string? Get(string plugin, string collection, string id) =>
        Storage.DocumentStore.Get(plugin, Require(collection), id);

    private static bool Has(string plugin, string collection, string id) =>
        Storage.DocumentStore.Has(plugin, Require(collection), id);

    private static string All(string plugin, string collection) =>
        Storage.DocumentStore.All(plugin, Require(collection));

    private static string Keys(string plugin, string collection) =>
        Storage.DocumentStore.Keys(plugin, Require(collection));

    private static int Count(string plugin, string collection) =>
        Storage.DocumentStore.Count(plugin, Require(collection));

    /// <summary>Returns true when the document was created (false on update).</summary>
    private static bool Put(string plugin, string collection, string id, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("db.put needs a JSON document", nameof(json));
        }

        return Storage.DocumentStore.Put(plugin, Require(collection), Require(id), json);
    }

    private static bool Delete(string plugin, string collection, string id) =>
        Storage.DocumentStore.Delete(plugin, Require(collection), id);

    private static int Clear(string plugin, string collection) =>
        Storage.DocumentStore.Clear(plugin, Require(collection));

    private static string Collections(string plugin) => Storage.DocumentStore.Names(plugin);

    private static string Require(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("collection and id must not be empty", nameof(value));
        }

        return value.Trim();
    }
}
