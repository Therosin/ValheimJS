using System;
using System.Runtime.Serialization;

namespace ValheimJS.World;

[DataContract]
internal sealed class WorldEntityInfo
{
    [DataMember(Name = "id")] public string Id = "";
    [DataMember(Name = "kind")] public string Kind = "";
    [DataMember(Name = "plugin")] public string Plugin = "";
    [DataMember(Name = "name")] public string Name = "";
}

/// <summary>Per-peer relevance diff; ids are handles (<c>ZDOID.ToString()</c>).</summary>
[DataContract]
internal sealed class WorldRelevanceMessage
{
    [DataMember(Name = "add")] public WorldEntityInfo[] Add = Array.Empty<WorldEntityInfo>();
    [DataMember(Name = "remove")] public string[] Remove = Array.Empty<string>();
}

/// <summary>Optional spawn/despawn metadata event for subscribed peers.</summary>
[DataContract]
internal sealed class WorldEntityMessage
{
    [DataMember(Name = "op")] public string Op = "";
    [DataMember(Name = "entity")] public WorldEntityInfo Entity = new();
}
