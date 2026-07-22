// Minimal stand-ins for GodotSharp types, named to match what
// RoslynExtractor.NearestGodotBase looks for by name.
namespace Godot;

public class GodotObject { }
public class RefCounted : GodotObject { }
public class Node : GodotObject
{
    public T? GetNode<T>(string path) where T : Node => default;
    public void EmitSignal(string name) { }
}
public class Node2D : Node { }
public class CanvasItem : Node { }
public class Control : CanvasItem { }
public class Resource : RefCounted { }

[System.AttributeUsage(System.AttributeTargets.Delegate)]
public class SignalAttribute : System.Attribute { }
