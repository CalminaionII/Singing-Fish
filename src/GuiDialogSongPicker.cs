using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SingingFish;

/// <summary>
/// The song menu (sprint + right-click a mounted fish with an empty hand): which song this fish sings. Choices: no song
/// (the default), Random (picked by the server from every song it offers), the songs that come with the mod, the server's own songs
/// (from its server songs folder), every other player's song on the server, and each song in the player's own songs folder
/// (ModData/singingfish/songs) - a button opens that folder, another reads it again. Picking sends the choice to the
/// server at once; a player's own song is uploaded when the server asks for it.
/// </summary>
public sealed class GuiDialogSongPicker : GuiDialog
{
    private readonly BlockPos pos;
    private readonly string currentKey, currentName;
    /// <summary>What the Song list shows now (the fish's song, then whatever was picked) - the Remove button acts on it.</summary>
    private string selectedKey;
    /// <summary>Remove was pressed once: a second press within a few seconds removes the song.</summary>
    private long removeArmedUntil;
    private float volume;
    private bool motion;
    private float range;
    /// <summary>What the Song list shows (the Show list): "" everything, <see cref="ServerOnly"/> the server's (and the mod's) songs,
    /// <see cref="PlayersOnly"/> other players', <see cref="MineOnly"/> this player's own, or one folder of their songs folder (offered once they have made folders in there).</summary>
    private string activeFolder = "";
    /// <summary>The Show list's "Server songs", "Player songs" and "My songs" - "*" cannot be in a folder name on any
    /// system the game runs on, so these never clash with a folder.</summary>
    private const string ServerOnly = "*server", PlayersOnly = "*players", MineOnly = "*";
    /// <summary>The server lists other players' songs to this player (so the Show list offers Player songs).</summary>
    private bool hasPlayerSongs;
    private string[] folders = [];
    private string[] keys = [];
    private string[] names = [];
    private string[] plain = [];      // what the server keeps as the song's name (no "(your song)")

    public override string ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public GuiDialogSongPicker(ICoreClientAPI capi, BlockPos pos, string currentKey, string currentName, float volume, bool motion, float range) : base(capi)
    {
        this.volume = volume;
        this.motion = motion;
        this.range = range;
        this.pos = pos.Copy();
        this.currentKey = currentKey ?? "";
        this.currentName = currentName ?? "";
        selectedKey = this.currentKey;
        Compose();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        if (SingingFishModSystem.ClientStore is { } store) { store.Changed += OnConverted; store.Rescan(); }
        SingingFishModSystem.ServerSongsChanged += OnConverted;
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        if (SingingFishModSystem.ClientStore is { } store) store.Changed -= OnConverted;
        SingingFishModSystem.ServerSongsChanged -= OnConverted;
    }

    /// <summary>The songs folder was read again (or a conversion finished), or the server sent its song list or settings,
    /// while the menu is open: redraw the list.</summary>
    private void OnConverted() => Compose();

