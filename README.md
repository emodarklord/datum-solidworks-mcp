# Datum

An MCP server that lets AI assistants (Claude, Codex, Cursor, local models) model parts in SOLIDWORKS through its COM API. You describe a part in plain English. The assistant sketches, extrudes, fillets and patterns it, checks the result in numbers and pictures, and exports it.

<p align="center"><img src="docs/images/bushing-build.gif" alt="A flanged bushing built step by step through Datum: profile sketch, revolve, one hole, a circular pattern, then fillets" width="560"></p>

Status: v0.2. Parts only. Assemblies and drawings come later.

## What it does

- **Works on the SOLIDWORKS you have.** One build covers SOLIDWORKS 2021 and later on every license, Student and Education included. `datum diagnose` reports what it found.
- **Keeps parts editable.** Sketches are fully defined by default, with dimensions from the sketch origin, so a person can change the part later in SOLIDWORKS.
- **Reports every change.** Each write replies with the volume before and after, the body count and any rebuild errors. A feature that fails or changes nothing is rolled back.
- **Can undo its own work.** Every change Datum makes is journaled, so the assistant can restore any earlier checkpoint.
- **Lets the assistant see the part.** `view_capture` returns a PNG of a standard view, or a four-view sheet drawn at one scale. It can show hidden lines, cut the view open, or number faces and edges with their handles.
- **Checks work instead of trusting a rebuild.** `model_measure` measures distances, angles and clearances, `requirements_check` compares a part with a written list and reports what is still unverified, and `model_flex` tries each dimension to find what breaks. Reports cover sizes, measured distances, material, sketch definition and flex results, and a list survives closing the part.
- **Reads parts it didn't build.** `model_summary` lists a part's features, dimensions and problems, with the likely cause of each rebuild error, and `sketch_read` reads finished sketches.
- **Builds assemblies with the same checks.** Components are placed by their origin and read back, a mate that fails is removed, and `assembly_check` reports overlaps with their volumes.
- **Makes drawings and audits them.** `drawing_check` looks for hidden lines left off, holes without centre marks or axis lines, model dimensions not shown, views or text past the border, overlapping each other or on the title block, and a BOM or balloons that don't match the assembly.
- **Keeps the tool list short.** About two dozen core tools are always listed. The rest load on demand, so idle tools don't cost tokens.

<p align="center">
  <img src="docs/images/bushing.png" alt="Flanged bushing with a through bore, four flange holes and rounded flange edges" width="32%">
  <img src="docs/images/plate.png" alt="Plate with four holes and rounded corners" width="32%">
  <img src="docs/images/bracket.png" alt="L bracket with a hole in each leg and rounded edges" width="32%">
</p>

All the images on this page were rendered by Datum's own `view_capture`, from parts it built through its tools.

## Requirements

- Windows 10 or 11, x64
- SOLIDWORKS 2021 or later, running on the same machine. Any license works.
- An MCP client that runs local servers over stdio, such as Claude Code, Claude Desktop, Cursor, VS Code or Codex.

Tested on SOLIDWORKS 2026 SP4.1 Student and SOLIDWORKS 2026 SP0 Premium.

## Install

