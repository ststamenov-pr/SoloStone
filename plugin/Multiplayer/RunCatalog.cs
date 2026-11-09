// SPDX-License-Identifier: AGPL-3.0-or-later
// What a run is made of. Everything here is named by the game's own card IDs, so each player's client shows its
// own art and text for heroes, hero powers, bosses and treasures; nothing is shipped or copied.
using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.CardSets.Client;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

namespace SoloStone.Multiplayer
{
    public sealed class BossDef
    {
        public string HeroId;      // the boss hero card; its hero power comes with it
        public int Tier;           // 1..3
        public int Health;
        public bool PassivePower;  // the hero power is a passive that must be switched on at game start
        public int DeckSize;
        public int MaxCost;
    }

    public static class RunRules
    {
        public const int MinPlayers = 2;
        public const int MaxPlayers = 4;
        public const int StarterDeckSize = 15;
        public const int MaxCopies = 2;
        public const int MaxCopiesLegendary = 1;
        public const int LootCards = 3;
        /// <summary>Beta: everyone drafts from the full collection, whatever their save owns.</summary>
        public const bool BetaFullCollection = true;

        public sealed class Stage
        {
            public int HeroHealth, BossClock, DuelClock, TurnSeconds, WinPoints;
        }

        // Early games are short on purpose (less health, tighter clocks); later ones get room and are worth more.
        public static readonly Stage[] Stages =
        {
            new Stage { HeroHealth = 20, BossClock = 300, DuelClock = 360, TurnSeconds = 45, WinPoints = 3 },
            new Stage { HeroHealth = 25, BossClock = 360, DuelClock = 480, TurnSeconds = 60, WinPoints = 4 },
            new Stage { HeroHealth = 30, BossClock = 420, DuelClock = 600, TurnSeconds = 75, WinPoints = 5 },
        };
        public const int DrawPoints = 1;
        /// <summary>Catch-up: losing a duel adds this much health for the rest of the run.</summary>
        public const int GritHealth = 3;
        /// <summary>Beating the Echo on a bye round adds this much health for the rest of the run.</summary>
        public const int EchoHealth = 2;

        public static double Pace(string pace) => pace == "quick" ? 0.75 : pace == "relaxed" ? 1.5 : 1.0;

        /// <summary>Round-robin duel seats for a table of n; -1 marks a bye (that player spars with an Echo).</summary>
        public static int[][] Pairings(int n, int round)
        {
            if (n == 2) return new[] { new[] { 0, 1 } };
            if (n == 3)
            {
                int bye = round % 3;
                int[] rest = Enumerable.Range(0, 3).Where(i => i != bye).ToArray();
                return new[] { new[] { rest[0], rest[1] }, new[] { bye, -1 } };
            }
            switch (round % 3)
            {
                case 0: return new[] { new[] { 0, 1 }, new[] { 2, 3 } };
                case 1: return new[] { new[] { 0, 2 }, new[] { 1, 3 } };
                default: return new[] { new[] { 0, 3 }, new[] { 1, 2 } };
            }
        }
    }

    public static class RunCatalog
    {
        /// <summary>Class name to hero card. The basic hero power comes from the hero card itself.</summary>
        public static readonly Dictionary<string, string> Heroes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WARRIOR"] = "HERO_01", ["SHAMAN"] = "HERO_02", ["ROGUE"] = "HERO_03", ["PALADIN"] = "HERO_04",
            ["HUNTER"] = "HERO_05", ["DRUID"] = "HERO_06", ["WARLOCK"] = "HERO_07", ["MAGE"] = "HERO_08",
            ["PRIEST"] = "HERO_09", ["DEMONHUNTER"] = "HERO_10", ["DEATHKNIGHT"] = "HERO_11",
        };

