// SPDX-License-Identifier: AGPL-3.0-or-later
// usage:
//   LanTable demo [players=3] [seed]        host and seat AI players in this process, print the whole run
//   LanTable host [nick] [--auto]           host a lobby on this machine's LAN address and print the join code
//   LanTable join <CODE> [nick] [--auto]    join a lobby on the local network
// In host/join mode, a line you type is sent as chat; "/ready", "/start", "/nick <name>", "/watch <game>" and
// "/quit" do what they say. With --auto the built-in AI drafts and plays for you.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SabberStoneCore.Model;
using SoloStone.Multiplayer;

internal static class Program
{
    private static string CardName(string id) => string.IsNullOrEmpty(id) ? "" : Cards.FromId(id)?.Name ?? id;

    // Prints what changed for one client since the last look.
    private sealed class Narrator
    {
        private readonly TableClient c;
        private readonly string tag;
        private int chat, errors, turn = -1;
        private string phase = "", game = "", watching = "", draft = "", loot = "";
        private bool final, paid;
        public Narrator(TableClient client, string tag) { c = client; this.tag = tag; }

        private void Line(string text) => Console.WriteLine((tag.Length > 0 ? "[" + tag + "] " : "") + text);

        public void Look(bool tableWide)
        {
            if (tableWide)
            {
                for (; chat < c.Chat.Count; chat++)
                    Console.WriteLine(c.Chat[chat].System ? "  * " + c.Chat[chat].Text : "  <" + c.Chat[chat].Nick + "> " + c.Chat[chat].Text);
                string key = c.Phase + "/" + c.Round;
                if (key != phase)
                {
                    phase = key;
                    if (c.Phase == "draft") Console.WriteLine("\n=== Draft ===");
                    else if (c.Phase == "intro" && c.Kind == "boss") Console.WriteLine("\n=== Stage " + (c.Stage + 1) + ": boss fight against " + CardName(c.BossId) + " ===");
                    else if (c.Phase == "intro") Console.WriteLine("\n=== Stage " + (c.Stage + 1) + ": duels, a win is worth " + RunRules.Stages[c.Stage].WinPoints + " ===");
                    else if (c.Phase == "standings")
                        Console.WriteLine("  standings: " + string.Join("   ", c.Rows.OrderByDescending(r => r.Score).Select(r => r.Nick + " " + r.Score)));
                }
                if (c.Final != null && !final)
                {
                    final = true;
                    Console.WriteLine("\n=== Final standings ===");
                    foreach (FinalRow r in c.Final)
                        Console.WriteLine("  " + (r.Place + 1) + ". " + r.Nick.PadRight(16) + " " + r.Score + " pts   duels " + r.Wins + "-" + r.Losses + "-" + r.Draws + "   bosses " + r.BossWins + "/3");
                }
                if (c.Final == null) final = false;
            }
            for (; errors < c.Errors.Count; errors++) Line("! " + c.Errors[errors]);
            string d = c.DraftStep + c.ClassId + c.PowerId;
            if (c.Phase == "draft" && d != draft)
            {
                draft = d;
                if (c.DraftStep == "class") Line("offered classes: " + string.Join(", ", c.ClassOffers));
                else if (c.DraftStep == "power") Line("picked " + c.ClassId + "; hero powers on offer: " + string.Join(", ", c.PowerOffers.Select(CardName)));
                else if (c.DraftStep == "deck") Line("picked hero power " + CardName(c.PowerId) + "; building a " + RunRules.StarterDeckSize + "-card deck");
                else if (c.DraftStep == "done") Line("deck locked in");
            }
            string l = c.Round + c.LootStep;
            if (l != loot)
            {
                loot = l;
                if (c.LootStep == "treasure") Line("won! treasures on offer: " + string.Join(", ", c.LootTreasures.Select(CardName)));
                else if (c.LootStep == "cards") Line((c.LootWon ? "" : "lost, no treasure; ") + "card bundles: " + string.Join("  |  ", c.LootBundles.Select(b => string.Join(", ", b.Select(CardName)))));
            }
            if (c.Game != null && c.Game.Setup.GameId != game)
            {
                game = c.Game.Setup.GameId;
                turn = -1;
                DuelSetup s = c.Game.Setup;
                Line("game " + game + " (" + s.Kind + "): " + s.Seats[0].Name + " [" + CardName(s.Seats[0].HeroId) + "] vs " + s.Seats[1].Name + " [" + CardName(s.Seats[1].HeroId) + "]"
                    + (s.Seats[c.MySeat].Passives.Count > 0 ? "   treasures: " + string.Join(", ", s.Seats[c.MySeat].Passives.Select(CardName)) : ""));
            }
            if (c.Game != null && c.Game.Setup.GameId == game)
            {
                if (c.Game.Over && turn != -2)
                {
                    turn = -2;
                    Line("game " + game + " over: " + (c.Game.Winner < 0 ? "draw" : c.Game.Winner == c.MySeat ? "WON" : "lost") + " (" + c.Game.EndReason + "), life " + c.Game.Life(0) + " / " + c.Game.Life(1));
                }
                else if (!c.Game.Over && c.Game.Game.Turn >= turn + 6)
                {
                    turn = c.Game.Game.Turn;
                    Line("  turn " + (turn + 1) / 2 + ": " + c.Game.Setup.Seats[0].Name + " " + c.Game.Life(0) + "  -  " + c.Game.Setup.Seats[1].Name + " " + c.Game.Life(1));
                }
            }
            string w = c.Watching == null ? "" : c.Watching.Setup.GameId + (c.Watching.Over ? " (over)" : "");
            if (w != watching)
            {
                watching = w;
                if (c.Watching != null && !c.Watching.Over)
                    Line("done for this round; spectating " + c.Watching.Setup.GameId + ": " + c.Watching.Setup.Seats[0].Name + " " + c.Watching.Life(0) + " vs " + c.Watching.Setup.Seats[1].Name + " " + c.Watching.Life(1)
                        + " (caught up on " + c.Watching.Log.Count + " moves)");
                else if (c.Watching != null)
                    Line("the game being spectated ended: " + (c.Watching.Winner < 0 ? "draw" : c.Watching.Setup.Seats[c.Watching.Winner].Name + " won"));
            }
            if (c.Reward == null) paid = false;
            if (c.Reward != null && !paid)
            {
                paid = true;
                Line("reward: " + c.Reward.Gold + " gold, " + c.Reward.Xp + " XP" + (c.Reward.Titles.Count > 0 ? ", titles: " + string.Join(", ", c.Reward.Titles) : ""));
            }
        }
    }

