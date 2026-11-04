using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ValheimJS.Services;

namespace ValheimJS.Commands;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute(string name) => Name = name;
    public string Name { get; }
    public string? Namespace { get; set; }
    public string Description { get; set; } = "";
    public string? Help { get; set; }
    public string[] Aliases { get; set; } = Array.Empty<string>();
    public string[] Roles { get; set; } = Array.Empty<string>();
    public string[] Permissions { get; set; } = Array.Empty<string>();
}

[DataContract]
public sealed class ChatCommandDefinition
{
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "namespace", EmitDefaultValue = false)] public string? Namespace { get; set; }
    [DataMember(Name = "description")] public string Description { get; set; } = "";
    [DataMember(Name = "help", EmitDefaultValue = false)] public string? Help { get; set; }
    [DataMember(Name = "aliases")] public string[] Aliases { get; set; } = Array.Empty<string>();
    /// <summary>Any listed role is sufficient; an empty array imposes no role requirement.</summary>
    [DataMember(Name = "roles")] public string[] Roles { get; set; } = Array.Empty<string>();
    [DataMember(Name = "permissions")] public string[] Permissions { get; set; } = Array.Empty<string>();
}

public abstract class ChatCommand
{
    protected ChatCommand(ChatCommandDefinition? definition = null)
    {
        if (definition != null) { Definition = definition; return; }
        var attribute = (CommandAttribute?)Attribute.GetCustomAttribute(GetType(), typeof(CommandAttribute));
        if (attribute == null) throw new InvalidOperationException("Supply a command definition or annotate the class with [Command].");
        Definition = new ChatCommandDefinition { Name = attribute.Name, Namespace = attribute.Namespace, Description = attribute.Description, Help = attribute.Help, Aliases = attribute.Aliases, Roles = attribute.Roles, Permissions = attribute.Permissions };
    }
    public ChatCommandDefinition Definition { get; }
    public virtual bool CanAccess(CommandUser user) => true;
    public virtual string? GetHelp(string query) => Definition.Help;
    public virtual Task Validate(ChatCommandContext context) => Task.CompletedTask;
    public abstract Task Execute(ChatCommandContext context);
}

public sealed class CommandUser
{
    private readonly Func<string, Task> _send;
    private readonly AccountService? _accounts;
    private readonly PermissionService? _permissions;
    public CommandUser(long peerId, string name, string? platformId, Action<string> send, AccountService? accounts = null, string? accountId = null, PermissionService? permissions = null)
        : this(peerId, name, platformId, text => { send(text); return Task.CompletedTask; }, accounts, accountId, permissions) { }
    internal CommandUser(long peerId, string name, string? platformId, Func<string, Task> send, AccountService? accounts = null, string? accountId = null, PermissionService? permissions = null)
    { PeerId = peerId; Name = name; PlatformId = platformId; _send = send; _accounts = accounts; AccountId = accountId; _permissions = permissions; }
    public long PeerId { get; }
    public string Name { get; }
    public string? PlatformId { get; }
    public string? AccountId { get; }
    public Account? Account => AccountId == null ? null : _accounts?.Get(AccountId);
    public bool HasRole(string role) => AccountId != null && _permissions != null && _permissions.HasRole(AccountId, role);
    public bool HasPermission(string permission) => AccountId != null && _permissions != null && _permissions.HasPermission(AccountId, permission);
    internal CommandUser WithReply(Func<string, Task> send) => new(PeerId, Name, PlatformId, send, _accounts, AccountId, _permissions);
    public Task SendMessage(string text) => _send(text);
}

public sealed class ChatCommandContext
{
    internal ChatCommandContext(string id, CommandUser user, ParsedChatCommand input, CancellationToken cancellation)
    { InvocationId = id; User = user; Input = input; Cancellation = cancellation; }
    public string InvocationId { get; }
    public CommandUser User { get; }
    public ParsedChatCommand Input { get; }
    public IReadOnlyDictionary<string, string> Options => Input.Options;
    public string Text => Input.Text;
    public CancellationToken Cancellation { get; }
}
