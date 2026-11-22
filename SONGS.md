# Songs for Singing Fish

Singing Fish comes with two songs (A Christmas Carol by Tom Lehrer and This Is Not A Christmas Song by NEFFEX - see
SONGS.txt in the mod). Players add their own, and a server owner can add songs for everyone.

## Picking a fish's song

A fish sings nothing until someone picks its song: sprint + right-click the mounted fish with an empty hand to open
the song menu. Choices: No song (the default), Random (a different song each time, picked by the server from the mod's songs, its server songs and every player's song it holds), Random (my songs) (picked only from the songs that player has added to the server), the songs that come with the mod, the server's songs, other players' songs, or
one of your own songs. The menu also sets the fish's Volume, its Range (how close, 1 - 16 blocks, someone must walk
for it to start singing; 4 by default) and whether it sings when someone walks up.

## Your own songs

Put `.mp3`, `.wav` or `.ogg` files in your songs folder - `VintagestoryData/ModData/singingfish/songs/` (the menu has a button that
opens it) - and press Refresh in the menu. Picking one of yours sends it to the server once; anyone near the fish
downloads it from there, so everyone hears it. Server settings (`ModConfig/SingingFishConfig.json`):
`PlayerSongs` (`Everyone` / `Admins` / `Nobody`, default Everyone - who may add songs; Nobody also hides songs players added
before), `ShowEveryonesSongs` (default true; off: each player's menu lists only the server's songs and their own, admins still
see all), `RandomPlaysPlayerSongs` (default true), `MaxSongMB` (default 6), `MaxSongsPerPlayer` (default 10),
`IncludeBuiltInSongs` (default true; off leaves the two songs that come with the mod out of the menu and Random). A file
from 1.0.0 - 1.0.1 with `AllowPlayerSongs` is converted (false = Nobody). Songs on the server are in everyone's menu with
the adder's name; a player can remove their own from the menu (pick it, Remove twice), an admin any.

## Server songs

An admin puts `.mp3`, `.wav` or `.ogg` files (subfolders too) in `ModData/singingfish/serversongs/` in the SERVER's data
folder and presses Refresh in any song menu (or restarts). They are listed for everyone as "(server)", Random picks
them, and players download one when a fish near them sings it. MP3/WAV are converted once on the server into
`ModData/singingfish/serverconverted/`. The server keeps players'
songs in `ModData/singingfish/playersongs/<player>/` - an admin can delete any of them.

MP3 and WAV are converted once (in the background) to a small MONO Ogg, which is what is played and sent to the server. An .ogg is used as it is - make it MONO: the game only places mono sounds in the world, so a stereo song is heard at full volume everywhere.

## Song packs (removed in 1.1.0)

1.0.x also read songs from other mods (`assets/<modid>/sounds/singingfish/*.ogg`). 1.1.0 reads only its own two: songs
players add are shared with everyone, and the server songs folder does what a pack did for a server. A fish set to a
song-pack song stays quiet until it gets a new song.
