using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using ValheimJS.Networking;

namespace ValheimJS.Core;

[DataContract]
public sealed class SyncEntity
{
    [DataMember(Name = "id")] public int Id { get; set; }
    [DataMember(Name = "x", EmitDefaultValue = false)] public float? X { get; set; }
    [DataMember(Name = "y", EmitDefaultValue = false)] public float? Y { get; set; }
    [DataMember(Name = "z", EmitDefaultValue = false)] public float? Z { get; set; }
    [DataMember(Name = "rotY", EmitDefaultValue = false)] public float? RotY { get; set; }
    [DataMember(Name = "flags", EmitDefaultValue = false)] public int? Flags { get; set; }
}

/// <summary>
/// Explicit binary sync for transient non-ZDO state. No JSON/base64 on the wire, no automatic
/// broadcasts, replication cadence, persistence or late-join snapshots. Prefer ZDOs for world state.
/// </summary>
public sealed class NetworkSync : IDisposable
{
    public const byte FrameVersion = 1;
    public const int MaxFrameBytes = 256 * 1024;
    private readonly IRpcTransport _transport;
    private readonly HashSet<SyncScope> _scopes = new();
    private bool _disposed;
    public NetworkSync(IRpcTransport transport) => _transport = transport;
    public SyncScope OpenScope()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetworkSync));
        var scope = new SyncScope(_transport, value => _scopes.Remove(value));
        _scopes.Add(scope);
        return scope;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var scope in new List<SyncScope>(_scopes)) scope.Dispose();
    }

    public static byte[] Encode(IReadOnlyList<SyncEntity> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));
        if (entities.Count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(entities));
        var size = 3;
        var ids = new HashSet<int>();
        foreach (var entity in entities)
        {
            Validate(entity);
            if (!ids.Add(entity.Id)) throw new ArgumentException("Duplicate sync entity id.");
            size += 3 + (entity.X.HasValue ? 4 : 0) + (entity.Y.HasValue ? 4 : 0) + (entity.Z.HasValue ? 4 : 0) + (entity.RotY.HasValue ? 4 : 0) + (entity.Flags.HasValue ? 1 : 0);
        }
        if (size > MaxFrameBytes) throw new ArgumentException("Sync frame exceeds 256 KiB.");
        var frame = new byte[size];
        frame[0] = FrameVersion;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)entities.Count);
        var offset = 3;
        foreach (var entity in entities)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset), (ushort)entity.Id); offset += 2;
            frame[offset++] = (byte)((entity.X.HasValue ? 1 : 0) | (entity.Y.HasValue ? 2 : 0) | (entity.Z.HasValue ? 4 : 0) | (entity.RotY.HasValue ? 8 : 0) | (entity.Flags.HasValue ? 16 : 0));
            Write(entity.X); Write(entity.Y); Write(entity.Z); Write(entity.RotY);
            if (entity.Flags.HasValue) frame[offset++] = (byte)entity.Flags.Value;
        }
        return frame;
        void Write(float? value)
        {
            if (!value.HasValue) return;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(offset), BitConverter.SingleToInt32Bits(value.Value)); offset += 4;
        }
    }

    public static SyncEntity[] Decode(byte[] frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (frame.Length < 3 || frame.Length > MaxFrameBytes || frame[0] != FrameVersion) throw new InvalidDataException("Unsupported or oversized sync frame.");
        var count = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1));
        if (count * 3 > frame.Length - 3) throw new InvalidDataException("Truncated sync frame.");
        var result = new SyncEntity[count];
        var ids = new HashSet<int>();
        var offset = 3;
        for (var index = 0; index < count; index++)
        {
            Need(3);
            var entity = new SyncEntity { Id = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset)) }; offset += 2;
            if (!ids.Add(entity.Id)) throw new InvalidDataException("Duplicate sync entity id.");
            var mask = frame[offset++];
            if ((mask & ~31) != 0) throw new InvalidDataException("Invalid sync field mask.");
            if ((mask & 1) != 0) entity.X = Read();
            if ((mask & 2) != 0) entity.Y = Read();
            if ((mask & 4) != 0) entity.Z = Read();
            if ((mask & 8) != 0) entity.RotY = Read();
            if ((mask & 16) != 0) { Need(1); entity.Flags = frame[offset++]; }
            result[index] = entity;
        }
        if (offset != frame.Length) throw new InvalidDataException("Trailing sync frame data.");
        return result;
        void Need(int bytes) { if (offset + bytes > frame.Length) throw new InvalidDataException("Truncated sync frame."); }
        float Read()
        {
            Need(4);
            var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(offset))); offset += 4;
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Non-finite sync value.");
            return value;
        }
    }

    public static float Quantise(float value, float step = 0.01f)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || float.IsNaN(step) || float.IsInfinity(step) || step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        return (float)(Math.Floor((double)value / step + 0.5) * step);
    }
    public static SyncEntity? Diff(SyncEntity previous, SyncEntity next, float step = 0.01f)
    {
        Validate(previous); Validate(next); Quantise(0, step);
        if (previous.Id != next.Id) throw new ArgumentException("Sync delta requires the same entity id.");
        var delta = new SyncEntity { Id = next.Id, X = Field(previous.X, next.X), Y = Field(previous.Y, next.Y), Z = Field(previous.Z, next.Z), RotY = Field(previous.RotY, next.RotY), Flags = next.Flags != previous.Flags ? next.Flags : null };
        return delta.X.HasValue || delta.Y.HasValue || delta.Z.HasValue || delta.RotY.HasValue || delta.Flags.HasValue ? delta : null;
        float? Field(float? before, float? after) => after.HasValue && (!before.HasValue || Math.Abs(after.Value - before.Value) >= step) ? Quantise(after.Value, step) : null;
    }
    private static void Validate(SyncEntity entity)
    {
        if (entity == null || entity.Id < 0 || entity.Id > ushort.MaxValue || entity.Flags < 0 || entity.Flags > byte.MaxValue) throw new ArgumentException("Sync id/flags outside wire range.");
        Finite(entity.X); Finite(entity.Y); Finite(entity.Z); Finite(entity.RotY);
        static void Finite(float? value)
        { if (value.HasValue && (float.IsNaN(value.Value) || float.IsInfinity(value.Value))) throw new ArgumentException("Non-finite sync value."); }
    }
}

