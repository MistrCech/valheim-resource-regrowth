# Changelog

## [0.8.8] - 2026-10-18

### Fixed

- Terrain regrowth stepped a zone again at once after the players had left it, however short a time ago the last step was:
  the time of the last step was dropped from the state in every pass that found the zone not idle (a player within
  `PresenceRadiusZones`), so the first pass after they left found no last step and stepped again, instead of waiting
  `StepHours` (24). A zone a player kept coming back to (a base) was eroded once for each time they went away for the
  idle hours, not once a day, and no step was written down for it. Found on the live world: the unprotected part of a
  player's moat (a base on the start island) had lost two steps in twenty hours -- every height change outside the
  workbenches' circles divided by 1.7 twice, those under 0.34 m gone -- and the state file held no step for those zones.
  Tested on a copy of the live world with the clocks run down (AfterHours 0.01, StepHours 0.5), one fake player visiting
  a zone between two passes: the old build stepped three zones (the visited one and two within `PresenceRadiusZones`) a second time
  within six minutes, the fixed one none of the 103 zones twice.

### Added

- `[Terrain] WorkbenchRadius` (48): ground within this many metres of a player's workbench is left as it is, whatever the
  workbench's own radius (10 m for `piece_workbench`) -- the distance that keeps a zone from being generated again
  (`[Safety] PlayerBuildRadius`). A base is built around its workbench and a moat, a levelled yard or a ramp dug around it
  mostly lies outside 10 m. Read at every pass, 0 gives the old behaviour. On the live world 4254 of 17300 modified vertices
  were open (not under any base object, 24.6 %), and the radius protects 41 % of those; the 386 open ones of the base
  above were all within 48 m of a workbench. Tested on a copy of that world: of its six zones none was stepped any more
  (before, 51 of 1484 height changes of the biggest went in one step) and the number of zones of the world with everything
  protected rose from 25 to 49.

## [0.8.7] - 2026-10-18

### Changed

- `[TreasureChests]` restocks every container of the world that comes with a loot table of its own, not only the ones named
  `TreasureChest_*`: a shipwreck's chest (`shipwreck_karve_chest`, `shipwreck_vikingship_chest`), the granaries and timber halls
  of the Deep North (`loot_deepNorth_Granary`, `loot_deepNorth_TimberHall`) and the ancient chests of Morkhalla
  (`Morkhalla_ChestAncient`): 32 kinds instead of 27. Player-built chests have no loot table and stay out; `ExcludePrefabs`
  keeps any prefab out. (A `Piece` component is no sign of a built chest: the treasure chests have one too, which a first
  version of this change took for one and the test caught: 2 kinds instead of 32.) Tested on a copy of the live world: three
  emptied wreck chests, two meadow chests and two sunken crypt chests were each filled again from their own loot table.

### Added

- README, "For players who come later": what the game puts once and neither vanilla nor the zone reset brings back where
  new players need it -- the Dyrnwyn fragments of the three `PlaceofMystery` places of the Ashlands (placed once, so
  the zone reset never touches them), molten cores, black cores (`Pickable_BlackCoreStand`) and frost cores
  (`Pickable_FrostCoreHanger`), tar, and the idols of Hildir's Sealed Tower (`goblin_trashpile`) -- and the entries that
  bring them back (`[LocationObjects] Prefabs`, `[SurtlingCores] Prefabs`; no code, only config). Tested on a copy of the
  live world with those entries: 121 destroyed objects of those kinds (tar of a pit, molten core stands and Dyrnwyn
  fragments of `PlaceofMystery`, sulfur rocks, the trash piles of the Sealed Tower) were put back where they stood, no
  slot twice (tar floats, so it settles a little off its place; none of it was put back again), and the startup accepted the
  black core stand and the frost core hanger as pickables that stay in place when taken.

## [0.8.6] - 2026-10-17

### Added

- An entry of `[SurtlingCores] Prefabs` may carry its own hours after a colon instead of the section's `AfterHours`:
  `goblin_totempole:24` is the totem on a Fuling totem pole (villages, stone towers). Yagluth is summoned with three,
  and vanilla never puts one back on its pole; taken, it is back with the village's creatures and chests rather than
  sooner. The startup line lists each pickable with its hours. Tested on a copy of the live world, both ways round:
  three totems taken from poles in two Fuling villages and a stone tower came back while two surtling cores taken with
  them waited for their own `:24`, and with `goblin_totempole:24` the cores came back while the totems waited.
