using System.Security.Cryptography;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SingingFish;

/// <summary>Shared rules for player songs: ids, pieces, checks.</summary>
public static class Songs
{
    /// <summary>Pieces a song travels in. A few MB in one network message is asking for trouble.</summary>
    public const int ChunkBytes = 192 * 1024;

    /// <summary>A song's id: the first 16 hex characters of its SHA-256. The same file is the same song everywhere, and
    /// the server recomputes it from the bytes, so nobody can claim an id they do not have the data for.</summary>
    public static string IdOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    public static bool IsWellFormedId(string id) =>
        id is { Length: 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Ogg files start "OggS"; anything else is not a song the game can play.</summary>
    public static bool LooksLikeOgg(byte[] b) => b is { Length: > 4 } && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S';

    /// <summary>A display name from anything a client sends: no path, no odd characters, at most 48 long.</summary>
    public static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "song";
        name = Path.GetFileNameWithoutExtension(name.Replace('\\', '/').Split('/')[^1]);
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '\'' or '(' or ')').Take(48).ToArray();
        var clean = new string(chars).Trim();
        return clean.Length == 0 ? "song" : clean;
    }
}

/// <summary>
/// CLIENT: the player's own songs folder (ModData/singingfish/songs - drop .mp3, .wav or .ogg files in it), MP3/WAV
/// converted to mono Ogg (ModData/singingfish/converted), and songs downloaded from servers (ModData/singingfish/
/// songcache).
///
/// NOTHING HEAVY ON THE MAIN THREAD: the folder is read (and every file hashed) on a background task, with each file's
/// result remembered by its path, size and time so a file is only hashed again when it changes; uploads are read off
/// the main thread too and only handed to the network on it. <see cref="Changed"/> tells the menu to redraw.
/// </summary>
public sealed class ClientSongStore
{
    /// <summary>One song in the folder. <see cref="Id"/> is null while an MP3/WAV is still being converted.
    /// <see cref="Folder"/> is where the player filed it inside the songs folder ("Christmas", "Rock/Old"), "" for loose.</summary>
    public sealed record Song(string Id, string Name, string File, string Folder)
    {
        public bool Busy => Id == null;
    }

    private sealed record Known(long Length, DateTime Written, string Id, string OggFile);

    private readonly ICoreClientAPI capi;
    private readonly Func<IClientNetworkChannel> channel;
    private readonly object gate = new();
    private IReadOnlyList<Song> ownList = [];
    private Dictionary<string, Song> own = new();
    private readonly Dictionary<string, Known> known = new();
    private readonly HashSet<string> asked = new();
    private readonly Dictionary<string, byte[][]> incoming = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> converting = new();
    private bool scanning, scanAgain;

    /// <summary>A downloaded song is ready (main thread).</summary>
    public event Action<string> Arrived;

    /// <summary>The songs folder was read again, or a conversion finished (main thread) - the menu redraws.</summary>
    public event Action Changed;

    public ClientSongStore(ICoreClientAPI capi, Func<IClientNetworkChannel> channel)
    {
        this.capi = capi;
        this.channel = channel;
        try { Directory.CreateDirectory(OwnFolder); Directory.CreateDirectory(CacheFolder); }
        catch (Exception e) { capi.Logger.Warning("[Singing Fish] Could not make the songs folders: {0}", e.Message); }
        Rescan();
    }

    public static string OwnFolder => Path.Combine(GamePaths.DataPath, "ModData", "singingfish", "songs");
    private static string CacheFolder => Path.Combine(GamePaths.DataPath, "ModData", "singingfish", "songcache");
    /// <summary>MP3/WAV converted to mono Ogg, named by the SOURCE file's id, so a converted song is reused.</summary>
    private static string ConvertedFolder => Path.Combine(GamePaths.DataPath, "ModData", "singingfish", "converted");

    /// <summary>The songs found the last time the folder was read (MP3/WAV still converting are listed as busy).</summary>
    public IReadOnlyList<Song> OwnSongs { get { lock (gate) return ownList; } }

