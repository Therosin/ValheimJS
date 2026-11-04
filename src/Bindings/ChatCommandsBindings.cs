using System.Collections.Generic;
using System;
using Jint;
using System.Globalization;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using ValheimJS.Commands;
using ValheimJS.Core;
using ValheimJS.Networking;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>Adapts JS command objects/promises to the process-owned native command registry.</summary>
[JsBinder("ChatCommands", Phase = JsBindPhase.Gameplay, Order = 160,
    Description = "Private chat-command registry with account role and permission checks.")]
internal sealed class ChatCommandsBindings : JsBindings
{
    private readonly ChatCommandScope _scope;
    public ChatCommandsBindings(JsBindingContext context) : base(context) => _scope = context.ChatCommands.OpenScope();
    [JsBinding(Scoped = true, Description = "Registers a command definition with async-capable validation and execution callbacks.")]
    public int RegisterCommand(string plugin, string definitionJson, JsCallback validate, JsCallback execute) =>
        _scope.Register(plugin, new ScriptCommand(NetworkJson.Read<ChatCommandDefinition>(definitionJson), validate, execute));
    [JsBinding(Scoped = true, Description = "Unregisters a command and its aliases; cancels in-flight invocations owned by it.")]
    public void UnregisterCommand(string plugin, int id) => _scope.Unregister(plugin, id);
    [JsBinding(Scoped = true, Description = "Attaches synchronous contextual help to a command owned by this plugin.")]
    public void SetHelp(string plugin, int id, JsCallback help) => _scope.SetHelp(plugin, id, query =>
    {
        var result = help.Invoke(query);
        if (!result.IsString()) throw new InvalidOperationException("Command help must return a string synchronously; promises are unsupported.");
        return result.AsString();
    });
    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "ChatCommandDefinition[]", Description = "Lists command definitions.")]
    public string List() => NetworkJson.Write(Context.ChatCommands.List());
    [JsBinding(Scoped = true, Description = "Sends a private reply from an active invocation owned by this plugin.")]
    public void Reply(string plugin, string invocationId, string text) => _scope.Reply(plugin, invocationId, text);
    [JsBinding(Scoped = true)] public bool HasRole(string plugin, string invocationId, string role) => _scope.HasRole(plugin, invocationId, role);
    [JsBinding(Scoped = true)] public bool HasPermission(string plugin, string invocationId, string permission) => _scope.HasPermission(plugin, invocationId, permission);
    public override void Release(string plugin) => _scope.Release(plugin);
    public override void Dispose() => _scope.Dispose();

    private sealed class ScriptCommand : ChatCommand
    {
        private readonly JsCallback _validate;
        private readonly JsCallback _execute;
        internal ScriptCommand(ChatCommandDefinition definition, JsCallback validate, JsCallback execute) : base(definition) { _validate = validate; _execute = execute; }
        public override Task Validate(ChatCommandContext context) => Call(_validate, context);
        public override Task Execute(ChatCommandContext context) => Call(_execute, context);
        private static Task Call(JsCallback callback, ChatCommandContext context)
        {
            var dto = new InvocationDto
            {
                InvocationId = context.InvocationId, Command = context.Input.Route, Text = context.Text,
                Options = new Dictionary<string, string>(context.Options),
                User = new UserDto { PeerId = context.User.PeerId.ToString(CultureInfo.InvariantCulture), Name = context.User.Name, PlatformId = context.User.PlatformId, Account = context.User.Account }
            };
            return callback.InvokeAsync(context.Cancellation, callback.Parse(NetworkJson.Write(dto)));
        }
    }
    [DataContract] private sealed class InvocationDto
    {
        [DataMember(Name = "invocationId")] public string InvocationId = "";
        [DataMember(Name = "command")] public string Command = "";
        [DataMember(Name = "text")] public string Text = "";
        [DataMember(Name = "options")] public Dictionary<string, string> Options = new();
        [DataMember(Name = "user")] public UserDto User = null!;
    }
    [DataContract] private sealed class UserDto
    {
        [DataMember(Name = "peerId")] public string PeerId = "";
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "platformId")] public string? PlatformId;
        [DataMember(Name = "account")] public Services.Account? Account;
    }
}
