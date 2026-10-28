using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.Lighting;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Rendering;
using TerrariaDynamics.Common.Storage;
using TerrariaDynamics.Common.WorldEditing;

namespace TerrariaDynamics.Common.TileYards;

// Source/Bounds use tile coordinates and exclusive edges. WorldPosition uses
// pixels; Rotation uses radians. Source is historical, not the current pose.
public sealed partial class Yard
{
    public const int MaxSelectionArea = 65536;

    public Guid Id { get; }
    public Rectangle Source { get; }
    public Point StorageOrigin { get; }
    public int Width => Source.Width;
    public int Height => Source.Height;
    public Rectangle Bounds => new(StorageOrigin.X, StorageOrigin.Y, Width, Height);
    public YardCoordinates Coordinates => new(Bounds.Location, new Point(Width, Height), WorldPosition, Rotation);
    // Only the client render path uses this delayed pose. Persistence, return
    // placement and physics continue to use the latest authoritative Coordinates.
    public YardCoordinates RenderCoordinates => renderInterpolator == null ? Coordinates
        : new(Bounds.Location, new Point(Width, Height), renderPosition, renderRotation);
    public YardState State { get; private set; } = YardState.PendingLoad;
    public bool IsLoaded => State is YardState.Ready or YardState.Returning;
    public bool IsValid => State is not (YardState.Removed or YardState.Unloaded);
    public event Action<Yard> StateChanged;
    public event Action<Yard> Invalidated;
    public int TileCount { get; internal set; }
    internal byte[] PendingTiles { get; set; }
    internal Dictionary<Point, YardTreeAppearance> TreeAppearances { get; } = new();
    internal YardLightContour LightContour { get; set; }
    private IYardHost host;
    private YardReturnPlacement returnPlacement;
    private Action<bool, string> returnComplete;
    private YardPoseInterpolator renderInterpolator;
    private Vector2 renderPosition;
    private float renderRotation;

    public Yard(Guid id, Rectangle source, Point storageOrigin)
    {
        Id = id;
        Source = source;
        StorageOrigin = storageOrigin;
        WorldPosition = new Vector2(source.X * YardCoordinates.TileSize, source.Y * YardCoordinates.TileSize);
    }

    internal void ApplyRemotePhysicsState(Vector2 position, float rotation, Vector2 velocity,
        float angularVelocity, ulong sequence, double time)
    {
        Vector2 previousPosition = WorldPosition;
        float previousRotation = Rotation;
        if (renderInterpolator == null) {
            renderInterpolator = new YardPoseInterpolator();
            renderInterpolator.Reset(previousPosition, previousRotation, time - YardPhysicsPacket.SendIntervalSeconds);
            renderPosition = previousPosition;
            renderRotation = previousRotation;
        }
        if (renderInterpolator.Push(sequence, position, rotation, time))
            ApplyPhysicsState(position, rotation, velocity, angularVelocity);
    }

    // Prepared once at the start of a draw, so walls, tiles, wires, trees and
    // lighting share exactly the same pose even when a draw spans several ms.
    internal void PrepareRenderPose(double time) =>
        renderInterpolator?.Sample(time, out renderPosition, out renderRotation);

