using System.Runtime.Serialization;

namespace ValheimJS.Services;

[DataContract]
public sealed class Account
{
    [DataMember(Name = "id")] public string Id { get; set; } = "";
    [DataMember(Name = "platformId")] public string PlatformId { get; set; } = "";
    [DataMember(Name = "knownName")] public string KnownName { get; set; } = "";
    [DataMember(Name = "createdAt")] public long CreatedAt { get; set; }
    [DataMember(Name = "lastSeenAt")] public long LastSeenAt { get; set; }
    [DataMember(Name = "profile")] public AccountProfile Profile { get; set; } = new();
}

[DataContract]
public sealed class AccountProfile
{
    [DataMember(Name = "displayName")] public string DisplayName { get; set; } = "";
    [DataMember(Name = "locale")] public string Locale { get; set; } = "";
}