    public bool IsOwn(string id) { lock (gate) return own.ContainsKey(id); }

    /// <summary>Reads the songs folder again in the background; <see cref="Changed"/> fires when done.</summary>
    public void Rescan()
    {
        lock (gate)
        {
            if (scanning) { scanAgain = true; return; }
            scanning = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                var (list, dict) = Scan();
                lock (gate)
                {
                    ownList = list;
                    own = dict;
                    if (!scanAgain) { scanning = false; break; }
                    scanAgain = false;
                }
            }
            capi.Event.EnqueueMainThreadTask(() => Changed?.Invoke(), "singingfish-songs");
        });
    }

    private (IReadOnlyList<Song>, Dictionary<string, Song>) Scan()
    {
        var dict = new Dictionary<string, Song>();
        var busy = new List<Song>();
        try
        {
            Directory.CreateDirectory(OwnFolder);
            var files = Directory.GetFiles(OwnFolder, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || SongConverter.CanConvert(f));
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 60 * 1024 * 1024) continue;
                    string name = Songs.CleanName(info.Name);
                    string folder = FolderOf(file);
                    Known k;
                    lock (gate) known.TryGetValue(file, out k);
                    if (k == null || k.Length != info.Length || k.Written != info.LastWriteTimeUtc || (k.OggFile != null && !File.Exists(k.OggFile)))
                    {
                        k = Examine(file, info);
                        lock (gate) { if (k != null) known[file] = k; else known.Remove(file); }
                    }
                    if (k == null) continue;
                    if (k.Id == null) { busy.Add(new Song(null, name, file, folder)); continue; }
                    dict[k.Id] = new Song(k.Id, name, k.OggFile, folder);
                }
                catch (Exception e) { capi.Logger.Warning("[Singing Fish] Could not read {0}: {1}", Path.GetFileName(file), e.Message); }
            }
        }
        catch (Exception e) { capi.Logger.Warning("[Singing Fish] Could not read the songs folder: {0}", e.Message); }
        return (dict.Values.Concat(busy).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(), dict);
    }

    /// <summary>The folder a file sits in, relative to the songs folder, with "/" between levels; "" at the top.</summary>
    private static string FolderOf(string file)
    {
        string rel = Path.GetRelativePath(OwnFolder, Path.GetDirectoryName(file) ?? OwnFolder).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    /// <summary>One file: an .ogg is hashed as it is; an MP3/WAV is looked up in the converted folder or converted.</summary>
    private Known Examine(string file, FileInfo info)
    {
        var bytes = File.ReadAllBytes(file);
        if (SongConverter.CanConvert(file))
        {
            string oggFile = Path.Combine(ConvertedFolder, Songs.IdOf(bytes) + ".ogg");
            if (!File.Exists(oggFile))
            {
                StartConverting(file, oggFile);
                return new Known(info.Length, info.LastWriteTimeUtc, null, null);
            }
            var ogg = File.ReadAllBytes(oggFile);
            return Songs.LooksLikeOgg(ogg) ? new Known(info.Length, info.LastWriteTimeUtc, Songs.IdOf(ogg), oggFile) : null;
        }
        return Songs.LooksLikeOgg(bytes) ? new Known(info.Length, info.LastWriteTimeUtc, Songs.IdOf(bytes), file) : null;
    }

    /// <summary>Converts one MP3/WAV off the main thread, then reads the folder again; a failure is logged.</summary>
    private void StartConverting(string source, string target)
    {
        if (!converting.TryAdd(target, true)) return;
        Task.Run(() =>
        {
            try
            {
                var ogg = SongConverter.ToMonoOgg(source);
                Directory.CreateDirectory(ConvertedFolder);
                string temp = target + ".part";
                File.WriteAllBytes(temp, ogg);
                File.Move(temp, target, true);
                lock (gate) known.Remove(source);
                Rescan();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[Singing Fish] Could not convert {0}: {1}", Path.GetFileName(source), e.Message);
            }
            finally { converting.TryRemove(target, out _); }
        });
    }

    /// <summary>This song's file on this computer (own folder, converted, or download cache), or null.</summary>
    private string FileOf(string id)
    {
        if (!Songs.IsWellFormedId(id)) return null;
        lock (gate) if (own.TryGetValue(id, out var s)) return s.File;
        string cached = Path.Combine(CacheFolder, id + ".ogg");
        return File.Exists(cached) ? cached : null;
    }

    public bool Has(string id) => FileOf(id) != null;

    /// <summary>This song's bytes (read now), or null.</summary>
    public byte[] Bytes(string id)
    {
        var file = FileOf(id);
        if (file == null) return null;
        try
        {
            var bytes = File.ReadAllBytes(file);
            return Songs.IdOf(bytes) == id ? bytes : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Asks the server for a song once per session.</summary>
    public void Request(string id)
    {
        if (!Songs.IsWellFormedId(id) || Has(id) || !asked.Add(id)) return;
        channel()?.SendPacket(new SongRequestPacket { Id = id });
    }

    /// <summary>A piece of a requested song from the server; the last one is checked and cached (the write off the main thread).</summary>
    public void OnDownloadChunk(SongChunkPacket p)
    {
        if (!Songs.IsWellFormedId(p?.Id)) return;
        if (p.Total <= 0) { incoming.Remove(p.Id); return; }                    // the server does not have it
        if (p.Total > 200 || p.Index < 0 || p.Index >= p.Total || p.Data == null) return;
        if (!incoming.TryGetValue(p.Id, out var parts) || parts.Length != p.Total) incoming[p.Id] = parts = new byte[p.Total][];
        parts[p.Index] = p.Data;
        if (parts.Any(x => x == null)) return;
        incoming.Remove(p.Id);
        string id = p.Id;
        Task.Run(() =>
        {
            var bytes = parts.SelectMany(x => x).ToArray();
            if (Songs.IdOf(bytes) != id || !Songs.LooksLikeOgg(bytes)) return;
            try { File.WriteAllBytes(Path.Combine(CacheFolder, id + ".ogg"), bytes); } catch (Exception) { return; }
            capi.Event.EnqueueMainThreadTask(() => Arrived?.Invoke(id), "singingfish-arrived");
        });
    }

    /// <summary>The server asked for one of this player's songs: read it off the main thread, send it in pieces on it.</summary>
    public void Upload(string id)
    {
        Song song;
        lock (gate) own.TryGetValue(id, out song);
        if (song == null) return;
        Task.Run(() =>
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(song.File); } catch (Exception) { return; }
            if (Songs.IdOf(bytes) != id) return;
            int total = Math.Max(1, (bytes.Length + Songs.ChunkBytes - 1) / Songs.ChunkBytes);
            var pieces = new List<SongChunkPacket>(total);
            for (int i = 0; i < total; i++)
            {
                int from = i * Songs.ChunkBytes, len = Math.Min(Songs.ChunkBytes, bytes.Length - from);
                pieces.Add(new SongChunkPacket { Id = id, Index = i, Total = total, Data = bytes.AsSpan(from, len).ToArray(), Name = song.Name });
            }
            capi.Event.EnqueueMainThreadTask(() => { foreach (var piece in pieces) channel()?.SendPacket(piece); }, "singingfish-upload");
        });
    }
}

