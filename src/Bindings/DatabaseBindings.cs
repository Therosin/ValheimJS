using System;
using System.Collections.Generic;
using Jint;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Per-plugin document storage. <b>Scoped</b>: every call is bound to the calling plugin, so a
/// plugin can only reach its own <c>db</c> folder.
/// <para>
/// Durability is C#; document shape belongs to scripts. Predicate and patch loops are executed
/// here through the shared bridge, so scripts never receive a filesystem path.
/// </para>
/// </summary>
[JsBinder("db", Phase = JsBindPhase.Engine, Order = 25,
    Description = "Per-plugin document collections.")]
internal sealed class DatabaseBindings : JsBindings
{
    public DatabaseBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "unknown",
        Description = "The document with this id, or null.")]
    public string? Get(string plugin, string collection, string id) =>
        Storage.DocumentStore.Get(plugin, Require(collection), Require(id));

    [JsBinding(Scoped = true, Description = "True when the document exists.")]
    public bool Has(string plugin, string collection, string id) =>
        Storage.DocumentStore.Has(plugin, Require(collection), Require(id));

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "unknown[]",
        Description = "Every document in the collection.")]
    public string All(string plugin, string collection) =>
        Storage.DocumentStore.All(plugin, Require(collection));

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "string[]",
        Description = "Document ids only; cheaper than all().")]
    public string Keys(string plugin, string collection) =>
        Storage.DocumentStore.Keys(plugin, Require(collection));

    [JsBinding(Scoped = true, Description = "Number of documents in the collection.")]
    public int Count(string plugin, string collection) =>
        Storage.DocumentStore.Count(plugin, Require(collection));

    [JsBinding(Scoped = true, Description = "Inserts or replaces a document; returns true when it was created.")]
    public bool Put(string plugin, string collection, string id, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("db.put needs a JSON document.", nameof(json));
        }

        return Storage.DocumentStore.Put(plugin, Require(collection), Require(id), json);
    }

    [JsBinding(Scoped = true, Description = "Deletes a document; returns true when it existed.")]
    public bool Delete(string plugin, string collection, string id) =>
        Storage.DocumentStore.Delete(plugin, Require(collection), Require(id));

    [JsBinding(Scoped = true, Description = "Deletes every document; returns how many were removed.")]
    public int Clear(string plugin, string collection) =>
        Storage.DocumentStore.Clear(plugin, Require(collection));

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "string[]",
        Description = "Collection names that already have documents on disk.")]
    public string Collections(string plugin) => Storage.DocumentStore.Names(plugin);

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "unknown[]",
        Description = "Documents matching a predicate; the predicate runs in the script, the loop in the host.")]
    public string Find(string plugin, string collection, JsCallback predicate)
    {
        var name = Require(collection);
        var matches = new List<string>();

        foreach (var json in Storage.DocumentStore.Raw(plugin, name))
        {
            var document = predicate.Parse(json);
            if (predicate.Invoke(document).AsBoolean())
            {
                matches.Add(json);
            }
        }

        return "[" + string.Join(",", matches) + "]";
    }

    [JsBinding(Scoped = true,
        Description = "Replaces every document the predicate matches with the patch's return value.")]
    public int UpdateWhere(string plugin, string collection, JsCallback predicate, JsCallback patch)
    {
        var name = Require(collection);
        var written = 0;

        foreach (var json in Storage.DocumentStore.Raw(plugin, name))
        {
            var document = predicate.Parse(json);
            if (!predicate.Invoke(document).AsBoolean())
            {
                continue;
            }

            var replacement = patch.Invoke(document);
            if (replacement.IsNull() || replacement.IsUndefined())
            {
                continue;
            }

            // Write back under the original id: a patch may merge fields but must not move a
            // document or leave a duplicate behind.
            var id = patch.GetPropertyId(document) ?? patch.GetPropertyId(replacement);
            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException("db.updateWhere: the document has no usable id.");
            }

            Storage.DocumentStore.Put(plugin, name, id!, patch.Stringify(replacement));
            written++;
        }

        return written;
    }

    private static string Require(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("collection and id must not be empty.", nameof(value));
        }

        return value.Trim();
    }
}
