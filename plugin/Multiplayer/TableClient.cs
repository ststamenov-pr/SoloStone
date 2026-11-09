// SPDX-License-Identifier: AGPL-3.0-or-later
// The player's side of a table. It keeps what the host last said (lobby, chat, run, offers) and a copy of the
// game the player is in or is spectating. Call Pump() regularly from one thread; read the public state after.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace SoloStone.Multiplayer
{
    public sealed class LobbyPlayer { public string Pid, Nick; public bool Ready, Connected; }
    public sealed class ChatLine { public string Pid, Nick, Text; public bool System; }

    public sealed class RunRow
    {
        public string Pid, Nick, ClassId, Status, GameId, Last;
        public int Score, Wins, Losses, Draws, BossWins;
        public List<string> Treasures = new List<string>();
    }

    public sealed class LiveGame { public string GameId, Kind, NameA, NameB; public int LifeA, LifeB; }

    public sealed class FinalRow { public string Pid, Nick; public int Place, Score, Wins, Losses, Draws, BossWins; }

    public sealed class TableClient : IDisposable
    {
        private readonly Link link;

        public string Pid = "", Code = "", HostPid = "", Pace = "standard";
        public bool InRun, Kicked;
        public bool Closed => link.Closed;
        public readonly List<LobbyPlayer> Players = new List<LobbyPlayer>();
        public readonly List<ChatLine> Chat = new List<ChatLine>();
        public readonly List<string> Errors = new List<string>();

        // run
        public string Phase = "lobby", Kind = "", BossId = "";
        public int Round = -1, Stage, SecondsLeft;
        public readonly List<RunRow> Rows = new List<RunRow>();
        public readonly List<LiveGame> LiveGames = new List<LiveGame>();
        public List<FinalRow> Final;
        public RunReward Reward;

        // offers
        public string DraftStep = "", ClassId = "", PowerId = "";
        public List<string> ClassOffers = new List<string>(), PowerOffers = new List<string>();
        public string LootStep = "done";
        public bool LootWon;
        public List<string> LootTreasures = new List<string>();
        public List<List<string>> LootBundles = new List<List<string>>();

        /// <summary>The game this player is in (MySeat 0 or 1), or null.</summary>
        public DuelSession Game;
        public int MySeat = -1;
        /// <summary>The game this player is spectating, or null.</summary>
        public DuelSession Watching;
        /// <summary>Set when a replica disagrees with the host; the game is then unusable and should be reported.</summary>
        public string Desync;

        public bool IsHost => Pid.Length > 0 && Pid == HostPid;
        public RunRow Me => Rows.FirstOrDefault(r => r.Pid == Pid);
        public bool MyMove => Game != null && !Game.Over && Game.ActiveSeat == MySeat;

        /// <summary>Joins the lobby behind a join code. Throws if the code is not a local-network lobby.</summary>
        public static TableClient Join(string code, string nick)
        {
            if (!JoinCode.TryDecode(code, out IPEndPoint endPoint)) throw new ArgumentException("That is not a valid lobby code.");
            return new TableClient(endPoint, nick);
        }

        public TableClient(IPEndPoint endPoint, string nick)
        {
            // Multiplayer stays on the local network. This is the only place the mod opens a connection.
            if (!LanAddress.IsLocal(endPoint.Address)) throw new ArgumentException("Multiplayer only connects to local network addresses.");
            var tcp = new TcpClient(endPoint.AddressFamily);
            tcp.Connect(endPoint);
            link = new Link(tcp);
            link.Send("hello", Wire.Clean(nick, 16), Wire.Version.ToString());
        }

        public void Dispose() => link.Dispose();

        // --- what the player can do -------------------------------------------------------------------------------

        public void SetNick(string nick) => link.Send("nick", nick);
        public void SetReady(bool ready) => link.Send("ready", ready ? "1" : "0");
        public void Say(string text) => link.Send("chat", text);
        public void SetPace(string pace) => link.Send("pace", pace);
        public void Kick(string pid) => link.Send("kick", pid);
        public void Start() => link.Send("start");
        public void Leave() { link.Send("leave"); link.Dispose(); }
        public void DraftClass(string classId) => link.Send("draft", "class", classId);
        public void DraftPower(string powerId) => link.Send("draft", "power", powerId);
        public void DraftDeck(IEnumerable<string> deck) => link.Send("draft", "deck", string.Join(",", deck));
        public void PickLoot(int index) => link.Send("loot", index.ToString());
        /// <summary>Plays the move with this OptionId from Game.Moves(). It takes effect when the host echoes it back.</summary>
        public void Act(int index) { if (Game != null) link.Send("act", Game.Setup.GameId, Game.Log.Count.ToString(), index.ToString()); }
        public void Concede() { if (Game != null) link.Send("concede", Game.Setup.GameId); }
        public void Watch(string gameId) => link.Send("watch", gameId);
        public void Unwatch() { Watching = null; link.Send("unwatch"); }

        // --- what the host says -----------------------------------------------------------------------------------

        /// <summary>Applies everything the host sent since the last call. Returns how many messages were handled.</summary>
        public int Pump()
        {
            int n = 0;
            while (link.Inbox.TryDequeue(out string[] f))
            {
                n++;
                try { Handle(f); }
                catch (Exception e) { Errors.Add("Bad message from the host (" + f[0] + "): " + e.Message); }
            }
            return n;
        }

        private static List<string> Csv(string s) => s.Length == 0 ? new List<string>() : s.Split(',').ToList();

        private DuelSession For(string gameId) =>
            Game != null && Game.Setup.GameId == gameId ? Game : Watching != null && Watching.Setup.GameId == gameId ? Watching : null;

        private void Handle(string[] f)
        {
            switch (f[0])
            {
                case "welcome": Pid = f[1]; Code = f[2]; break;
                case "error": Errors.Add(f[1]); break;
                case "kicked": Kicked = true; break;
                case "chat":
                    Chat.Add(new ChatLine { Pid = f[1], Nick = f[2], Text = f[3], System = f[4] == "1" });
                    if (Chat.Count > 200) Chat.RemoveAt(0);
                    break;
                case "lobby":
                    HostPid = f[1]; Pace = f[2]; InRun = f[3] == "1"; Code = f[4];
                    Players.Clear();
                    for (int i = 5; i + 3 < f.Length; i += 4) Players.Add(new LobbyPlayer { Pid = f[i], Nick = f[i + 1], Ready = f[i + 2] == "1", Connected = f[i + 3] == "1" });
                    if (!InRun) { Phase = "lobby"; Game = null; Watching = null; DraftStep = ""; LootStep = "done"; }
                    else { Final = null; Reward = null; }
                    break;
                case "run":
                {
                    string before = Phase;
                    Phase = f[1]; Round = int.Parse(f[2]); Stage = int.Parse(f[3]); Kind = f[4]; BossId = f[5]; SecondsLeft = int.Parse(f[6]);
                    int count = int.Parse(f[7]), at = 8;
                    Rows.Clear();
                    for (int i = 0; i < count; i++, at += 12)
                        Rows.Add(new RunRow
                        {
                            Pid = f[at], Nick = f[at + 1], ClassId = f[at + 2], Score = int.Parse(f[at + 3]), Wins = int.Parse(f[at + 4]),
                            Losses = int.Parse(f[at + 5]), Draws = int.Parse(f[at + 6]), BossWins = int.Parse(f[at + 7]), Status = f[at + 8],
                            GameId = f[at + 9], Treasures = Csv(f[at + 10]), Last = f[at + 11],
                        });
                    int live = int.Parse(f[at++]);
                    LiveGames.Clear();
                    for (int i = 0; i < live; i++, at += 6)
                        LiveGames.Add(new LiveGame { GameId = f[at], Kind = f[at + 1], NameA = f[at + 2], NameB = f[at + 3], LifeA = int.Parse(f[at + 4]), LifeB = int.Parse(f[at + 5]) });
                    // A new round: whatever was on screen from the last one is done with.
                    if (Phase == "intro" && before != "intro") { Game = null; Watching = null; }
                    break;
                }
                case "draftoffer":
                    DraftStep = f[1]; ClassOffers = Csv(f[2]); ClassId = f[3]; PowerOffers = Csv(f[4]); PowerId = f[5];
                    break;
                case "lootoffer":
                    LootStep = f[1]; LootWon = f[2] == "1"; LootTreasures = Csv(f[3]);
                    LootBundles = f[4].Length == 0 ? new List<List<string>>() : f[4].Split('|').Select(Csv).ToList();
                    break;
                case "game":
                {
                    var session = new DuelSession(DuelSetup.Unpack(f[3]));
                    foreach (string step in Csv(f[4])) if (!session.Apply(int.Parse(step))) Desync = "replay of " + f[1];
                    int seat = int.Parse(f[2]);
                    if (seat >= 0) { Game = session; MySeat = seat; Watching = null; }
                    else Watching = session;
                    break;
                }
                case "step":
                {
                    DuelSession s = For(f[1]);
                    if (s == null) break;
                    if (int.Parse(f[2]) != s.Log.Count || !s.Apply(int.Parse(f[3]))) Desync = "move " + f[2] + " of " + f[1];
                    break;
                }
                case "call":
                {
                    DuelSession s = For(f[1]);
                    if (s == null) break;
                    if (f[3] == "conceded") { if (!s.Over) s.Concede(1 - int.Parse(f[2])); }
                    else s.ResolveTimeout(f[3]);
                    if (s.Winner != int.Parse(f[2])) Desync = "result of " + f[1];
                    break;
                }
                case "watchover":
                    break; // the finished game stays readable in Watching until the player moves on
                case "final":
                {
                    int count = int.Parse(f[1]);
                    Final = new List<FinalRow>();
                    for (int i = 0, at = 2; i < count; i++, at += 8)
                        Final.Add(new FinalRow { Pid = f[at], Nick = f[at + 1], Place = int.Parse(f[at + 2]), Score = int.Parse(f[at + 3]), Wins = int.Parse(f[at + 4]), Losses = int.Parse(f[at + 5]), Draws = int.Parse(f[at + 6]), BossWins = int.Parse(f[at + 7]) });
                    break;
                }
                case "reward":
                    Reward = new RunReward { Place = int.Parse(f[1]), Gold = int.Parse(f[2]), Xp = int.Parse(f[3]), Titles = Csv(f[4]) };
                    break;
            }
        }
    }

    /// <summary>
    /// Plays for a player with the built-in AI: drafts the suggested deck, takes the first loot, makes the AI's
    /// move when it is its turn, and spectates while it waits. Used by the tests and the terminal table.
    /// </summary>
    public sealed class AutoPlayer
    {
        private readonly TableClient client;
        private readonly Dice dice;
        private string askedClass = "", askedPower = "", askedDeck = "", askedLoot = "", askedMove = "", askedWatch = "";
        public int GamesWatched, MovesWatched;
        private int watchedLog = -1, attempt, errorsSeen;

        public AutoPlayer(TableClient client, int seed) { this.client = client; dice = new Dice(seed); }

        public void Tick()
        {
            TableClient c = client;
            if (c.Phase == "lobby") askedClass = askedPower = askedDeck = askedLoot = "";
            string runKey = c.Round + "/" + c.Phase;
            if (c.Phase == "draft")
            {
                if (c.DraftStep == "class" && askedClass != runKey) { askedClass = runKey; c.DraftClass(dice.Pick(c.ClassOffers)); }
                else if (c.DraftStep == "power" && askedPower != runKey) { askedPower = runKey; c.DraftPower(dice.Pick(c.PowerOffers)); }
                else if (c.DraftStep == "deck" && askedDeck != runKey) { askedDeck = runKey; c.DraftDeck(RunCatalog.SuggestedDeck(c.ClassId, dice)); }
            }
            if (c.LootStep != "done")
            {
                string key = c.Round + c.LootStep;
                if (askedLoot != key) { askedLoot = key; c.PickLoot(0); }
            }
            if (c.MyMove)
            {
                string key = c.Game.Setup.GameId + "#" + c.Game.Log.Count;
                if (askedMove != key) { askedMove = key; attempt = 0; errorsSeen = c.Errors.Count; c.Act(c.Game.AiChoice()); }
                else if (c.Errors.Count > errorsSeen)
                {
                    // The host turned the move down (the engine can refuse a move it listed): try the others in order.
                    errorsSeen = c.Errors.Count;
                    var moves = c.Game.Moves();
                    if (attempt < moves.Count) c.Act(moves[attempt++].OptionId);
                }
            }
            // Done for this round? Watch someone who is still playing.
            RunRow me = c.Me;
            bool waiting = me != null && me.Status == "waiting" && (c.Phase == "boss" || c.Phase == "duel");
            if (waiting && (c.Watching == null || c.Watching.Over) && c.LiveGames.Count > 0)
            {
                string target = c.LiveGames[0].GameId;
                if (askedWatch != target) { askedWatch = target; GamesWatched++; watchedLog = -1; c.Watch(target); }
            }
            if (c.Watching != null && c.Watching.Log.Count != watchedLog)
            {
                if (watchedLog >= 0) MovesWatched += c.Watching.Log.Count - watchedLog;
                watchedLog = c.Watching.Log.Count;
            }
        }
    }
}
