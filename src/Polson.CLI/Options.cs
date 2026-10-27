namespace Polson.CLI;

using System;
using CommandLine;

public class Options
{
    #region Properties
    [Option("debug", Required = false, HelpText = "Enable debug logging.")]
    public bool Debug { get; set; }

    [Option("options", Required = false, HelpText = "Additional options string.")]
    public string AdditionalOptions { get; set; } = string.Empty;
    #endregion
}

[Verb("server", isDefault: true, HelpText = "Start the Polson MCP server in stdio or HTTP mode.")]
public class ServerOptions : Options
{
    #region Properties
    [Option("http", Required = false, HelpText = "Enable the MCP server HTTP transport instead of default stdio.")]
    public bool Http { get; set; }

    [Option("port", Required = false, HelpText = "HTTP listening port (default: 5000 or from appsettings.json).")]
    public int? Port { get; set; }

    [Option("project-dir", Required = false, HelpText = "Working project directory for scene artifacts and exports.")]
    public string ProjectDir { get; set; } = string.Empty;

    [Option("timeout", Required = false, HelpText = "Script execution timeout in seconds (default: 30).")]
    public int? Timeout { get; set; }
    #endregion
}

[Verb("preserve-chatlog", Hidden = true, HelpText = "Hook: read the host's hook payload from stdin and copy its chat transcript into the project's events/ directory. Wired in by create-project.")]
public class PreserveChatlogOptions : Options
{
    #region Properties
    [Option("host", Required = false, HelpText = "Which host's transcript store to search when the payload does not name a transcript: 'agy' or 'claude'. Baked in by create-project.")]
    public string Host { get; set; } = string.Empty;

    [Option("project-dir", Required = false, HelpText = "The project to preserve into. Baked in by create-project, because a hook runs in whatever directory its host chose and not necessarily the project's.")]
    public string ProjectDir { get; set; } = string.Empty;
    #endregion
}

[Verb("report", HelpText = "Summarise what actually happened in a project run, from its event log.")]
public class ReportOptions : Options
{
    #region Properties
    [Value(0, MetaName = "project-dir", Required = true, HelpText = "The project directory to report on.")]
    public string ProjectDir { get; set; } = string.Empty;

    [Option("json", Required = false, HelpText = "Emit the report as JSON rather than as a table.")]
    public bool Json { get; set; }
    #endregion
}

[Verb("techniques", HelpText = "List the techniques a production can request or deny (create-project --request / --deny): their ids, what each asks for, and what in a run shows it was used.")]
public class TechniquesOptions : Options
{
    #region Properties
    [Option("workflow", Required = false, HelpText = "Only the techniques that apply to this workflow.")]
    public string Workflow { get; set; } = string.Empty;

    [Option("json", Required = false, HelpText = "Emit the catalogue as JSON, for the studio's form.")]
    public bool Json { get; set; }
    #endregion
}

[Verb("compare", HelpText = "Set 2 to 6 finished films side by side in one MP4, each labelled and held on its last frame until the longest ends: one audience's film beside another's.")]
public class CompareOptions : Options
{
    #region Properties
    [Value(0, MetaName = "output", Required = true, HelpText = "The .mp4 to write.")]
    public string Output { get; set; } = string.Empty;

    // A file, not arguments: the labels are a director's words (a place, a language), and words on a command line mean quoting.
    [Option("films", Required = true, HelpText = "A JSON file listing the films in order: [{\"path\": \"a.mp4\", \"title\": \"Audience A · Dubai\", \"detail\": \"in Arabic\"}, ...].")]
    public string Films { get; set; } = string.Empty;

    [Option("width", Required = false, Default = 1920, HelpText = "The video's width in pixels, 640 to 3840. 3840 keeps four 1080p films at full size.")]
    public int Width { get; set; }

    [Option("sound", Required = false, Default = 0, HelpText = "The film, counting from 1, whose sound the video keeps. 0, the default, is silent: several narrations at once cannot be listened to.")]
    public int Sound { get; set; }
    #endregion
}

[Verb("reset", HelpText = "Clear a project's previous run so it can be run again, keeping the brief, the instructions and everything else that was generated.")]
public class ResetOptions : Options
{
    #region Properties
    [Value(0, MetaName = "project-dir", Required = true, HelpText = "The project directory to clear.")]
    public string ProjectDir { get; set; } = string.Empty;

