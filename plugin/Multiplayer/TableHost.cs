// SPDX-License-Identifier: AGPL-3.0-or-later
// The host side of a table: the lobby (nicknames, ready flags, chat) and, once started, the run.
//
// A run is three stages. Each stage is a boss fight (everyone fights the same boss, each in their own game)
// followed by duels between the players; only duel wins score. Nobody is ever stuck: whoever finishes a boss
// fight picks their loot and then waits, and while waiting they can chat and spectate any game still going.
//
// The host is the only authority. It keeps a copy of every game, decides the order of moves, plays the AI
// seats, and enforces the clocks. Call Pump() regularly from one thread; nothing here calls back from another.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

namespace SoloStone.Multiplayer
{
    /// <summary>How long each part of a run lasts, in seconds. Tests shrink these.</summary>
    public sealed class Timing
    {
        public double Draft = 210, Intro = 6, Loot = 50, Standings = 10, AiDelay = 0.9, ClockScale = 1;
    }

    public sealed class RunReward
    {
        public int Gold, Xp, Place;
        public List<string> Titles = new List<string>();

        /// <summary>The payout for one finished run. Leaving early forfeits it.</summary>
        public static RunReward For(int place, int wins, int losses, int draws, int bossWins)
        {
            int[] placeGold = { 60, 35, 20, 10 }, placeXp = { 120, 70, 40, 20 };
            int p = Math.Min(Math.Max(place, 0), 3);
            var r = new RunReward { Place = place, Gold = 20 + 10 * bossWins + 15 * wins + 5 * draws + placeGold[p], Xp = 60 + 30 * bossWins + 40 * wins + 15 * draws + placeXp[p] };
            if (place == 0) r.Titles.Add("Champion");
            if (bossWins >= 3) r.Titles.Add("Bossbreaker");
            if (wins > 0 && losses == 0 && draws == 0) r.Titles.Add("Untouchable");
            return r;
        }
    }

    public sealed class TableHost : IDisposable
    {
        private sealed class Member
        {
            public string Pid, Nick = "";
            public Link Link;
            public bool Ready, Connected = true, Gone;
            // run state
            public string ClassId = "", PowerId = "", DraftStep = "class", Status = "lobby", GameId = "", Last = "";
            public List<string> ClassOffers = new List<string>(), Deck = new List<string>(), Treasures = new List<string>();
            public int BonusHealth, Score, Wins, Losses, Draws, BossWins;
            public List<string> LootTreasures;
            public List<List<string>> LootBundles;
            public bool LootWon;
            public double LootDeadline;
        }

        private sealed class HostGame
        {
            public DuelSession Session;
            public double ClockEnd, TurnEnd, AiAt;
            public int TurnSeen, ActionsThisTurn, Refused;
            public readonly HashSet<string> Watchers = new HashSet<string>();
        }

        private readonly TcpListener listener;
        private readonly ConcurrentQueue<Link> accepted = new ConcurrentQueue<Link>();
        private readonly List<Link> strangers = new List<Link>();
        private readonly List<Member> members = new List<Member>();
        private readonly Dictionary<string, HostGame> games = new Dictionary<string, HostGame>(StringComparer.Ordinal);
        private readonly Func<double> now;
        private readonly Dice dice;
        private volatile bool disposed;
        private string hostPid = "", pace = "standard", phase = "lobby";
        private int nextPid = 1, nextGame = 1, round = -1;
        private double deadline;
        private string[] bosses = new string[3];
        private List<string>[] bossDecks = new List<string>[3];
        private int[][] pairs = new int[0][];
        private List<Member> seats = new List<Member>();

        public readonly Timing Timing;
        public IPEndPoint EndPoint { get; }
        /// <summary>What friends type to join. It is this machine's local address, nothing more.</summary>
        public string Code { get; }
        public string Phase => phase;
        public int PlayerCount => members.Count(m => !m.Gone);

