# Sarkastic.gg Resource Regrowth

A server-side [BepInEx](https://github.com/BepInEx/BepInEx) plugin for Valheim 1.0 dedicated servers.
It brings one-time world content back once an area has been left alone for a while:

- **Surtling core stands** get their core back.
- **Treasure chests** (never player-built ones, and never one holding something of a player's) are restocked with fresh loot, whatever was left in them replaced.
- **One-time creature spawners** (dungeons, camps, caves) spawn their creature again.
- A **location regrows as a whole**: a dungeon with its entrance, a camp, a village or a ruin comes
  back all at once -- every core, every creature and every chest in it.
- **Terrain** that players dug, raised, levelled or paved slowly returns to the land's own shape,
  except around player bases and wards.
- **Location objects** (off by default): destroyed objects a location or village generated -- wild
  **beehives** in abandoned houses, for example -- are put back exactly where they stood.
- **Zones** (off by default): a zone players have been in is **generated again by the game itself**
  once nobody has been near it for three days -- trees, rocks and ore deposits, bushes, pickables, the
  location with its buildings, chests, creatures and dungeon, and the ground -- as if no player had
  ever been there. Never at a base (a workbench); players' things stay as they are.

Players need nothing: vanilla clients work. The plugin depends on BepInEx and nothing else. It
patches one game method, and only with Zones switched on (see Zones).

## How it works

Everything handled here stays in the world when it is used up, so regrowing it only changes
the object's data. The plugin deletes and creates no objects.

| What | Depleted when | Regrown by |
|---|---|---|
| Surtling core stand | the core was taken (`picked`) | setting `picked` back to false |
| Treasure chest | vanilla filled it once, and it is now empty or a player has been near it since it was last restocked | replacing its contents with fresh loot from the chest's own loot table, as vanilla fills it when the chest is first generated |
| One-time spawner | its creature is gone | clearing the spawned link; vanilla spawns again the next time a player loads the area |
| Terrain (a zone's `_TerrainCompiler`) | any vertex of the zone's heightmap is modified or painted | a step every `StepHours`: every height change outside a protected area is divided by `Divider` (1.7) and dropped once below `MinDelta`; paint goes back to nothing once the height under it is back, except paved and cultivated ground. The data is written in the game's own format; loaded terrain reloads it and every client rebuilds the ground. |

Location objects are the one exception to "no object is created": a destroyed beehive is gone from the
world, so it is created again. Where it stood is known exactly, not guessed:

- every location the world generated keeps a record of its position, rotation and seed, and every
  village or camp keeps the position and rotation of each of its buildings;
- the object's place follows from the location's or building's layout, the way the game placed it;
- objects that only spawn by chance are decided by replaying the game's own roll from that seed, so
  only places that really had one count.

Checked on a live world: of 169 beehive places the replay said 47 had a hive, and all 29 hives still
standing were on exactly those places (same height to the centimetre), none anywhere else.
A hive hangs under the roof of its house; when the pieces of the house that held it are gone
(pulled down by players), the hive is not put back, so nothing floats in the air.

Whether a player took everything from a chest, some of it or nothing cannot be told from the chest,
so a chest counts as used once a player has been near it; when its time comes it is emptied and
filled afresh, partly looted or not.

**A location regrows as a whole** (`[Locations] Together`). Every object belongs to the location the
world generated it with: inside a dungeon (5000 m up) to the dungeon its generator built -- rooms lie
within about 60 m of it -- and to the location at the same spot on the surface, so a burial chamber's
interior and its entrance are one; a cave whose interior comes whole with the location (troll and
bear caves) by its interior radius; on the surface to the location whose ground it stands on. Each
used-up object keeps its own clock, and as soon as one of them has waited its category's `AfterHours`,
everything used up in the location comes back together, and every chest in it is restocked: a burial
chamber whose cores were taken 10 hours ago returns whole, chests and draugr included. With
`Together = false` each object regrows on its own.

Every `CheckIntervalMinutes` the plugin scans the world. A location (or a lone object) regrows only when all of these hold:

- one of its objects has been used up for its category's `AfterHours`;
- no player has been near any of it for `IdleHours`;
- none of it lies within `PlayerBuildRadius` of a player-built piece or tombstone;
- none of it is in use: no connected player owns it, and the server does not have it loaded.
  Dedicated Simulation, for example, loads the zones around players on the server.

A scan of a large world (165,000 objects) takes about 15 ms of work, spread over about 40 frames.

### Zones

Vanilla never brings back a felled tree, a mined copper or tin deposit, a stone tower or a house
players pulled down. With `[Zones] Enabled`, a zone (64 m square) that a player has been in or next to
is, once nobody has been near it (`PresenceRadiusZones`) for `IdleHours` (72), generated again by the
game's own zone generation, the way Dedicated Simulation generates the zones around players
(ZoneSystem.SpawnZone, ghost mode). Every object the game makes takes the place of the old one of its
kind standing there, which goes; what is left over of what only the zone itself makes -- vegetation,
stumps and logs, wild creatures, items lying about, the record of what was done to the ground -- goes
too. So trees, rocks and ore deposits, bushes, pickables, the location with its buildings, chests,
spawners and dungeon come back. Measured on a copy of a live world against a fresh world with the same
seed: 860 of 916 zones without a location came out exactly as first generated, the rest with a few
trees or rocks elsewhere (the game decides placement partly by what colliders are around at that
moment).

A location comes back the way round it was: the game draws its rotation from an unseeded random
number when it first places it, so the rotation is read from the location's own record before the
zone is cleared and given back when it is placed again (a patch of `ZoneSystem.SpawnLocation`, the
only game method this plugin touches). With the same rotation its dungeon gets the same seed and the
same rooms. Because old objects are matched to what the game made object by object, a location or
dungeon that reaches over the zone's edge (a boar runestone's spawners, a crypt's rooms) is replaced
there too, and a neighbour's objects reaching into the zone stay.

Players' things stay exactly where they are: pieces they built (also portals, beds, crops), tombstones,
ships and carts, items a player has carried (an item remembers having been in a bag), tamed creatures,
and the world's chests, barrels and stands holding something of theirs (the old one stays, the game's
new copy goes). Beside a piece they built or a tombstone nothing changes -- within 4 m for trees, rocks,
buildings and chests, 2 m for smaller things, and wherever the colliders of a big rock, deposit or tree
reach within 2 m of one, the game's new copies go and the old ones stay -- so no tree grows through a
wall, nothing they kept by their walls goes, and a mountain rock (its stone covers the ground up to 23 m
from its centre) does not come back over a chest built where it was mined out. A zone with their pieces
in it keeps its ground record (Terrain regrowth deals with it, with its own protections).

Never generated again:

- a zone with a **workbench** in it or within `PlayerBuildRadius` (48 m) of it: someone's base. Of
  players' things only a workbench keeps a zone as it is;
- a protected location (`ProtectedLocations`: the start temple with the guardian stones, boss altars,
  traders, Hildir's dungeons) and every location the game places only once; nothing left over within a
  protected location's reach in a neighbouring zone goes, and no trader;
- a zone the server has loaded or next to one it has, or anything in it a connected player owns;
- anything in the first `GraceMinutes` (30) after a start: who was near while the server was off is
  not known.

Fields, paved roads, dirt paths, dug and raised ground and the trees players planted go with the reset
unless a workbench is near: whoever wants a road or a field kept puts a workbench by it. A zone with
players' pieces in it (crops are pieces) keeps its ground record, so a field with crops on it stays a field.

If the game cannot generate the zone now, or throws while doing it, nothing old has been deleted
yet: whatever the attempt made is removed, the zone is marked generated and its location placed again,
and after an exception zone resets stop until the next start.

At its first run every zone generated so far counts as visited -- what players did there before
cannot be told -- and no zone is generated again before `IdleHours` from then. Presence is kept for
the longer of the two idle times, also while Zones are off, and a zone without a record counts as seen
at the time from which presence is known completely, so switching Zones on later or raising
`IdleHours` never makes a recently visited zone due. At most `PerPass` zones per pass, longest idle
first, one per frame (each a few milliseconds to a few tens); a zone that cannot be done now waits an
hour. `DryRun` (on by default) only logs which zones would be generated again and what they hold.

### Terrain protection

Ground within the radius of a **player base object** -- anything the game itself treats as a base
when it decides where creatures may not spawn (a PlayerBase effect area: workbench, forge,
stonecutter, beds, fires ...) -- or of a **ward** is left as it is; the radius is the object's own
(`ProtectMultiplier` scales it). A base that is gone, say a workbench broken by creatures, protects
nothing any more, and the land returns. `ProtectPiecesRadius` (0) adds a radius around every
player-built piece and tombstone for those who want roads and fields kept whole; paved and
cultivated paint is kept anyway (`KeepPaved`, `KeepCultivated`), only its height returns. The log
lists the protecting kinds and their radii at start.

### Time

All times are **real hours** (wall clock, UTC). They keep counting while nobody is online and
while the server is stopped. The world clock would not work here: on a dedicated server it
only runs while a player is online.

The plugin cannot know what happened before it was installed. Both clocks start at its first
run on a world, so nothing regrows until `IdleHours` and `AfterHours` have passed since then.

After a pass that regrew anything the world is saved right away (the same call as the autosave), so
a crash cannot lose the regrowth while keeping the reset clocks.

What it remembers is kept in `<world>.regrowth.txt` next to the world save. That file records
when players were last near each zone, and since when each object has been depleted.

Objects are identified by prefab and position, because the game hands out new object ids on
every world load.

### Limitations

- On a server where players own the objects around them (no server-side simulation), a spawner reset may
  not reach a player who stays connected the whole time and already had that area loaded in the same
  session. The game only sends a link, never its removal, so that player's game still thinks the creature
  exists. The creature appears once they reconnect. With Dedicated Simulation the server owns these
  objects, so this does not happen there.
- A core stand or chest that some machine currently has loaded is left for a later pass.
- Location objects: the first scan after a start reads the layouts of the locations and village
  buildings the world has (a few hundred prefabs, loaded in the background through the game's own
  loader: measured about 0.3 s of work over 45 s, no frame over 100 ms); later scans only read new ones. A location
  object's own zone must not be loaded, like everything else.

## Install

Copy `SarkasticGG_Resource_Regrowth.dll` to `BepInEx/plugins/` on the server and start it once.
That creates `BepInEx/config/sarkasticeu.resource_regrowth.cfg`.

It starts in **dry run**: it only logs what it would regrow. Watch the log for a few passes, then
set `DryRun = false`. The config file is re-read before every pass, so `DryRun`, the delays and
the logging amount change without a restart; `Enabled`, the prefab lists and the exclusions are
applied at startup.

## Configuration

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | Enabled | true | |
| General | DryRun | true | only log, change nothing |
| General | CheckIntervalMinutes | 10 | real minutes between scans |
| General | LogDetailsPerPass | 20 | objects logged individually per category and scan |
| Safety | IdleHours | 5 | real hours since a player was near the zone |
| Safety | PresenceRadiusZones | 2 | a player counts as near all zones (64 m) within this many of their own |
| Safety | PlayerBuildRadius | 48 | metres around player-built pieces and tombstones that are never touched |
| Locations | Together | true | a location regrows as a whole: once one thing in it is due, every core, creature and chest in it comes back |
| SurtlingCores | Enabled / AfterHours | true / 10 | |
| SurtlingCores | Prefabs | Pickable_SurtlingCoreStand | hide-when-picked, non-respawning pickables to restore |
| TreasureChests | Enabled / AfterHours | true / 24 | hours after a player was first near the chest (or it was seen empty) since it was last restocked |
| TreasureChests | ExcludePrefabs | | chest prefabs never to refill, e.g. `TreasureChest_meadows_buried` |
| DungeonMonsters | Enabled / AfterHours | true / 24 | |
| DungeonMonsters | ExcludePrefabs | | spawner prefabs never to reset |
| LocationObjects | Enabled / AfterHours | false / 48 | put back destroyed objects of locations and villages (needs a restart) |
| LocationObjects | Prefabs | Beehive | which ones; only world-generated objects are ever put back |
| Zones | Enabled | false | generate visited zones again as the world first made them (needs a restart) |
| Zones | DryRun | true | only log which zones would be generated again |
| Zones | IdleHours | 72 | real hours with nobody near before a zone is generated again |
| Zones | PerPass | 8 | most zones per pass |
| Zones | GraceMinutes | 30 | minutes after a start before any zone is generated again |
| Zones | ProtectedLocations | StartTemple, boss altars, traders, Hildir's dungeons | never generated again (also every location placed only once) |

## Build

Requires the .NET SDK and a Valheim dedicated server with BepInEx installed (for the reference assemblies).
Point `VALHEIM_DEDI_INSTALL` at it, or pass `-p:ValheimServerDir=...`:

```
VALHEIM_DEDI_INSTALL=/path/to/valheim_server dotnet build src/ResourceRegrowth.csproj -c Release
```

The DLL ends up in `bin/Release/`.