1. Download `datum-0.4.0-win-x64.zip` from [Releases](https://github.com/emodarklord/datum-solidworks-mcp/releases) and unzip it somewhere permanent, for example `C:\Tools\datum`. The exe is self-contained, so you don't need to install .NET. It isn't code-signed, so Windows SmartScreen may warn the first time you run it.
2. Add Datum to the AI apps on your PC:

   ```
   C:\Tools\datum\datum.exe install
   ```

   It finds Claude Code, Claude Desktop, Cursor, VS Code and Codex, adds a `datum` entry to each one's settings, and leaves every other server alone. Each file is backed up first, to `%LOCALAPPDATA%\Datum\backups`. Add `--dry-run` to see what it would change, `--client cursor,codex` to pick apps, or run `datum uninstall` to take it out again. Restart the apps afterwards.
3. Start SOLIDWORKS, then check that Datum can reach it:

   ```
   C:\Tools\datum\datum.exe diagnose
   ```

   This prints the Datum version, the folder it may write to, and the SOLIDWORKS version, license and add-ins. Run `datum diagnose --launch` to start SOLIDWORKS if it isn't running.

Then ask for a part, for example: *"Make a 60 × 40 × 10 mm plate with a 6 mm hole 8 mm in from each corner, fillet the vertical edges 3 mm, and export it as plate.step."*

### Manual setup

For a client `datum install` doesn't know, add a stdio server whose command is the full path to `datum.exe`. In Claude Code's `.mcp.json` format:

```json
{
  "mcpServers": {
    "datum": { "command": "C:\Tools\datum\datum.exe", "args": [] }
  }
}
```

The Store version of Claude Desktop reads its settings from `%LOCALAPPDATA%\Packages\Claude_<id>\LocalCache\Roaming\Claude\claude_desktop_config.json`, not the `%APPDATA%\Claude` file its "Edit Config" button opens. `datum install` writes to the right one.

Options (for `datum` itself, and accepted by `datum install`):

| Option | Effect |
|---|---|
| `--allow <folder>` | An extra folder Datum may write to. Repeat it for more folders. `DATUM_ALLOW` takes a `;`-separated list. The default is `Documents\Datum`. |
| `--all-tools` | List every tool up front, for clients that don't handle `tools/list_changed`. |
| `--allow-invoke-writes` | Let `invoke` make calls that may change a model. Without it the gateway only reads. `DATUM_ALLOW_INVOKE_WRITES=1` does the same. |
| `--allow-macros` | Let `run_macro` run SOLIDWORKS macros. Datum can't undo what a macro does. |

## Tools

These are always available:

| Area | Tools |
|---|---|
| Status | `sw_status`, `find_tools`, `toolset_enable` |
| Documents | `doc_new`, `doc_open`, `doc_save`, `doc_export` (STEP, IGES, STL, Parasolid, PDF, DXF, DWG), `doc_list`, `doc_activate`, `doc_close`, `doc_allow_edit` |
| Inspection | `model_tree`, `model_info` (mass, volume, bounding box, material), `model_faces` (faces and edges with filters), `model_select`, `selection_get` |
| Sketching | `sketch_begin`, `sketch_add` (lines, centerlines, rectangles, circles, arcs, polygons, slots, polylines and points, in one batch), `sketch_constrain`, `sketch_end` |
| Features | `feature_extrude` (boss or cut) |
| Undo | `checkpoint_list`, `checkpoint_restore` |
| Viewing | `view_capture` |

These toolsets load on request (`toolset_enable`), and `find_tools` searches them:

| Toolset | Tools |
|---|---|
| `features` | `feature_revolve`, `feature_sweep`, `feature_rib`, `feature_loft`, `feature_fillet`, `feature_chamfer`, `feature_hole`, `feature_pattern` (linear or circular), `plane_create`, `axis_create`, `feature_mirror`, `feature_shell` |
| `edit` | `sketch_edit`, `try_start`, `try_diff`, `try_end`, `model_dimensions`, `dimension_set`, `feature_suppress`, `feature_delete`, `rebuild`, `checkpoint_create`, `material_set`, `appearance_set`, `property_list`, `property_set` |
| `check` | `model_measure`, `sketch_read`, `model_summary`, `model_flex`, `requirements_set`, `requirements_check` |
| `assembly` | `component_add`, `component_list`, `component_fix`, `component_move`, `mate_add`, `mate_list`, `assembly_check` (interference, free components, mate errors, missing files), `assembly_bom`, `config_list`, `config_add`, `config_activate`, `explode_create`, `explode_show` |
| `motion` | `motion_new`, `motion_motor`, `motion_path`, `motion_run`, `motion_export`, `motion_turntable` |
| `drawing` | `drawing_new`, `view_add`, `view_list`, `view_set`, `view_delete`, `sheet_set`, `view_entities`, `view_section`, `view_detail`, `view_auxiliary`, `dimensions_import`, `dimension_list`, `dimension_add`, `dimension_tolerance`, `marks_add`, `note_add`, `annotation_list`, `annotation_move`, `annotation_delete`, `title_set`, `bom_add`, `bom_read`, `balloons_add`, `explode_lines`, `drawing_check` |
| `weldment` | `weldment_profiles`, `weldment_member`, `weldment_trim`, `cut_list` |
| `sheetmetal` | `sheet_base`, `sheet_bend`, `sheet_hem`, `sheet_flatten`, `sheet_export_flat` (DXF or DWG, with the outline size read back) |
| `intent` | `intent_export` (this session's modelling steps as JSON) |
| `diagnostics` | `sw_addins` |
| `api` | `api_search` (find members from plain words), `run_macro` (off unless started with `--allow-macros`), `api_members` (an interface's members as signatures, read from your installed SOLIDWORKS), `invoke` (call any API member: methods, property reads and writes, `out` parameters, results as handles) |

Tools that the connected SOLIDWORKS can't run (because of its version, license or add-ins) are hidden. `find_tools` says why.

Conventions:
- Lengths are in mm and angles in degrees.
- Faces, edges and features come back as short handles (`face#12`, `edge#40`, `feat#7`). Other tools accept them, and they stay the same across rebuilds.
- Errors read `CODE: message. Hint: what to do`.

## Benchmark

Three Claude models each built 30 parts through Datum alone, and all 30 passed for all three. A part passes on its volume, box and body count, measured from the saved file against answers worked out by hand. The parts are single solids with one clear answer, so this shows the tools work, not that any model is better. Method and caveats are in [benchmark/results.md](benchmark/results.md).

## How well tested each part is

"Live" means checked against a running SOLIDWORKS, in automated live tests and by an AI client building real parts. Everything is also covered by offline tests that run on every commit.

| Area | Status |
|---|---|
| Connection, version and license detection, `diagnose` | Live on 2026 SP4.1 Student and 2026 SP0 Premium |
| Documents: new, open, save, export (STEP and others) | Live on both |
| Sketching, fully defined by default | Live on both |
| Revolve, hole, circular pattern, fillet | Live on both |
| Extrude and cut, chamfer, linear pattern, mirror, shell | Live on Student |
| Inspection: tree, mass properties, faces and edges, selection | Live on Student; `model_info` on both |
| Dimension edits, suppress, delete, rebuild, checkpoints | Live on Student |
| `view_capture` (views and four-view sheet) | Live on both |
| `view_capture` hidden lines, sections and handle labels | Live on Student |
| Measuring, `sketch_read`, `model_summary`, `model_flex`, requirements | Live on Student |
| Reference planes and axes, materials, custom properties | Live on Student |
| Assemblies: components, mates, interference, BOM, configurations, exploded views | Live on Student |
| Drawings: views, sections, details, dimensions, tolerances, title blocks, BOMs, balloons, `drawing_check` | Live on Student |
| `invoke` gateway | Live on Student: reads, `out` parameters, enums by name, and changing a fillet's radius through `GetDefinition` and `ModifyDefinition` |
| `datum install` | Tested offline against every client's file format; run on a real PC with Claude Code, Claude Desktop (Store build) and Codex |
| SOLIDWORKS 2021–2025 | Not tested yet. The API members Datum uses all exist in 2021; reports welcome |

## Safety

- Datum writes only inside its allowed folders: `Documents\Datum` by default, plus any you add with `--allow`.
- It changes only documents it created, or ones you release with `doc_allow_edit`. Releasing a document backs it up first.
- It won't overwrite a file or discard unsaved changes unless the call asks to. A replaced file is backed up to `%LOCALAPPDATA%\Datum\backups`.
- Checkpoints cover Datum's own changes only, not edits you make by hand. Restoring one discards the later ones, so there's no redo. A deleted feature can't be brought back, so restores stop there.
- Some SOLIDWORKS settings are switched temporarily, such as the dimension input prompt and the background colour while capturing. They're journaled and restored, even after a crash, the next time Datum connects.
- The `invoke` gateway only reads unless Datum is started with `--allow-invoke-writes`, and even then it writes only to documents Datum may edit. File, window, macro and option calls are refused in favour of the curated tools. Datum can't undo a gateway write, so checkpoints can't restore past one.

## Limitations

- Assemblies work at the top level: sub-assemblies can be added and counted, but not edited.
- Motion studies are the animation kind. A gear mate can read erratically in `motion_run` on a large assembly, and two motors tied by a gear mate don't move.
- A passing `drawing_check` doesn't prove a drawing is good, so look at the sheet too.
- Drawings cover views, dimensions, tolerances, centre marks, notes, title blocks, BOMs and balloons. GD&T frames and weld symbols aren't curated yet; `invoke` reaches them.
- SOLIDWORKS can't put centre marks on detail views, so a detail relies on its parent view's marks.
- `view_capture` renders through the SOLIDWORKS window, so that window must not be minimized. While a sketch is open, its entities don't show in the image.
- Sheet metal has no edge flange tool. An open profile in `sheet_base` or a `sheet_bend` makes the same shapes.
- `try_start` works through checkpoints rather than a real copy, so a delete during a trial can't be discarded.
- Intent replays parts, including steps on faces and edges, which it finds again by their geometry. It doesn't replay assembly steps yet.
- `run_macro` has only been tested as far as its refusals, not with a real macro.
- SOLIDWORKS grows during very long sessions. Restart it between large batches.
- Windows only, because SOLIDWORKS is.

## Building from source

You need the .NET 10 SDK and a local SOLIDWORKS install: the interop assemblies are read from `<SOLIDWORKS>\api\redist`.

```
dotnet build datum.slnx
dotnet test tests/Datum.Tests
```

Set `SolidWorksApiDir` if your install is somewhere else:

```
dotnet build datum.slnx -p:SolidWorksApiDir="D:\SOLIDWORKS\api\redist"
```

`tests/Datum.LiveTests` drives a running SOLIDWORKS and must be run on a machine that has it. CI builds and runs only the projects that don't need SOLIDWORKS.

To build the release exe:

```
dotnet publish src/Datum.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/datum
```

## Not affiliated

SOLIDWORKS is a trademark of Dassault Systèmes. This project is not affiliated with or endorsed by Dassault Systèmes.
