# Song packs for Singing Fish

Singing Fish comes with two songs (A Christmas Carol by Tom Lehrer and This Is Not A Christmas Song by NEFFEX - see
SONGS.txt in the mod). A song pack is a tiny mod that holds more `.ogg` files; every song in every installed pack can be
sung, alongside the mod's own.

## Layout

```
mysongs.zip
  modinfo.json
  assets/
    mysongs/
      sounds/
        singingfish/
          my-first-song.ogg
          another-song.ogg
```

`modinfo.json`:

```json
{
  "type": "content",
  "modid": "mysongs",
  "name": "My Singing Fish Songs",
  "version": "1.0.0",
  "dependencies": { "game": "1.22.0", "singingfish": "1.0.0" }
}
```

* The folder under `assets/` is the pack's own mod id; the `sounds/singingfish/` part must be exactly that.
* Ogg Vorbis files only (`.ogg`), mono or stereo. Keep songs short - the fish is a novelty.
* On a server, put the pack in the server's Mods folder: the game sends it to every player, so everyone hears the
  same song. A pack only on your own computer works in single player.
* Only add music you are allowed to share.

## Picking a fish's song

A fish sings nothing until someone picks its song: sneak + right-click the mounted fish with an empty hand to open
the song menu. Choices: No song (the default), Random (a different song-pack song each time), any song-pack song, or
one of your own songs. The menu also sets the fish's Volume, its Range (how close, 1 - 16 blocks, someone must walk
for it to start singing; 4 by default) and whether it sings when someone walks up.

## Your own songs (no pack needed)

Put `.mp3`, `.wav` or `.ogg` files in your songs folder - `VintagestoryData/ModData/singingfish/songs/` (the menu has a button that
opens it) - and press Refresh in the menu. Picking one of yours sends it to the server once; anyone near the fish
downloads it from there, so everyone hears it. Server settings (`ModConfig/SingingFishConfig.json`):
`AllowPlayerSongs` (default true), `MaxSongMB` (default 6), `MaxSongsPerPlayer` (default 10). The server keeps players'
songs in `ModData/singingfish/playersongs/<player>/` - an admin can delete any of them.

MP3 and WAV are converted once (in the background) to a small MONO Ogg, which is what is played and sent to the server. An .ogg is used as it is - make it MONO: the game only places mono sounds in the world, so a stereo song is heard at full volume everywhere. Song packs must use .ogg.
