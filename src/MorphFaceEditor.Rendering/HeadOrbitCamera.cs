using System.Numerics;
using DxMatrix = SharpDX.Matrix;
using DxMathUtil = SharpDX.MathUtil;
using DxVector3 = SharpDX.Vector3;

namespace MorphFaceEditor.Rendering;

public sealed class HeadOrbitCamera
{
    private HeadPreviewBounds _bounds = new(new Vector3(-1), new Vector3(1));

    public Vector3 Target { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public float Distance { get; private set; } = 4;
    public float MinimumDistance { get; private set; } = 0.5f;
    public float MaximumDistance { get; private set; } = 20;
    public float VerticalFieldOfViewDegrees { get; set; } = 35;

    public Vector3 Position
    {
        get
        {
            var cosPitch = MathF.Cos(Pitch);
            return Target + new Vector3(
                MathF.Sin(Yaw) * cosPitch * Distance,
                MathF.Sin(Pitch) * Distance,
                MathF.Cos(Yaw) * cosPitch * Distance);
        }
    }

    public void Fit(HeadPreviewBounds bounds)
    {
        UpdateBounds(bounds);
        ResetFront();
    }

    public void UpdateBounds(HeadPreviewBounds bounds)
    {
        _bounds = bounds;
        MinimumDistance = Math.Max(bounds.Radius * 0.7f, 0.01f);
        MaximumDistance = Math.Max(bounds.Radius * 12f, MinimumDistance * 2f);
        Distance = Math.Clamp(Distance, MinimumDistance, MaximumDistance);
    }

    public void ResetFront()
    {
        Target = _bounds.Center;
        Yaw = 0;
        Pitch = 0;
        Distance = FitDistance(_bounds.Radius);
    }

    public void ResetThreeQuarter()
    {
        ResetFront();
        Yaw = DxMathUtil.DegreesToRadians(32);
        Pitch = DxMathUtil.DegreesToRadians(4);
    }

    public void ResetPosition()
    {
        Target = _bounds.Center;
        Distance = FitDistance(_bounds.Radius);
    }

    public void Orbit(float horizontalRadians, float verticalRadians)
    {
        if (!float.IsFinite(horizontalRadians) || !float.IsFinite(verticalRadians))
        {
            return;
        }
        Yaw = WrapRadians(Yaw + horizontalRadians);
        var pitchLimit = DxMathUtil.DegreesToRadians(82);
        Pitch = Math.Clamp(Pitch + verticalRadians, -pitchLimit, pitchLimit);
    }

    public void Zoom(float wheelDelta)
    {
        if (!float.IsFinite(wheelDelta))
        {
            return;
        }
        Distance = Math.Clamp(
            Distance * MathF.Exp(-wheelDelta * 0.0012f),
            MinimumDistance,
            MaximumDistance);
    }

    public void Pan(float horizontalWorldUnits, float verticalWorldUnits)
    {
        if (!float.IsFinite(horizontalWorldUnits) || !float.IsFinite(verticalWorldUnits))
        {
            return;
        }

        var towardCamera = Vector3.Normalize(Position - Target);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, towardCamera));
        var up = Vector3.Normalize(Vector3.Cross(towardCamera, right));
        Target += right * horizontalWorldUnits + up * verticalWorldUnits;
    }

    public Vector3 TransformCameraDirectionToWorld(Vector3 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) ||
            direction.LengthSquared() < 1e-12f)
        {
            throw new ArgumentException("Camera-space direction must be finite and non-zero.", nameof(direction));
        }

        var towardCamera = Vector3.Normalize(Position - Target);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, towardCamera));
        var up = Vector3.Normalize(Vector3.Cross(towardCamera, right));
        return Vector3.Normalize(
            right * direction.X +
            up * direction.Y +
            towardCamera * direction.Z);
    }

    internal DxMatrix CreateViewMatrix() => DxMatrix.LookAtRH(ToDx(Position), ToDx(Target), DxVector3.UnitY);

    internal float NearPlane => Math.Max(MinimumDistance * 0.02f, 0.001f);

    internal float FarPlane => Math.Max(MaximumDistance * 4f, 10f);

    internal DxMatrix CreateProjectionMatrix(float aspectRatio) => DxMatrix.PerspectiveFovRH(
        DxMathUtil.DegreesToRadians(VerticalFieldOfViewDegrees),
        Math.Max(aspectRatio, 0.01f),
        NearPlane,
        FarPlane);

    private float FitDistance(float radius)
    {
        var halfFov = DxMathUtil.DegreesToRadians(VerticalFieldOfViewDegrees) * 0.5f;
        var fitted = radius / Math.Max(MathF.Sin(halfFov), 0.01f) * 1.12f;
        return Math.Clamp(fitted, MinimumDistance, MaximumDistance);
    }

    private static float WrapRadians(float value)
    {
        const float fullTurn = MathF.PI * 2;
        value %= fullTurn;
        return value > MathF.PI ? value - fullTurn : value < -MathF.PI ? value + fullTurn : value;
    }

    private static DxVector3 ToDx(Vector3 value) => new(value.X, value.Y, value.Z);
}
