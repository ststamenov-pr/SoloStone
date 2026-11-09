// SPDX-License-Identifier: AGPL-3.0-or-later
// One game of a run, as every machine at the table simulates it.
//
// A game is fully described by its setup (seed, heroes, decks, hero powers, passives) and the ordered list of
// choices made in it. The host is the only one who appends to that list; players, the AI and spectators all
// apply the same choices to their own copy of the rules engine and therefore see the same game. That is also
// all spectating is: receive the setup and the choices so far, then keep following.
using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.CardSets.Client;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Decisions;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;

namespace SoloStone.Multiplayer
{
    public sealed class SeatSetup
    {
        public string Pid = "";          // the player in this seat; empty for a boss or an Echo
        public string Name = "";
        public string HeroId = "";
        public string HeroPowerId = "";  // empty: keep the hero card's own
        public int Health;
        public List<string> Deck = new List<string>();
        public List<string> Passives = new List<string>();

        internal string Pack() => string.Join(";", Pid, Name.Replace(";", ",").Replace("|", "/"), HeroId, HeroPowerId, Health.ToString(),
            string.Join(",", Deck), string.Join(",", Passives));

        internal static SeatSetup Unpack(string text)
        {
            string[] f = text.Split(';');
            if (f.Length != 7) throw new FormatException("Bad seat.");
            return new SeatSetup
            {
                Pid = f[0], Name = f[1], HeroId = f[2], HeroPowerId = f[3], Health = int.Parse(f[4]),
                Deck = f[5].Length == 0 ? new List<string>() : f[5].Split(',').ToList(),
                Passives = f[6].Length == 0 ? new List<string>() : f[6].Split(',').ToList(),
            };
        }
    }

    public sealed class DuelSetup
    {
        public string GameId = "";
        public string Kind = "duel";   // boss | duel | echo
        public int Stage;
        public int Seed;
        public int First = 1;          // which seat starts: 1 or 2
        public int ClockSeconds, TurnSeconds;
        public SeatSetup[] Seats = { new SeatSetup(), new SeatSetup() };

        public string Pack() => string.Join("|", GameId, Kind, Stage.ToString(), Seed.ToString(), First.ToString(),
            ClockSeconds.ToString(), TurnSeconds.ToString(), Seats[0].Pack(), Seats[1].Pack());

        public static DuelSetup Unpack(string text)
        {
            string[] f = text.Split('|');
            if (f.Length != 9) throw new FormatException("Bad game setup.");
            return new DuelSetup
            {
                GameId = f[0], Kind = f[1], Stage = int.Parse(f[2]), Seed = int.Parse(f[3]), First = int.Parse(f[4]),
                ClockSeconds = int.Parse(f[5]), TurnSeconds = int.Parse(f[6]),
                Seats = new[] { SeatSetup.Unpack(f[7]), SeatSetup.Unpack(f[8]) },
            };
        }
    }

    public sealed class DuelSession
    {
        public readonly DuelSetup Setup;
        public readonly Game Game;
        public readonly List<int> Log = new List<int>();
        public bool Over { get; private set; }
        /// <summary>0 or 1; -1 for a draw. Only meaningful once <see cref="Over"/>.</summary>
        public int Winner { get; private set; } = -1;
        public string EndReason { get; private set; } = "";

