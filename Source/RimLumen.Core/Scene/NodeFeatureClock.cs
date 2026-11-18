using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

// The ILumenNodeFeatureBackend lifecycle of a node feature with a Duration (distortion, screen dim): Time is seconds
// since the node's last (re)start.
internal struct NodeFeatureClock
{
    internal bool Started;
    internal bool Active;
    internal bool Draining;
    internal float Time;

    // Ensures: a draining node produces no new output, including a Trigger after Stop(true).
    internal void Restart()
    {
        if (Draining) return;
        Started = true;
        Active = true;
        Time = 0f;
    }

    internal void Update(in LumenNodeFeatureFrame frame)
    {
        Active = frame.Active && !Draining;
        if (Started) Time += frame.DeltaTime;
    }

    internal void Drain()
    {
        Draining = true;
        Active = false;
    }

    // Ensures: nothing shows before the first restart; afterwards a node with duration > 0 shows while Time < duration
    //          whether or not it is still active (output already launched finishes), and a node with duration 0 shows
    //          exactly while it is active.
    internal readonly bool IsVisible(float duration) => Started && (duration > 0f ? Time < duration : Active);

    internal readonly bool IsDrained(float duration) => Draining && !IsVisible(duration);
}