    // Not `--archive`, which is what archiving would be called if it were the option rather than the
    // behaviour. It is the default here for the reason `create-project --reset` made it the default
    // there: deleting a run once cost the only measurements that said what a change was worth.
    [Option("delete", Required = false, HelpText = "Delete the previous run instead of archiving it under previous/. Irreversible — the default moves everything aside and keeps it.")]
    public bool Delete { get; set; }
    #endregion
}

[Verb("create-project", HelpText = "Generate a self-contained design project directory for an agent to work in.")]
public class CreateProjectOptions : Options
{
    #region Properties
    [Value(0, MetaName = "directory", Required = true, HelpText = "Parent directory the project directory is created in.")]
    public string Directory { get; set; } = string.Empty;

    [Value(1, MetaName = "id", Required = true, HelpText = "Project id, and the name of the directory created (letters, digits, dot, underscore, dash).")]
    public string Id { get; set; } = string.Empty;

    [Value(2, MetaName = "sdk", Required = false, HelpText = "Agent SDK to target: 'agy' (Google Antigravity, the default) or 'claude' (Claude Code). It decides what the config files are called. Omit it for an Antigravity project.")]
    public string Sdk { get; set; } = string.Empty;

    // No `Default`, deliberately: the parser cannot distinguish an omitted flag from one typed with
    // the default value, and regenerating an existing project needs that distinction — see
    // ProjectGenerator's workflow resolution. Omitted means "logo" for a new project and "whatever
    // this project already is" for one being reset.
    [Option("workflow", Required = false, HelpText = "Workflow template to generate: 'logo' (the default), 'infographic', 'drawing', 'comic', 'painting', 'comic_studio' (multi-agent) or 'harness'. Workflows are discovered from the embedded templates, so an unknown name lists what is actually available. Omitted on --reset or --force, the workflow recorded in project.json is kept.")]
    public string Workflow { get; set; } = string.Empty;

    [Option("type", Required = false, HelpText = "Narrows the chosen workflow's direction. Which types exist depends on the workflow and 'comic_studio' has none, so an unknown name lists the ones on offer. Omitted on --reset or --force, the type recorded in project.json is kept when the workflow is unchanged.")]
    public string Type { get; set; } = string.Empty;

    [Option("prompt", Required = false, HelpText = "The subject, in a line — a short form of --brief. Treated as untrusted data and quoted into brief.md exactly as --brief is.")]
    public string Prompt { get; set; } = string.Empty;

    [Option("document", Required = false, HelpText = "Source material to copy into the project's documents/ folder — a PDF of a report, a CSV, a brand guide, a reference clip. Give one or more paths after the flag. Each must be a type Documents.ask can read; a file already there under the same name is kept if identical and refused if not.")]
    public IEnumerable<string> Documents { get; set; } = [];

    [Option("request", Required = false, Separator = ',', HelpText = "Techniques the agent must use in this production, by id, comma-separated: e.g. alternative-grid,brand-language. `polson techniques --workflow <name>` lists them. Written into the instructions and project.json, and checked by `polson report`. Kept across --reset unless --request or --deny is given again.")]
    public IEnumerable<string> Request { get; set; } = [];

    [Option("deny", Required = false, Separator = ',', HelpText = "Techniques the agent must not use in this production, by id, comma-separated: e.g. mystery-reveal. They override the workflow's own defaults. See --request.")]
    public IEnumerable<string> Deny { get; set; } = [];

    [Option("deadline", Required = false, HelpText = "Minutes the commission is allowed to take, written into project.json and stated in the instructions so the agent can plan against it. Omit it for the workflow's own default — a logo is quarter of an hour, a study painting is two. Pass 0 for no deadline.")]
    public int? Deadline { get; set; }

    [Option("test", Required = false, HelpText = "Run this workflow as an evaluation of the framework as well as a commission: the agent reports friction, errors and gaps in findings.md, and reading Polson's own source is denied so the run can say whether the published API was sufficient. Applies to any workflow, each of which exercises a different part of the stack.")]
    public bool Test { get; set; }

    [Option("auto", Required = false, HelpText = "Autonomous mode: the agent asks the director nothing and waits for no one. Where the workflow would ask, it takes the answer it would have proposed and records the decision for review afterwards. For a director who wants to see what a workflow makes and leave it alone; omit it for the full director-in-the-loop run. Not kept across --reset unless given again.")]
    public bool Auto { get; set; }

