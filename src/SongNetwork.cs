using ProtoBuf;

namespace SingingFish;

// Every packet class needs its own [ProtoContract]; protobuf-net cannot infer one, and on a dedicated server a missing
// contract is a kick, not an error line. Single player never shows it.

/// <summary>CLIENT -> SERVER: "this fish should sing this" (from the song menu).</summary>
[ProtoContract]
public class SetSongPacket
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    /// <summary>"" random, "pack:&lt;asset location&gt;", "user:&lt;id&gt;".</summary>
    [ProtoMember(4)] public string Key { get; set; }
    /// <summary>What to call it in the menu - client-supplied, cleaned and capped by the server.</summary>
    [ProtoMember(5)] public string Name { get; set; }
}

/// <summary>CLIENT -> SERVER: "stop this fish singing now" (the menu's Stop singing button).</summary>
[ProtoContract]
public class StopSongPacket
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
}

/// <summary>CLIENT -> SERVER: "sing now" (the menu's Play).</summary>
[ProtoContract]
public class PlaySongPacket
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
}

/// <summary>CLIENT -> SERVER: this fish's volume (0..1) and whether it sings when someone walks up.</summary>
[ProtoContract]
public class FishSettingsPacket
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Z { get; set; }
    [ProtoMember(4)] public float Volume { get; set; }
    [ProtoMember(5)] public bool Motion { get; set; }
}

/// <summary>SERVER -> CLIENT: "send me this song of yours" - the server does not hold it yet.</summary>
[ProtoContract]
public class SongNeedPacket
{
    [ProtoMember(1)] public string Id { get; set; }
}

/// <summary>
/// Either way, one piece of a song: songs are a few MB, so they travel in pieces. Upload (client -> server) carries
/// the player's file name for the server's folder listing; the server recomputes the id from the bytes. Download
/// (server -> client) with <see cref="Total"/> 0 means "the server does not have it".
/// </summary>
[ProtoContract]
public class SongChunkPacket
{
    [ProtoMember(1)] public string Id { get; set; }
    [ProtoMember(2)] public int Index { get; set; }
    [ProtoMember(3)] public int Total { get; set; }
    [ProtoMember(4)] public byte[] Data { get; set; }
    [ProtoMember(5)] public string Name { get; set; }
}

/// <summary>CLIENT -> SERVER: "a fish near me sings this and I do not have it."</summary>
[ProtoContract]
public class SongRequestPacket
{
    [ProtoMember(1)] public string Id { get; set; }
}

/// <summary>SERVER -> CLIENT: why a song was not taken (too big, limit reached, uploads off, not a song).</summary>
[ProtoContract]
public class SongRefusedPacket
{
    [ProtoMember(1)] public string Id { get; set; }
    [ProtoMember(2)] public string LangKey { get; set; }
    [ProtoMember(3)] public string Arg { get; set; }
}