    internal void Attach(IYardHost owner)
    {
        ThrowIfInvalid();
        if (host != null)
            throw new InvalidOperationException("Yard is already attached to a world.");
        host = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    internal void MarkLoaded(int tileCount)
    {
        ThrowIfInvalid();
        if (State != YardState.PendingLoad || host == null)
            throw new InvalidOperationException("Only an attached pending Yard can be loaded.");
        TileCount = tileCount;
        PendingTiles = null;
        SetState(YardState.Ready);
    }

    // Clients mirror lifecycle state without running the authoritative return timer.
    internal void ApplyRemoteState(Vector2 position, float rotation, bool returning)
    {
        SetTransform(position, rotation);
        if (IsLoaded)
            SetState(returning ? YardState.Returning : YardState.Ready);
    }

    public bool TryBeginReturn(Action<bool, string> complete, out string message)
    {
        ThrowIfInvalid();
        if (host == null)
            throw new InvalidOperationException("Yard is not attached to a world.");
        if (!host.CanBeginReturn(this, out message))
            return false;
        returnPlacement = new YardReturnPlacement(Coordinates);
        returnComplete = complete;
        SetTransform(returnPlacement.WorldPosition, 0);
        SetState(YardState.Returning);
        return true;
    }

    // Called only by the world update loop. Waiting and its callback belong to
    // this instance; the host performs the eventual world mutation by reference.
    internal void Update()
    {
        if (State != YardState.Returning || returnPlacement == null)
            return;
        YardReturnStatus status = returnPlacement.Tick(Coordinates);
        if (status == YardReturnStatus.Waiting)
            return;
        IYardHost owner = host;
        Action<bool, string> complete = returnComplete;
        returnPlacement = null;
        returnComplete = null;
        bool returned = false;
        string message;
        if (status == YardReturnStatus.Moved)
            message = owner.ReturnMovedMessage;
        else
            returned = owner.TryReturn(this, out message);
        if (IsValid)
            SetState(YardState.Ready);
        try {
            complete?.Invoke(returned, message);
        }
        catch (Exception error) {
            owner.ReportObserverError(error);
        }
    }

    internal void Invalidate(bool removed)
    {
        if (!IsValid)
            return;
        ReleasePhysics();
        returnPlacement = null;
        returnComplete = null;
        PendingTiles = null;
        TileCount = 0;
        LinearVelocity = Vector2.Zero;
        AngularVelocity = 0;
        renderInterpolator = null;
        LightContour = null;
        TreeAppearances.Clear();
        playerCollision = null;
        SetState(removed ? YardState.Removed : YardState.Unloaded);
        Notify(Invalidated);
        StateChanged = null;
        Invalidated = null;
        host = null;
    }

    private void SetState(YardState state)
    {
        State = state;
        Notify(StateChanged);
    }

    private void Notify(Action<Yard> observers)
    {
        if (observers == null)
            return;
        foreach (Action<Yard> observer in observers.GetInvocationList()) {
            try {
                observer(this);
            }
            catch (Exception error) {
                // Notification failures must never undo a committed tile move.
                host?.ReportObserverError(error);
            }
        }
    }

    private void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new ObjectDisposedException(nameof(Yard), "Yard no longer belongs to an active world.");
    }

    public static bool TryAllocate(Rectangle source, IEnumerable<Yard> existing,
        int playableWidth, int storageHeight, out Yard region)
    {
        region = null;
        if (source.X < 0 || source.Y < 0 || source.Width <= 0 || source.Height <= 0
            || (long)source.Width * source.Height > MaxSelectionArea
            || (long)source.X + source.Width > playableWidth
            || (long)source.Y + source.Height > storageHeight)
            return false;
        if (!YardStorageAllocator.TryAllocate(new Point(source.Width, source.Height),
            existing.Select(yard => yard.Bounds), playableWidth, storageHeight, out Point origin))
            return false;
        region = new Yard(Guid.NewGuid(), source, origin);
        return true;
    }

    internal TagCompound Save(byte[] tiles)
    {
        var tag = new TagCompound {
            ["id"] = Id.ToString("D"),
            ["sourceX"] = Source.X,
            ["sourceY"] = Source.Y,
            ["storageX"] = StorageOrigin.X,
            ["storageY"] = StorageOrigin.Y,
            ["width"] = Width,
            ["height"] = Height,
            ["worldX"] = WorldPosition.X,
            ["worldY"] = WorldPosition.Y,
            ["rotation"] = Rotation,
            ["velocityX"] = LinearVelocity.X,
            ["velocityY"] = LinearVelocity.Y,
            ["angularVelocity"] = AngularVelocity,
            ["tiles"] = tiles
        };
        tag["treeAppearanceVersion"] = 1;
        tag["treeAppearances"] = TreeAppearances.Select(pair => {
            TagCompound tree = pair.Value.Save();
            tree["x"] = pair.Key.X;
            tree["y"] = pair.Key.Y;
            return tree;
        }).ToList();
        return tag;
    }

    internal void LoadMetadata(TagCompound tag)
    {
        if (!tag.ContainsKey("worldX") || !tag.ContainsKey("worldY") || !tag.ContainsKey("rotation")
            || !tag.ContainsKey("treeAppearances"))
            throw new InvalidDataException("Missing Yard metadata.");
        if (tag.GetInt("treeAppearanceVersion") != 1)
            throw new InvalidDataException("Unsupported Yard tree appearance version.");
        ApplyPhysicsState(new Vector2(tag.GetFloat("worldX"), tag.GetFloat("worldY")), tag.GetFloat("rotation"),
            new Vector2(tag.GetFloat("velocityX"), tag.GetFloat("velocityY")), tag.GetFloat("angularVelocity"));
        foreach (TagCompound tree in tag.GetList<TagCompound>("treeAppearances")) {
            var local = new Point(tree.GetInt("x"), tree.GetInt("y"));
            if (local.X < 0 || local.Y < 0 || local.X >= Width || local.Y >= Height
                || !TreeAppearances.TryAdd(local, YardTreeAppearance.Load(tree)))
                throw new InvalidDataException("Invalid or duplicate Yard tree binding.");
        }
    }
}