    [Option("inline-manuals", Required = false, HelpText = "The default, and kept so that commands which ask for it still parse: the workflow's judgment manuals are written into the instructions file — for Claude Code as files under manuals/ that CLAUDE.md imports. See --no-inline-manuals.")]
    public bool InlineManuals { get; set; }

    [Option("no-inline-manuals", Required = false, HelpText = "Leave the workflow's judgment manuals to be searched for instead of writing them into the instructions file. Inlining is the default since 2026-10-20: pointing at a manual has never reliably produced a read — `polson://manual/index` was never opened in any run, and a Gemini logo run read none of its manuals — while an open model given them inline began critiquing and reworking in their terms. They cost tokens on every turn, but they are a stable prefix, so a host that caches pays a fraction after the first turn.")]
    public bool NoInlineManuals { get; set; }

    /// <summary>Whether the judgment manuals go into the instructions: unless `--no-inline-manuals` says otherwise.</summary>
    public bool ManualsInlined => InlineManuals || !NoInlineManuals;

    [Option("standalone", Required = false, HelpText = "Also generate what the Polson orchestrator needs to host the agent itself. Without it the project is managed by a desktop or IDE host.")]
    public bool Standalone { get; set; }

    [Option("brief", Required = false, HelpText = "Path to a file holding the client brief. Use --prompt to pass the text itself. Treated as untrusted data and normalised before it is written.")]
    public string Brief { get; set; } = string.Empty;

    [Option("force", Required = false, HelpText = "Generate into a directory that already has contents. Overwrites every generated file, including brief.md.")]
    public bool Force { get; set; }

    [Option("reset", Required = false, HelpText = "Clear a previous run — events/, scripts/ and artifacts/ — and regenerate every generated file including the instructions, keeping only brief.md as you wrote it.")]
    public bool Reset { get; set; }
    #endregion
}

[Verb("eval", HelpText = "Execute a JavaScript drawing file and save rendered image and SVG output.")]
public class EvalOptions : Options
{
    #region Properties
    [Value(0, MetaName = "script-file", Required = false, HelpText = "Path to the JavaScript script file or raw code to execute. Omit it when using --svg-in.")]
    public string ScriptFile { get; set; } = string.Empty;

    [Option("svg-in", Required = false, HelpText = "Render an existing .svg file instead of running a script. Useful for inspecting an artifact a run left behind.")]
    public string SvgIn { get; set; } = string.Empty;

    [Option("width", Required = false, Default = 800, HelpText = "Viewport width in pixels (default: 800).")]
    public int Width { get; set; } = 800;

    [Option("height", Required = false, Default = 600, HelpText = "Viewport height in pixels (default: 600).")]
    public int Height { get; set; } = 600;

    [Option("format", Required = false, Default = "webp", HelpText = "Output image format ('webp', 'png', 'jpeg'; default: 'webp').")]
    public string Format { get; set; } = "webp";

    [Option("quality", Required = false, Default = 85, HelpText = "Image encoding quality (1-100; default: 85).")]
    public int Quality { get; set; } = 85;

    [Option("img", Required = false, HelpText = "Custom output path for the rendered image file (defaults to output.<format>).")]
    public string? OutImg { get; set; }

    [Option("svg", Required = false, Default = "output.svg", HelpText = "Output path for the rendered SVG XML file.")]
    public string? OutSvg { get; set; } = "output.svg";

    [Option("pdf", Required = false, HelpText = "Also write a print PDF of the paper, with bleed and print boxes (needs --pdf-size). The paper is the trimmed page.")]
    public string? OutPdf { get; set; }

    [Option("pdf-size", Required = false, HelpText = "Trimmed page for --pdf: 'A0'-'A6', 'B0'-'B3', 'letter', 'legal', 'tabloid', or '420x594mm' / '18x24in'.")]
    public string? PdfSize { get; set; }

    [Option("pdf-bleed", Required = false, HelpText = "Bleed for --pdf, with a unit (default: '3mm'; '0.125in' in the US; '0' for none).")]
    public string? PdfBleed { get; set; }

    [Option("pdf-safe-margin", Required = false, HelpText = "How far inside the trim text must stay for --pdf (default: '5mm'; '0' to skip).")]
    public string? PdfSafeMargin { get; set; }

    [Option("sif", Required = false, HelpText = "Also write the last Motion.composition the script built as Synfig's .sif, pictures beside it. A layer Synfig cannot hold is refused by name.")]
    public string? OutSif { get; set; }
    #endregion
}
