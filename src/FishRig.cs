using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SingingFish;

/// <summary>
/// The moving fish while it sings, driven every frame by code - no song-length animation is baked.
///
/// WHY: an animation as long as the song (one keyframe a frame for the mouth) made the game work out every frame of it
/// when the fish first sang: 1 - 6 seconds on the main thread (client-main.log). Now the fish's model carries only the
/// short tail-flick loop, and every frame this:
/// <list type="number">
/// <item>advances that loop (the game's own ClientAnimator - which also recomputes every joint's matrix);</item>
/// <item>turns the head by <see cref="HeadDeg"/> about the fish's up axis through the head's pivot - the head joint and
/// every joint inside it (jaw, eyes) - by putting a rotation in front of their matrices;</item>
/// <item>opens the jaw by <see cref="JawDeg"/> about its own hinge, by putting a rotation behind its matrix (in the
/// model's rest space, where the hinge is known), then the head turn in front.</item>
/// </list>
/// Pivots and axes come from the model in its rest pose (the GetLocalTransformMatrix chain), the same space the
/// animator's matrices map from. The model is built with joints forced on the head, jaw and tail
/// (Shape.ResolveAndFindJoints' requireJointsForElements), and with the fish's own animations removed so nothing else
/// is worked out. Drawn by the game's AnimatableRenderer at the antler mount's place for the item.
/// </summary>
public sealed class FishRig : IRenderer, IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly AnimatorBase animator;
    private readonly AnimatableRenderer renderer;
    /// <summary>The animations the renderer and animator share - only ever the tail loop.</summary>
    private readonly Dictionary<string, AnimationMetaData> active;
    private readonly int[] headJoints, jawJoints, tailJoints;
    private readonly float[] headPivot, headAxis, jawPivot, jawAxis, tailPivot, tailAxis;
    private readonly float[] tmpA = new float[16], tmpB = new float[16], rot = new float[16];

    /// <summary>Called once a frame with the frame time: returns the front-half turn, jaw opening and tail flick in degrees.</summary>
    public System.Func<float, (float head, float jaw, float tail)> Drive;

    public double RenderOrder => 0.9;      // before the AnimatableRenderer (1) draws with the matrices
    public int RenderRange => 99;

    public bool Visible
    {
        get => renderer.ShouldRender;
        set => renderer.ShouldRender = value;
    }

    /// <summary>
    /// A do-nothing loop kept running while the fish is drawn: with an animation running the animator recomputes every
    /// joint's matrix each frame (AnimatorBase.Matrices is the default pose, never recomputed, when none runs - patching
    /// that would pile up). Everything that moves is turned by code.
    /// </summary>
    public const string TailAnim = "singingfish-idle";

    private FishRig(ICoreClientAPI capi, AnimatorBase animator, AnimatableRenderer renderer, Dictionary<string, AnimationMetaData> active,
        int[] headJoints, int[] jawJoints, float[] headPivot, float[] headAxis, float[] jawPivot, float[] jawAxis,
        int[] tailJoints, float[] tailPivot, float[] tailAxis)
    {
        this.capi = capi; this.animator = animator; this.renderer = renderer; this.active = active;
        this.headJoints = headJoints; this.jawJoints = jawJoints; this.tailJoints = tailJoints;
        this.headPivot = headPivot; this.headAxis = headAxis; this.jawPivot = jawPivot; this.jawAxis = jawAxis;
        this.tailPivot = tailPivot; this.tailAxis = tailAxis;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "singingfish");
    }

    /// <summary>
    /// Builds the rig for one fish item. <paramref name="shape"/> must already carry the tail animation; the jaw
    /// (name and local axis index 0/1/2) comes from FindJaw.
    /// </summary>
    public static FishRig Create(ICoreClientAPI capi, string key, Shape shape, ITexPositionSource textures, Vec3d at,
        string headName, string jawName, int jawAxisIndex, string tailName, string bodyName = "Chest", string hingeName = "Midsection")
    {
        var require = new[] { headName, jawName, tailName, bodyName, hingeName }.Where(n => n != null).Distinct().ToArray();
        var byName = shape.CollectAndResolveReferences(capi.Logger, key);
        shape.CacheInvTransforms();
        shape.ResolveAndFindJoints(capi.Logger, key, byName, require);
        capi.Tesselator.TesselateShape(new TesselationMetaData
        {
            TexSource = textures, WithJointIds = true, WithDamageEffect = true, TypeForLogging = key, Rotation = new Vec3f()
        }, shape, out var mesh);

        var animator = AnimationUtil.GetAnimator(capi, key, shape);
        var active = new Dictionary<string, AnimationMetaData>();
        var renderer = new AnimatableRenderer(capi, at, new Vec3f(), animator, active, mesh, EnumRenderStage.Opaque) { ShouldRender = false };

        byName.TryGetValue(headName ?? "", out var head);
        ShapeElement jaw = null;
        if (jawName != null) byName.TryGetValue(jawName, out jaw);

        int[] JointsUnder(ShapeElement el)
        {
            var set = new HashSet<int>();
            void Walk(ShapeElement e) { if (e.JointId > 0) set.Add(e.JointId); foreach (var c in e.Children ?? []) Walk(c); }
            if (el != null) Walk(el);
            return set.ToArray();
        }

        // rest-space pivots and axes: a part's pivot sits in its PARENT's space
        float[] headPivot = null, headAxis = null, jawPivot = null, jawAxis = null;
        int[] turning = [];
        byName.TryGetValue(bodyName ?? "", out var body);
        byName.TryGetValue(hingeName ?? "", out var hinge);
        if (body != null && hinge != null && hinge.ParentElement == body)
        {
            // THE FRONT HALF TURNS, like the toy: the body bends where the middle section joins it - everything but the
            // middle section and what hangs off it (the tail) turns about the fish's up axis through that joint
            var bodyRest = RestMatrix(body);
            headPivot = Point(bodyRest, hinge.RotationOrigin);
            headAxis = Dir(bodyRest, 0, 1, 0);
            var rear = new HashSet<int>(JointsUnder(hinge));
            var all = new HashSet<int>();
            void All(ShapeElement e) { if (e.JointId > 0) all.Add(e.JointId); foreach (var c in e.Children ?? []) All(c); }
            foreach (var e in shape.Elements ?? []) All(e);
            all.ExceptWith(rear);
            turning = all.ToArray();
        }
        else if (head != null)
        {
            // no body / middle section to bend at: the head alone turns
            var parent = RestMatrix(head.ParentElement);
            headPivot = Point(parent, head.RotationOrigin);
            headAxis = Dir(parent, 0, 1, 0);
            turning = JointsUnder(head);
        }
        if (jaw != null)
        {
            var parent = RestMatrix(jaw.ParentElement);
            jawPivot = Point(parent, jaw.RotationOrigin);
            // the axis the fish's own eat turns about, in the jaw parent's frame (vsapi applies X, then Y, then Z: a turn
            // about X is about the parent's X; about Y, the parent's Y turned by the jaw's rest X; about Z, by X then Y)
            var local = Mat4f.Create();
            if (jawAxisIndex >= 1) Mat4f.RotateX(local, local, (float)(jaw.RotationX * GameMath.DEG2RAD));
            if (jawAxisIndex >= 2) Mat4f.RotateY(local, local, (float)(jaw.RotationY * GameMath.DEG2RAD));
            var axisInParent = Dir(local, jawAxisIndex == 0 ? 1 : 0, jawAxisIndex == 1 ? 1 : 0, jawAxisIndex == 2 ? 1 : 0);
            jawAxis = Dir(parent, axisInParent[0], axisInParent[1], axisInParent[2]);
        }
        // the tail end (Rear): flicks about its parent's up axis through its own joint
        float[] tailPivot = null, tailAxis = null;
        int[] tailJoints = [];
        if (tailName != null && byName.TryGetValue(tailName, out var tail))
        {
            var parent = RestMatrix(tail.ParentElement);
            tailPivot = Point(parent, tail.RotationOrigin);
            tailAxis = Dir(parent, 0, 1, 0);
            tailJoints = JointsUnder(tail);
        }
        return new FishRig(capi, animator, renderer, active, turning, jaw == null ? [] : JointsUnder(jaw),
            headPivot, headAxis, jawPivot, jawAxis, tailJoints, tailPivot, tailAxis);
    }

    public float[] CustomTransform { set => renderer.CustomTransform = value; }

    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (!renderer.ShouldRender) return;
        // the tail loop always runs while it is drawn: with an animation running the animator recomputes every joint's
        // matrix each frame (AnimatorBase.Matrices is the default pose, never recomputed, when none runs - patching that
        // would pile up)
        if (!active.ContainsKey(TailAnim))
            active[TailAnim] = new AnimationMetaData { Code = TailAnim, Animation = TailAnim, AnimationSpeed = 1f, EaseInSpeed = 6, EaseOutSpeed = 4 }.Init();
        animator.OnFrame(active, dt);
        var (headDeg, jawDeg, tailDeg) = Drive?.Invoke(dt) ?? (0f, 0f, 0f);
        var m = animator.Matrices;
        if (m == null) return;

        if (tailPivot != null && Math.Abs(tailDeg) > 0.01f)
        {
            Turn(rot, tailPivot, tailAxis, tailDeg);
            foreach (int j in tailJoints) PostMultiply(m, j, rot);
        }

        if (jawPivot != null && Math.Abs(jawDeg) > 0.01f)
        {
            Turn(rot, jawPivot, jawAxis, jawDeg);
            foreach (int j in jawJoints) PostMultiply(m, j, rot);
        }
        if (headPivot != null && Math.Abs(headDeg) > 0.01f)
        {
            Turn(rot, headPivot, headAxis, headDeg);
            foreach (int j in headJoints) PreMultiply(m, j, rot);
        }
    }

    /// <summary>T(p) R(axis, deg) T(-p).</summary>
    private static void Turn(float[] into, float[] p, float[] axis, float deg)
    {
        Mat4f.Identity(into);
        Mat4f.Translate(into, into, p[0], p[1], p[2]);
        Mat4f.Rotate(into, into, deg * GameMath.DEG2RAD, axis);
        Mat4f.Translate(into, into, -p[0], -p[1], -p[2]);
    }

    private void PreMultiply(float[] m, int joint, float[] r)
    {
        int at = joint * 16;
        if (at + 16 > m.Length) return;
        Array.Copy(m, at, tmpA, 0, 16);
        Mat4f.Mul(tmpB, r, tmpA);
        Array.Copy(tmpB, 0, m, at, 16);
    }

    private void PostMultiply(float[] m, int joint, float[] r)
    {
        int at = joint * 16;
        if (at + 16 > m.Length) return;
        Array.Copy(m, at, tmpA, 0, 16);
        Mat4f.Mul(tmpB, tmpA, r);
        Array.Copy(tmpB, 0, m, at, 16);
    }

    /// <summary>The rest-pose matrix of an element (root..element, GetLocalTransformMatrix in blocks).</summary>
    private static float[] RestMatrix(ShapeElement el)
    {
        var chain = new List<ShapeElement>();
        for (var e = el; e != null; e = e.ParentElement) chain.Insert(0, e);
        var m = Mat4f.Create();
        var tmp = new float[16];
        foreach (var e in chain)
        {
            Mat4f.Identity(tmp);
            Mat4f.Mul(m, m, e.GetLocalTransformMatrix(0, tmp));
        }
        return m;
    }

    private static float[] Point(float[] m, double[] p)
    {
        double x = (p?[0] ?? 0) / 16, y = (p?[1] ?? 0) / 16, z = (p?[2] ?? 0) / 16;
        return [(float)(m[0] * x + m[4] * y + m[8] * z + m[12]), (float)(m[1] * x + m[5] * y + m[9] * z + m[13]), (float)(m[2] * x + m[6] * y + m[10] * z + m[14])];
    }

    private static float[] Dir(float[] m, double x, double y, double z)
    {
        double dx = m[0] * x + m[4] * y + m[8] * z, dy = m[1] * x + m[5] * y + m[9] * z, dz = m[2] * x + m[6] * y + m[10] * z;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return l < 1e-9 ? [0, 1, 0] : [(float)(dx / l), (float)(dy / l), (float)(dz / l)];
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        renderer.Dispose();
    }
}