    private void BuildList()
    {
        var k = new List<string> { "", "random" };
        var n = new List<string> { Lang.Get("singingfish:song-none"), Lang.Get("singingfish:song-random") };
        var pl = new List<string> { "", "" };
        var catalog = SingingFishModSystem.Catalog;
        string me = SingingFishModSystem.OwnFolder;
        // Random (my songs): while this player may add songs here or already has some on the server
        if (me.Length > 0 && (SingingFishModSystem.CanUpload || catalog.Any(s => s.Owner == me)))
        {
            k.Add(BEBehaviorSingingFish.RandomByPrefix + me);
            n.Add(Lang.Get("singingfish:song-random-mine"));
            pl.Add("");
        }
        var own = SingingFishModSystem.ClientStore?.OwnSongs ?? [];
        var ownIds = own.Where(s => !s.Busy).Select(s => s.Id).ToHashSet();
        var onServer = catalog.Select(s => s.Id).ToHashSet();
        folders = own.Select(s => s.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        // other players' songs the server lists to this player (none when it keeps them private and this is no admin)
        hasPlayerSongs = catalog.Any(s => (s.Owner ?? "").Length > 0 && s.Owner != me && !ownIds.Contains(s.Id));

        // the Show list: everything, one kind of song, or one folder of this player's songs folder
        bool server = activeFolder == ServerOnly, players = activeFolder == PlayersOnly, mine = activeFolder == MineOnly;
        if (players && !hasPlayerSongs || activeFolder.Length > 0 && !server && !players && !mine && !folders.Contains(activeFolder))
        {
            activeFolder = "";
            server = players = mine = false;
        }
        bool all = activeFolder == "";
        bool folder = !all && !server && !players && !mine;

        // the songs that come with the mod count as the server's
        if (all || server)
            foreach (var loc in BEBehaviorSingingFish.ModSongs(capi))
            {
                if (!BEBehaviorSingingFish.ModSongAllowed(loc)) continue;   // the server leaves the mod's own songs out
                k.Add("pack:" + loc);
                n.Add(Pretty(loc.GetName()));
                pl.Add(Pretty(loc.GetName()));
            }
        // the songs on the server: its own (dropped in its songs folder), other players', and this player's that are not
        // in this computer's songs folder (added from another computer); the rest of theirs are listed below
        foreach (var song in catalog)
        {
            if (ownIds.Contains(song.Id)) continue;
            string owner = song.Owner ?? "";
            bool isServer = owner.Length == 0, isMine = !isServer && owner == me;
            if (!(all || server && isServer || players && !isServer && !isMine || mine && isMine)) continue;
            k.Add("user:" + song.Id);
            n.Add(isServer ? Lang.Get("singingfish:song-server", song.Name)
                : isMine ? Lang.Get("singingfish:song-yours", song.Name)
                : Lang.Get("singingfish:song-by", song.Name, owner.Replace('_', ' ')));
            pl.Add(song.Name);
        }
        foreach (var song in own)
        {
            if (!(all || mine || folder)) break;
            // a server that does not take this player's songs: only the ones it already holds can be picked
            if (!SingingFishModSystem.CanUpload && (song.Busy || !onServer.Contains(song.Id))) continue;
            if (folder && !string.Equals(song.Folder, activeFolder, StringComparison.OrdinalIgnoreCase)) continue;
            // under All songs / My songs a filed song shows where it is; inside its folder the folder is on every row, so not
            string shown = !folder && song.Folder.Length > 0 ? song.Folder + "/" + song.Name : song.Name;
            k.Add(song.Busy ? "busy:" + song.Folder + "/" + song.Name : "user:" + song.Id);
            n.Add(Lang.Get(song.Busy ? "singingfish:song-busy" : "singingfish:song-yours", shown));
            pl.Add(song.Name);
        }
        if (!k.Contains(currentKey))
        {
            // a song the server no longer lists, or one from a song pack (no longer read)
            k.Add(currentKey);
            // another player's Random (my songs): shown with their name
            n.Add(currentKey.StartsWith(BEBehaviorSingingFish.RandomByPrefix)
                ? Lang.Get("singingfish:song-random-by", currentKey[BEBehaviorSingingFish.RandomByPrefix.Length..].Replace('_', ' '))
                : currentName.Length > 0 ? currentName : currentKey);
            pl.Add(currentName);
        }
        if (!k.Contains(selectedKey)) selectedKey = currentKey;
        keys = k.ToArray();
        names = n.ToArray();
        plain = pl.ToArray();
    }

    private static string Pretty(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file).Replace('-', ' ').Replace('_', ' ');
        return name.Length == 0 ? file : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private void Compose()
    {
        BuildList();
        ClearComposers();
        ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        ElementBounds dialog = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        // Two parts: this fish (song, volume, trigger range, walk-up switch, Play / Stop), then your own songs (folder). The help
        // text is measured, so a longer translation pushes the buttons down instead of running under them.
        const int W = 380, L = 70;
        int y = 32;
        var label = CairoFont.WhiteDetailText();
        var c = capi.Gui.CreateCompo("singingfish-songs", dialog)
            .AddShadedDialogBG(bg)
            .AddDialogTitleBar(Lang.Get("singingfish:menu-title"), () => TryClose())
            .BeginChildElements(bg);

        {
            // Show: All songs, Server songs, Player songs (when the server lists any), My songs, then each folder of the
            // player's songs folder
            var values = new List<string> { "", ServerOnly };
            var shown = new List<string> { Lang.Get("singingfish:menu-folder-all"), Text("singingfish:menu-folder-server", "Server songs") };
            if (hasPlayerSongs) { values.Add(PlayersOnly); shown.Add(Text("singingfish:menu-folder-players", "Player songs")); }
            values.Add(MineOnly); shown.Add(Text("singingfish:menu-folder-mine", "My songs"));
            values.AddRange(folders); shown.AddRange(folders);
            c.AddStaticText(Lang.Get("singingfish:menu-folder"), label, ElementBounds.Fixed(0, y + 5, L, 22));
            c.AddDropDown(values.ToArray(), shown.ToArray(), Math.Max(0, values.IndexOf(activeFolder)), OnFolder, ElementBounds.Fixed(L, y, W - L, 30), "folder");
            y += 40;
        }
        c.AddStaticText(Lang.Get("singingfish:menu-song"), label, ElementBounds.Fixed(0, y + 5, L, 22));
        int selected = Math.Max(0, Array.IndexOf(keys, selectedKey));
        c.AddDropDown(keys, names, selected, OnPick, ElementBounds.Fixed(L, y, W - L, 30), "song");
        y += 40;
        if (CanRemove(selectedKey))
        {
            bool armed = capi.World.ElapsedMilliseconds < removeArmedUntil;
            c.AddSmallButton(Lang.Get(armed ? "singingfish:menu-remove-sure" : "singingfish:menu-remove"), OnRemove, ElementBounds.Fixed(L, y - 4, W - L, 26));
            y += 32;
        }
        c.AddStaticText(Lang.Get("singingfish:menu-volume"), label, ElementBounds.Fixed(0, y + 2, L, 22));
        c.AddSlider(OnVolume, ElementBounds.Fixed(L, y + 2, W - L, 20), "volume");
        y += 34;
        c.AddStaticText(Text("singingfish:menu-range", "Range"), label, ElementBounds.Fixed(0, y + 2, L, 22));
        c.AddSlider(OnRange, ElementBounds.Fixed(L, y + 2, W - L, 20), "range");
        y += 34;
        c.AddStaticText(Lang.Get("singingfish:menu-motion"), label, ElementBounds.Fixed(0, y + 5, W - 40, 22));
        c.AddSwitch(OnMotion, ElementBounds.Fixed(W - 30, y, 30, 30), "motion");
        y += 40;
        c.AddSmallButton(Lang.Get("singingfish:menu-play"), OnPlay, ElementBounds.Fixed(0, y, (W - 10) / 2, 28));
        c.AddSmallButton(Lang.Get("singingfish:menu-stop"), OnStop, ElementBounds.Fixed((W + 10) / 2, y, (W - 10) / 2, 28));
        y += 46;

        c.AddStaticText(Lang.Get("singingfish:menu-own"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, W, 24));
        y += 30;
        var helpFont = CairoFont.WhiteSmallText();
        string help = Lang.Get(SingingFishModSystem.CanUpload ? "singingfish:menu-help" : "singingfish:menu-help-noupload");
        int helpHeight = (int)Math.Ceiling(capi.Gui.Text.GetMultilineTextHeight(helpFont, help, GuiElement.scaled(W)) / RuntimeEnv.GUIScale) + 4;
        c.AddStaticText(help, helpFont, ElementBounds.Fixed(0, y, W, helpHeight));
        y += helpHeight + 8;
        c.AddSmallButton(Lang.Get("singingfish:menu-openfolder"), OnOpenFolder, ElementBounds.Fixed(0, y, 190, 28));
        c.AddSmallButton(Lang.Get("singingfish:menu-refresh"), OnRefresh, ElementBounds.Fixed(200, y, 90, 28));
        c.AddSmallButton(Lang.Get("singingfish:menu-close"), () => TryClose(), ElementBounds.Fixed(W - 80, y, 80, 28));
        SingleComposer = c.EndChildElements().Compose();
        SingleComposer.GetSlider("volume")?.SetValues((int)Math.Round(volume * 100), 0, 100, 5, "%");
        SingleComposer.GetSlider("range")?.SetValues(
            Math.Clamp((int)Math.Round(range), BEBehaviorSingingFish.MinRange, BEBehaviorSingingFish.MaxRange),
            BEBehaviorSingingFish.MinRange, BEBehaviorSingingFish.MaxRange, 1, Text("singingfish:menu-range-unit", " blocks"));
        SingleComposer.GetSwitch("motion")?.SetValue(motion);
    }

    /// <summary>A translation, or the English given here if the mod's lang file does not have the key yet.</summary>
    private static string Text(string key, string english) => Lang.GetIfExists(key) ?? english;

    private void OnFolder(string folder, bool selected)
    {
        if (!selected || folder == activeFolder) return;
        activeFolder = folder ?? "";
        capi.Event.EnqueueMainThreadTask(Compose, "singingfish-folder");   // not from inside the dropdown's own handler
    }

    private void OnPick(string key, bool selected)
    {
        if (key.StartsWith("busy:")) return;   // still converting - nothing to pick yet
        int i = Array.IndexOf(keys, key);
        string name = i >= 0 ? plain[i] : "";
        capi.Network.GetChannel(SingingFishModSystem.Channel)?.SendPacket(new SetSongPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Key = key, Name = name });
        bool hadButton = CanRemove(selectedKey);
        selectedKey = key;
        removeArmedUntil = 0;
        if (hadButton || CanRemove(key)) capi.Event.EnqueueMainThreadTask(Compose, "singingfish-pick");   // show or hide Remove
    }

