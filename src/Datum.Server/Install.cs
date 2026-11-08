using System.Diagnostics;
using Datum.Core;

internal static class Install
{
    // install: add or update Datum in each MCP client found on this machine; uninstall: take it out again.
    public static int Run(bool install, string[] options, string version)
    {
        if (Parse(options) is not { } parsed)
        {
            Console.Error.WriteLine($"unknown option; clients are {string.Join(", ", ClientSetup.ClientNames)}");
            return 1;
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("can't tell where datum.exe is");
        var entry = install ? new ServerEntry(exe, parsed.ServerArgs) : null;
        Console.WriteLine($"Datum {version} at {exe}{(parsed.DryRun ? " (dry run: nothing is written)" : "")}");
        if (install && IsTemporary(exe))
        {
            Console.WriteLine("  warning: datum.exe is in a downloads or temp folder; move it somewhere permanent first, or these settings break when that folder is cleaned");
        }

        var workspace = Workspace.Create([], null);
        var restart = new List<string>();
        var failed = false;
        foreach (var client in ClientSetup.Detect(ClientPaths.Current()).Where(c => parsed.Clients is null || parsed.Clients.Contains(c.Name)))
        {
            if (client.Name == "claude-code" && FindOnPath("claude") is { } cli)
            {
                failed |= !ViaClaudeCli(cli, entry, parsed.DryRun);
                restart.Add(client.Label);
                continue;
            }

            var change = ClientSetup.Plan(client, entry, parsed.Force);
            var where = client.ConfigFile is null ? "" : $" ({client.ConfigFile})";
            if (change.Kind == ChangeKind.Refused)
            {
                failed = true;
                Console.WriteLine($"  {client.Label}: not changed: {change.Message}");
                continue;
            }

            if (change.NewText is null)
            {
                Console.WriteLine($"  {client.Label}: {change.Message}{(change.Kind == ChangeKind.NotFound ? "" : where)}");
                continue;
            }

            if (parsed.DryRun)
            {
                Console.WriteLine($"  {client.Label}: would be {change.Message}{where}");
                continue;
            }

            var backup = ClientSetup.Apply(change, workspace.BackUp);
            Console.WriteLine($"  {client.Label}: {change.Message}{where}{(backup is null ? "" : $", previous file backed up to {backup}")}");
            restart.Add(client.Label);
        }

        if (restart.Count > 0 && !parsed.DryRun)
        {
            Console.WriteLine($"Restart {string.Join(", ", restart)} to {(install ? "load" : "unload")} Datum.");
        }

        return failed ? 1 : 0;
    }

    private static Options? Parse(string[] options)
    {
        HashSet<string>? clients = null;
        var serverArgs = new List<string>();
        bool dryRun = false, force = false;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--client" when i + 1 < options.Length:
                    clients = [.. options[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    if (!clients.IsSubsetOf(ClientSetup.ClientNames))
                    {
                        return null;
                    }

                    break;
                case "--allow" when i + 1 < options.Length:
                    serverArgs.AddRange(["--allow", Path.GetFullPath(options[++i])]);
                    break;
                case "--all-tools" or "--allow-invoke-writes" or "--read-only":
                    serverArgs.Add(options[i]);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    return null;
            }
        }

        return new Options(clients, serverArgs, dryRun, force);
    }

    // Claude Code rewrites ~/.claude.json while it runs, so its own CLI is the safer way in when it's installed.
    private static bool ViaClaudeCli(string cli, ServerEntry? entry, bool dryRun)
    {
        string[] remove = ["mcp", "remove", "--scope", "user", ClientSetup.ServerName];
        string[]? add = entry is null ? null : ["mcp", "add", "--scope", "user", ClientSetup.ServerName, "--", entry.Command, .. entry.Args];
        if (dryRun)
        {
            Console.WriteLine($"  Claude Code: would run claude {string.Join(" ", add ?? remove)}");
            return true;
        }

        var removed = RunCli(cli, remove) == 0;
        if (add is null)
        {
            Console.WriteLine($"  Claude Code: {(removed ? "removed" : "not installed")} (claude mcp remove)");
            return true;
        }

        var ok = RunCli(cli, add) == 0;
        Console.WriteLine(ok ? $"  Claude Code: {(removed ? "updated" : "added")} (claude mcp add --scope user)" : "  Claude Code: claude mcp add failed; run it yourself with the command above");
        return ok;
    }

    private static int RunCli(string cli, string[] arguments)
    {
        var start = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, name + ".exe"))
            .FirstOrDefault(File.Exists);

    private static bool IsTemporary(string exe) =>
        exe.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
        || exe.Contains($"{Path.DirectorySeparatorChar}Downloads{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private sealed record Options(HashSet<string>? Clients, List<string> ServerArgs, bool DryRun, bool Force);
}