- README: "Boss items" -- what bosses are summoned with, where it comes from and which entries keep it from running out
  on a long-lived world: `goblin_totempole:24`, and in `[LocationObjects] Prefabs` `Charred_altar_bellfragment` (the
  altars of a Charred fortress, the only source of Fader's bell fragments), `blackmarble_altar_crystal` (the crystal
  altars of infested mines, the fragments of the Seeker Queen's key), `Morkhalla_coal_pile_memorial` and
  `Morkhalla_firepit` (memorial coal). Tested on the same copy: the bell altars broken in two Charred fortresses and
  six crystal altars in two infested mines were put back at their own places (0.00 m off), and so were all ten fire
  pits and coal piles broken in a Morkhalla room, three of them beside a bench or a training dummy (see Fixed).
- A location object that is not put back because what held it is gone is logged once, with what held it.

### Fixed

- `[LocationObjects]`: an object of the list that is also in `[SurtlingCores] Prefabs` (`hanging_hairstrands` on the
  live server) never counted as standing: the scan sorted it as a pickable only. Every place of it looked empty, and
  one more was put on it each time `AfterHours` was up -- on the live world of 17 October 2026 there were 513
  `hanging_hairstrands` on 149 spots, up to six on one. (`Pickable_MountainCaveCrystal` is in both lists there too,
  but it is not a non-respawning hide-when-picked pickable, so `[SurtlingCores]` ignores it: one stood on each of its
  255 spots.) What stands is now noted apart from what kind of thing it is, and more objects on one spot (within 5 cm)
  than the location's layout has there are put right: the extra ones go, one not picked stays, where putting back
  could happen too (not near a player's build, in a zone the server has not loaded). Tested on the live copy with the
  live settings: the first pass removed the 364 extra ones on 115 spots, one stayed on each of the 149 spots, and none
  was put back again in the four passes after; no other prefab's count changed except by what was put back.
- `[LocationObjects]`: two objects of the list could each count as what held the other up, so two fire pits side by
  side, both broken, waited for each other forever. Objects of the list hold nothing now.
- `[LocationObjects]`: what holds an object up was picked from the location's layout among everything breakable
  (WearNTear) but looked for in the world only among buildable pieces (Piece), so an object beside something
  breakable that is not a buildable piece -- a Morkhalla bench or training dummy beside a fire pit, a green ground
  torch beside a scrap pile in a sunken crypt -- waited for it forever, though it stood. One test does both now
  (`LocationObjects.Holds`). On the live copy 12 scrap piles of one crypt waited for such torches; 11 came back, and
  the twelfth still waits: no torch stands where its stood.

## [0.8.5] - 2026-10-13

### Added

- `[Zones] RefreshNow` (off): a one-off, for when the owner wants everything that nothing protects back at once. In the
  first pass after a start every visited zone nobody has been near for the last 30 minutes is generated again --
  without `IdleHours`, `GraceMinutes` and the `PerPass` limit -- and the ground of every modified zone is brought
  back at once, paint too, whatever `KeepPaved` and `KeepCultivated` say. What a workbench or a ward protects,
  protected locations and the 4 m around players' pieces stay as they are. The setting switches itself off in the
  config when the pass is done, so the usual clocks apply again; it needs `[Zones] Enabled` and no `DryRun`.

## [0.8.4] - 2026-10-12

### Changed

- Thunderstore package only: the plugin is licensed under the MIT licence (`LICENSE`, in the repository and in the
  package); the README says so. The plugin is the 0.8.0 DLL, unchanged.

## [0.8.3] - 2026-10-12

### Changed

- Thunderstore package only (`Sarkastic_gg/Resource_Regrowth`): the README has badges and links (Discord, Patreon)
  and the package is listed in the "Deep North Update" section. The plugin is the 0.8.0 DLL, unchanged.

## [0.8.2] - 2026-10-12

### Changed

- Thunderstore package only: the README and the description say that the plugin is recommended with
  `Sarkastic_gg/Dedicated_Simulation`. The plugin is the 0.8.0 DLL, unchanged.

## [0.8.1] - 2026-10-12

### Changed

- Thunderstore package only (`Sarkastic_gg/Resource_Regrowth`): a new pixel art icon. The plugin is the
  0.8.0 DLL, unchanged; a package's icon can only change with a new version number.

## [0.8.0] - 2026-10-11

### Added

- `[LocationObjects] ClearRemains` (on): when a wanted object is put back, its fractured remains
  (`<name>_frac`, what the game leaves when a pile or rock is hit) within 1.5 m go with it. With
  `Prefabs = Beehive,mudpile2` the iron scrap piles of sunken crypts come back where they stood, with the
  crypt's chests and creatures; without the cleanup the pile would stand twice (whole and as pieces to mine).

### Changed

- A player's building or tombstone holds back (`PlayerBuildRadius`) only what is on the same level: a base on
  the surface no longer holds back chests, creatures, cores and objects inside the dungeon below it
  (interiors lie 5000 m up), nor a tombstone inside a dungeon what comes back on the surface above it.
  Zones with a workbench are still never generated again.

## [0.7.2] - 2026-10-10

### Fixed

- `[Zones]`: a big rock, ore deposit or tree the game makes again no longer comes back over a player's
  piece standing where it was mined out or felled. Besides the 4 m around every piece (2 m for small
  things), an object whose colliders' bounds reach within 2 m of a piece now counts as beside it: the
  game's new copy goes and the old state stays. Measured in the game with rays: of 329 rocks around one
  mountain spot, 38 cover the ground farther than 4 m from where they stand, a mountain rock up to 23 m.
  Tested on a copy of a live world with players' things far from any workbench (a chest with items, a
  sign, a cart, a tombstone, a carried sword on the ground, a tamed wolf, a tagged portal, a chest put
  4.9 m from a mined-out mountain rock's centre, inside its collider's bounds): after the reset every one
  stood where it was with its data unchanged, the zone's ground record kept its bytes, and the rock 0.7.1
  brought back there stayed away.

## [0.7.1] - 2026-10-10

### Changed

- `[Zones]`: only a workbench (in the zone or within `PlayerBuildRadius`) keeps a zone as it is.
  Cultivated ground, paved ground and the trees players planted no longer do: away from a workbench they
  go with the reset like the rest of the ground. A zone with players' pieces in it (crops among them)
  still keeps its ground record.

## [0.7.0] - 2026-10-10

### Changed

- A treasure chest holding anything that has been in a player's bag is someone's storage and is never
  restocked (it used to be emptied and filled afresh like any chest a player had been near).

### Added

- `[Zones]` (off by default, `DryRun` on by default): a zone players have been in is generated again
  by the game itself once nobody has been near it for `IdleHours` (72): trees, rocks and ore deposits,
  bushes, pickables, the location with its buildings, chests, spawners and dungeon, and the ground --
  as if no player had ever been there. Vanilla never brings back a felled tree, a mined deposit or a
  house pulled down. In one frame the zone is marked not generated, its location not placed, and
  `ZoneSystem.SpawnZone` runs again in ghost mode; every object the game makes then replaces the old
  one of its prefab standing in its place (within half a metre), in the zone or over its edge, and
  what is left over of what only the zone itself makes (vegetation, stumps, logs, fractured rocks,
  wild creatures, birds, fish, items lying about, the ground's record) goes. A location gets back the
  rotation it had: the game draws it from an unseeded random number, so it is read from the
  location's proxy (rounded to the game's 22.5-degree step, as the save truncates Euler angles to half
  a degree) and given back by a prefix on `ZoneSystem.SpawnLocation` -- the plugin's first game patch,
  applied only with Zones switched on. Players' things are never deleted and stay where they are
  (pieces incl. portals, ships, carts and crops; tombstones; items a player has carried; tamed
  creatures; the world's chests, barrels and stands holding something of theirs -- the old one stays,
  the new copy goes); beside their pieces and tombstones nothing changes (4 m, 2 m for small things),
  and a zone with their pieces keeps its ground record. Never generated again: zones with a workbench
  in them or within `PlayerBuildRadius` (a base), cultivated ground or paved ground outside the
  location, zones where players planted saplings, protected locations (`ProtectedLocations`,
  plus every location the game places only once, and what of them reaches into a neighbour), zones
  loaded on the server or next to one that is, and the first `GraceMinutes` (30) after a start. A
  generation that fails or throws is undone: nothing old is deleted before the game has made the zone
  again. At its first run every zone generated so far counts as visited, and nothing is generated again
  before `IdleHours` from then; presence is now kept for the longer idle time (also with Zones off)
  and only moves forward (`presencesince` in the state); at most `PerPass` zones per pass, one per
  frame, a zone that cannot be done now waiting an hour.

Tested on a copy of the live world (9 October) with every zone due at once: 1156 of 1190 zones were
generated again, 0 exceptions, about 8 ms of work per zone; 34 were kept (19 at a workbench, 8 fields,
6 protected locations, 1 with players' saplings); all 663 objects of players' stayed exactly where they
were (prefab and position to the centimetre); all 263 locations came back with the same rotation and
position and all 27 dungeons with the same rooms (the first tries lost five crypts and a troll cave's
interior to guessed ownership and reused ZDO objects, and changed three dungeons' rooms through the
half-degree truncation -- all fixed before release); zones without a location matched a fresh world of
the same seed in 41 of 45 changed cases; objects players had removed came back (rocks, trees, obsidian
and copper deposits, beehives, bone piles). A dungeon generation made to throw was undone: the zone and
its neighbours stayed as they were and resets stopped. With the defaults, the first run counted all
1190 zones as visited and generated none (72 h to go). A review by three
independent readers found that the world's chests holding players' things, trees grown from players'
saplings, a failed generation (no undo), a trader reaching into a neighbouring zone, items the world
placed but nobody loaded yet (read as carried), presence forgotten while Zones were off, wasted
per-pass slots and switching Zones on without the patch were not handled; all fixed above.

## [0.6.0] - 2026-10-09

### Changed

- A location regrows as a whole (`[Locations] Together`, on by default). Players found that a
  burial chamber gave its surtling cores back after 10 hours while its chests and draugr stayed
  gone (24 hours, each on its own clock). Every object now belongs to its location -- inside a dungeon
  to the dungeon its generator built and the location above it, so interior and entrance are one; a
  troll or bear cave by its interior radius; on the surface the location it stands in -- and as soon
  as one object's clock has run its category's `AfterHours`, everything used up in the location comes
  back at once and every chest in it is restocked. The safety conditions apply to the whole
  location: nobody near any of it for `IdleHours`, none of it near a player's build, none of it loaded.
- Treasure chests are restocked whether they were emptied, partly looted or not touched: whatever is
  left in them is replaced by fresh loot from their own table. A chest left with a few feathers
  never came back before. What a player took cannot be told from a chest, so a chest's clock starts
  when a player is first near it after it was last restocked (or when it is seen empty). Player-built
  chests are never touched, as before.
- Dry runs also list the locations still waiting, with what is used up in them and when they are due.

Tested on a copy of the live world (9 October, 19:21) with the live state: the 37 locations with something used up grouped
correctly -- burial chambers with their cores, chests and skeletons inside, troll caves, ruins, farms,
houses -- and with every wait set to zero 24 locations came back whole: 14 cores, 45 spawners and 33
chests, 14 of them empty and 19 with leftovers, all with fresh loot; the 3 empty chests within 48 m of
a player's build stayed empty.

## [0.5.1] - 2026-10-07

### Changed

- Renamed to Sarkastic.gg Resource Regrowth: the community moved from sarkastic.eu to sarkastic.gg.
  The DLL is now `SarkasticGG_Resource_Regrowth.dll`; delete `SarkasticEU_Resource_Regrowth.dll`
  from `BepInEx/plugins` when upgrading. The plugin GUID, the config file
  `sarkasticeu.resource_regrowth.cfg` and the state file are unchanged, and nothing else is.

## [0.5.0] - 2026-09-28

### Added

- `[LocationObjects]` (off by default): destroyed objects that a location or a village generated
  are put back where they stood -- `Prefabs` = `Beehive`, the wild beehives of abandoned houses, bear
  caves, ruined towers and meadow villages. Unlike the other categories this creates objects. The
  place is exact: every generated location leaves a proxy with its position, rotation and seed, and
  every village or camp generator keeps its buildings' positions and rotations; objects that spawn
  by chance are decided by replaying the game's roll from that seed. Checked on a copy of the live
  world: 169 hive places, 47 of them with a hive by the replay, all 29 standing hives on those
  places (0.00 m apart in height too) and none elsewhere. Of the 18 missing, 14 would be put back,
  3 lie near player builds and 1 has lost the house that held it -- such a hive is not put back, so
  nothing hangs in the air (pulling down another house for the test kept that one out too). The
  put-back hives survived a
  restart and the server instantiated them where the originals hang, 4.08 m above the floor under
  the roof. Same rules as the rest: `AfterHours` (48) since first seen missing, `IdleHours`,
  `PlayerBuildRadius`, and the zone must not be loaded.
- The first scan after a start reads the layouts of the world's locations and village buildings
  through the game's own asynchronous loader: about 0.3 s of work over 45 s, no frame over 100 ms
  (loading them directly cost 9.9 s and frames up to 440 ms). Later scans only read new ones.

## [0.4.0] - 2026-09-13

### Added

- `[Terrain]`: dug, raised, levelled and paved ground slowly returns to the land's own shape once
  the area has been left alone (`IdleHours`, then `AfterHours` after the zone's ground was first
  seen modified, then a step every `StepHours`). Each step divides the remaining height change
  by `Divider` and drops it below `MinDelta`; paint goes back to nothing once the height under it
  is back, paved and cultivated ground excepted. Ground around player base objects (the game's
  own PlayerBase areas: workbench, forge, beds, fires ...) and wards is left alone by the object's
  own radius, so a base that creatures broke protects nothing any more. The data is written in
  the game's format (`TerrainComp.Save`); verified on a local 1.0.12 server: all 38 modified
  zones of a copied live world round-trip byte-identically, and a step reduced e.g. 242 to 230
  modified vertices with 587 to 230 painted ones, 60 vertices near a base left untouched.
