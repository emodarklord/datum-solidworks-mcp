# Benchmark results

30 single-part tasks in `tasks.json`, from a plain block to a V-pulley and a keyed shaft. Each has an expected volume and box worked out by hand.

A part passes when the saved file has one body, its volume is within 0.5% and each box size is within 0.5 mm. `Datum.Benchmark score` measures that in SOLIDWORKS, so a result doesn't depend on what a session says it did.

## Run of 2026-11-07

SOLIDWORKS 2026 SP4.1 Student, Claude Code subagents, Datum tools only.

| Model | Passed | How it ran | Time | Tool calls | Tokens |
|---|---|---|---|---|---|
| Sonnet | 30/30 | A fresh session per part | median 29 s per part | median 11 per part | median 60,006 per part |
| Haiku | 30/30 | One session for all 30 | 9.3 min in total | 274 in total | 134,394 in total |
| Opus | 30/30 | One session for all 30 | 7.0 min in total | 278 in total | 118,906 in total |

## How to read this

- All three models passed everything, so this set doesn't separate them. It shows that the tools can do these parts, not which model is best.
- These are single parts with one clear answer. Assemblies, drawings and vague requests aren't tested here.
- Sonnet's row can't be compared directly with the others: a session can learn from earlier parts in a shared run.
- Tokens include Claude Code's own instructions and tool lists. That is why a one-feature block used about 59,000 tokens.
- Sonnet first scored 29/30. The miss was an error in the answer key, not in the part: the keyway removes the top of the shaft, so the height is 29.46, not 30. The key was corrected and the same part then passed.
- Sessions had to recover from real problems, which didn't count against them. Two bugs they hit are fixed: a through cut on the far side of a sketch, and a second rectangle on the same centre.
- These runs used a Datum build from before those fixes.
- Other MCP servers are compared in the head-to-head below.

## Head-to-head, 2026-11-08

The same 30 tasks were run through two other SOLIDWORKS MCP servers and through Datum v0.5.0, using the same harness and scoring. Each task got a fresh `claude -p` session on Sonnet, with only that server's tools allowed.

| Server | Version | Passed | Median time | Median tool calls | Median tokens | Cost at API prices |
|---|---|---|---|---|---|---|
| Datum | 0.5.0 | 30/30 | 29 s | 9 | 356k | $3.54 |
| [andrewbartels1/SolidworksMCP-python](https://github.com/andrewbartels1/SolidworksMCP-python) | commit `f329da9` | 30/30 | 65 s | 16 | 631k | $5.44 |
| [chrisncm17/solidworks-mcp](https://github.com/chrisncm17/solidworks-mcp) | commit `408a182` | 22/30 | 112 s | 22 | 1.15M | $10.44 |

How it was set up:
- SOLIDWORKS 2026 SP4.1 Student, English UI. Its only part template is in inches.
- Each server was started as its README describes, with no changes to its code. chrisncm17's install script wasn't run, because it edits client configs.
- chrisncm17 saves only into its own `server\generated` folder and never overwrites, so its prompt asked for a file name instead of a path. For every server, the newest part saved under the task id during the run is the one scored.
- The harness closes every open document after each task.

What happened with chrisncm17:
- In its first full run, its SOLIDWORKS session started failing every sketch with `RPC_E_SERVERFAULT` after the third part and never recovered. Its 28 remaining tasks were rerun with SOLIDWORKS restarted before each one, which the other two servers didn't get. The table shows those reruns.
- Three parts came out 25.4 times too large in each direction. It links sizes through global variables, which take the template's units, so with an inch template 80 mm became 80 inches.
- Five parts were built correctly, or nearly, in a scratch document but never saved under the right name. A failed first attempt had left an empty file under that name, the server won't overwrite it, and the headless session wasn't allowed to delete files.

How to read this:
- These are single parts with one clear answer. They measure whether the tools can make simple geometry reliably, not assemblies, drawings or design judgement.
- Time and tokens are per part and include Claude Code's own instructions and tool lists. More tools listed up front means more tokens on every call.
- Each server got one run per task, so small differences are noise. The inch-template problem would not show up on a machine with a metric template.

## Run it yourself

```bash
dotnet run --project benchmark/Datum.Benchmark -c Release -- run benchmark/tasks.json <folder> <model> --claude <claude.exe> --datum <datum.exe>
```

`run` needs Claude Code to be logged in. `score` works on any folder of `<id>.SLDPRT` files, however they were made. `--mcp-config <file>` runs another server instead of Datum; add `--parts <folder>` and `--name-only` for a server that saves into a folder of its own. The `run` mode has been tried only as far as the login step, because the runs above used subagents.