        public TableHost(IPAddress address = null, int port = LanAddress.DefaultPort, Timing timing = null, Func<double> clock = null, int seed = 0)
        {
            address = address ?? LanAddress.Mine()[0];
            if (!LanAddress.IsLocal(address)) throw new ArgumentException("A lobby can only be hosted on a local network address.");
            Timing = timing ?? new Timing();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            now = clock ?? (() => watch.Elapsed.TotalSeconds);
            dice = new Dice(seed != 0 ? seed : Environment.TickCount);
            listener = new TcpListener(address, port);
            listener.Start(8);
            EndPoint = (IPEndPoint)listener.LocalEndpoint;
            Code = JoinCode.Encode(address, EndPoint.Port);
            new Thread(AcceptLoop) { IsBackground = true, Name = "SoloStone LAN lobby" }.Start();
        }

        private void AcceptLoop()
        {
            while (!disposed)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    // The listener is bound to a local address, but check the caller too: nothing from outside gets in.
                    if (!LanAddress.IsLocal(((IPEndPoint)client.Client.RemoteEndPoint).Address)) { client.Close(); continue; }
                    accepted.Enqueue(new Link(client));
                }
                catch (Exception) { if (disposed) return; }
            }
        }

        public void Dispose()
        {
            disposed = true;
            try { listener.Stop(); } catch (Exception) { }
            foreach (Member m in members) m.Link?.Dispose();
            foreach (Link l in strangers) l.Dispose();
        }

        // --- messaging --------------------------------------------------------------------------------------------

        private Member Find(string pid) => members.FirstOrDefault(m => m.Pid == pid);
        private void Send(Member m, params string[] fields) { if (m != null && m.Connected) m.Link.Send(fields); }
        private void All(params string[] fields) { foreach (Member m in members) Send(m, fields); }
        private void Say(string text) => All("chat", "", "", text, "1");

        private void PushLobby()
        {
            var f = new List<string> { "lobby", hostPid, pace, phase == "lobby" ? "0" : "1", Code };
            foreach (Member m in members.Where(m => !m.Gone)) { f.Add(m.Pid); f.Add(m.Nick); f.Add(m.Ready ? "1" : "0"); f.Add(m.Connected ? "1" : "0"); }
            All(f.ToArray());
        }

        private void PushRun()
        {
            if (phase == "lobby") return;
            var f = new List<string>
            {
                "run", phase, round.ToString(), Stage.ToString(), Kind, Kind == "boss" && round >= 0 ? bosses[Stage] : "",
                Math.Max(0, (int)Math.Ceiling(deadline - now())).ToString(), seats.Count.ToString(),
            };
            foreach (Member m in seats)
                f.AddRange(new[] { m.Pid, m.Nick, m.ClassId, m.Score.ToString(), m.Wins.ToString(), m.Losses.ToString(), m.Draws.ToString(),
                    m.BossWins.ToString(), m.Connected ? m.Status : "away", m.GameId, string.Join(",", m.Treasures), m.Last });
            List<HostGame> live = games.Values.Where(g => !g.Session.Over).ToList();
            f.Add(live.Count.ToString());
            foreach (HostGame g in live)
            {
                DuelSetup s = g.Session.Setup;
                f.AddRange(new[] { s.GameId, s.Kind, s.Seats[0].Name, s.Seats[1].Name, g.Session.Life(0).ToString(), g.Session.Life(1).ToString() });
            }
            All(f.ToArray());
        }

        // --- pump -------------------------------------------------------------------------------------------------

        /// <summary>Handles everything that arrived and everything that is due. Call it several times a second.</summary>
        public void Pump()
        {
            while (accepted.TryDequeue(out Link link)) strangers.Add(link);
            foreach (Link link in strangers.ToArray())
            {
                if (link.Closed) { strangers.Remove(link); continue; }
                if (link.Inbox.TryDequeue(out string[] msg)) { strangers.Remove(link); Hello(link, msg); }
            }
            foreach (Member m in members.ToArray())
            {
                while (m.Connected && m.Link.Inbox.TryDequeue(out string[] msg))
                {
                    try { Handle(m, msg); }
                    catch (Exception e) { Send(m, "error", "The host could not handle that: " + e.Message); }
                }
                if (m.Connected && m.Link.Closed) Dropped(m, "lost connection");
            }
            Tick();
        }

        private void Hello(Link link, string[] msg)
        {
            if (msg.Length < 3 || msg[0] != "hello" || msg[2] != Wire.Version.ToString()) { link.Send("error", "This lobby runs a different version of the mod."); link.Dispose(); return; }
            if (phase != "lobby") { link.Send("error", "That lobby is in the middle of a run."); link.Dispose(); return; }
            if (PlayerCount >= RunRules.MaxPlayers) { link.Send("error", "That lobby is full."); link.Dispose(); return; }
            var m = new Member { Pid = "p" + nextPid++, Link = link };
            m.Nick = UniqueNick(msg[1], m);
            members.Add(m);
            if (hostPid.Length == 0) hostPid = m.Pid;
            Send(m, "welcome", m.Pid, Code);
            Say(m.Nick + " joined the lobby.");
            PushLobby();
        }

        private string UniqueNick(string wanted, Member self)
        {
            string name = Wire.Clean(wanted, 16);
            if (name.Length == 0) name = "Guest";
            string candidate = name;
            for (int i = 2; members.Any(m => m != self && !m.Gone && string.Equals(m.Nick, candidate, StringComparison.OrdinalIgnoreCase)); i++)
                candidate = (name.Length > 14 ? name.Substring(0, 14) : name) + " " + i;
            return candidate;
        }

        private void Dropped(Member m, string why)
        {
            m.Connected = false;
            m.Link.Dispose();
            if (phase == "lobby")
            {
                members.Remove(m);
                Say(m.Nick + " " + why + ".");
                if (m.Pid == hostPid) hostPid = members.Count > 0 ? members[0].Pid : "";
                PushLobby();
                return;
            }
            // Mid-run their seat carries on under AI control, so nobody else is stranded.
            m.Gone = true;
            Say(m.Nick + " " + why + ". The AI plays on for them.");
            if (m.Status == "drafting") AutoDraft(m);
            if (m.Status == "looting") while (m.Status == "looting") PickLoot(m, 0);
            foreach (HostGame g in games.Values) g.Watchers.Remove(m.Pid);
            PushRun();
        }

        private void Handle(Member m, string[] f)
        {
            switch (f[0])
            {
                case "chat":
                    string text = Wire.Clean(f.Length > 1 ? f[1] : "", 240);
                    if (text.Length > 0) All("chat", m.Pid, m.Nick, text, "0");
                    break;
                case "nick":
                    if (phase != "lobby") { Send(m, "error", "You cannot change your nickname during a run."); break; }
                    string nick = UniqueNick(f.Length > 1 ? f[1] : "", m);
                    if (nick != m.Nick) { Say(m.Nick + " is now " + nick + "."); m.Nick = nick; PushLobby(); }
                    break;
                case "ready":
                    if (phase == "lobby") { m.Ready = f.Length > 1 && f[1] == "1"; PushLobby(); }
                    break;
                case "pace":
                    if (m.Pid == hostPid && phase == "lobby" && f.Length > 1 && (f[1] == "quick" || f[1] == "standard" || f[1] == "relaxed")) { pace = f[1]; PushLobby(); }
                    break;
                case "kick":
                    Member target = f.Length > 1 ? Find(f[1]) : null;
                    if (m.Pid != hostPid || phase != "lobby" || target == null || target == m) { Send(m, "error", "Only the host can remove players, and only in the lobby."); break; }
                    Send(target, "kicked");
                    Dropped(target, "was removed by the host");
                    break;
                case "start": StartRun(m); break;
                case "leave": Dropped(m, "left"); break;
                case "draft": Draft(m, f); break;
                case "loot": if (f.Length > 1 && int.TryParse(f[1], out int pick)) PickLoot(m, pick); break;
                case "act": Act(m, f); break;
                case "concede":
                    if (games.TryGetValue(m.GameId, out HostGame mine) && !mine.Session.Over) { mine.Session.Concede(SeatOf(mine, m)); Call(mine, "conceded"); }
                    break;
                case "watch": Watch(m, f.Length > 1 ? f[1] : ""); break;
                case "unwatch": foreach (HostGame g in games.Values) g.Watchers.Remove(m.Pid); break;
            }
        }

        // --- run: start and draft ---------------------------------------------------------------------------------

        private int Stage => round < 0 ? 0 : round / 2;
        private string Kind => round < 0 ? "" : round % 2 == 0 ? "boss" : "duel";
        private double PaceFactor => RunRules.Pace(pace) * Timing.ClockScale;

        private void StartRun(Member by)
        {
            if (by.Pid != hostPid) { Send(by, "error", "Only the host can start the run."); return; }
            if (phase != "lobby") { Send(by, "error", "A run is already going."); return; }
            if (members.Count < RunRules.MinPlayers) { Send(by, "error", "You need at least " + RunRules.MinPlayers + " players."); return; }
            if (members.Any(m => m.Pid != hostPid && !m.Ready)) { Send(by, "error", "Not everyone is ready."); return; }
            seats = dice.Sample(members, members.Count);
            for (int tier = 1; tier <= 3; tier++)
            {
                BossDef boss = dice.Pick(RunCatalog.Bosses.Where(b => b.Tier == tier).ToList());
                bosses[tier - 1] = boss.HeroId;
                bossDecks[tier - 1] = RunCatalog.BossDeck(boss, dice);
            }
            games.Clear();
            round = -1;
            phase = "draft";
            deadline = now() + Timing.Draft * RunRules.Pace(pace);
            foreach (Member m in seats)
            {
                m.ClassId = m.PowerId = m.GameId = m.Last = "";
                m.Deck.Clear(); m.Treasures.Clear();
                m.BonusHealth = m.Score = m.Wins = m.Losses = m.Draws = m.BossWins = 0;
                m.Status = "drafting";
                m.DraftStep = "class";
                m.ClassOffers = dice.Sample(RunCatalog.Heroes.Keys.OrderBy(k => k, StringComparer.Ordinal), 3);
                PushDraft(m);
            }
            Say("The run begins. Draft your class, hero power and starter deck.");
            PushLobby();
            PushRun();
        }

        private void PushDraft(Member m) => Send(m, "draftoffer", m.DraftStep, string.Join(",", m.ClassOffers), m.ClassId,
            m.ClassId.Length > 0 ? string.Join(",", RunCatalog.HeroPowers(m.ClassId)) : "", m.PowerId);

        private void Draft(Member m, string[] f)
        {
            if (phase != "draft" || f.Length < 3 || f[1] != m.DraftStep) { Send(m, "error", "That draft step is not open."); return; }
            if (m.DraftStep == "class")
            {
                if (!m.ClassOffers.Contains(f[2])) { Send(m, "error", "That class was not offered to you."); return; }
                m.ClassId = f[2];
                m.DraftStep = "power";
            }
            else if (m.DraftStep == "power")
            {
                if (!RunCatalog.HeroPowers(m.ClassId).Contains(f[2])) { Send(m, "error", "Pick one of the three hero powers."); return; }
                m.PowerId = f[2];
                m.DraftStep = "deck";
            }
            else if (m.DraftStep == "deck")
            {
                List<string> deck = f[2].Split(',').ToList();
                string problem = RunCatalog.DeckProblem(m.ClassId, deck);
                if (problem != null) { Send(m, "error", problem); return; }
                m.Deck = deck;
                m.DraftStep = "done";
                m.Status = "waiting";
            }
            PushDraft(m);
            PushRun();
        }

        private void AutoDraft(Member m)
        {
            if (m.ClassId.Length == 0) m.ClassId = m.ClassOffers[0];
            if (m.PowerId.Length == 0) m.PowerId = RunCatalog.HeroPowers(m.ClassId)[0];
            if (m.DraftStep != "done") m.Deck = RunCatalog.SuggestedDeck(m.ClassId, dice);
            m.DraftStep = "done";
            m.Status = "waiting";
            PushDraft(m);
        }

        // --- run: rounds ------------------------------------------------------------------------------------------

        private void NextRound()
        {
            round++;
            if (round >= 6) { Finish(); return; }
            phase = "intro";
            pairs = Kind == "duel" ? RunRules.Pairings(seats.Count, Stage) : new int[0][];
            foreach (Member m in seats) { m.Status = "waiting"; m.GameId = ""; m.Last = ""; }
            deadline = now() + Timing.Intro;
            PushRun();
        }

        private SeatSetup SeatFor(Member m, string name = null) => new SeatSetup
        {
            Pid = name == null ? m.Pid : "", Name = name ?? m.Nick, HeroId = RunCatalog.Heroes[m.ClassId], HeroPowerId = m.PowerId,
            Health = RunRules.Stages[Stage].HeroHealth + m.BonusHealth, Deck = new List<string>(m.Deck), Passives = new List<string>(m.Treasures),
        };

        private void StartRound()
        {
            RunRules.Stage st = RunRules.Stages[Stage];
            phase = Kind;
            deadline = now() + (Kind == "boss" ? st.BossClock : st.DuelClock) * PaceFactor;
            if (Kind == "boss")
            {
                BossDef boss = RunCatalog.Boss(bosses[Stage]);
                Card hero = Cards.FromId(boss.HeroId);
                string power = Cards.FromAssetId(hero[GameTag.HERO_POWER]).Id;
                foreach (Member m in seats)
                {
                    var bossSeat = new SeatSetup { Name = hero.Name, HeroId = boss.HeroId, Health = boss.Health, Deck = new List<string>(bossDecks[Stage]) };
                    if (boss.PassivePower) bossSeat.Passives.Add(power);
                    Begin("boss", 1, SeatFor(m), bossSeat, st.BossClock, st.TurnSeconds);
                }
            }
            else
            {
                foreach (int[] pair in pairs)
                {
                    Member a = seats[pair[0]];
                    if (pair[1] < 0)
                    {
                        Member source = dice.Pick(seats.Where(x => x != a).ToList());
                        Begin("echo", 1 + dice.Next(2), SeatFor(a), SeatFor(source, "Echo of " + source.Nick), st.DuelClock, st.TurnSeconds);
                    }
                    else Begin("duel", 1 + dice.Next(2), SeatFor(a), SeatFor(seats[pair[1]]), st.DuelClock, st.TurnSeconds);
                }
            }
            PushRun();
        }

        private void Begin(string kind, int first, SeatSetup one, SeatSetup two, int clock, int turn)
        {
            var setup = new DuelSetup
            {
                GameId = "g" + nextGame++, Kind = kind, Stage = Stage, Seed = 1 + dice.Next(int.MaxValue - 1), First = first,
                ClockSeconds = (int)(clock * PaceFactor), TurnSeconds = (int)Math.Max(1, turn * PaceFactor), Seats = new[] { one, two },
            };
            var game = new HostGame { Session = new DuelSession(setup) };
            double t = now();
            game.ClockEnd = t + clock * PaceFactor;
            game.TurnEnd = t + turn * PaceFactor;
            game.AiAt = t + Timing.AiDelay;
            game.TurnSeen = game.Session.Game.Turn;
            games[setup.GameId] = game;
            string packed = setup.Pack();
            for (int i = 0; i < 2; i++)
            {
                Member m = Find(setup.Seats[i].Pid);
                if (m == null) continue;
                m.GameId = setup.GameId;
                m.Status = "fighting";
                foreach (HostGame other in games.Values) other.Watchers.Remove(m.Pid); // your own game always wins over watching
                Send(m, "game", setup.GameId, i.ToString(), packed, "");
            }
        }

        private static int SeatOf(HostGame g, Member m) => g.Session.Setup.Seats[0].Pid == m.Pid ? 0 : g.Session.Setup.Seats[1].Pid == m.Pid ? 1 : -1;

        private IEnumerable<Member> Audience(HostGame g)
        {
            foreach (SeatSetup s in g.Session.Setup.Seats) { Member m = Find(s.Pid); if (m != null) yield return m; }
            foreach (string pid in g.Watchers) { Member m = Find(pid); if (m != null) yield return m; }
        }

        private void Act(Member m, string[] f)
        {
            if (f.Length < 4 || !games.TryGetValue(f[1], out HostGame g) || g.Session.Over) { Send(m, "error", "That game is not running."); return; }
            int seat = SeatOf(g, m);
            if (seat < 0 || seat != g.Session.ActiveSeat) { Send(m, "error", "It is not your move."); return; }
            if (!int.TryParse(f[2], out int seq) || seq != g.Session.Log.Count || !int.TryParse(f[3], out int index) || !g.Session.Apply(index))
            {
                Send(m, "error", "That move is not possible right now.");
                // The engine can refuse a move it listed. If it has refused as many as there are, nothing can be played:
                // call the game rather than leave two people staring at it.
                if (g.Session.Broken || ++g.Refused >= Math.Max(1, g.Session.Moves().Count)) Stalled(g);
                return;
            }
            Applied(g, index);
        }

        // A move went into the host's copy: tell everyone following this game, then see where that leaves it.
        private void Applied(HostGame g, int index)
        {
            DuelSession s = g.Session;
            foreach (Member a in Audience(g)) Send(a, "step", s.Setup.GameId, (s.Log.Count - 1).ToString(), index.ToString());
            if (s.Game.Turn != g.TurnSeen)
            {
                g.TurnSeen = s.Game.Turn;
                g.ActionsThisTurn = 0;
                g.TurnEnd = now() + s.Setup.TurnSeconds;
            }
            g.AiAt = now() + Timing.AiDelay;
            g.Refused = 0;
            if (s.Over) Ended(g);
        }

        // The host ended the game itself (clock, stall, concession): every copy applies the same call.
        private void Call(HostGame g, string reason)
        {
            if (!g.Session.Over) g.Session.ResolveTimeout(reason);
            foreach (Member a in Audience(g)) Send(a, "call", g.Session.Setup.GameId, g.Session.Winner.ToString(), reason);
            Ended(g);
        }

        private void Stalled(HostGame g)
        {
            string card = g.Session.PendingChoiceCard();
            Say("A game was called on health: the rules engine could not continue" + (card.Length > 0 ? " after " + (Cards.FromId(card)?.Name ?? card) + " (" + card + ")" : "") + ".");
            Call(g, "stalled");
        }

        private bool AiSeat(HostGame g, int seat)
        {
            Member m = Find(g.Session.Setup.Seats[seat].Pid);
            return m == null || !m.Connected;
        }

        private void Tick()
        {
            double t = now();
            if (phase == "draft" && (t >= deadline || seats.All(m => m.DraftStep == "done" || !m.Connected)))
            {
                foreach (Member m in seats) if (m.DraftStep != "done") AutoDraft(m);
                NextRound();
            }
            else if (phase == "intro" && t >= deadline) StartRound();
            else if (phase == "standings" && t >= deadline) NextRound();
            if (phase != "boss" && phase != "duel") return;

            foreach (HostGame g in games.Values.ToArray())
            {
                DuelSession s = g.Session;
                if (s.Over) continue;
                if (t >= g.ClockEnd) { Call(g, "time ran out"); continue; }
                bool ai = AiSeat(g, s.ActiveSeat);
                if (!(ai && t >= g.AiAt) && t < g.TurnEnd) continue;
                // Either the AI's move is due or a player ran out their turn clock (then the turn is ended for them).
                int end = s.EndTurnMove();
                int choice = (!ai || ++g.ActionsThisTurn > 40) && end >= 0 ? end : s.AiChoice();
                int applied = s.ApplyAny(choice);
                if (applied < 0) Stalled(g);
                else Applied(g, applied);
            }
            foreach (Member m in seats) if (m.Status == "looting" && t >= m.LootDeadline) while (m.Status == "looting") PickLoot(m, 0);
            if (seats.All(m => m.Status == "waiting"))
            {
                if (phase == "boss") NextRound();
                else { phase = "standings"; deadline = t + (round == 5 ? Math.Min(4, Timing.Standings) : Timing.Standings); PushRun(); }
            }
        }

        private void Ended(HostGame g)
        {
            DuelSession s = g.Session;
            RunRules.Stage st = RunRules.Stages[s.Setup.Stage];
            for (int i = 0; i < 2; i++)
            {
                Member m = Find(s.Setup.Seats[i].Pid);
                if (m == null) continue;
                string result = s.Winner < 0 ? "draw" : s.Winner == i ? "win" : "loss";
                m.Last = result + ":" + s.Setup.Seats[1 - i].Name;
                m.GameId = "";
                if (s.Setup.Kind == "duel")
                {
                    if (result == "win") { m.Wins++; m.Score += st.WinPoints; }
                    else if (result == "draw") { m.Draws++; m.Score += RunRules.DrawPoints; }
                    else { m.Losses++; m.BonusHealth += RunRules.GritHealth; }
                    m.Status = "waiting";
                }
                else if (s.Setup.Kind == "echo")
                {
                    if (result == "win") m.BonusHealth += RunRules.EchoHealth;
                    m.Status = "waiting";
                }
                else
                {
                    if (result == "win") m.BossWins++;
                    OfferLoot(m, result == "win");
                }
            }
            if (s.Setup.Kind == "duel")
                Say(s.Winner < 0 ? s.Setup.Seats[0].Name + " and " + s.Setup.Seats[1].Name + " drew (+" + RunRules.DrawPoints + " each)."
                    : s.Setup.Seats[s.Winner].Name + " beat " + s.Setup.Seats[1 - s.Winner].Name + " (+" + st.WinPoints + ").");
            else if (s.Setup.Kind == "boss")
                Say(s.Winner == 0 ? s.Setup.Seats[0].Name + " defeated " + s.Setup.Seats[1].Name + "." : s.Setup.Seats[0].Name + " fell to " + s.Setup.Seats[1].Name + ".");
            foreach (string pid in g.Watchers) Send(Find(pid), "watchover", s.Setup.GameId);
            g.Watchers.Clear();
            PushRun();
        }

        // --- loot -------------------------------------------------------------------------------------------------

        private void OfferLoot(Member m, bool won)
        {
            List<Card> pool = RunCatalog.Pool(m.ClassId).Where(c => !(c.Rarity == Rarity.LEGENDARY && m.Deck.Contains(c.Id))).ToList();
            List<Card> own = pool.Where(c => c.Class != CardClass.NEUTRAL).ToList(), neutral = pool.Where(c => c.Class == CardClass.NEUTRAL).ToList();
            List<string> Ids(IEnumerable<Card> cards) => cards.Select(c => c.Id).ToList();
            m.LootWon = won;
            m.LootTreasures = won ? dice.Sample(RunCatalog.Treasures.Where(t => !m.Treasures.Contains(t)), 3) : new List<string>();
            m.LootBundles = new List<List<string>>
            {
                Ids(dice.Sample(own, RunRules.LootCards)), Ids(dice.Sample(neutral, RunRules.LootCards)),
                Ids(dice.Sample(own, 2).Concat(dice.Sample(neutral, 1))),
            };
            m.Status = "looting";
            m.LootDeadline = now() + Timing.Loot;
            PushLoot(m);
            if (!m.Connected) while (m.Status == "looting") PickLoot(m, 0);
        }

        private void PushLoot(Member m) => Send(m, "lootoffer", m.Status != "looting" ? "done" : m.LootTreasures.Count > 0 ? "treasure" : "cards",
            m.LootWon ? "1" : "0", string.Join(",", m.LootTreasures ?? new List<string>()),
            string.Join("|", (m.LootBundles ?? new List<List<string>>()).Select(b => string.Join(",", b))));

        private void PickLoot(Member m, int index)
        {
            if (m.Status != "looting") { Send(m, "error", "There is nothing to pick."); return; }
            if (m.LootTreasures.Count > 0)
            {
                if (index < 0 || index >= m.LootTreasures.Count) { Send(m, "error", "Pick one of the treasures."); return; }
                m.Treasures.Add(m.LootTreasures[index]);
                m.LootTreasures = new List<string>();
            }
            else
            {
                if (index < 0 || index >= m.LootBundles.Count) { Send(m, "error", "Pick one of the bundles."); return; }
                m.Deck.AddRange(m.LootBundles[index]);
                m.Status = "waiting";
            }
            PushLoot(m);
            PushRun();
        }

        // --- spectating -------------------------------------------------------------------------------------------

        // A player who is between games may follow any game still going. They get the setup and every move so far,
        // then each new move as it happens; their own next game starting ends the watching automatically.
        private void Watch(Member m, string gameId)
        {
            if (m.Status != "waiting" || (phase != "boss" && phase != "duel")) { Send(m, "error", "You can spectate while you wait for the others."); return; }
            if (!games.TryGetValue(gameId, out HostGame g) || g.Session.Over) { Send(m, "error", "That game is already over."); return; }
            foreach (HostGame other in games.Values) other.Watchers.Remove(m.Pid);
            g.Watchers.Add(m.Pid);
            Send(m, "game", gameId, "-1", g.Session.Setup.Pack(), string.Join(",", g.Session.Log));
        }

        // --- end --------------------------------------------------------------------------------------------------

        private void Finish()
        {
            List<Member> order = seats.OrderByDescending(m => m.Score).ThenByDescending(m => m.BossWins).ToList();
            var f = new List<string> { "final", order.Count.ToString() };
            int place = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Member m = order[i];
                if (i > 0 && (m.Score != order[i - 1].Score || m.BossWins != order[i - 1].BossWins)) place = i;
                RunReward reward = RunReward.For(place, m.Wins, m.Losses, m.Draws, m.BossWins);
                f.AddRange(new[] { m.Pid, m.Nick, place.ToString(), m.Score.ToString(), m.Wins.ToString(), m.Losses.ToString(), m.Draws.ToString(), m.BossWins.ToString() });
                if (!m.Gone) Send(m, "reward", reward.Place.ToString(), reward.Gold.ToString(), reward.Xp.ToString(), string.Join(",", reward.Titles));
            }
            All(f.ToArray());
            Say("Run over. " + order[0].Nick + " takes first place.");
            // Back to the lobby with whoever is still here.
            phase = "lobby";
            round = -1;
            games.Clear();
            members.RemoveAll(m => m.Gone);
            foreach (Member m in members) { m.Ready = false; m.Status = "lobby"; }
            if (Find(hostPid) == null) hostPid = members.Count > 0 ? members[0].Pid : "";
            PushLobby();
        }
    }
}
