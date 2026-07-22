namespace MiniProject;

public class Player : Godot.Node2D
{
    [Godot.SignalAttribute]
    public delegate void DiedEventHandler();

    private readonly Logger _logger = new();

    public void TakeDamage()
    {
        _logger.Log("ouch");
        EmitSignal("died");
    }

    public void ShowHud()
    {
        var hud = GetNode<Hud>("Hud");
    }
}