    /// <summary>Whether this player may remove this song from the server: an admin any song the server holds, a player
    /// one they added.</summary>
    private static bool CanRemove(string key)
    {
        if (key == null || !key.StartsWith("user:")) return false;
        var song = SingingFishModSystem.Catalog.FirstOrDefault(s => s.Id == key[5..]);
        if (song == null) return false;
        return SingingFishModSystem.IsAdmin || (song.Owner ?? "").Length > 0 && song.Owner == SingingFishModSystem.OwnFolder;
    }

    /// <summary>Remove, pressed twice: the first press asks "sure?" for a few seconds, the second removes it for everyone.</summary>
    private bool OnRemove()
    {
        if (!CanRemove(selectedKey)) return true;
        if (capi.World.ElapsedMilliseconds >= removeArmedUntil)
        {
            removeArmedUntil = capi.World.ElapsedMilliseconds + 4000;
            capi.Event.EnqueueMainThreadTask(Compose, "singingfish-remove");
            capi.Event.RegisterCallback(_ => { if (IsOpened()) Compose(); }, 4100);
            return true;
        }
        removeArmedUntil = 0;
        capi.Network.GetChannel(SingingFishModSystem.Channel)?.SendPacket(new RemoveSongPacket { Id = selectedKey[5..] });
        return true;
    }

