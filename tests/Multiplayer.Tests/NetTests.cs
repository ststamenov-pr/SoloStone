// SPDX-License-Identifier: AGPL-3.0-or-later
// Lobby and whole-run tests: a real host and real clients talking over loopback TCP.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using SoloStone.Multiplayer;
using static Program;

internal static partial class Tests
{
    static partial void AddNet(Action<string, Action> add)
    {
        add("lobby: codes, nicknames, chat, ready, kick, limits", Lobby);
        add("lobby: nothing outside the local network gets in or out", LocalOnly);
        foreach (int n in new[] { 2, 3, 4 }) add("a full run with " + n + " players, with spectating", () => FullRun(n));
        add("a player who drops mid-run is played by the AI", DropOut);
        add("an idle player cannot stall the table", Idle);
    }

    private static Timing Fast() => new Timing { Draft = 5, Intro = 0, Loot = 5, Standings = 0, AiDelay = 0 };

    private static TableHost Host(Timing timing = null, int seed = 5) => new TableHost(IPAddress.Loopback, 0, timing ?? Fast(), null, seed);

    // Pumps the host and the clients until `done`, or fails after `seconds`.
    private static void Until(TableHost host, IList<TableClient> clients, Func<bool> done, string what, double seconds = 20, Action each = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            host.Pump();
            foreach (TableClient c in clients) c.Pump();
            each?.Invoke();
            if (done()) return;
            if (watch.Elapsed.TotalSeconds > seconds)
            {
                TableClient c = clients.Count > 0 ? clients[0] : null;
                throw new Exception("Timed out waiting for: " + what + (c == null ? "" : " [phase " + c.Phase + " round " + c.Round + " rows "
                    + string.Join(",", c.Rows.Select(r => r.Status)) + " live " + c.LiveGames.Count + " errors "
                    + string.Join(" / ", clients.SelectMany(x => x.Errors).Distinct().Take(4)) + "]"));
            }
            Thread.Sleep(1);
        }
    }

    private static void Lobby()
    {
        using (TableHost host = Host())
        {
            Check(JoinCode.TryDecode(host.Code, out IPEndPoint ep) && ep.Port == host.EndPoint.Port, "the code names this lobby");
            var ana = TableClient.Join(host.Code, "  Ana  ");
            var all = new List<TableClient> { ana };
            Until(host, all, () => ana.Players.Count == 1, "host joined");
            Check(ana.IsHost && ana.Players[0].Nick == "Ana" && ana.Code == host.Code, "first player is the host");
            var ben = TableClient.Join(host.Code.ToLowerInvariant(), "ana");
            all.Add(ben);
            Until(host, all, () => ben.Players.Count == 2, "second player joined");
            Equal("ana 2", ben.Players[1].Nick, "a taken nickname gets a number");

            ben.SetNick("Ben\tthe" + (char)0x200B + "Bold and Very Long Name");
            ben.Say("hello\nthere");
            Until(host, all, () => ana.Chat.Any(c => !c.System && c.Text == "hello there"), "chat arrives cleaned");
            Equal("Ben theBold and", ana.Players[1].Nick, "nickname is cleaned and cut to 16");
            Check(ana.Chat.Any(c => c.System && c.Text.Contains("is now")), "renames are announced");
            Check(ana.Chat.Last(c => !c.System).Nick.StartsWith("Ben"), "chat carries the nickname");

            ben.Start();
            Until(host, all, () => ben.Errors.Count > 0, "non-host cannot start");
            ana.Start();
            Until(host, all, () => ana.Errors.Any(e => e.Contains("ready")), "cannot start until everyone is ready");
            ben.SetReady(true);
            Until(host, all, () => ana.Players[1].Ready, "ready shows");

            var cy = TableClient.Join(host.Code, "Cy"); var di = TableClient.Join(host.Code, "Di"); var ed = TableClient.Join(host.Code, "Ed");
            all.AddRange(new[] { cy, di, ed });
            Until(host, all, () => ed.Errors.Any(e => e.Contains("full")) || cy.Errors.Any(e => e.Contains("full")) || di.Errors.Any(e => e.Contains("full")), "fifth player is refused");
            Equal(4, host.PlayerCount, "four seats");

            TableClient kicked = new[] { cy, di, ed }.First(c => c.Pid.Length > 0);
            ana.Kick(kicked.Pid);
            Until(host, all, () => kicked.Kicked && ana.Players.Count == 3, "kick");
            ana.SetPace("quick");
            Until(host, all, () => ben.Pace == "quick", "pace");
            ben.Leave();
            Until(host, all, () => ana.Players.Count == 2, "leave");
            foreach (TableClient c in all) c.Dispose();
        }
    }

    private static void LocalOnly()
    {
        bool threw = false;
        try { new TableClient(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 80), "x").Dispose(); } catch (ArgumentException) { threw = true; }
        Check(threw, "the client refuses a public address before connecting");
        threw = false;
        try { new TableHost(IPAddress.Parse("8.8.8.8"), 0).Dispose(); } catch (ArgumentException) { threw = true; }
        Check(threw, "the host refuses to listen on a public address");
        threw = false;
        try { TableClient.Join("ZZZZZ-ZZZZZ", "x"); } catch (ArgumentException) { threw = true; }
        Check(threw, "a bad code is refused");
        using (TableHost host = Host())
        {
            // A caller speaking another version is turned away.
            var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect(host.EndPoint);
            var raw = new Link(tcp);
            raw.Send("hello", "Old", "0");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (raw.Inbox.IsEmpty && watch.Elapsed.TotalSeconds < 5) { host.Pump(); Thread.Sleep(1); }
            Check(raw.Inbox.TryDequeue(out string[] reply) && reply[0] == "error", "version mismatch is an error");
            Equal(0, host.PlayerCount, "they did not get a seat");
            raw.Dispose();
        }
    }

    private sealed class Table : IDisposable
    {
        public TableHost Host;
        public List<TableClient> Clients = new List<TableClient>();
        public List<AutoPlayer> Bots = new List<AutoPlayer>();
        public void Dispose() { foreach (TableClient c in Clients) c.Dispose(); Host.Dispose(); }
    }

    private static Table Seat(int n, Timing timing = null, int seed = 5, int bots = -1)
    {
        var t = new Table { Host = Host(timing, seed) };
        for (int i = 0; i < n; i++)
        {
            var c = TableClient.Join(t.Host.Code, "Player " + (i + 1));
            t.Clients.Add(c);
            Until(t.Host, t.Clients, () => c.Pid.Length > 0, "join");
            if (bots < 0 || i < bots) t.Bots.Add(new AutoPlayer(c, seed * 10 + i));
        }
        foreach (TableClient c in t.Clients.Skip(1)) c.SetReady(true);
        Until(t.Host, t.Clients, () => t.Clients[0].Players.Count == n && t.Clients[0].Players.Skip(1).All(p => p.Ready), "ready");
        t.Clients[0].Start();
        return t;
    }

    private static void FullRun(int n)
    {
        using (Table t = Seat(n, seed: 20 + n))
        {
            var phases = new HashSet<string>();
            bool sawWaitingWhileOthersPlay = false;
            Until(t.Host, t.Clients, () => t.Clients.All(c => c.Final != null && c.Reward != null && !c.InRun), "the run to finish", 90, () =>
            {
                foreach (AutoPlayer bot in t.Bots) bot.Tick();
                TableClient c0 = t.Clients[0];
                phases.Add(c0.Phase);
                if ((c0.Phase == "boss" || c0.Phase == "duel") && c0.Rows.Any(r => r.Status == "waiting") && c0.Rows.Any(r => r.Status == "fighting"))
                    sawWaitingWhileOthersPlay = true;
            });
            foreach (TableClient c in t.Clients) Equal(null, c.Desync, "no copy of a game ever disagreed with the host");
            foreach (string p in new[] { "draft", "boss", "duel" }) Check(phases.Contains(p), "saw phase " + p);
            List<FinalRow> final = t.Clients[0].Final;
            Equal(n, final.Count, "standings");
            Equal(0, final[0].Place, "someone is first");
            int duels = n == 3 ? 2 : 3;
            foreach (FinalRow row in final) Equal(duels, row.Wins + row.Losses + row.Draws, row.Nick + " played every duel");
            Equal(final.Sum(r => r.Wins), final.Sum(r => r.Losses), "every duel win is someone's loss");
            int max = RunRules.Stages.Sum(s => s.WinPoints);
            foreach (FinalRow row in final) Check(row.Score >= 0 && row.Score <= max, "score is duel points only");
            foreach (TableClient c in t.Clients)
            {
                Check(c.Reward.Gold > 0 && c.Reward.Xp > 0, "everyone who stayed is paid");
                Equal(final.First(r => r.Pid == c.Pid).Place, c.Reward.Place, "reward matches the standings");
                Check(c.Phase == "lobby" && !c.InRun, "back in the lobby afterwards");
            }
            Check(sawWaitingWhileOthersPlay, "someone waited while another game was still going");
            Check(t.Bots.Sum(b => b.GamesWatched) > 0, "waiting players spectated");
            Check(t.Bots.Sum(b => b.MovesWatched) > 0, "spectators followed the moves live");
            Check(t.Clients[0].Chat.Any(c => c.System && (c.Text.Contains("defeated") || c.Text.Contains("fell to"))), "results are announced in chat");
            foreach (ChatLine line in t.Clients[0].Chat.Where(c => c.System && c.Text.Contains("called on health"))) Console.WriteLine("     note: " + line.Text);
        }
    }

    private static void DropOut()
    {
        using (Table t = Seat(3, seed: 9))
        {
            bool dropped = false;
            Until(t.Host, t.Clients, () => t.Clients[0].Final != null && t.Clients[1].Final != null, "the run to finish without the third player", 120, () =>
            {
                foreach (AutoPlayer bot in t.Bots.Take(dropped ? 2 : 3)) bot.Tick();
                if (!dropped && t.Clients[2].Game != null) { dropped = true; t.Clients[2].Dispose(); }
            });
            Equal(3, t.Clients[0].Final.Count, "they stay in the standings");
            Until(t.Host, t.Clients.Take(2).ToList(), () => !t.Clients[0].InRun, "back to the lobby");
            Check(t.Clients[0].Chat.Any(c => c.System && c.Text.Contains("The AI plays on")), "the table is told");
            Equal(2, t.Clients[0].Players.Count, "the lobby afterwards holds who stayed");
        }
    }

    private static void Idle()
    {
        // Clocks shrunk hard: the second player never drafts, never moves and never picks loot.
        var timing = new Timing { Draft = 1, Intro = 0, Loot = 0.3, Standings = 0, AiDelay = 0, ClockScale = 0.004 };
        using (Table t = Seat(2, timing, seed: 3, bots: 1))
        {
            Until(t.Host, t.Clients, () => t.Clients[0].Final != null, "the run to finish despite an idle player", 90, () => t.Bots[0].Tick());
            FinalRow idle = t.Clients[0].Final.First(r => r.Pid == t.Clients[1].Pid);
            Equal(3, idle.Wins + idle.Losses + idle.Draws, "their duels were still played out");
            Equal(null, t.Clients[1].Desync, "the idle player's copy still agrees");
        }
    }
}
