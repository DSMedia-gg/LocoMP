using System;
using System.Collections.Generic;
using LocoMP.Core.Items;
using LocoMP.Core.Net;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>A client's read-only view of one item. A possessed item's holder is a session peer id +
/// name (peer 0 = a world item, or a holder currently offline in their grace window); a world item
/// carries a pose. The stable scope key never reaches the client (07 §M3).</summary>
public sealed class ClientItem
{
    internal ClientItem(ItemDef def) => Def = def;

    public ItemDef Def { get; internal set; }
    public ItemLocationKind Location { get; internal set; }
    public Pose WorldPose { get; internal set; }
    public int OwnerPeerId { get; internal set; }
    public string OwnerName { get; internal set; } = string.Empty;
}

/// <summary>
/// The client's item subsystem, owned by <see cref="NetClient"/>: a mirror of every item's identity/
/// state/location on the receive side, and the propose calls (pickup/drop/purchase, plus the
/// world-source register/despawn) the Shim's item hooks drive on the send side. Everything here is
/// proposals and mirrors — an item only ever changes when the server says so (03 §3).
/// </summary>
public sealed class ClientItems
{
    private readonly ITransport _transport;
    private readonly Func<bool> _joined;
    private readonly Dictionary<int, ClientItem> _items = new();

    internal ClientItems(ITransport transport, Func<bool> joined)
    {
        _transport = transport;
        _joined = joined;
    }

    /// <summary>The mirrored item set, keyed by item id.</summary>
    public IReadOnlyDictionary<int, ClientItem> Items => _items;

    public event Action<ClientItem>? ItemAdded;
    public event Action<ClientItem>? ItemMoved;
    public event Action<int>? ItemRemoved;

    /// <summary>The registrant's echo of a world-item registration (token, item): the Shim maps its
    /// local GameObject onto the server id. Fires only for a non-zero correlation token.</summary>
    public event Action<uint, ClientItem>? RegisterAccepted;

    /// <summary>The server refused one of our proposals: (reason, itemId — 0 for a purchase).</summary>
    public event Action<string, int>? RequestRejected;

    // ── send side (silent no-op until joined, like the other subsystems) ──

    public void RequestPickup(int itemId) => SendIdOnly(MessageType.ItemPickupRequest, itemId);

    public void RequestDrop(int itemId, Pose pose)
    {
        if (!_joined()) return;
        var w = new PacketWriter(40)
            .WriteByte((byte)MessageType.ItemDropRequest)
            .WriteVarUInt((uint)itemId);
        PresenceCodec.WritePose(w, pose);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    public void Purchase(string prefabName)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.ItemPurchaseRequest)
            .WriteString(prefabName)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>World source only: register a world item the host owns. The <paramref name="token"/>
    /// comes back on the <see cref="RegisterAccepted"/> echo so the Shim maps its GameObject.</summary>
    public void RegisterWorldItem(string prefabName, Pose pose, string state, uint token)
    {
        if (!_joined()) return;
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.ItemRegister)
            .WriteVarUInt(token);
        ItemCodec.WriteItemDef(w, new ItemDef(0, prefabName, state)); // id ignored — server assigns
        PresenceCodec.WritePose(w, pose);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>World source only: a world item is gone in the native world.</summary>
    public void DespawnItem(int itemId) => SendIdOnly(MessageType.ItemDespawnRequest, itemId);

    // ── receive side ──

    internal bool TryHandle(MessageType type, PacketReader r)
    {
        switch (type)
        {
            case MessageType.ItemSpawned:
            {
                uint token = r.ReadVarUInt();
                ItemDef def = ItemCodec.ReadItemDef(r);
                var item = new ClientItem(def);
                ReadLocation(r, item);
                _items[def.Id] = item;
                ItemAdded?.Invoke(item);
                if (token != 0) RegisterAccepted?.Invoke(token, item);
                return true;
            }
            case MessageType.ItemMoved:
            {
                int itemId = (int)r.ReadVarUInt();
                if (!_items.TryGetValue(itemId, out ClientItem? item))
                {
                    // Read past the location so a stale id doesn't desync the stream, then drop it.
                    var scratch = new ClientItem(new ItemDef(itemId, string.Empty, string.Empty));
                    ReadLocation(r, scratch);
                    return true;
                }
                ReadLocation(r, item);
                ItemMoved?.Invoke(item);
                return true;
            }
            case MessageType.ItemDespawned:
            {
                int itemId = (int)r.ReadVarUInt();
                if (_items.Remove(itemId)) ItemRemoved?.Invoke(itemId);
                return true;
            }
            case MessageType.ItemRejected:
            {
                string reason = r.ReadString();
                int itemId = (int)r.ReadVarUInt();
                RequestRejected?.Invoke(reason, itemId);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Wipe the mirror on disconnect (the next join's item burst rebuilds it).</summary>
    internal void Reset() => _items.Clear();

    private static void ReadLocation(PacketReader r, ClientItem item)
    {
        var kind = (ItemLocationKind)r.ReadByte();
        item.Location = kind;
        if (kind == ItemLocationKind.World)
        {
            item.WorldPose = PresenceCodec.ReadPose(r);
            item.OwnerPeerId = 0;
            item.OwnerName = string.Empty;
        }
        else
        {
            item.WorldPose = Pose.Identity;
            item.OwnerPeerId = (int)r.ReadVarUInt();
            item.OwnerName = r.ReadString();
        }
    }

    private void SendIdOnly(MessageType type, int id)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)type)
            .WriteVarUInt((uint)id)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }
}
