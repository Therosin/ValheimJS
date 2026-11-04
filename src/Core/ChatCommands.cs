using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValheimJS.Commands;

namespace ValheimJS.Core;

/// <summary>Native command registry, account access checks and async lifecycle.</summary>
public sealed class ChatCommands : IDisposable
{
    private sealed class Registration
    {
        internal string Owner = "";
        internal ChatCommand Command = null!;
        internal string[] Routes = null!;
        internal Func<string, string?>? Help;
    }
    private sealed class Invocation
    {
        internal Registration Registration = null!;
        internal CommandUser User = null!;
        internal CancellationTokenSource Cancellation = new(TimeSpan.FromSeconds(30));
    }
    private readonly Dictionary<string, Registration> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Invocation> _active = new();
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private bool _disposed;
    public ChatCommands(Action<Action> post, Action<string>? log = null) { _post = post; _log = log ?? (_ => { }); }
    public ChatCommandScope OpenScope() => !_disposed ? new ChatCommandScope(this) : throw new ObjectDisposedException(nameof(ChatCommands));
    internal IDisposable Register(string owner, ChatCommand command)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChatCommands));
        var definition = command.Definition;
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Command name is required.");
        if (definition.Name.Contains(':')) throw new ArgumentException("Use the namespace field for a command category.");
        var primary = ChatCommandParser.Route(string.IsNullOrEmpty(definition.Namespace) ? definition.Name : definition.Namespace + ":" + definition.Name);
        var routes = new[] { primary }.Concat(definition.Aliases ?? Array.Empty<string>()).Select(ChatCommandParser.Route).ToArray();
        if (routes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Length) throw new ArgumentException("Duplicate command/alias in definition.");
        foreach (var route in routes) if (_routes.ContainsKey(route)) throw new InvalidOperationException($"Command route '!{route}' is already registered.");
        var registration = new Registration { Owner = owner, Command = command, Routes = routes };
        foreach (var route in routes) _routes.Add(route, registration);
        return new Cleanup(() =>
        {
            foreach (var route in routes) _routes.Remove(route);
            foreach (var id in _active.Where(item => item.Value.Registration == registration).Select(item => item.Key).ToArray()) Finish(id);
        });
    }
    public ChatCommandDefinition[] List() => _routes.Values.Distinct().Select(item => item.Command.Definition).ToArray();
    private static bool CanAccess(Registration registration, CommandUser user)
    {
        var definition = registration.Command.Definition;
        return registration.Command.CanAccess(user)
            && (definition.Roles == null || definition.Roles.Length == 0 || definition.Roles.Any(user.HasRole))
            && !(definition.Permissions?.Any(permission => !user.HasPermission(permission)) ?? false);
    }
    public ChatCommandDefinition[] AccessibleCommands(CommandUser user) => _routes.Values.Distinct()
        .Where(item => CanAccess(item, user)).Select(item => item.Command.Definition).ToArray();
    public string Help(CommandUser user, string route, string query)
    {
        var key = ChatCommandParser.Route(route.StartsWith("!") ? route.Substring(1) : route);
        if (!_routes.TryGetValue(key, out var registration)) throw new ArgumentException($"Unknown command '!{key}'.");
        if (!CanAccess(registration, user)) throw new UnauthorizedAccessException("You do not have permission to view this command's help.");
        var help = registration.Help != null ? registration.Help(query) : registration.Command.GetHelp(query);
        return !string.IsNullOrEmpty(help) ? help! : !string.IsNullOrEmpty(registration.Command.Definition.Description)
            ? registration.Command.Definition.Description : $"No help is available for '!{key}'.";
    }
    internal void SetHelp(string owner, ChatCommand command, Func<string, string?> help)
    {
        var registration = _routes.Values.FirstOrDefault(item => item.Owner == owner && ReferenceEquals(item.Command, command))
            ?? throw new InvalidOperationException("Command is no longer registered or belongs to another owner.");
        registration.Help = help;
    }
    /// <summary>Returns true for every ! input, including unknown or malformed commands.</summary>
    public bool Submit(CommandUser caller, string text)
    {
        if (!ChatCommandParser.IsCommand(text)) return false;
        if (_disposed) return true;
        ParsedChatCommand input;
        try { input = ChatCommandParser.Parse(text); }
        catch (ArgumentException error) { caller.SendMessage(error.Message); return true; }
        if (!_routes.TryGetValue(input.Route, out var registration)) { caller.SendMessage($"Unknown command '!{input.Route}'."); return true; }
        if (_active.Count >= 64 || _active.Values.Count(item => item.User.PeerId == caller.PeerId) >= 4)
        { caller.SendMessage("Too many commands are already running."); return true; }
        var id = Guid.NewGuid().ToString("N");
        var invocation = new Invocation { Registration = registration, User = caller };
        _active.Add(id, invocation);
        var user = caller.WithReply(message =>
        {
            var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellation = invocation.Cancellation.Token.Register(() => sent.TrySetCanceled());
            _post(() =>
            {
                try { Reply(registration.Owner, id, message); sent.TrySetResult(true); }
                catch (Exception error) { sent.TrySetException(error); }
                finally { cancellation.Dispose(); }
            });
            return sent.Task;
        });
        var context = new ChatCommandContext(id, user, input, invocation.Cancellation.Token);
        invocation.Cancellation.Token.Register(() => _post(() =>
        {
            if (!_active.ContainsKey(id)) return;
            try { caller.SendMessage("Command cancelled or timed out."); }
            finally { Finish(id); }
        }));
        Run(id, () => registration.Command.Validate(context), true, () => Run(id, () => registration.Command.Execute(context), false, () => Finish(id)));
        return true;
    }
    private void Run(string id, Func<Task> action, bool validation, Action next)
    {
        if (!_active.TryGetValue(id, out var invocation) || invocation.Cancellation.IsCancellationRequested) return;
        Task task;
        try
        {
            if (!CanAccess(invocation.Registration, invocation.User))
            {
                Failed(id, new UnauthorizedAccessException("You do not have permission to execute this command."), true);
                return;
            }
            task = action() ?? throw new InvalidOperationException("Command returned no task.");
        }
        catch (Exception error) { Failed(id, error, validation); return; }
        task.ContinueWith(completed => _post(() =>
        {
            if (!_active.ContainsKey(id)) return;
            if (completed.IsFaulted) Failed(id, completed.Exception!.GetBaseException(), validation);
            else if (completed.IsCanceled) Finish(id);
            else next();
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void Failed(string id, Exception error, bool validation)
    {
        if (!_active.TryGetValue(id, out var invocation)) return;
        _log("Command failed: " + error);
        try { invocation.User.SendMessage(validation || error is UnauthorizedAccessException ? error.Message : "Command execution failed."); }
        finally { Finish(id); }
    }
    internal void Reply(string owner, string id, string text)
    {
        if (!_active.TryGetValue(id, out var invocation) || invocation.Registration.Owner != owner || invocation.Cancellation.IsCancellationRequested)
            throw new InvalidOperationException("Command invocation is no longer active or belongs to another owner.");
        invocation.User.SendMessage(text);
    }
    internal CommandUser User(string owner, string id)
    {
        if (!_active.TryGetValue(id, out var invocation) || invocation.Registration.Owner != owner || invocation.Cancellation.IsCancellationRequested)
            throw new InvalidOperationException("Command invocation is no longer active or belongs to another owner.");
        return invocation.User;
    }
    private void Finish(string id)
    {
        if (!_active.TryGetValue(id, out var invocation)) return;
        _active.Remove(id); invocation.Cancellation.Cancel(); invocation.Cancellation.Dispose();
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var id in _active.Keys.ToArray()) Finish(id);
        _routes.Clear();
    }
    private sealed class Cleanup : IDisposable
    {
        private Action? _action;
        internal Cleanup(Action action) => _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>Registrations owned by one consumer; the JS adapter releases by plugin on unload.</summary>
public sealed class ChatCommandScope : IDisposable
{
    private readonly ChatCommands _registry;
    private readonly string _prefix = Guid.NewGuid().ToString("N") + ":";
    private readonly Dictionary<int, (string Owner, IDisposable Registration, ChatCommand Command)> _registrations = new();
    private int _next;
    private bool _disposed;
    internal ChatCommandScope(ChatCommands registry) => _registry = registry;
    public int Register(string owner, ChatCommand command)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChatCommandScope));
        var registration = _registry.Register(_prefix + owner, command);
        var id = ++_next; _registrations.Add(id, (owner, registration, command)); return id;
    }
    public void Unregister(string owner, int id)
    {
        if (!_registrations.TryGetValue(id, out var registration) || registration.Owner != owner) return;
        _registrations.Remove(id); registration.Registration.Dispose();
    }
    public void Reply(string owner, string invocationId, string text) => _registry.Reply(_prefix + owner, invocationId, text);
    public void SetHelp(string owner, int id, Func<string, string?> help)
    {
        if (!_registrations.TryGetValue(id, out var registration) || registration.Owner != owner)
            throw new InvalidOperationException("Command is no longer registered or belongs to another owner.");
        _registry.SetHelp(_prefix + owner, registration.Command, help);
    }
    public bool HasRole(string owner, string invocationId, string role) => _registry.User(_prefix + owner, invocationId).HasRole(role);
    public bool HasPermission(string owner, string invocationId, string permission) => _registry.User(_prefix + owner, invocationId).HasPermission(permission);
    public void Release(string owner) { foreach (var id in _registrations.Keys.ToArray()) Unregister(owner, id); }
    public void Dispose() { if (_disposed) return; _disposed = true; foreach (var item in _registrations.Values) item.Registration.Dispose(); _registrations.Clear(); }
}
