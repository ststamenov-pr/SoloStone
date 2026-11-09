// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

internal static class Program
{
    private static int failed, passed;

    internal static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("Check failed: " + what);
    }

    internal static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(what + ": expected " + expected + ", got " + actual);
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("ok   " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + "\n     " + e.Message + "\n" + e.StackTrace.Split('\n').Take(4).Aggregate("", (a, l) => a + "     " + l.Trim() + "\n")); }
    }

    private static int Main(string[] args)
    {
        // "fuzz 500": play that many AI games with random decks and list cards after which the engine could not continue.
        if (args.Length > 0 && args[0] == "fuzz") return Tests.Fuzz(args.Length > 1 ? int.Parse(args[1]) : 300);
        string only = args.Length > 0 ? args[0] : null;
        foreach (var t in Tests.All())
            if (only == null || t.Key.Contains(only, StringComparison.OrdinalIgnoreCase)) Run(t.Key, t.Value);
        Console.WriteLine(passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
