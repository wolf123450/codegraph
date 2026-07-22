# codegraph

Code relationship visualization for Godot 4 C# projects. Two halves:

1. **extractor/** — Roslyn console tool. Loads your `.csproj`/`.sln`, walks the semantic
   model, parses `.tscn` files and `project.godot`, and emits a single `graph.json`.
2. **viewer/** — a single `index.html` using d3-force. Physics playground: nodes repel,
   edges act as springs with a minimum rest length, drag anything, group bubbles,
   edge-kind styling, role color coding, node size by file size.

## Quick start

```bash
# Extract (requires .NET 8 SDK; run from anywhere)
cd extractor
dotnet run -- /path/to/YourGame.csproj --root /path/to/godot/project --out ../viewer/graph.json

# View
cd ../viewer
python3 -m http.server 8080
# open http://localhost:8080
```

The viewer also accepts a `graph.json` via the file picker or drag-and-drop, so you can
open `index.html` directly from disk without a server. With no data it loads a small
embedded sample so you can see the physics immediately.

## graph.json schema (v1)

```jsonc
{
  "meta": {
    "schemaVersion": 1,
    "generatedAt": "2026-07-20T00:00:00Z",
    "projectRoot": "/abs/path/to/godot/project"
  },
  "nodes": [
    {
      "id": "MyGame.Player",            // fully qualified type name, or "scene:<relpath>"
      "label": "Player",
      "kind": "class",                  // class | interface | struct | enum | scene
      "file": "scripts/actors/Player.cs", // relative to projectRoot; scenes: the .tscn path
      "bytes": 4821,                    // summed across partial-class files
      "loc": 176,                       // non-empty lines, summed across partials
      "role": "object",                 // manager | object | ui | data | utility | interface | scene | plain
      "group": "actors",                // grouping key (directory / namespace / override)
      "isStatic": false,
      "baseType": "CharacterBody2D",    // immediate base, display name
      "godotBase": "Node",              // nearest Godot ancestor category: Node|Control|Resource|RefCounted|GodotObject|null
      "signalsDeclared": ["HealthChanged"],  // [Signal] delegates (EventHandler suffix stripped)
      "signalsEmitted": ["HealthChanged"],   // EmitSignal / EmitSignalX call sites found in this type
      "scenes": ["scenes/player.tscn"]  // scenes this script is attached to
    }
  ],
  "edges": [
    // kind semantics:
    //  inherits    A : B (both project types)
    //  implements  A : IB
    //  calls       method invocation or object creation; weight = call-site count
    //  composes    field/property holds a project type (incl. one level of generic args)
    //  signal      A listens to a signal/event declared on B ("signal" field = name).
    //              Sources: C# `+=` on [Signal]-generated events and plain C# events,
    //              Connect(...) calls, and .tscn [connection] entries.
    //  sceneAttach scene:<path> -> script type attached in that scene
    //  nodeRef     GetNode<T>/GetNodeOrNull<T>/FindChild-style typed scene lookups
    { "from": "MyGame.Player", "to": "MyGame.IDamageable", "kind": "implements" },
    { "from": "MyGame.Player", "to": "MyGame.Inventory",   "kind": "calls", "weight": 7 },
    { "from": "MyGame.HUD",    "to": "MyGame.Player",      "kind": "signal", "signal": "HealthChanged" }
  ]
}
```

Notes on the abstraction (deliberate imprecision):

- Multiple types in one file each report the full file's bytes/LOC. Fine for a size
  *impression*; don't treat it as a metric.
- Signal edges point listener → declarer. Emission is recorded as node metadata
  (`signalsEmitted`) rather than edges, because the emitter usually doesn't know its
  audience — that decoupling is the point of signals, and the graph reflects it.
- Plain C# `event` subscriptions are also emitted as `signal` edges: architecturally
  they play the same role. If you want them distinguished, split the kind in
  `RoslynExtractor.cs` where the `+=` handler is resolved.
- Object creation (`new Foo()`) is folded into `calls` weight.

## codegraph.config.json (optional, `--config`)

```jsonc
{
  "groupBy": "directory",          // "directory" (default) | "namespace"
  "groupDepth": 2,                 // path segment used when groupBy=directory (0-based)
  "roles":  { "MyGame.AudioDirector": "manager" },   // per-type overrides
  "groups": { "MyGame.SaveService": "infrastructure" },
  "rolePathPrefixes": { "scripts/ui/": "ui", "scripts/data/": "data" }
}
```

Role heuristics without overrides: autoloads in `project.godot` → `manager`;
nearest Godot base `Resource` → `data`, `Control` → `ui`, `Node` → `object`;
static classes → `utility`; interfaces → `interface`; everything else → `plain`.

## Known fiddly bits / TODOs

- **Cross-scene signal resolution.** `.tscn` `[connection]` entries are resolved to the
  scripts attached to the named nodes *within the same scene file*. Connections to
  nodes deeper in instanced sub-scenes are skipped (logged to stderr).
- **Generics / extension methods.** Call edges resolve through the semantic model, so
  most generic instantiations land on the right type, but exotic cases (reflection,
  `Callable.From` indirection, `CallDeferred("method_name")` string calls) are invisible.
- **Source generators.** Godot's generated partials (`*.g.cs` under `obj/`) are excluded
  from size counts and syntax walking; their symbols still participate in resolution.
- **First run of MSBuildWorkspace** needs the same .NET SDK your Godot project targets;
  watch stderr for `[workspace]` diagnostics if the load is incomplete.
