# Sarkastic.gg Resource Regrowth

![version: 0.9.0](https://img.shields.io/badge/version-0.9.0-3b6e8f?style=flat-square)  ![players: vanilla clients](https://img.shields.io/badge/players-vanilla%20clients-4f7a4a?style=flat-square)  ![Valheim: 1.0](https://img.shields.io/badge/Valheim-1.0-7a5c2e?style=flat-square)  ![BepInEx: 5.4.2351](https://img.shields.io/badge/BepInEx-5.4.2351-5b5f66?style=flat-square)  ![license: MIT](https://img.shields.io/badge/license-MIT-5b5f66?style=flat-square)

[Source code](https://github.com/MistrCech/valheim-resource-regrowth)  ·  [Report a bug](https://github.com/MistrCech/valheim-resource-regrowth/issues/new)  ·  [Changelog](https://github.com/MistrCech/valheim-resource-regrowth/blob/main/CHANGELOG.md)  ·  [Dedicated Simulation](https://thunderstore.io/c/valheim/p/Sarkastic_gg/Dedicated_Simulation/)

[![Join us on Discord](https://img.shields.io/badge/Join_us_on-Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.sarkastic.gg)  [![Support us on Patreon](https://img.shields.io/badge/Support_us_on-Patreon-FF424D?style=for-the-badge&logo=patreon&logoColor=white)](https://www.patreon.com/sarkasticgamingservers)

A server-side [BepInEx](https://github.com/BepInEx/BepInEx) plugin for Valheim 1.0 dedicated servers.
It brings one-time world content back once an area has been left alone for a while:

- **Surtling core stands** get their core back.
- **Treasure chests** (never player-built ones) are restocked with fresh loot: what was left of the old loot is replaced, and what a player left in them stays, the loot taking the free slots. So are the other containers the world comes with a loot table of their own for: a shipwreck's chest, the granaries and timber halls of the Deep North, the ancient chests of Morkhalla.
- **One-time creature spawners** (dungeons, camps, caves) spawn their creature again.
- A **location regrows as a whole**: a dungeon with its entrance, a camp, a village or a ruin comes
  back all at once -- every core, every creature, every chest and every location object in it, and the doors close.
- **Terrain** that players dug, raised, levelled or paved slowly returns to the land's own shape,
  except around player bases and wards.
- **Location objects** (off by default): destroyed objects a location or village generated -- wild
  **beehives** in abandoned houses, for example -- are put back exactly where they stood. A muddy scrap
  pile in a sunken crypt (`mudpile2`, add it to `Prefabs`) is one too: the iron comes back with the
  crypt's chests and creatures, and what is left of the pile that was hit goes with it. So are the jewels
  and coins lying on the ground in crypts and caves (see What lies on the ground).
- **Zones** (off by default): a zone players have been in is **generated again by the game itself**
  once nobody has been near it for three days -- trees, rocks and ore deposits, bushes, pickables, the
  location with its buildings, chests, creatures and dungeon, and the ground -- as if no player had
  ever been there. Never at a base (a workbench); players' things stay as they are.

Players need nothing: vanilla clients work. The plugin depends on BepInEx and nothing else. It
patches two game methods, each only with its part switched on (Zones; Location objects).

**Recommended with [Dedicated Simulation](https://thunderstore.io/c/valheim/p/Sarkastic_gg/Dedicated_Simulation/)** (`Sarkastic_gg/Dedicated_Simulation`, [GitHub](https://github.com/MistrCech/valheim-serverside)), a fork of Serverside Simulations by ddormer that keeps the world simulated on the server. This plugin works on its own, but the two are made to run together: the zone reset generates a zone the way Dedicated Simulation does.

## How it works

Everything handled here stays in the world when it is used up, so regrowing it only changes
the object's data. The plugin deletes and creates no objects.

| What | Depleted when | Regrown by |
|---|---|---|
| Surtling core stand | the core was taken (`picked`) | setting `picked` back to false |
| Treasure chest | vanilla filled it once, and it is now empty or a player has been near it since it was last restocked | replacing what is left of its loot with fresh loot from the chest's own loot table, as vanilla fills it when the chest is first generated; what has been in a player's bag stays in its slot |
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
An object is put back only while something breakable the location built around it still stands (within 3 m, at
most 0.5 m above it), so nothing floats in the air: a hive hangs under the roof of its house, and when players
have pulled the house down, it is not put back. One of them standing is enough, and the same test (WearNTear)
picks them from the layout and finds them in the world, so a dungeon's bench or training dummy beside a fire pit
counts too. Objects of the list itself do not -- two fire pits side by side, both broken, would wait for each
other. An object that waits is logged once, with what held it:
`Not putting back Morkhalla_firepit of morkhalla_endcap01 at -1352,5001,8263: what held it is gone (Morkhalla_Trainingdummy1 at -1351.0,5001.5,8260.8).`

More objects of the list on one spot (within 5 cm) than the location's layout has there are put right: the extra
ones go -- one not picked stays -- where putting back could happen too. Up to 0.8.5 a pickable that is also in
`[SurtlingCores] Prefabs` (`hanging_hairstrands`) never counted as standing, so every place of it got one more each
time its hours were up: on the live world of 17 October 2026, 513 of them stood on 149 spots, up to six on one.

A pile or rock that is hit does not vanish at once: the game replaces it with its fractured remains
(`<name>_frac`), which are what is mined. So when an object is put back, the remains of it within 1.5 m
go (`ClearRemains`), or it would stand twice. Checked on a live world copy: a sunken crypt had 301 pile
places, the replay said 174 had a pile, 162 stood whole, 12 were gone -- and there were exactly 12
fractured remains; 11 were put back at their own places (the twelfth had lost a gate that held it) and
11 remains went, none of the new piles on another. A base on the surface does not hold back what comes
back inside a dungeon (5000 m up), nor does a tombstone down there hold back the surface above it.

Whether a player took everything from a chest, some of it or nothing cannot be told from the chest,
so a chest counts as used once a player has been near it, or with nothing left in it but what a player put there;
when its time comes its loot is replaced, partly looted or not. What a player left in it (an item remembers having been in a bag) stays where it is, and
the new loot goes in the slots that are free, never onto their stack: a stone left in a crypt's chest neither keeps
the chest from being restocked nor is lost, and a chest full of a player's things stays as it is. What a player puts on a
stack of the loot (dragged onto it, or with "place stacks") stays the world's in the game; a stack holding more than the
loot can roll (at the world's resource rate) counts as theirs, and a few put on one, up to that, go with the loot.
`[TreasureChests] KeepPlayersItems = false` replaces those too.

**A location regrows as a whole** (`[Locations] Together`). Every object belongs to the location the
world generated it with: inside a dungeon (5000 m up) to the location of its zone, as the rooms of a
dungeon lie within the zone of the generator that built them and a cave comes whole with its location,
so a burial chamber's interior and its entrance are one; on the surface to the location whose ground it
stands on, or the camp or village whose room it stands by. Each used-up or missing object keeps its own
clock, and as soon as one of them has waited its category's `AfterHours`, everything used up or missing in
the location comes back together: the cores, the creatures, the location objects, and every chest in it
restocked. The doors in it that a player can close by hand close (not those with a key, and not those
that stay open once opened, which nobody could open again). A burial chamber whose cores were taken 10
hours ago returns whole, chests, draugr and scrap piles included. With `Together = false` each object
regrows on its own.

Every `CheckIntervalMinutes` the plugin scans the world. A location (or a lone object) regrows only when all of these hold:

- one of its objects has been used up or missing for its category's `AfterHours`;
- no player has been near any of it for `IdleHours`;
- none of it lies within `PlayerBuildRadius` of a player-built piece or tombstone;
- none of it is in use: no connected player owns it, and the server does not have it loaded.
  Dedicated Simulation, for example, loads the zones around players on the server.

Whatever holds a location back holds all of it: nothing comes back while the rest does not, and the log says
what holds it, once a run (`Held:`). Only a dungeon has two sides. A build by its entrance on the surface, a
portal or a ladder, keeps back what is on the surface there, which then has no say in when the interior comes back;
the interior comes back whole. A build or a tombstone inside holds everything. A piece that fell after being put back
and an object whose holder is gone stay away by themselves: they neither make the location due nor hold it back.

The decisions run over frames, as the scan does: on a world of 864,000 objects and a Ryzen 9 7900X3D a scan takes
570-830 ms of work over about 320 frames, the worst of them 9-23 ms (35-40 ms in the first pass after a start, which also
reads the layouts). The same decisions taken in one frame cost 430-500 ms on that world. On a live server, a Windows VM on
an EPYC host, the same world takes about three times as long: 2.0-2.1 s of work over 530 frames, the worst 23-40 ms.
A location's layout is read in one frame: listing the
Ashlands ruins (`Charred*`) makes that frame 137 ms, as CharredRuins1 has 6 places on that world.

### Boss items

What bosses are summoned with must not run out on a world that is played for years. Most of it comes from
creatures, which keep spawning (deer trophies, ancient seeds, Seeker brute trophies), or comes back by itself (a
dragon egg, 8 hours after it was taken); withered bones come from the scrap piles of sunken crypts (`mudpile2`,
above) and their chests. What the world placed only once comes back with these entries (Valheim 1.0.20):

| For | Comes from | Entry |
|---|---|---|
| Yagluth: 3 Fuling totems (`GoblinTotem`) | the totem on a pole in Fuling villages and stone towers (`goblin_totempole`); Fuling berserkers drop one now and then | `[SurtlingCores] Prefabs`: `goblin_totempole:24` -- taken from its pole, it is back with the village's creatures and chests |
| Fader: 3 bells, made of fragments (`BellFragment`) | the altars of a Charred fortress (`Charred_altar_bellfragment`), the only source | `[LocationObjects] Prefabs`: `Charred_altar_bellfragment` |
| the Seeker Queen's door: the key, made of fragments (`DvergrKeyFragment`) | the crystal altars of infested mines (`blackmarble_altar_crystal`) | `[LocationObjects] Prefabs`: `blackmarble_altar_crystal` |
| the memorial (`NorthMemorialPlace`): 3 memorial coal (`MemorialCoal`) | coal piles and fire pits in the Morkhalla dungeons of `MorkBorg` (`Morkhalla_coal_pile_memorial`, `Morkhalla_firepit`); `JotunWarrior` drops one now and then | `[LocationObjects] Prefabs`: both |

Hateful blood (`HatefulBlood`, three for `FrozenKing`) comes only from `BlackIce_Core`, which only `FimbulLocation01`
has, and the game does not generate that location in 1.0.20; there is nothing to regrow yet.

### For players who come later

On a world that new players keep joining, what the game placed once and what only the zone reset brings back is not
enough where the zone reset does not go: places the game makes only once (`PlaceofMystery`, traders, the
Ancient upgrade station), Hildir's dungeons and boss arenas (`[Zones] ProtectedLocations`), and anything within 48 m
of a workbench. Valheim 1.0.20 has these, scarce and needed, that no hourly list covers by default:

| What | Where | Entry |
|---|---|---|
| Dyrnwyn: blade and tip fragments (`DyrnwynBladeFragment`, `DyrnwynTipFragment`) | the three `PlaceofMystery` places of the Ashlands, placed once per world (the hilt, `Pickable_Swordpiece1`, is put by no place or room the scan of 1.0.20 found) | `[LocationObjects] Prefabs`: `Pickable_Swordpiece1,Pickable_Swordpiece2,Pickable_Swordpiece3` |
| Molten cores (`MoltenCore`) | the stands of `PlaceofMystery`, `CharredFortress` and `MorgenHole` (picked, they vanish) | `[LocationObjects] Prefabs`: `Pickable_MoltenCoreStand` |
| Black cores (`BlackCore`) | the stands in the Dvergr towns under the Mistlands | `[SurtlingCores] Prefabs`: `Pickable_BlackCoreStand` (they stay in place when taken, like surtling cores) |
| Frost cores (`FrostCore`) | the hangers in the Hole of the Deep North | `[SurtlingCores] Prefabs`: `Pickable_FrostCoreHanger` |
| Tar (`Tar`) | the blobs of tar pits; picked, they vanish and vanilla never puts them back (the creatures of a pit come back with `[DungeonMonsters]`) | `[LocationObjects] Prefabs`: `Pickable_Tar,Pickable_TarBig` |
| Black Metal and Black Marble idols (`Upgrader4*`, `Upgrader5*`, the tokens of the Ancient upgrade station) in Hildir's Sealed Tower | the trash piles of the dungeon (`goblin_trashpile`), which the zone reset skips | `[LocationObjects] Prefabs`: `goblin_trashpile` |

The Morkhalla eyes (`Morkhalla_Eye1` to `Morkhalla_Eye7_gemstone`: gemstones, hundreds in one hall) are no entry
here: listed in `[SurtlingCores]` they would bring a gem hoard back on every visit.

### What lies on the ground

The jewels, coins and bones lying about in crypts and caves are objects the world placed once, and nothing in the game
brings them back. Listed in `[LocationObjects] Prefabs` they come back with the rest of their location, each rolling
anew what it is, as it does when the game first places it: a ruby this time can be amber the next.

| Lies in | Prefab | What it can be |
|---|---|---|
| burial chambers, Hildir's crypt, troll caves | `Pickable_ForestCryptRandom` | jewels and coins |
| sunken crypts | `Pickable_SunkenCryptRandom` | jewels, coins, withered bones |
| mountain caves, Hildir's cave | `Pickable_MountainCaveRandom`, `Pickable_Item` | jewellery and wolf parts |
| dolmens, `ShipSetting01` | `Pickable_DolmenTreasure` | a small treasure |
| burial chambers, caves, ruins, the Deep North | `Pickable_ForestCryptRemains01` to `04` | bone fragments |
| mountain caves, `MorgenHole` | `Pickable_MeatPile` | entrails |
| mountain caves with a lake | `Pickable_Fishingrod` | a fishing rod |

Stones, flint and branches lying on the land belong to the land, not to a location: those come back with `[Zones]`, and
not in a zone it protects.

### Copper deposits

A copper deposit is an iceberg. The game puts it 2 m under the ground, and its 130 pieces reach 13 m down from there and only
4 m up, with one copper ore in a piece on average. On a world with 427 untouched deposits, all of them stood at that height
within 4 cm, and how many pieces reached over the ground went by the lie of the land:

| `RaiseMeters` | Pieces over the ground, median | The worst deposit | Rock on level ground |
|---|---|---|---|
| 0 (the game) | 46 | 18 | 2 m tall |
| 2 | 69 | 46 | 4 m |
| 4 | 88 | 62 | 6 m |
| 6 | 104 | 78 | 8 m, its underside showing |

The rest has to be dug for, and a pit goes 8 m down at most. Raised, the deposits of zones still to be generated stand higher,
and so do those standing, once, within a pass of the setting changing. One a player has broken open stays where it is, with
their pit around it, and so does one within 20 m of a player's building: the rock is 28 m across and must not come up through a
floor. Players need nothing; a client reads the height from the object, so one standing by a deposit as it moves sees it
where it was until the zone loads again (change the setting while the server is down to avoid that). The world is saved as soon
as deposits move. The ore in a deposit stays the same.

### Buildings of a camp or village

The walls, poles, roofs and stairs of a Fuling village, camp or stone tower are location objects too. Listed in
`Prefabs`, a building players pulled down comes back after the section's `AfterHours` (48 by default; 24 brings it back
with the place's creatures and chests) instead of only when `[Zones]` generates its zone again after three idle days --
and in a protected location too, which the zone reset never generates again. The Fuling pieces of Valheim 1.0.20:

`goblin_woodwall_1m,goblin_woodwall_2m,goblin_woodwall_2m_ribs,goblin_pole,goblin_pole_small,goblin_roof_45d,goblin_roof_45d_corner,goblin_roof_cap,goblin_stairs,goblin_stepladder,goblin_fence,goblin_banner,goblin_bed,stone_wall_2x1,wood_wall_log,wood_stepladder`

A piece (anything of the list the game treats as breakable, `WearNTear`: walls, but the cloth hangings and braziers of
the default list too) counts as standing only on its own place: within 10 cm, at the same height -- one the game
settles on the ground after placing it (`SnapToGround`, the log benches) at any height. So of two walls stacked one on
the other, the lower one no longer stands in for the upper one.

Pieces of the list do not hold each other up for the rule above, so all of a building's missing pieces come back in the
same pass, and the game's own support test decides what stands: once a player comes near, a piece with nothing under it
falls, as a player's would. Such a piece is not put back again -- it would come back every `AfterHours` and fall at every
visit, its wood dropped each time -- until one the game made stands in its place once more, which in practice is when
`[Zones]` generates its zone again; from then on it is put back as before. Both are logged:

`goblin_banner at -5053,41,1197, put back 0.07 h ago, fell for lack of support: not put back again before something stands in its place once more (its zone generated again).`

`goblin_banner of gobvill_hut03 at -5053,41,1197, which fell after being put back, stands in its place again; it is put back as before from now on.`

The fall is seen only where the server runs the piece -- with Dedicated Simulation, which simulates what is near
players; without it a player's game runs the fall, and such a piece comes back every `AfterHours`. Where its zone is
never generated again (a protected location, `[Zones]` off), a piece that fell stays out. So does one that fell
because players broke what held it, until the zone is generated again, even once what held it is back. A piece put
back before 0.8.9 carries no sign of it: if it falls, it comes back once more and is told the next time. The state
file keeps these as `fell` lines, also while `[LocationObjects]` is off.

Tested on a copy of the live world (23 October 2026) with these 16 prefabs added to the default list and the clocks
run down: of 272 missing pieces 185 were put back (stone towers, villages, camps), 83 waited near players' builds and 3
for a totem pole that had held them. Each of the 177 in the 10 zones then generated again by the game itself stood where
the game puts its own, to the millimetre, and none beside one standing. With a simulated player near a village for two
minutes, one banner fell, was not put back again, and stood again when `[Zones]` generated the village.

### Whole locations

`[LocationObjects] Locations` takes location names instead of prefabs: every breakable part of them comes back with the
rest of the location, without listing it: building pieces, treasure chests, props, torches, roots, rocks and nests, also in
the rooms of their dungeon or village. A chest that was broken comes back with the rest, filled (not one in
`[TreasureChests] ExcludePrefabs`, which the game would fill). The doors in
the doorways of a dungeon, which its generator draws as it goes and no layout knows, come back too once the plugin has seen them go; one that went
before 0.9.0 waits for `[Zones]`. `*` matches any run of characters (`Crypt*`, `Mistlands_*`). `LocationsExclude` names prefabs this never puts back.
Creature spawners and what is picked have their own entries, and what comes and goes by itself (a bird, a fire that burns
out) is never put back.

### Zones

Vanilla never brings back a felled tree, a mined copper or tin deposit, a stone tower or a house
players pulled down. With `[Zones] Enabled`, a zone (64 m square) that a player has been in or next to
is, once nobody has been near it (`PresenceRadiusZones`) for `IdleHours` (72), generated again by the
game's own zone generation, the way Dedicated Simulation generates the zones around players
(ZoneSystem.SpawnZone, ghost mode). Every object the game makes takes the place of the old one of its
kind standing there (the nearest, so of a stack of walls each takes its own), which goes; what is left
over of what only the zone itself makes -- vegetation,
stumps and logs, wild creatures, items lying about, the record of what was done to the ground -- goes
too. So trees, rocks and ore deposits, bushes, pickables, the location with its buildings, chests,
spawners and dungeon come back. Measured on a copy of a live world against a fresh world with the same
seed: 860 of 916 zones without a location came out exactly as first generated, the rest with a few
trees or rocks elsewhere (the game decides placement partly by what colliders are around at that
moment).

A location comes back the way round it was: the game draws its rotation from an unseeded random
number when it first places it, so the rotation is read from the location's own record before the
zone is cleared and given back when it is placed again (a patch of `ZoneSystem.SpawnLocation`). With
the same rotation its dungeon gets the same seed and the
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
(`ProtectMultiplier` scales it), except that a **workbench** keeps the ground within `WorkbenchRadius`
(48 m, the distance that keeps a zone from being generated again), because a workbench is what a base is
built around and its own radius is only 10 m: a moat or a levelled yard dug around a house lies mostly
outside that. A base that is gone, say a workbench broken by creatures, protects
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
when players were last near each zone, since when each object has been depleted, and which pieces it put
back fell.

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
set `DryRun = false`. The config file is re-read before every pass, so `DryRun`, the delays, the
logging amount, `[SurtlingCores] Prefabs`, the exclusions and `[Zones] ProtectedLocations` change
without a restart (a list read again is said in the log); `Enabled`, the switches of the parts and
`[LocationObjects] Prefabs` are applied at startup.

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
| Locations | Together | true | a location regrows as a whole: once one thing in it is due, every core, creature, chest and location object in it comes back |
| SurtlingCores | Enabled / AfterHours | true / 10 | |
| SurtlingCores | Prefabs | Pickable_SurtlingCoreStand | hide-when-picked, non-respawning pickables to restore; `name:hours` gives one its own hours instead of AfterHours (`goblin_totempole:24`, the Fuling totem) |
| TreasureChests | Enabled / AfterHours | true / 24 | hours after a player was first near the chest (or it was seen empty) since it was last restocked |
| TreasureChests | ExcludePrefabs | | chest prefabs never to refill, e.g. `TreasureChest_meadows_buried` or `shipwreck_karve_chest` |
| TreasureChests | KeepPlayersItems | true | what a player left in a chest stays, and the loot takes the free slots; false: it is replaced with the rest, and gone |
| Terrain | Enabled / AfterHours / StepHours | true / 48 / 24 | what players dug or paved is first stepped back 48 h after it was first seen, once nobody has been near for `Safety IdleHours`, and then at most once every 24 h |
| Terrain | Divider / MinDelta | 1.7 / 0.2 | a step divides every height change outside a protected area by this and drops it once below this many metres |
| Terrain | ProtectMultiplier | 1 | the protected radius of a base object or ward is its own times this |
| Terrain | WorkbenchRadius | 48 | metres around a player's workbench that are never stepped back (its own radius is 10); 0 = only its own |
| Terrain | ProtectPiecesRadius | 0 | also protect this many metres around any player-built piece or tombstone |
| Terrain | KeepPaved / KeepCultivated | true / true | paved and cultivated ground stays so; only its height returns |
| DungeonMonsters | Enabled / AfterHours | true / 24 | |
| DungeonMonsters | ExcludePrefabs | | spawner prefabs never to reset |
| LocationObjects | Enabled / AfterHours | false / 48 | put back destroyed objects of locations and villages (needs a restart) |
| LocationObjects | Prefabs | Beehive | which ones; only world-generated objects are ever put back (boss items: see Boss items; jewels and coins: see What lies on the ground; walls and roofs: see Buildings of a camp or village) |
| LocationObjects | Locations / LocationsExclude | | locations whose every breakable part is put back, `*` matching any run of characters; prefabs that never are (see Whole locations; needs a restart) |
| CopperDeposits | RaiseMeters | 0 | metres higher than the game puts copper deposits, up to 8 (see Copper deposits); back to 0 puts them back |
| Zones | Enabled | false | generate visited zones again as the world first made them (needs a restart) |
| Zones | DryRun | true | only log which zones would be generated again |
| Zones | IdleHours | 72 | real hours with nobody near before a zone is generated again |
| Zones | PerPass | 8 | most zones per pass |
| Zones | GraceMinutes | 30 | minutes after a start before any zone is generated again |
| Zones | RefreshNow | false | one-off, switches itself off in the config: in the first pass after a start, generate again every zone nobody has been near for the last 30 minutes (no IdleHours, no GraceMinutes, no PerPass) and bring the ground of every modified zone back at once, paint too; what a workbench or a ward protects stays |
| Zones | ProtectedLocations | StartTemple, boss altars, traders, Hildir's dungeons | never generated again (also every location placed only once) |

## Build

Requires the .NET SDK and a Valheim dedicated server with BepInEx installed (for the reference assemblies).
Point `VALHEIM_DEDI_INSTALL` at it, or pass `-p:ValheimServerDir=...`:

```
VALHEIM_DEDI_INSTALL=/path/to/valheim_server dotnet build src/ResourceRegrowth.csproj -c Release
```

The DLL ends up in `bin/Release/`.

## License

MIT, see [LICENSE](https://github.com/MistrCech/valheim-resource-regrowth/blob/main/LICENSE). Copyright (c) 2026 MistrCech / Sarkastic.gg.