/// <summary>
/// SERVER: every song the server holds, for everyone. Two kinds:
/// <list type="bullet">
/// <item>Players' uploads, kept as ModData/singingfish/playersongs/&lt;player&gt;/&lt;name&gt;_&lt;id&gt;.ogg.</item>
/// <item>Server songs: whatever an admin drops into ModData/singingfish/serversongs (.ogg, .mp3, .wav, folders too) -
/// no mod needed. MP3/WAV are converted once, in the background, into ModData/singingfish/serverconverted.</item>
/// </list>
/// Both are sent to players by id when a fish near them sings one, and listed in everyone's song menu (the catalog).
/// Folders are read off the main thread at start and whenever someone presses Refresh in the song menu; an admin may
/// delete files by hand too (Refresh picks that up), or remove a song from the menu.
/// </summary>
public sealed class ServerSongStore
{
    /// <summary>One song on the server. <see cref="Owner"/> is the uploader's folder name, "" for a server song;
    /// <see cref="Source"/> is the file an admin put in the server songs folder (the MP3 for a converted one).</summary>
    public sealed record Entry(string Id, string Name, string Owner, string File, string Source)
    {
        public bool ServerSong => Owner.Length == 0;
    }

    private sealed record Known(long Length, DateTime Written, string Id, string OggFile);

