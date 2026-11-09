// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using SabberStoneCore.CardSets.Client;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;
using SoloStone.Multiplayer;
using static Program;

internal static partial class Tests
{
    internal static IEnumerable<KeyValuePair<string, Action>> All()
    {
        var list = new List<KeyValuePair<string, Action>>();
        void Add(string name, Action a) => list.Add(new KeyValuePair<string, Action>(name, a));
        Add("addresses: only local networks pass", Addresses);
        Add("join codes round-trip and reject the internet", Codes);
        Add("wire: fields survive tabs, newlines and backslashes", WireFormat);
        Add("catalogue: every hero, power, boss and treasure exists and is scripted", Catalogue);
        Add("decks: validation and suggested decks", Decks);
        Add("pairings: everyone meets, byes rotate", Pairings);
        Add("games replay identically from setup and choices", Replay);
        Add("every boss can be fought to a finish by the AI", BossFights);
        Add("every drafted hero power can be used", HeroPowers);
        Add("treasures and boss passives take effect", Passives);
        Add("timeouts and concessions", Timeouts);
        AddNet(Add); // lobby and run tests live in NetTests.cs
        return list;
    }

    static partial void AddNet(Action<string, Action> add);

    private static void Addresses()
    {
        foreach (string ok in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.1.20", "169.254.3.4", "100.64.0.1", "100.127.255.255", "::1", "fe80::1", "fd12:3456::1" })
            Check(LanAddress.IsLocal(IPAddress.Parse(ok)), ok + " is local");
        foreach (string no in new[] { "8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.0.1", "100.63.0.1", "100.128.0.1", "1.1.1.1", "2001:4860:4860::8888", "0.0.0.0" })
            Check(!LanAddress.IsLocal(IPAddress.Parse(no)), no + " is not local");
        Check(LanAddress.Mine().Count > 0, "this machine has an address");
    }

    private static void Codes()
    {
        string code = JoinCode.Encode(IPAddress.Parse("192.168.1.20"), 47625);
        Equal(11, code.Length, "code length");
        Check(JoinCode.TryDecode(code, out var ep) && ep.Address.ToString() == "192.168.1.20" && ep.Port == 47625, "round trip");
        Check(JoinCode.TryDecode(code.ToLowerInvariant().Replace("-", " "), out ep), "case and spacing are forgiven");
        foreach (var pair in new[] { ("10.0.0.7", 1), ("172.20.4.4", 65535), ("100.100.1.1", 5000), ("127.0.0.1", 47625) })
            Check(JoinCode.TryDecode(JoinCode.Encode(IPAddress.Parse(pair.Item1), pair.Item2), out ep) && ep.Address.ToString() == pair.Item1 && ep.Port == pair.Item2, pair.Item1);
        Check(!JoinCode.TryDecode("", out ep) && !JoinCode.TryDecode("ABCDE", out ep) && !JoinCode.TryDecode("!!!!!-!!!!!", out ep), "junk");
        char last = code[code.Length - 1];
        string typo = code.Substring(0, code.Length - 1) + (last == 'A' ? 'B' : 'A');
        Check(!JoinCode.TryDecode(typo, out ep) || ep.Port != 47625 || ep.Address.ToString() != "192.168.1.20", "a typo does not decode to the same lobby");
        bool threw = false;
        try { JoinCode.Encode(IPAddress.Parse("8.8.8.8"), 80); } catch (ArgumentException) { threw = true; }
        Check(threw, "public addresses cannot be encoded");
        // Whatever ten characters someone types, a decoded code never points outside the local network.
        var dice = new Dice(99);
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (int i = 0; i < 20000; i++)
        {
            var chars = new char[10];
            for (int k = 0; k < 10; k++) chars[k] = alphabet[dice.Next(32)];
            if (JoinCode.TryDecode(new string(chars), out ep)) Check(LanAddress.IsLocal(ep.Address), "decoded " + ep);
        }
    }

    private static void WireFormat()
    {
        var fields = new[] { "chat", "a\tb", "line1\nline2", "back\\slash", "", "end" };
        string line = Wire.Encode(fields);
        Check(!line.Contains("\n"), "one line");
        Check(fields.SequenceEqual(Wire.Decode(line)), "round trip");
        Equal("Ana Banana", Wire.Clean("  Ana " + (char)0x200B + (char)7 + "  Banana\r\n", 16), "clean");
        Equal("Ben theBold and", Wire.Clean("Ben\tthe" + (char)0x200B + "Bold and Very Long Name", 16), "no trailing space, never over the limit");
        Equal("hello there", Wire.Clean("hello\nthere", 240), "line breaks become spaces");
        Equal(16, Wire.Clean(new string('x', 99), 16).Length, "length limit");
    }

    private static void Catalogue()
    {
        Equal(11, RunCatalog.Heroes.Count, "classes");
        foreach (var hero in RunCatalog.Heroes)
        {
            Check(Cards.FromId(hero.Value)?.Type == CardType.HERO, hero.Value);
            string[] powers = RunCatalog.HeroPowers(hero.Key);
            Equal(3, powers.Distinct().Count(), hero.Key + " offers three different hero powers");
            foreach (string id in powers)
            {
                Card c = Cards.FromId(id);
                Check(c != null && c.Type == CardType.HERO_POWER && c.Implemented, id + " is a scripted hero power");
            }
            Check(RunCatalog.Pool(hero.Key).Count > 100, hero.Key + " has a draft pool");
        }
        foreach (int tier in new[] { 1, 2, 3 }) Check(RunCatalog.Bosses.Count(b => b.Tier == tier) >= 2, "tier " + tier + " has a choice of bosses");
        foreach (BossDef boss in RunCatalog.Bosses)
        {
            Card hero = Cards.FromId(boss.HeroId);
            Check(hero != null && hero.Type == CardType.HERO, boss.HeroId);
            Card power = Cards.FromAssetId(hero[GameTag.HERO_POWER]);
            Check(power != null && power.Implemented, boss.HeroId + " hero power is scripted");
            Equal(boss.PassivePower, DelveCards.Passives.Contains(power.Id), boss.HeroId + " passive flag");
        }
        foreach (string id in RunCatalog.Treasures) Check(DelveCards.Passives.Contains(id) && Cards.FromId(id).Implemented, id);
        Check(RunRules.BetaFullCollection, "beta: full collection");
    }

    private static void Decks()
    {
        var dice = new Dice(4);
        foreach (string cls in RunCatalog.Heroes.Keys)
        {
            List<string> deck = RunCatalog.SuggestedDeck(cls, dice);
            Equal(null, RunCatalog.DeckProblem(cls, deck), cls + " suggested deck");
        }
        List<string> mage = RunCatalog.SuggestedDeck("MAGE", dice);
        Check(RunCatalog.DeckProblem("MAGE", mage.Take(14).ToList()).Contains("exactly"), "size");
        string warriorCard = RunCatalog.Pool("WARRIOR").First(c => c.Class == CardClass.WARRIOR).Id;
        Check(RunCatalog.DeckProblem("MAGE", mage.Take(14).Append(warriorCard).ToList()).Contains("another class"), "class");
        string common = RunCatalog.Pool("MAGE").First(c => c.Rarity != Rarity.LEGENDARY).Id;
        Check(RunCatalog.DeckProblem("MAGE", Enumerable.Repeat(common, 3).Concat(mage.Take(12)).ToList()).Contains("Too many"), "copies");
        string legend = RunCatalog.Pool("MAGE").First(c => c.Rarity == Rarity.LEGENDARY).Id;
        Check(RunCatalog.DeckProblem("MAGE", new[] { legend, legend }.Concat(mage.Where(id => id != legend).Take(13)).ToList()).Contains("Too many"), "legendary");
        Check(RunCatalog.DeckProblem("MAGE", mage.Take(14).Append("LOOTA_801").ToList()).Contains("Unknown"), "not collectible");
        Check(RunCatalog.DeckProblem("NOPE", mage) != null, "class name");
    }

    private static void Pairings()
    {
        Equal(1, RunRules.Pairings(2, 1).Length, "two players");
        var byes = Enumerable.Range(0, 3).Select(r => RunRules.Pairings(3, r).First(p => p[1] < 0)[0]).OrderBy(x => x).ToArray();
        Check(byes.SequenceEqual(new[] { 0, 1, 2 }), "each of three gets one bye");
        var met = new HashSet<string>();
        for (int r = 0; r < 3; r++) foreach (int[] p in RunRules.Pairings(4, r)) met.Add(p[0] + "-" + p[1]);
        Equal(6, met.Count, "four players all meet");
    }

    internal static DuelSetup Duel(int seed, string a = "MAGE", string b = "WARRIOR", int powerA = 1, int powerB = 1)
    {
        var dice = new Dice(seed);
        return new DuelSetup
        {
            GameId = "g" + seed, Kind = "duel", Stage = 0, Seed = seed, First = 1 + seed % 2, ClockSeconds = 300, TurnSeconds = 45,
            Seats = new[]
            {
                new SeatSetup { Pid = "a", Name = "A", HeroId = RunCatalog.Heroes[a], HeroPowerId = RunCatalog.HeroPowers(a)[powerA], Health = 20, Deck = RunCatalog.SuggestedDeck(a, dice) },
                new SeatSetup { Pid = "b", Name = "B", HeroId = RunCatalog.Heroes[b], HeroPowerId = RunCatalog.HeroPowers(b)[powerB], Health = 20, Deck = RunCatalog.SuggestedDeck(b, dice) },
            },
        };
    }

    internal static DuelSetup BossSetup(BossDef boss, int seed, string cls = "PALADIN")
    {
        var dice = new Dice(seed);
        string power = Cards.FromAssetId(Cards.FromId(boss.HeroId)[GameTag.HERO_POWER]).Id;
        var setup = new DuelSetup
        {
            GameId = "b" + seed, Kind = "boss", Stage = boss.Tier - 1, Seed = seed, First = 1, ClockSeconds = 300, TurnSeconds = 45,
            Seats = new[]
            {
                new SeatSetup { Pid = "a", Name = "A", HeroId = RunCatalog.Heroes[cls], Health = RunRules.Stages[boss.Tier - 1].HeroHealth, Deck = RunCatalog.SuggestedDeck(cls, dice) },
                new SeatSetup { Name = "Boss", HeroId = boss.HeroId, Health = boss.Health, Deck = RunCatalog.BossDeck(boss, dice) },
            },
        };
        if (boss.PassivePower) setup.Seats[1].Passives.Add(power);
        return setup;
    }

    private static void PlayOut(DuelSession s, int cap = 4000)
    {
        int turnActions = 0, turn = s.Game.Turn;
        for (int i = 0; i < cap && !s.Over; i++)
        {
            if (s.Game.Turn != turn) { turn = s.Game.Turn; turnActions = 0; }
            int choice = ++turnActions > 40 && s.EndTurnMove() >= 0 ? s.EndTurnMove() : s.AiChoice();
            if (s.ApplyAny(choice) < 0) s.ResolveTimeout("stalled");
            if (s.Game.Turn > 120) s.ResolveTimeout();
        }
        Check(s.Over, "game ended");
    }

    internal static int Fuzz(int games)
    {
        var stalls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string[] classes = RunCatalog.Heroes.Keys.ToArray();
        int crashed = 0;
        for (int seed = 1; seed <= games; seed++)
        {
            var dice = new Dice(seed * 7919);
            string a = classes[seed % 11], b = classes[(seed / 11 + 3) % 11];
            DuelSetup setup = Duel(seed, a, b, seed % 3, (seed / 3) % 3);
            // Random decks from the whole pool, so every card gets its turn.
            for (int i = 0; i < 2; i++)
            {
                var pool = RunCatalog.Pool(i == 0 ? a : b).Where(c => c.Cost <= 6).ToList();
                setup.Seats[i].Deck = Enumerable.Range(0, 24).Select(_ => dice.Pick(pool).Id).ToList();
                setup.Seats[i].Health = 30;
            }
            try
            {
                var s = new DuelSession(setup);
                int turn = s.Game.Turn, actions = 0;
                for (int i = 0; i < 3000 && !s.Over; i++)
                {
                    if (s.Game.Turn != turn) { turn = s.Game.Turn; actions = 0; }
                    int choice = ++actions > 40 && s.EndTurnMove() >= 0 ? s.EndTurnMove() : s.AiChoice();
                    if (s.ApplyAny(choice) < 0)
                    {
                        string card = s.PendingChoiceCard();
                        stalls.TryGetValue(card, out int n);
                        stalls[card] = n + 1;
                        break;
                    }
                    if (s.Game.Turn > 90) break;
                }
            }
            catch (Exception e) { crashed++; if (crashed <= 5) Console.WriteLine("crash seed " + seed + ": " + e.GetType().Name + " " + e.Message.Split((char)10)[0]); }
        }
        Console.WriteLine(games + " games, " + crashed + " crashed, stalls: " + string.Join(" ", stalls.Select(p => p.Key + "x" + p.Value)));
        return 0;
    }

    private static void Replay()
    {
        for (int seed = 1; seed <= 6; seed++)
        {
            string[] classes = RunCatalog.Heroes.Keys.ToArray();
            DuelSetup setup = Duel(seed, classes[seed % 11], classes[(seed * 3 + 1) % 11], seed % 3, (seed + 1) % 3);
            setup.Seats[0].Passives.Add(RunCatalog.Treasures[seed % RunCatalog.Treasures.Length]);
            var played = new DuelSession(DuelSetup.Unpack(setup.Pack()));
            PlayOut(played);
            // A second machine (or a spectator who joins late) gets the setup and the choices, nothing else.
            var replica = new DuelSession(DuelSetup.Unpack(setup.Pack()));
            foreach (int choice in played.Log) Check(replica.Apply(choice), "replayed choice is legal");
            // If the game was called (clock or stall) rather than won, the host announces that and everyone applies it.
            if (!replica.Over) replica.ResolveTimeout(played.EndReason);
            Equal(played.StateHash(), replica.StateHash(), "state after replay, seed " + seed);
            Equal(played.Winner, replica.Winner, "winner");
        }
    }

    private static void BossFights()
    {
        foreach (BossDef boss in RunCatalog.Bosses)
        {
            var s = new DuelSession(BossSetup(boss, 7 + boss.Tier));
            Equal(boss.Health, s.Seat(1).Hero.Health, boss.HeroId + " health");
            Equal(RunRules.Stages[boss.Tier - 1].HeroHealth, s.Seat(0).Hero.Health, "challenger health");
            PlayOut(s);
            Check(s.Winner == 0 || s.Winner == 1, boss.HeroId + " has a result");
        }
    }

    private static void HeroPowers()
    {
        foreach (string cls in RunCatalog.Heroes.Keys)
            for (int p = 0; p < 3; p++)
            {
                string power = RunCatalog.HeroPowers(cls)[p];
                bool used = false;
                for (int seed = 1; seed <= 6 && !used; seed++)
                {
                    var s = new DuelSession(Duel(seed, cls, "WARRIOR", p, 0));
                    Equal(power, s.Seat(0).Hero.HeroPower.Card.Id, cls + " hero power installed");
                    for (int i = 0; i < 600 && !s.Over && !used; i++)
                    {
                        var hp = s.ActiveSeat == 0 ? s.Moves().FirstOrDefault(o => o.TaskType == PlayerTaskType.HERO_POWER) : null;
                        if (hp != null) { Check(s.Apply(hp.OptionId), "hero power applies"); used = true; }
                        else if (s.ApplyAny(s.AiChoice()) < 0) break;
                    }
                }
                Check(used, cls + " could use " + power);
            }
    }

    private static void Passives()
    {
        DuelSetup With(string passive, int seed = 3)
        {
            DuelSetup d = Duel(seed, "MAGE", "WARRIOR", 0, 0);
            d.First = 1;
            d.Seats[0].Passives.Add(passive);
            return d;
        }
        DuelSetup plainSetup = Duel(3, "MAGE", "WARRIOR", 0, 0);
        plainSetup.First = 1;
        var plain = new DuelSession(plainSetup);
        Equal(40, new DuelSession(With("LOOTA_800")).Seat(0).Hero.Health, "Potion of Vitality doubles health");
        Equal(plain.Seat(0).BaseMana + 1, new DuelSession(With("LOOTA_801")).Seat(0).BaseMana, "Crystal Gem adds a crystal");
        Equal(plain.Seat(0).HandZone.Count + 2, new DuelSession(With("LOOTA_804")).Seat(0).HandZone.Count, "Small Backpacks draws two");
        Equal(plain.Seat(0).CurrentSpellPower + 3, new DuelSession(With("LOOTA_825")).Seat(0).CurrentSpellPower, "Robe of the Magi");

        // Captured Flag: the first minion we play is +1/+1.
        var flag = new DuelSession(With("LOOTA_828"));
        Minion played = PlayFirstMinion(flag, 0);
        Check(played != null, "played a minion under Captured Flag");
        Equal(played.Card.ATK + 1, played.AttackDamage, "Captured Flag attack");
        Equal(played.Card.Health + 1, played.Health, "Captured Flag health");

        // Khadgar's Scrying Orb and Glyph of Warding change costs in hand.
        var orb = new DuelSession(With("LOOTA_824"));
        foreach (IPlayable c in orb.Seat(0).HandZone.Where(c => c.Card.Type == CardType.SPELL && c.Card.Cost > 0))
            Equal(c.Card.Cost - 1, c.Cost, "spell costs (1) less: " + c.Card.Name);
        var glyph = new DuelSession(With("LOOTA_831"));
        Check(glyph.Seat(1).HandZone.Any(c => c.Card.Type == CardType.MINION), "opponent holds a minion");
        foreach (IPlayable c in glyph.Seat(1).HandZone.Where(c => c.Card.Type == CardType.MINION))
            Equal(c.Card.Cost + 1, c.Cost, "enemy minion costs (1) more: " + c.Card.Name);

        // The Floor is Lava: a minion that is played takes 2 damage at once.
        BossDef lava = RunCatalog.Boss("LOOTA_BOSS_47h");
        var floor = new DuelSession(BossSetup(lava, 5));
        Minion burnt = PlayFirstMinion(floor, 0, m => m.Health > 2 && !m.DivineShield);
        Check(burnt == null || burnt.Damage == 2 || burnt.IsDead, "the floor burned the minion");

        // Charrrrrge!: the boss's minions can attack the turn they are played.
        var charge = new DuelSession(BossSetup(RunCatalog.Boss("LOOTA_BOSS_31h"), 6));
        Minion charger = PlayFirstMinion(charge, 1);
        Check(charger == null || charger.HasCharge, "boss minion has Charge");
    }

    // Plays turns with the AI until `seat` plays a minion from hand; returns it (or null if the game ended first).
    private static Minion PlayFirstMinion(DuelSession s, int seat, Func<Card, bool> want = null)
    {
        for (int i = 0; i < 800 && !s.Over; i++)
        {
            if (s.ActiveSeat == seat)
            {
                var play = s.Moves().FirstOrDefault(o => o.TaskType == PlayerTaskType.PLAY_CARD && s.Game.IdEntityDic[o.EntityId] is Minion m
                    && m.Zone == s.Seat(seat).HandZone && (want == null || want(m.Card)));
                if (play != null)
                {
                    var minion = (Minion)s.Game.IdEntityDic[play.EntityId];
                    Check(s.Apply(play.OptionId), "play minion");
                    return minion;
                }
            }
            int end = s.EndTurnMove();
            Check(s.ApplyAny(end >= 0 ? end : s.AiChoice()) >= 0, "pass");
        }
        return null;
    }

    private static void Timeouts()
    {
        var duel = new DuelSession(Duel(2));
        duel.ResolveTimeout();
        Check(duel.Over && duel.Winner == -1, "equal health is a draw");
        Check(!duel.Apply(0), "no moves after the end");
        var boss = new DuelSession(BossSetup(RunCatalog.Bosses[0], 2));
        boss.ResolveTimeout();
        Equal(1, boss.Winner, "the boss outlasts the challenger");
        var quit = new DuelSession(Duel(2));
        quit.Concede(0);
        Equal(1, quit.Winner, "conceding hands over the win");
    }
}