        public DuelSession(DuelSetup setup)
        {
            Setup = setup;
            Card hero1 = Cards.FromId(setup.Seats[0].HeroId), hero2 = Cards.FromId(setup.Seats[1].HeroId);
            if (hero1 == null || hero2 == null) throw new ArgumentException("Unknown hero in game setup.");
            Game = new Game(new GameConfig
            {
                Player1Name = setup.Seats[0].Name, Player2Name = setup.Seats[1].Name,
                Player1HeroClass = hero1.Class, Player1HeroCard = hero1, Player1Deck = Cardlist(setup.Seats[0].Deck),
                Player2HeroClass = hero2.Class, Player2HeroCard = hero2, Player2Deck = Cardlist(setup.Seats[1].Deck),
                FormatType = FormatType.FT_WILD, StartPlayer = setup.First == 2 ? 2 : 1, Shuffle = true, RandomSeed = setup.Seed,
                SkipMulligan = true, History = false, Logging = false,
            });
            Game.StartGame();
            // Same order on every machine: seat 1 then seat 2, hero power, health, then passives as listed.
            for (int i = 0; i < 2; i++)
            {
                Controller c = Seat(i);
                SeatSetup seat = setup.Seats[i];
                if (seat.HeroPowerId.Length > 0) DelveCards.SetHeroPower(c, seat.HeroPowerId);
                if (seat.Health > 0) c.Hero.BaseHealth = seat.Health;
                foreach (string passive in seat.Passives) DelveCards.InstallPassive(c, passive);
            }
            Check();
        }

        private static List<Card> Cardlist(List<string> ids) => ids.Select(id => Cards.FromId(id) ?? throw new ArgumentException("Unknown card: " + id)).ToList();

        public Controller Seat(int seat) => seat == 0 ? Game.Player1 : Game.Player2;

        /// <summary>
        /// What the engine is waiting for: the turn player's move, or a card's follow-up choice (Discover and the
        /// like), which can belong to either player. Null when nothing is pending.
        /// </summary>
        public PendingDecision Decision()
        {
            if (Over) return null;
            IReadOnlyList<PendingDecision> open = Game.GetPendingDecisions();
            return open.Count > 0 ? open[0] : null;
        }

        /// <summary>The seat that has to decide next.</summary>
        public int ActiveSeat
        {
            get
            {
                PendingDecision d = Decision();
                return d != null ? (d.ControllerId == Game.Player1.Id ? 0 : 1) : Game.CurrentPlayer == Game.Player1 ? 0 : 1;
            }
        }

        /// <summary>The legal moves right now. A move is named by its OptionId, which is the same on every machine.</summary>
        public IReadOnlyList<DecisionOption> Moves() => Decision()?.Options ?? (IReadOnlyList<DecisionOption>)new DecisionOption[0];

        public int Life(int seat) => Seat(seat).Hero.Health + Seat(seat).Hero.Armor;

        /// <summary>Plays the move with this OptionId. False (and nothing changes) if it is not on offer.</summary>
        public bool Apply(int optionId)
        {
            PendingDecision d = Decision();
            if (d == null || !d.Options.Any(o => o.OptionId == optionId)) return false;
            try
            {
                if (!Game.TryAnswer(new DecisionAnswer(d.Id, d.ControllerId, new[] { optionId }), out _)) return false;
            }
            catch (Exception)
            {
                // The engine gave up on a move it offered. Every copy fails the same way; the host then calls the game.
                Broken = true;
                return false;
            }
            Log.Add(optionId);
            Check();
            return true;
        }

        /// <summary>True once the engine has thrown while applying a move; no further move can be trusted.</summary>
        public bool Broken { get; private set; }

        /// <summary>
        /// Plays <paramref name="preferred"/> or, failing that, the first move the engine accepts. Returns the move that
        /// was played, or -1 when there is no workable move at all; the host then calls the game on health.
        /// </summary>
        public int ApplyAny(int preferred)
        {
            if (Apply(preferred)) return preferred;
            if (Broken) return -1;
            foreach (int id in Moves().Select(o => o.OptionId).ToList())
            {
                if (id != preferred && Apply(id)) return id;
                if (Broken) return -1;
            }
            return -1;
        }

        /// <summary>The card whose follow-up choice is pending, or "" (for reports when a game stalls).</summary>
        public string PendingChoiceCard()
        {
            PendingDecision d = Decision();
            return d != null && d.SourceId != 0 && Game.IdEntityDic.TryGetValue(d.SourceId, out IPlayable source) ? source.Card.Id : "";
        }