    private static int Demo(int players, int seed)
    {
        var timing = new Timing { Draft = 30, Intro = 0.2, Loot = 10, Standings = 0.2, AiDelay = 0 };
        using (var host = new TableHost(null, 0, timing, null, seed))
        {
            Console.WriteLine("Lobby hosted on " + host.EndPoint + "   join code " + host.Code);
            string[] names = { "Ana", "Ben", "Cy", "Di" };
            var clients = new List<TableClient>();
            var bots = new List<AutoPlayer>();
            var eyes = new List<Narrator>();
            for (int i = 0; i < players; i++)
            {
                clients.Add(TableClient.Join(host.Code, names[i]));
                bots.Add(new AutoPlayer(clients[i], seed * 10 + i));
                eyes.Add(new Narrator(clients[i], names[i]));
            }
            bool said = false, started = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 300)
            {
                host.Pump();
                foreach (TableClient c in clients) c.Pump();
                if (!said && clients.All(c => c.Players.Count == players))
                {
                    said = true;
                    clients[1].Say("hi all, ready when you are");
                    foreach (TableClient c in clients.Skip(1)) c.SetReady(true);
                    clients[0].Say("starting!");
                }
                if (said && !started && clients[0].Players.Skip(1).All(p => p.Ready)) { started = true; clients[0].Start(); }
                for (int i = 0; i < players; i++) { bots[i].Tick(); eyes[i].Look(i == 0); }
                if (started && clients.All(c => c.Final != null && c.Reward != null && !c.InRun))
                {
                    for (int i = 0; i < players; i++) eyes[i].Look(i == 0);
                    Console.WriteLine("\nSpectating: " + bots.Sum(b => b.GamesWatched) + " games watched, " + bots.Sum(b => b.MovesWatched) + " moves followed live. Copies that disagreed with the host: "
                        + clients.Count(c => c.Desync != null) + ".");
                    return clients.Any(c => c.Desync != null) ? 1 : 0;
                }
                Thread.Sleep(1);
            }
            Console.WriteLine("The demo did not finish in time.");
            return 1;
        }
    }

    private static int Play(TableHost host, TableClient client, bool auto)
    {
        var bot = auto ? new AutoPlayer(client, Environment.TickCount) : null;
        var eyes = new Narrator(client, "");
        var typed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        new Thread(() => { string line; while ((line = Console.ReadLine()) != null) typed.Enqueue(line); }) { IsBackground = true }.Start();
        while (!client.Closed && !client.Kicked)
        {
            host?.Pump();
            client.Pump();
            bot?.Tick();
            eyes.Look(true);
            while (typed.TryDequeue(out string line))
            {
                if (line == "/quit") { client.Leave(); return 0; }
                else if (line == "/ready") client.SetReady(true);
                else if (line == "/start") client.Start();
                else if (line.StartsWith("/nick ")) client.SetNick(line.Substring(6));
                else if (line.StartsWith("/watch ")) client.Watch(line.Substring(7).Trim());
                else if (line.Length > 0) client.Say(line);
            }
            Thread.Sleep(5);
        }
        Console.WriteLine(client.Kicked ? "The host removed you." : "The lobby closed.");
        return 0;
    }

    private static int Main(string[] args)
    {
        bool auto = args.Contains("--auto");
        string[] a = args.Where(x => x != "--auto").ToArray();
        string mode = a.Length > 0 ? a[0] : "demo";
        try
        {
            if (mode == "demo") return Demo(a.Length > 1 ? Math.Max(2, Math.Min(4, int.Parse(a[1]))) : 3, a.Length > 2 ? int.Parse(a[2]) : 7);
            if (mode == "host")
            {
                using (var host = new TableHost())
                {
                    Console.WriteLine("Hosting on " + host.EndPoint + ". Join code: " + host.Code + "   (type /start when everyone is /ready)");
                    return Play(host, TableClient.Join(host.Code, a.Length > 1 ? a[1] : "Host"), auto);
                }
            }
            if (mode == "join" && a.Length > 1) return Play(null, TableClient.Join(a[1], a.Length > 2 ? a[2] : "Guest"), auto);
        }
        catch (Exception e) { Console.WriteLine(e.Message); return 1; }
        Console.WriteLine("usage: LanTable demo [players] [seed] | host [nick] [--auto] | join <CODE> [nick] [--auto]");
        return 2;
    }
}
