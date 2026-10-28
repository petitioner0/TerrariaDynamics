using System;
using System.Reflection;
using Terraria;

namespace TerrariaDynamics.Common.WorldEditing;

// Timers retain real storage addresses. Only the native timer table is scoped;
// tiles, actors and world dimensions are never copied or replaced.
internal sealed class YardWiringState
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly FieldInfo X = Required("_mechX");
    private static readonly FieldInfo Y = Required("_mechY");
    private static readonly FieldInfo Time = Required("_mechTime");
    private static readonly FieldInfo CountField = Required("_numMechs");
    private static readonly FieldInfo UserField = Required("CurrentUser");
    private readonly int[] x = new int[((int[])X.GetValue(null)).Length];
    private readonly int[] y = new int[((int[])Y.GetValue(null)).Length];
    private readonly int[] time = new int[((int[])Time.GetValue(null)).Length];
    public int Count { get; private set; }
    public static int CurrentUser => (int)UserField.GetValue(null);

    private static FieldInfo Required(string name) => typeof(Wiring).GetField(name, Flags)
        ?? throw new NotSupportedException($"Unsupported tModLoader wiring field: {name}.");

    public Scope Enter() => new(this);

    public sealed class Scope : IDisposable
    {
        private readonly YardWiringState state;
        private readonly object oldX = X.GetValue(null), oldY = Y.GetValue(null), oldTime = Time.GetValue(null);
        private readonly int oldCount = (int)CountField.GetValue(null);
        private readonly int oldUser = CurrentUser;
        private readonly bool running = Wiring.running;

        public Scope(YardWiringState state)
        {
            this.state = state;
            X.SetValue(null, state.x);
            Y.SetValue(null, state.y);
            Time.SetValue(null, state.time);
            CountField.SetValue(null, state.Count);
        }

        public void Dispose()
        {
            state.Count = (int)CountField.GetValue(null);
            X.SetValue(null, oldX);
            Y.SetValue(null, oldY);
            Time.SetValue(null, oldTime);
            CountField.SetValue(null, oldCount);
            Wiring.SetCurrentUser(oldUser);
            Wiring.running = running;
        }
    }
}