        /// <summary>The "end turn" move, or -1 when a follow-up choice has to be answered first.</summary>
        public int EndTurnMove()
        {
            PendingDecision d = Decision();
            if (d == null || d.Kind != DecisionKind.Options) return -1;
            DecisionOption end = d.Options.FirstOrDefault(o => o.TaskType == PlayerTaskType.END_TURN);
            return end == null ? -1 : end.OptionId;
        }

        /// <summary>The move the built-in AI would make for the active seat, or -1 if there is none.</summary>
        public int AiChoice()
        {
            PendingDecision d = Decision();
            if (d == null || d.Options.Count == 0) return -1;
            if (d.Kind != DecisionKind.Options)
            {
                // A follow-up choice: the engine's own policy if it has one, else the first thing on offer.
                DecisionAnswer policy = Game.GetSelectorAnswer();
                return policy != null && policy.DecisionId == d.Id ? policy.SelectedOptionIds[0] : d.Options[0].OptionId;
            }
            try
            {
                // Score the moves on a copy: listing them on the real game would touch its caches.
                List<PlayerTask> tasks = Game.CloneExact().CurrentPlayer.Options();
                int index = tasks.Count == d.Options.Count ? tasks.IndexOf(LocalBot.Choose(tasks)) : -1;
                if (index >= 0) return index;
            }
            catch (Exception) { /* fall through to the simplest legal move */ }
            int endTurn = EndTurnMove();
            return endTurn >= 0 ? endTurn : d.Options[0].OptionId;
        }

        private void Check()
        {
            if (Over || Game.State != State.COMPLETE) return;
            PlayState one = Game.Player1.PlayState, two = Game.Player2.PlayState;
            Finish(one == PlayState.WON ? 0 : two == PlayState.WON ? 1 : one == PlayState.LOST && two != PlayState.LOST ? 1
                : two == PlayState.LOST && one != PlayState.LOST ? 0 : -1, "hero defeated");
        }

        private void Finish(int winner, string reason)
        {
            Over = true;
            Winner = winner;
            EndReason = reason;
        }

        /// <summary>The clock ran out: a boss outlasts the challenger; in a duel the healthier hero wins.</summary>
        public void ResolveTimeout(string reason = "time ran out")
        {
            if (Over) return;
            if (Setup.Kind == "boss") { Finish(1, reason); return; }
            int a = Life(0), b = Life(1);
            Finish(a == b ? -1 : a > b ? 0 : 1, reason);
        }

        public void Concede(int seat)
        {
            if (!Over) Finish(1 - seat, "conceded");
        }

        /// <summary>A fingerprint of the whole game state, to prove two machines agree.</summary>
        public string StateHash()
        {
            // Built from values that are read fresh, so two copies agree whenever the games do.
            var sb = new System.Text.StringBuilder();
            sb.Append(Game.Turn).Append('/').Append(ActiveSeat).Append('/').Append(Over ? Winner + 2 : 0);
            for (int i = 0; i < 2; i++)
            {
                Controller c = Seat(i);
                sb.Append('|').Append(c.Hero.Card.Id).Append(':').Append(c.Hero.Health).Append('+').Append(c.Hero.Armor)
                    .Append(' ').Append(c.BaseMana).Append('-').Append(c.UsedMana).Append(' ').Append(c.Hero.HeroPower.Card.Id)
                    .Append(" d").Append(c.DeckZone.Count).Append(" h");
                foreach (IPlayable card in c.HandZone) sb.Append(card.Card.Id).Append('@').Append(card.Cost).Append(',');
                sb.Append(" b");
                foreach (Minion m in c.BoardZone.OfType<Minion>()) sb.Append(m.Card.Id).Append('=').Append(m.AttackDamage).Append('/').Append(m.Health).Append(',');
            }
            ulong hash = 14695981039346656037UL;
            foreach (char ch in sb.ToString()) { hash ^= ch; hash *= 1099511628211UL; }
            return hash.ToString("x16");
        }
    }
}
