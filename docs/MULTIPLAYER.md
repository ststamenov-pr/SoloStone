# Multiplayer (beta, in progress)

SoloStone is an unofficial offline single-player mod for Hearthstone®. It requires your own legally obtained Hearthstone client. Not affiliated with, endorsed by, or sponsored by Blizzard Entertainment. Hearthstone is a trademark or registered trademark of Blizzard Entertainment, Inc.

A dungeon run for 2 to 4 players on one local network. This page says what the mode is, what is built and
tested today, and what is still missing before it can be played inside the game.

> [!IMPORTANT]
> **Not playable in the game client yet.** The rules, the bosses, the lobby, the run and spectating are written and
> tested without the client (see [Status](#status)). The screens and the hook that lets the game client show
> these games are not written. Until they are, the only way to play a run is the terminal table below.

## What stays true

- **Local network only.** A lobby is one player's game listening on their own LAN address. The join code is that
  address and port in a friendlier alphabet (`K7QF2-M0A9X`), nothing else. The code refuses to listen on, connect
  to, or decode a code for anything but loopback, private ranges (10/8, 172.16/12, 192.168/16), link-local and
  the 100.64/10 block that private mesh VPNs use.
- **No matchmaking, no server of ours, no relay.** Nothing is hosted anywhere. Friends who are not on the same
  network need a private VPN between them; SoloStone does not provide one.
- **No shipped art.** Heroes, hero powers, bosses and treasures are named by the game's own card IDs, so each
  player's client shows its own art and text for them, exactly as it does for every other card in the mod.

## The run

1. **Draft.** Each player is offered three classes, picks one, picks one of that class's three hero powers, then
   builds a 15-card starter deck from the class and neutral cards.
2. **Boss.** Everyone fights the same boss, each in their own game against the AI. Win: choose one of three
   passive treasures. Win or lose: add one of three bundles of three cards to the deck.
3. **Duel.** Players face each other. Only duel wins score. At a table of three, one player sits out each round
   and spars with an AI "Echo" of a rival's build (no points; a win gives +2 health for the rest of the run).
4. Three stages of boss then duels.

| Stage | Hero health | Boss health | Boss clock | Duel clock | Turn | Duel win |
|---|---|---|---|---|---|---|
| 1 | 20 | 15 | 5:00 | 6:00 | 0:45 | 3 points |
| 2 | 25 | 30 | 6:00 | 8:00 | 1:00 | 4 points |
| 3 | 30 | 40 | 7:00 | 10:00 | 1:15 | 5 points |

Early games are short on purpose and later ones are longer and worth more. When a clock runs out, a duel goes to
the hero with more health and armor (equal: a draw, 1 point each) and a boss simply outlasts the challenger. A
turn clock that runs out ends the turn. The host can set the pace to quick (×0.75), standard or relaxed (×1.5).
Losing a duel gives +3 health for the rest of the run, so nobody is out of it early.

**Waiting and spectating.** A player who finishes a boss fight picks loot at once and then waits; the duels
start for everyone together. While waiting, a player can chat and spectate any game still running, boss fight or
duel. Their own next game starting ends the spectating.

**Leaving.** A player who drops mid-run is played by the AI so the others are not stranded. They cannot rejoin
that run and forfeit its reward.

### Content

| | Cards |
|---|---|
| Hero powers | Each class: its basic power and two more. Dalaran Heist powers for Druid, Hunter, Mage, Paladin, Priest, Rogue, Warlock and Warrior (`DALA_<Class>_HP1/HP2` where scripted); Unstable Explosion and Mystic Barrier fill the gaps (all of Shaman, Demon Hunter and Death Knight) |
| Bosses, stage 1 | Giant Rat, Wee Whelp, Elder Jari |
| Bosses, stage 2 | Pathmaker Hamm, Kraxx, Gutmook (passive: Digest Magic) |
| Bosses, stage 3 | Lava-Filled Chamber (The Floor is Lava), Candlebeard (Charrrrrge!), Pathmaker Hamm, Fungalmancer Flurgl |
| Treasures | Potion of Vitality, Crystal Gem, Small Backpacks, Khadgar's Scrying Orb, Robe of the Magi, Captured Flag, Glyph of Warding, Battle Totem |

The adventure scripts were removed from this engine long ago; the ones above were written for this mode in
`engine/SabberStoneCore/src/CardSets/Client/Multiplayer/DelveCards.cs`. Boss decks are random neutral minions up to
a cost cap per stage. None of this is balanced yet.

### Rewards

| For | Gold | XP |
|---|---|---|
| Finishing a run | 20 | 60 |
| Each boss beaten | 10 | 30 |
| Each duel won / drawn | 15 / 5 | 40 / 15 |
| Place 1st / 2nd / 3rd / 4th | 60 / 35 / 20 / 10 | 120 / 70 / 40 / 20 |

Titles: **Champion** (first place), **Bossbreaker** (all three bosses), **Untouchable** (won every duel). The host
computes the reward and sends it; crediting it to the save is part of the missing client work.

### Beta: full collection

`RunRules.BetaFullCollection` is on: the draft pool is every implemented collectible card of the class plus the
neutral ones, whatever the player's save owns. `RunCatalog.PoolFilter` can narrow the pool, for example to the
meta the installed mod plays.

## How it works

Every game is its setup (seed, heroes, decks, hero powers, passives) plus the ordered list of moves made in it.
The host keeps a copy of every game, decides the order of moves, plays the AI seats and enforces the clocks.
Players and spectators apply the same moves to their own copy of the rules engine and therefore see the same
game. A spectator who joins late gets the setup and the moves so far and catches up.

| File | What it is |
|---|---|
| `plugin/Multiplayer/LanAddress.cs` | The local-network rule and join codes |
| `plugin/Multiplayer/Wire.cs` | The line-based wire format and the connection |
| `plugin/Multiplayer/RunCatalog.cs` | Rules of a run, classes, bosses, treasures, deck validation |
| `plugin/Multiplayer/Duel.cs` | One game as every machine simulates it |
| `plugin/Multiplayer/TableHost.cs` | Lobby, chat and the run, on the hosting player's machine |
| `plugin/Multiplayer/TableClient.cs` | A player's view of the table, and the AI stand-in used by tests |

These files use the engine and plain .NET only (no Unity, no game types). They are the only game-side code that
opens a socket, so an ordinary plugin build leaves them out; `-p:Multiplayer=true` compiles them in (checked for
.NET Framework 4.8, the plugin's target).
Both sides are driven by calling `Pump()` from one thread; nothing calls back from a network thread.

Known limits: every machine holds the whole game state, hands included, because it has to simulate it, so this is
for friends, not for strangers. The opening mulligan is skipped. In about one game in two hundred the engine
cannot continue after a card's follow-up choice; the host then calls that game on health and says which card.

## Status

**Done and tested** (18 tests, real TCP on loopback, the real engine and card data):

- lobby: host, join by code, nicknames (cleaned, unique), ready, kick, pace, chat, four-seat limit;
- nothing outside the local network is listened on, connected to or accepted;
- draft, boss fights, loot, waiting, duels, byes, scoring, clocks, standings, rewards, for 2, 3 and 4 players;
- spectating, including joining a game late;
- a game replayed from its setup and moves ends in the same state on another copy;
- every boss, every drafted hero power and every treasure;
- a player who drops, and a player who never acts, do not hold the table up.

**Not done:**

1. **Screens in the game client**: lobby, chat, draft and deck builder, loot, waiting room, standings.
2. **Showing a game through the client.** `plugin/NativeMatch.cs` assumes one human in seat 1 and the AI in seat 2
   (`HumanPlayerId`, `BotPlayerId`). Boss fights fit that shape and need the least work: build the match from a
   `DuelSetup` and send the player's moves through the host. Duels need the local player in seat 2 on one of the
   two machines, and spectating needs a view with no local player; both are changes to the core of that file.
3. **Network permission.** The installer blocks every program in the game copy from the network and the plugin
   refuses to start if it finds a connection. LAN play needs a deliberate, local-subnet-only exception in both.
   Neither was touched.
4. **Crediting rewards** to the save, and the **mulligan**.
5. **Balance**, and rejoining a run after a drop.

None of the plugin's game-facing code was changed, because it cannot be compiled without a Hearthstone client's
libraries, which were not available where this was written.

## Try it without the client

```text
dotnet run --project tests/Multiplayer.Tests -c Release
dotnet run --project tools/LanTable -c Release -- demo 3
```

The first runs the tests. The second hosts a lobby, seats three AI players and prints the whole run. Between two
machines on one network:

```text
dotnet run --project tools/LanTable -c Release -- host Ana --auto
dotnet run --project tools/LanTable -c Release -- join <CODE> Ben --auto
```

Type to chat; `/ready`, `/start`, `/nick <name>`, `/watch <game>` and `/quit` do what they say. `--auto` lets the
built-in AI draft and play for you. The terminal table draws no cards: it is for testing lobbies, runs and
spectating, not for playing.
