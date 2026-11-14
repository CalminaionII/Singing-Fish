using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SingingFish;

/// <summary>
/// The song menu (sneak + right-click a mounted fish with an empty hand): which song this fish sings. Choices: no song
/// (the default), a random song-pack song, each song-pack song, and each song in the player's own songs folder
/// (ModData/singingfish/songs) - a button opens that folder, another reads it again. Picking sends the choice to the
/// server at once; a player's own song is uploaded when the server asks for it.
/// </summary>
public sealed class GuiDialogSongPicker : GuiDialog
{
    private readonly BlockPos pos;
    private readonly string currentKey, currentName;
    private float volume;
    private bool motion;
    private float range;
    /// <summary>Which folder of the songs folder the list shows, "" for everything. Only offered once the player has
    /// made folders in there; picking one narrows the list to that folder's songs.</summary>
    private string activeFolder = "";
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
        Compose();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        if (SingingFishModSystem.ClientStore is { } store) { store.Changed += OnConverted; store.Rescan(); }
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        if (SingingFishModSystem.ClientStore is { } store) store.Changed -= OnConverted;
    }

    /// <summary>The songs folder was read again (or a conversion finished) while the menu is open: redraw the list.</summary>
    private void OnConverted() => Compose();

    private void BuildList()
    {
        var k = new List<string> { "", "random" };
        var n = new List<string> { Lang.Get("singingfish:song-none"), Lang.Get("singingfish:song-random") };
        var pl = new List<string> { "", "" };
        foreach (var loc in BEBehaviorSingingFish.PackSongs(capi))
        {
            k.Add("pack:" + loc);
            n.Add(Pretty(loc.GetName()));
            pl.Add(Pretty(loc.GetName()));
        }
        var own = SingingFishModSystem.ClientStore?.OwnSongs ?? [];
        folders = own.Select(s => s.Folder).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (activeFolder != "" && !folders.Contains(activeFolder)) activeFolder = "";
        if (activeFolder != "")
        {
            // one folder: only its songs (song packs are under All folders)
            k.RemoveRange(2, k.Count - 2); n.RemoveRange(2, n.Count - 2); pl.RemoveRange(2, pl.Count - 2);
        }
        foreach (var song in own)
        {
            if (activeFolder != "" && !string.Equals(song.Folder, activeFolder, StringComparison.OrdinalIgnoreCase)) continue;
            // under All folders a filed song shows where it is; inside its folder the folder is on every row, so not
            string shown = activeFolder == "" && song.Folder.Length > 0 ? song.Folder + "/" + song.Name : song.Name;
            k.Add(song.Busy ? "busy:" + song.Folder + "/" + song.Name : "user:" + song.Id);
            n.Add(Lang.Get(song.Busy ? "singingfish:song-busy" : "singingfish:song-yours", shown));
            pl.Add(song.Name);
        }
        if (!k.Contains(currentKey))
        {
            // someone else's song, or a pack this player does not have
            k.Add(currentKey);
            n.Add(currentName.Length > 0 ? currentName : currentKey);
            pl.Add(currentName);
        }
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

        if (folders.Length > 0)
        {
            var values = new[] { "" }.Concat(folders).ToArray();
            var shown = new[] { Lang.Get("singingfish:menu-folder-all") }.Concat(folders).ToArray();
            c.AddStaticText(Lang.Get("singingfish:menu-folder"), label, ElementBounds.Fixed(0, y + 5, L, 22));
            c.AddDropDown(values, shown, Math.Max(0, Array.IndexOf(values, activeFolder)), OnFolder, ElementBounds.Fixed(L, y, W - L, 30), "folder");
            y += 40;
        }
        c.AddStaticText(Lang.Get("singingfish:menu-song"), label, ElementBounds.Fixed(0, y + 5, L, 22));
        int selected = Math.Max(0, Array.IndexOf(keys, currentKey));
        c.AddDropDown(keys, names, selected, OnPick, ElementBounds.Fixed(L, y, W - L, 30), "song");
        y += 40;
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
        string help = Lang.Get("singingfish:menu-help");
        int helpHeight = (int)Math.Ceiling(capi.Gui.Text.GetMultilineTextHeight(helpFont, help, GuiElement.scaled(W)) / RuntimeEnv.GUIScale) + 4;
        c.AddStaticText(help, helpFont, ElementBounds.Fixed(0, y, W, helpHeight));
        y += helpHeight + 8;
        c.AddSmallButton(Lang.Get("singingfish:menu-openfolder"), OnOpenFolder, ElementBounds.Fixed(0, y, 190, 28));
        c.AddSmallButton(Lang.Get("singingfish:menu-refresh"), () => { SingingFishModSystem.ClientStore?.Rescan(); return true; }, ElementBounds.Fixed(200, y, 90, 28));
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
