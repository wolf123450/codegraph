using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeGraph.Extractor;

/// <summary>
/// Two-pass extraction over one or more Roslyn projects.
/// Pass 1 (CollectTypesAsync): register every user-source type as a graph node,
/// accumulating file sizes across partials and [Signal] declarations.
/// Pass 2 (ExtractEdgesAsync): walk method bodies + member declarations for edges.
/// Run pass 1 over ALL projects before running pass 2 on any of them, so
/// cross-project references resolve.
/// </summary>
public sealed class RoslynExtractor
{
    private readonly GraphBuilder _graph;
    private readonly string _root;
    private readonly Config _config;

    // symbol identity -> node id, populated in pass 1
    private readonly Dictionary<INamedTypeSymbol, string> _typeIds =
        new(SymbolEqualityComparer.Default);

    public RoslynExtractor(GraphBuilder graph, string projectRoot, Config? config = null)
    {
        _graph = graph;
        _root = Path.GetFullPath(projectRoot);
        _config = config ?? new Config();
    }

    private static bool IsGenerated(string? path) =>
        path is null
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private string Rel(string absolute)
    {
        var full = Path.GetFullPath(absolute);
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(_root, full).Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    // ---------------------------------------------------------------- pass 1

    public async Task CollectTypesAsync(Project project)
    {
        foreach (var doc in project.Documents)
        {
            if (IsGenerated(doc.FilePath)) continue;
            if (doc.FilePath is not null && _config.IsExcluded(Rel(doc.FilePath))) continue;
            var model = await doc.GetSemanticModelAsync();
            var rootNode = await doc.GetSyntaxRootAsync();
            if (model is null || rootNode is null || doc.FilePath is null) continue;

            foreach (var decl in rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol sym) continue;
                if (sym.ContainingType is not null) continue; // skip nested types for now (abstraction, not census)

                var id = TypeId(sym);
                _typeIds[sym] = id;

                var kind = sym.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    _ => "class",
                };
                var node = _graph.GetOrAddNode(id, sym.Name, kind);
                node.IsStatic = sym.IsStatic;
                node.BaseType = sym.BaseType is { SpecialType: not SpecialType.System_Object } bt
                    ? bt.Name : null;
                node.GodotBase = NearestGodotBase(sym);

                var rel = Rel(doc.FilePath);
                if (!node.Files.Contains(rel))
                {
                    node.Files.Add(rel);
                    node.File ??= rel;
                    var text = await doc.GetTextAsync();
                    node.Bytes += text.Length;
                    node.Loc += text.Lines.Count(l => !string.IsNullOrWhiteSpace(l.ToString()));
                }
                _graph.FileToTypeId.TryAdd(rel, id);

                // [Signal] delegate declarations
                foreach (var member in sym.GetMembers().OfType<INamedTypeSymbol>()
                             .Where(m => m.TypeKind == TypeKind.Delegate))
                {
                    if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "SignalAttribute"))
                        node.SignalsDeclared.Add(StripEventHandler(member.Name));
                }
            }
        }
    }

    private static string TypeId(INamedTypeSymbol sym) =>
        sym.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string StripEventHandler(string name) =>
        name.EndsWith("EventHandler", StringComparison.Ordinal) ? name[..^"EventHandler".Length] : name;

    /// Walk the base chain to the nearest recognizable Godot ancestor category.
    private static string? NearestGodotBase(INamedTypeSymbol sym)
    {
        for (var bt = sym.BaseType; bt is not null; bt = bt.BaseType)
        {
            switch (bt.Name)
            {
                case "Resource": return "Resource";
                case "Control": return "Control";
                case "Node": case "Node2D": case "Node3D": case "CanvasItem": return "Node";
                case "RefCounted": return "RefCounted";
                case "GodotObject": return "GodotObject";
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- pass 2

    public async Task ExtractEdgesAsync(Project project)
    {
        foreach (var doc in project.Documents)
        {
            if (IsGenerated(doc.FilePath)) continue;
            var model = await doc.GetSemanticModelAsync();
            var rootNode = await doc.GetSyntaxRootAsync();
            if (model is null || rootNode is null) continue;

            foreach (var decl in rootNode.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol sym) continue;
                if (!_typeIds.TryGetValue(sym, out var fromId)) continue;

                ExtractHierarchy(sym, fromId);
                ExtractMembers(sym, fromId);
                new BodyWalker(this, model, fromId, _graph.Nodes[fromId], decl).Visit(decl);
            }
        }
    }

    private void ExtractHierarchy(INamedTypeSymbol sym, string fromId)
    {
        if (sym.BaseType is not null && _typeIds.TryGetValue(sym.BaseType.OriginalDefinition, out var baseId))
            _graph.AddEdge(fromId, baseId, EdgeKind.Inherits);
        foreach (var iface in sym.Interfaces)
            if (_typeIds.TryGetValue(iface.OriginalDefinition, out var ifaceId))
                _graph.AddEdge(fromId, ifaceId, EdgeKind.Implements);
    }

    private void ExtractMembers(INamedTypeSymbol sym, string fromId)
    {
        foreach (var member in sym.GetMembers())
        {
            var type = member switch
            {
                IFieldSymbol f => f.Type,
                IPropertySymbol p => p.Type,
                _ => null,
            };
            if (type is null) continue;
            foreach (var referenced in Unwrap(type))
                if (_typeIds.TryGetValue(referenced.OriginalDefinition, out var toId) && toId != fromId)
                    _graph.AddEdge(fromId, toId, EdgeKind.Composes);
        }
    }

    /// Yield a type plus one level of generic arguments / array elements
    /// (List<Enemy>, Enemy[], Dictionary<string, ItemData> all count).
    private static IEnumerable<INamedTypeSymbol> Unwrap(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arr) type = arr.ElementType;
        if (type is not INamedTypeSymbol named) yield break;
        yield return named;
        foreach (var arg in named.TypeArguments.OfType<INamedTypeSymbol>())
            yield return arg;
    }

    // ------------------------------------------------------------- body walk

    private sealed class BodyWalker : CSharpSyntaxWalker
    {
        private readonly RoslynExtractor _x;
        private readonly SemanticModel _model;
        private readonly string _fromId;
        private readonly GraphNode _fromNode;
        private readonly SyntaxNode _root;

        public BodyWalker(RoslynExtractor x, SemanticModel model, string fromId, GraphNode fromNode, SyntaxNode root)
        {
            _x = x; _model = model; _fromId = fromId; _fromNode = fromNode; _root = root;
        }

        // Don't descend into NESTED type declarations; they're handled (or skipped) separately.
        // The root declaration itself must still be walked, so only no-op for non-root nodes.
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (node == _root) base.VisitClassDeclaration(node);
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            if (node == _root) base.VisitStructDeclaration(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            base.VisitInvocationExpression(node);
            if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method) return;
            var name = method.Name;

            // --- Godot signal emission -------------------------------------
            if (name == "EmitSignal")
            {
                var sig = SignalNameFromArg(node.ArgumentList.Arguments.FirstOrDefault());
                if (sig is not null) _fromNode.SignalsEmitted.Add(sig);
                return;
            }
            if (name.StartsWith("EmitSignal", StringComparison.Ordinal) && name.Length > "EmitSignal".Length)
            {
                _fromNode.SignalsEmitted.Add(name["EmitSignal".Length..]); // 4.3+ generated per-signal emitters
                return;
            }

            // --- Connect("sig", callable) ----------------------------------
            if (name == "Connect")
            {
                var sig = SignalNameFromArg(node.ArgumentList.Arguments.FirstOrDefault());
                var targetType = node.Expression is MemberAccessExpressionSyntax ma
                    ? _model.GetTypeInfo(ma.Expression).Type
                    : method.ContainingType;
                if (sig is not null && targetType is INamedTypeSymbol nt
                    && _x._typeIds.TryGetValue(nt.OriginalDefinition, out var declId))
                    _x._graph.AddEdge(_fromId, declId, EdgeKind.Signal, sig);
                return;
            }

            // --- typed scene lookups ---------------------------------------
            if (name is "GetNode" or "GetNodeOrNull" or "FindChild" && method.TypeArguments.Length == 1)
            {
                if (method.TypeArguments[0] is INamedTypeSymbol t
                    && _x._typeIds.TryGetValue(t.OriginalDefinition, out var refId) && refId != _fromId)
                    _x._graph.AddEdge(_fromId, refId, EdgeKind.NodeRef);
                return;
            }

            // --- ordinary call edge ----------------------------------------
            AddCall(method.ContainingType);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            base.VisitObjectCreationExpression(node);
            if (_model.GetSymbolInfo(node).Symbol is IMethodSymbol ctor)
                AddCall(ctor.ContainingType); // folded into `calls` (see README)
        }

        public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            base.VisitAssignmentExpression(node);
            if (!node.IsKind(SyntaxKind.AddAssignmentExpression)) return;
            if (_model.GetSymbolInfo(node.Left).Symbol is not IEventSymbol ev) return;
            if (!_x._typeIds.TryGetValue(ev.ContainingType.OriginalDefinition, out var declId)) return;
            // Covers both [Signal]-generated events and plain C# events; both are
            // decoupled listen relationships, which is what we're charting.
            _x._graph.AddEdge(_fromId, declId, EdgeKind.Signal, ev.Name);
        }

        private void AddCall(INamedTypeSymbol? containing)
        {
            if (containing is null) return;
            if (_x._typeIds.TryGetValue(containing.OriginalDefinition, out var toId) && toId != _fromId)
                _x._graph.AddEdge(_fromId, toId, EdgeKind.Calls);
        }

        /// Extracts "Foo" from `SignalName.Foo`, `SomeType.SignalName.Foo`, or "foo" literal.
        private static string? SignalNameFromArg(ArgumentSyntax? arg) => arg?.Expression switch
        {
            MemberAccessExpressionSyntax ma when ma.Expression.ToString().EndsWith("SignalName")
                => ma.Name.Identifier.Text,
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression)
                => ToPascal(lit.Token.ValueText),
            _ => null,
        };

        private static string ToPascal(string snake) =>
            string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }
}