    private readonly ICoreServerAPI sapi;
    private readonly object gate = new();
    private Dictionary<string, Entry> byId = new();
    private readonly Dictionary<string, Known> known = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> converting = new();
    private readonly Dictionary<string, (string Id, string Name, byte[][] Parts)> incoming = new();
    private bool scanning, scanAgain;
    private long lastRescanMs = -100000;

    /// <summary>The list of songs changed (set from any thread; the mod system sends the new catalog on its tick).</summary>
    public volatile bool Dirty;

    public ServerSongStore(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        try { Directory.CreateDirectory(PlayerRoot); Directory.CreateDirectory(ServerRoot); }
        catch (Exception e) { sapi.Logger.Warning("[Singing Fish] Could not make the song folders: {0}", e.Message); }
        Rescan();
    }

    private static string PlayerRoot => Path.Combine(GamePaths.DataPath, "ModData", "singingfish", "playersongs");
    public static string ServerRoot => Path.Combine(GamePaths.DataPath, "ModData", "singingfish", "serversongs");
    private static string ConvertedRoot => Path.Combine(GamePaths.DataPath, "ModData", "singingfish", "serverconverted");

    /// <summary>Every song now: server songs first, then by owner and name.</summary>
    public List<Entry> All()
    {
        lock (gate) return byId.Values.OrderBy(e => e.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Entry Get(string id)
    {
        if (!Songs.IsWellFormedId(id)) return null;
        lock (gate) return byId.TryGetValue(id, out var e) && File.Exists(e.File) ? e : null;
    }

    public bool Has(string id) => Get(id) != null;

    /// <summary>The menu's Refresh: read both folders again, at most every few seconds whoever asks.</summary>
    public void RescanSoon()
    {
        long now = sapi.World.ElapsedMilliseconds;
        if (now - lastRescanMs < 5000) return;
        lastRescanMs = now;
        Rescan();
    }

    /// <summary>Reads both folders again in the background; <see cref="Dirty"/> is set when done.</summary>
    public void Rescan()
    {
        lock (gate)
        {
            if (scanning) { scanAgain = true; return; }
            scanning = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                var found = Scan();
                lock (gate)
                {
                    byId = found;
                    if (!scanAgain) { scanning = false; break; }
                    scanAgain = false;
                }
            }
            Dirty = true;
        });
    }

    private Dictionary<string, Entry> Scan()
    {
        var found = new Dictionary<string, Entry>();
        try
        {
            Directory.CreateDirectory(PlayerRoot);
            foreach (var dir in Directory.GetDirectories(PlayerRoot))
            {
                string owner = Path.GetFileName(dir);
                foreach (var file in Directory.GetFiles(dir, "*.ogg", SearchOption.AllDirectories))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string id = stem.Length >= 16 ? stem[^16..] : "";
                    if (!Songs.IsWellFormedId(id)) continue;
                    string name = stem.Length > 17 ? stem[..^17].Replace('_', ' ') : "";
                    found[id] = new Entry(id, Songs.CleanName(name), owner, file, null);
                }
            }
        }
        catch (Exception e) { sapi.Logger.Warning("[Singing Fish] Could not read the player songs folder: {0}", e.Message); }