        // Two extra hero powers per class beside the basic one. Classes the Dalaran Heist never gave two usable
        // powers borrow from a small neutral pair.
        private static readonly string[] NeutralPowers = { "LOOTA_BOSS_05p", "LOOTA_BOSS_42p" };
        private static readonly Dictionary<string, string[]> ExtraPowers = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["WARRIOR"] = new[] { "DALA_Warrior_HP1", "LOOTA_BOSS_42p" },
            ["ROGUE"] = new[] { "DALA_Rogue_HP1", "DALA_Rogue_HP2" },
            ["PALADIN"] = new[] { "DALA_Paladin_HP1", "DALA_Paladin_HP2" },
            ["HUNTER"] = new[] { "DALA_Hunter_HP1", "LOOTA_BOSS_05p" },
            ["DRUID"] = new[] { "DALA_Druid_HP1", "DALA_Druid_HP2" },
            ["WARLOCK"] = new[] { "DALA_Warlock_HP1", "DALA_Warlock_HP2" },
            ["MAGE"] = new[] { "DALA_Mage_HP1", "DALA_Mage_HP2" },
            ["PRIEST"] = new[] { "DALA_Priest_HP1", "LOOTA_BOSS_42p" },
        };

        public static readonly BossDef[] Bosses =
        {
            new BossDef { HeroId = "LOOTA_BOSS_18h", Tier = 1, Health = 15, DeckSize = 15, MaxCost = 4 }, // Giant Rat
            new BossDef { HeroId = "LOOTA_BOSS_44h", Tier = 1, Health = 15, DeckSize = 15, MaxCost = 4 }, // Wee Whelp
            new BossDef { HeroId = "LOOTA_BOSS_42h", Tier = 1, Health = 15, DeckSize = 15, MaxCost = 4 }, // Elder Jari
            new BossDef { HeroId = "LOOTA_BOSS_05h", Tier = 2, Health = 30, DeckSize = 20, MaxCost = 6 }, // Pathmaker Hamm
            new BossDef { HeroId = "LOOTA_BOSS_39h", Tier = 2, Health = 30, DeckSize = 20, MaxCost = 6 }, // Kraxx
            new BossDef { HeroId = "LOOTA_BOSS_19h", Tier = 2, Health = 30, DeckSize = 20, MaxCost = 6, PassivePower = true }, // Gutmook
            new BossDef { HeroId = "LOOTA_BOSS_47h", Tier = 3, Health = 40, DeckSize = 25, MaxCost = 8, PassivePower = true }, // Lava-Filled Chamber
            new BossDef { HeroId = "LOOTA_BOSS_31h", Tier = 3, Health = 40, DeckSize = 25, MaxCost = 8, PassivePower = true }, // Candlebeard
            new BossDef { HeroId = "LOOTA_BOSS_28h", Tier = 3, Health = 40, DeckSize = 25, MaxCost = 8 }, // Pathmaker Hamm
            new BossDef { HeroId = "LOOTA_BOSS_46h", Tier = 3, Health = 40, DeckSize = 25, MaxCost = 8 }, // Fungalmancer Flurgl
        };

        /// <summary>Passive treasures a boss win can award (Dungeon Run).</summary>
        public static readonly string[] Treasures =
        {
            "LOOTA_800", "LOOTA_801", "LOOTA_804", "LOOTA_824", "LOOTA_825", "LOOTA_828", "LOOTA_831", "LOOTA_846",
        };

        /// <summary>Narrows the draft pool, e.g. to the meta the installed mod plays. The default is every implemented card.</summary>
        public static Func<Card, bool> PoolFilter = card => true;

        public static BossDef Boss(string heroId) => Bosses.First(b => b.HeroId == heroId);

        public static string BasicPower(string heroClass)
        {
            Card hero = Cards.FromId(Heroes[heroClass]);
            return Cards.FromAssetId(hero[GameTag.HERO_POWER]).Id;
        }

        /// <summary>The three hero powers a class drafts from: the basic one and two more.</summary>
        public static string[] HeroPowers(string heroClass)
        {
            string[] extra = ExtraPowers.TryGetValue(heroClass, out var found) ? found : NeutralPowers;
            return new[] { BasicPower(heroClass), extra[0], extra[1] };
        }

        // Cards that need a sideboard built in the deck editor cannot go into a drafted deck.
        private static readonly HashSet<string> NeedsSideboard = new HashSet<string>(StringComparer.Ordinal) { "JAIL_397", "JAIL_831" };

        private static bool Playable(Card c) => c.Collectible && c.Implemented && PoolFilter(c) && !NeedsSideboard.Contains(c.Id)
            && (c.Type == CardType.MINION || c.Type == CardType.SPELL || c.Type == CardType.WEAPON);

        /// <summary>Cards a class may draft: its own and the neutral ones, in a stable order.</summary>
        public static List<Card> Pool(string heroClass)
        {
            CardClass cls = (CardClass)Enum.Parse(typeof(CardClass), heroClass);
            return Cards.All.Where(c => Playable(c) && (c.Class == cls || c.Class == CardClass.NEUTRAL))
                .OrderBy(c => c.Cost).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>Why a starter deck is not legal, or null.</summary>
        public static string DeckProblem(string heroClass, IList<string> deck)
        {
            if (!Heroes.ContainsKey(heroClass ?? "")) return "Pick a class first.";
            if (deck == null || deck.Count != RunRules.StarterDeckSize) return "A starter deck has exactly " + RunRules.StarterDeckSize + " cards.";
            CardClass cls = (CardClass)Enum.Parse(typeof(CardClass), heroClass);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string id in deck)
            {
                Card c = id == null ? null : Cards.FromId(id);
                if (c == null || !Playable(c)) return "Unknown card: " + id;
                if (c.Class != cls && c.Class != CardClass.NEUTRAL) return c.Name + " belongs to another class.";
                counts.TryGetValue(id, out int n);
                counts[id] = ++n;
                if (n > (c.Rarity == Rarity.LEGENDARY ? RunRules.MaxCopiesLegendary : RunRules.MaxCopies)) return "Too many copies of " + c.Name + ".";
            }
            return null;
        }

        /// <summary>A sensible starter deck: a curve of minions with a few spells, drawn with the given dice.</summary>
        public static List<string> SuggestedDeck(string heroClass, Dice dice)
        {
            List<Card> pool = Pool(heroClass).Where(c => c.Rarity != Rarity.LEGENDARY).ToList();
            var deck = new List<string>();
            int[] curve = { 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6 };
            foreach (int cost in curve)
            {
                List<Card> at = pool.Where(c => c.Cost == cost && c.Type == CardType.MINION && deck.Count(id => id == c.Id) < RunRules.MaxCopies).ToList();
                if (at.Count == 0) at = pool.Where(c => deck.Count(id => id == c.Id) < RunRules.MaxCopies).ToList();
                deck.Add(dice.Pick(at).Id);
            }
            return deck;
        }

        /// <summary>A boss deck: neutral minions up to the boss's cost cap. Built once by the host and sent with the game.</summary>
        public static List<string> BossDeck(BossDef boss, Dice dice)
        {
            List<Card> pool = Cards.All.Where(c => Playable(c) && c.Class == CardClass.NEUTRAL && c.Type == CardType.MINION
                && c.Cost >= 1 && c.Cost <= boss.MaxCost && c.Rarity != Rarity.LEGENDARY).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
            var deck = new List<string>();
            for (int i = 0; i < boss.DeckSize; i++) deck.Add(dice.Pick(pool).Id);
            return deck;
        }
    }

    /// <summary>A small seeded generator, so the host's choices do not depend on the runtime's Random.</summary>
    public sealed class Dice
    {
        private uint state;
        public Dice(int seed) { state = (uint)seed * 2654435761u + 1u; }
        public int Next(int n)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (int)(state % (uint)Math.Max(1, n));
        }
        public T Pick<T>(IList<T> list) => list[Next(list.Count)];
        public List<T> Sample<T>(IEnumerable<T> source, int count)
        {
            var list = source.ToList();
            for (int i = list.Count - 1; i > 0; i--) { int j = Next(i + 1); T t = list[i]; list[i] = list[j]; list[j] = t; }
            return list.Take(count).ToList();
        }
    }
}
