using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is ["--version"])
{
    Console.WriteLine($"datum {Version()}");
    return 0;
}

if (args is ["diagnose", .. var diagnoseOptions])
{
    return await DiagnoseAsync(launch: diagnoseOptions.Contains("--launch"));
}

if (args is [("install" or "uninstall") and var command, .. var installOptions])
{
    return Install.Run(command == "install", installOptions, Version());
}

if (ParseServeOptions(args) is not { } serveOptions)
{
    Console.Error.WriteLine("""
        usage:
          datum [--all-tools] [--read-only] [--allow <folder>]... [--allow-invoke-writes]
                                                      run the MCP server on stdio
              --all-tools        list every tool up front instead of loading toolsets on demand
              --read-only        offer only tools that can't change a model or file (also DATUM_READ_ONLY=1)
              --allow <folder>   folder Datum may write to (repeatable; also DATUM_ALLOW, ;-separated;
                                 default Documents\Datum)
              --allow-invoke-writes   let invoke make calls that may change a model (also DATUM_ALLOW_INVOKE_WRITES=1)
              --allow-macros     let run_macro run SOLIDWORKS macros, which Datum can't undo
          datum diagnose [--launch]                   print SOLIDWORKS version, license and add-ins
          datum install [--client <names>] [--allow <folder>]... [--all-tools] [--read-only] [--allow-invoke-writes] [--dry-run] [--force]
                                                      add Datum to the MCP clients on this PC
                                                      (claude-code, claude-desktop, cursor, vscode, codex)
          datum uninstall [--client <names>] [--dry-run]
          datum --version
        """);
    return 1;
}

await ServeAsync(serveOptions);
return 0;

static DatumOptions? ParseServeOptions(string[] args)
{
    var allTools = false;
    var macros = false;
    var readOnly = Environment.GetEnvironmentVariable("DATUM_READ_ONLY") == "1";
    var invokeWrites = Environment.GetEnvironmentVariable("DATUM_ALLOW_INVOKE_WRITES") == "1";
    var allowed = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--all-tools":
                allTools = true;
                break;
            case "--allow" when i + 1 < args.Length:
                allowed.Add(args[++i]);
                break;
            case "--read-only":
                readOnly = true;
                break;
            case "--allow-macros":
                macros = true;
                break;
            case "--allow-invoke-writes":
                invokeWrites = true;
                break;
            default:
                return null;
        }
    }

    return new DatumOptions { AllTools = allTools, ReadOnly = readOnly, AllowInvokeWrites = invokeWrites, AllowMacros = macros, Workspace = Workspace.Create(allowed, Environment.GetEnvironmentVariable("DATUM_ALLOW")) };
}

// stdout carries the MCP protocol, so every log line has to go to stderr.
static async Task ServeAsync(DatumOptions options)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddDatum(() => new ComBackend(), options).WithStdioServerTransport();
    await builder.Build().RunAsync();
}

static async Task<int> DiagnoseAsync(bool launch)
{
    try
    {
        using var client = await SolidWorksClient.ConnectAsync(new ComBackend(), launch, TimeSpan.FromSeconds(15));
        var folders = Workspace.Create([], Environment.GetEnvironmentVariable("DATUM_ALLOW")).AllowedFolders;
        Console.Write($"Datum {Version()}\nWrites to: {string.Join("; ", folders)}\n");
        Console.Write(DiagnoseReport.Format(await client.ReadInfoAsync()));
        return 0;
    }
    catch (DatumException e)
    {
        Console.Error.WriteLine(e);
        if (e.Code == ErrorCode.NotRunning)
        {
            Console.Error.WriteLine("Or run 'datum diagnose --launch' to start it.");
        }

        return e.Code is ErrorCode.NotRunning or ErrorCode.Unreachable ? 2 : 3;
    }
}

static string Version() => typeof(DatumOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