/// <summary>Consumer-owned binary stream listeners, sharing the common RPC transport.</summary>
public sealed class SyncScope : IDisposable
{
    private const string Channel = "ValheimJS:Sync";
    private readonly IRpcTransport _transport;
    private readonly Action<SyncScope> _onDispose;
    private readonly Dictionary<int, (string Owner, IDisposable Registration)> _subscriptions = new();
    private int _nextId;
    private bool _disposed;
    internal SyncScope(IRpcTransport transport, Action<SyncScope> onDispose) { _transport = transport; _onDispose = onDispose; }
    public int On(string owner, string stream, Action<NetworkMessage, SyncEntity[]> handler)
    {
        EnsureAlive();
        var id = ++_nextId;
        var registration = _transport.Subscribe(Channel, stream, false, (message, bytes) =>
        {
            if (!_disposed && _subscriptions.ContainsKey(id)) handler(message, NetworkSync.Decode(bytes));
            return Array.Empty<byte>();
        });
        _subscriptions.Add(id, (owner, registration));
        return id;
    }
    public void Off(string owner, int id)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription) || subscription.Owner != owner) return;
        _subscriptions.Remove(id); subscription.Registration.Dispose();
    }
    public void Send(long peer, string stream, IReadOnlyList<SyncEntity> entities)
    { EnsureAlive(); _transport.Notify(peer, Channel, stream, NetworkSync.Encode(entities)); }
    public void Release(string owner) { foreach (var id in new List<int>(_subscriptions.Keys)) Off(owner, id); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions.Values) subscription.Registration.Dispose();
        _subscriptions.Clear(); _onDispose(this);
    }
    private void EnsureAlive() { if (_disposed) throw new ObjectDisposedException(nameof(SyncScope)); }
}
