namespace GlassLink.Sim;

/// <summary>
/// Only one thing drives the sim's camera at a time: the automatic pop-out or Learn. Held from the first camera move
/// until the user's view is back. Before this, a Learn started during a pop-out moved the camera together with it, and
/// the pop-out's own Right-Alt+click was stored as the user's click point (#37).
/// </summary>
public static class CameraLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>What holds the camera now ("" when free), for the status page and error messages.</summary>
    public static string Owner { get; private set; } = "";

    public static bool TryEnter(string owner, int timeoutMs = 0)
    {
        if (!Gate.Wait(timeoutMs))
        {
            return false;
        }

        Owner = owner;
        return true;
    }

    public static void Exit()
    {
        Owner = "";
        Gate.Release();
    }
}