    /// <summary>Refresh: this player's songs folder, and the server's song folders (for songs an admin dropped in).</summary>
    private bool OnRefresh()
    {
        SingingFishModSystem.ClientStore?.Rescan();
        capi.Network.GetChannel(SingingFishModSystem.Channel)?.SendPacket(new RescanSongsPacket());
        return true;
    }

    private bool OnVolume(int value)
    {
        volume = value / 100f;
        SendSettings();
        return true;
    }

    private bool OnRange(int value)
    {
        range = value;
        SendSettings();
        return true;
    }

    private void OnMotion(bool on)
    {
        motion = on;
        SendSettings();
    }

    private void SendSettings() =>
        capi.Network.GetChannel(SingingFishModSystem.Channel)?.SendPacket(new FishSettingsPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Volume = volume, Motion = motion, Range = range });

    private bool OnPlay()
    {
        capi.Network.GetChannel(SingingFishModSystem.Channel)?.SendPacket(new PlaySongPacket { X = pos.X, Y = pos.Y, Z = pos.Z });
        return true;
    }

    private bool OnStop()
    {
        capi.Network.GetChannel(SingingFishModSystem.Channel)?.SendPacket(new StopSongPacket { X = pos.X, Y = pos.Y, Z = pos.Z });
        return true;
    }

    private bool OnOpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ClientSongStore.OwnFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ClientSongStore.OwnFolder, UseShellExecute = true });
        }
        catch (Exception)
        {
            capi.ShowChatMessage(Lang.Get("singingfish:menu-folderat", ClientSongStore.OwnFolder));
        }
        return true;
    }
}