        try
        {
            Directory.CreateDirectory(ServerRoot);
            var files = Directory.GetFiles(ServerRoot, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || SongConverter.CanConvert(f));
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 60 * 1024 * 1024) continue;
                    Known k;
                    lock (gate) known.TryGetValue(file, out k);
                    if (k == null || k.Length != info.Length || k.Written != info.LastWriteTimeUtc || (k.OggFile != null && !File.Exists(k.OggFile)))
                    {
                        k = Examine(file, info);
                        lock (gate) { if (k != null) known[file] = k; else known.Remove(file); }
                    }
                    if (k?.Id == null) continue;   // not a song, or still converting
                    string rel = Path.GetRelativePath(ServerRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    int slash = rel.LastIndexOf('/');
                    string name = Songs.CleanName(rel);
                    if (slash > 0) name = rel[..slash] + "/" + name;
                    // the same song uploaded by a player and in the server folder: listed once, as the server's
                    found[k.Id] = new Entry(k.Id, name, "", k.OggFile, file);
                }
                catch (Exception e) { sapi.Logger.Warning("[Singing Fish] Could not read {0}: {1}", Path.GetFileName(file), e.Message); }
            }
        }
        catch (Exception e) { sapi.Logger.Warning("[Singing Fish] Could not read the server songs folder: {0}", e.Message); }
        return found;
    }

    /// <summary>One server song file: an .ogg is hashed as it is; an MP3/WAV is looked up in the converted folder or converted.</summary>
    private Known Examine(string file, FileInfo info)
    {
        var bytes = File.ReadAllBytes(file);
        if (SongConverter.CanConvert(file))
        {
            string oggFile = Path.Combine(ConvertedRoot, Songs.IdOf(bytes) + ".ogg");
            if (!File.Exists(oggFile))
            {
                StartConverting(file, oggFile);
                return new Known(info.Length, info.LastWriteTimeUtc, null, null);
            }
            var ogg = File.ReadAllBytes(oggFile);
            return Songs.LooksLikeOgg(ogg) ? new Known(info.Length, info.LastWriteTimeUtc, Songs.IdOf(ogg), oggFile) : null;
        }
        return Songs.LooksLikeOgg(bytes) ? new Known(info.Length, info.LastWriteTimeUtc, Songs.IdOf(bytes), file) : null;
    }

    private void StartConverting(string source, string target)
    {
        if (!converting.TryAdd(target, true)) return;
        Task.Run(() =>
        {
            try
            {
                var ogg = SongConverter.ToMonoOgg(source);
                Directory.CreateDirectory(ConvertedRoot);
                string temp = target + ".part";
                File.WriteAllBytes(temp, ogg);
                File.Move(temp, target, true);
                lock (gate) known.Remove(source);
                sapi.Logger.Notification("[Singing Fish] Server song {0} is ready", Path.GetFileName(source));
                Rescan();
            }
            catch (Exception e) { sapi.Logger.Warning("[Singing Fish] Could not convert {0}: {1}", Path.GetFileName(source), e.Message); }
            finally { converting.TryRemove(target, out _); }
        });
    }

    /// <summary>The folder a player's uploads go in - also how the song menu names the uploader.</summary>
    public static string FolderNameOf(string playerName) => Songs.CleanName(playerName).Replace(' ', '_');

    private static string FolderFor(IServerPlayer player) => Path.Combine(PlayerRoot, FolderNameOf(player.PlayerName));

    private int CountFor(IServerPlayer player)
    {
        try { return Directory.Exists(FolderFor(player)) ? Directory.GetFiles(FolderFor(player), "*.ogg").Length : 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>One piece of a song from a player; the last checks and keeps it. The caller has checked they may add songs.</summary>
    public void OnUploadChunk(IServerPlayer player, SongChunkPacket p, SingingFishConfig config, IServerNetworkChannel channel)
    {
        if (!Songs.IsWellFormedId(p?.Id) || p.Data == null || p.Data.Length > Songs.ChunkBytes) return;
        int maxChunks = config.MaxSongBytes / Songs.ChunkBytes + 1;
        string key = player.PlayerUID;
        void Refuse(string lang, string arg = null)
        {
            incoming.Remove(key);
            channel.SendPacket(new SongRefusedPacket { Id = p.Id, LangKey = lang, Arg = arg }, player);
        }
        if (p.Total <= 0 || p.Total > maxChunks) { Refuse("singingfish:refused-big", config.MaxSongMB.ToString()); return; }
        if (p.Index < 0 || p.Index >= p.Total) return;
        if (Has(p.Id)) { incoming.Remove(key); return; }

        if (!incoming.TryGetValue(key, out var up) || up.Id != p.Id || up.Parts.Length != p.Total)
            incoming[key] = up = (p.Id, Songs.CleanName(p.Name), new byte[p.Total][]);
        up.Parts[p.Index] = p.Data;
        if (up.Parts.Any(x => x == null)) return;
        incoming.Remove(key);

        var bytes = up.Parts.SelectMany(x => x).ToArray();
        if (bytes.Length > config.MaxSongBytes) { Refuse("singingfish:refused-big", config.MaxSongMB.ToString()); return; }
        if (!Songs.LooksLikeOgg(bytes) || Songs.IdOf(bytes) != p.Id) { Refuse("singingfish:refused-notogg"); return; }
        if (CountFor(player) >= config.MaxSongsPerPlayer) { Refuse("singingfish:refused-limit", config.MaxSongsPerPlayer.ToString()); return; }
        try
        {
            Directory.CreateDirectory(FolderFor(player));
            string file = Path.Combine(FolderFor(player), up.Name.Replace(' ', '_') + "_" + p.Id + ".ogg");
            File.WriteAllBytes(file, bytes);
            lock (gate) byId[p.Id] = new Entry(p.Id, up.Name, FolderNameOf(player.PlayerName), file, null);
            Dirty = true;
            sapi.Logger.Audit("[Singing Fish] {0} added the song {1} ({2} KB)", player.PlayerName, up.Name, bytes.Length / 1024);
        }
        catch (Exception e) { sapi.Logger.Warning("[Singing Fish] Could not keep {0}'s song: {1}", player.PlayerName, e.Message); }
    }

    /// <summary>
    /// Deletes a song for good: an upload's file, or a server song's own file (and its converted copy). The caller has
    /// checked who may. Fish set to it go quiet.
    /// </summary>
    public bool Remove(string id, IServerPlayer by)
    {
        var e = Get(id);
        if (e == null) return false;
        try
        {
            if (e.Source != null && File.Exists(e.Source)) File.Delete(e.Source);
            if (e.File != e.Source && File.Exists(e.File)) File.Delete(e.File);
        }
        catch (Exception ex)
        {
            sapi.Logger.Warning("[Singing Fish] Could not remove the song {0}: {1}", e.Name, ex.Message);
            return false;
        }
        lock (gate) { byId.Remove(id); if (e.Source != null) known.Remove(e.Source); }
        Dirty = true;
        sapi.Logger.Audit("[Singing Fish] {0} removed the song {1} ({2})", by.PlayerName, e.Name, e.ServerSong ? "server song" : "added by " + e.Owner);
        return true;
    }

    /// <summary>A player wants a song a fish near them sings: send it in pieces, or "do not have it".</summary>
    public void OnRequest(IServerPlayer player, SongRequestPacket p, IServerNetworkChannel channel)
    {
        var e = Get(p?.Id);
        byte[] bytes = null;
        try { if (e != null) bytes = File.ReadAllBytes(e.File); } catch (Exception) { }
        if (bytes == null) { channel.SendPacket(new SongChunkPacket { Id = p?.Id, Total = 0 }, player); return; }
        int total = Math.Max(1, (bytes.Length + Songs.ChunkBytes - 1) / Songs.ChunkBytes);
        for (int i = 0; i < total; i++)
        {
            int from = i * Songs.ChunkBytes, len = Math.Min(Songs.ChunkBytes, bytes.Length - from);
            channel.SendPacket(new SongChunkPacket { Id = p.Id, Index = i, Total = total, Data = bytes.AsSpan(from, len).ToArray() }, player);
        }
    }
}
