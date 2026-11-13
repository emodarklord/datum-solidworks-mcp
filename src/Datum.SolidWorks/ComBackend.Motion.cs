using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swmotionstudy;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int SettleLimit = 40;
    private const int SettlePause = 150;
    private const int SteadyReadings = 4;
    private const double TimeTolerance = 1e-3;
    private const double StartMoment = 0.01;
    private const int StepsPerSample = 4;
    private const double MinimumTurn = 1e-4;

    public string NewMotionStudy(string id, string? name, double seconds) => Call(() =>
    {
        var document = Find(id);
        AssemblyOf(document, id);
        var study = (IMotionStudy?)ManagerOf(document).CreateMotionStudy()
            ?? throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't make a motion study", "Motion studies need an assembly saved at least once");
        study.StudyType = (int)swMotionStudyType_e.swMotionStudyTypeAssembly;
        if (name is not null)
        {
            study.Name = name;
        }

        study.Activate();
        return study.SetDuration(seconds) ? study.Name : throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS refused a {seconds} s study");
    });

    public string AddMotionMotor(string id, string study, MotionMotorSpec spec) => Call(() =>
    {
        var document = Find(id);
        var motion = StudyOf(document, study);
        var target = FaceOf(id, document, spec.Face);
        var motor = (ISimulationMotorFeatureData)motion.CreateDefinition((int)swFeatureNameID_e.swFmAEMRotationalMotor);
        motor.Location = target;
        motor.DirectionReference = target;
        motor.ReverseDirection = spec.Reverse;
        if (spec.Swing is { } swing)
        {
            motor.OscillatingMotor(swing * Math.PI / 180, 1 / spec.Period!.Value);
        }
        else
        {
            motor.ConstantSpeedMotor(spec.Rpm!.Value * 2 * Math.PI / 60);
        }

        return (motion.CreateFeature(motor) as IFeature)?.Name
            ?? throw new DatumException(ErrorCode.FeatureFailed, $"SOLIDWORKS couldn't add a motor on {spec.Face}", "Use a cylindrical face that is free to turn");
    });

    public void AddMotionPath(string id, string study, MotionPathSpec spec) => Call(() =>
    {
        var document = Find(id);
        var motion = StudyOf(document, study);
        var made = spec.NormalizedKind == "explode"
            ? motion.CreateByExplode(false, spec.Seconds, spec.Start)
            : motion.CreateByCollapse(false, spec.Seconds, spec.Start);
        return made ? true : throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't add the {spec.NormalizedKind}", "Explode and collapse need an exploded view in the active configuration");
    });

    public string? ActiveMotionStudy(string id) => Call(() =>
    {
        var document = Find(id);
        if (document.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            return null;
        }

        var manager = ManagerOf(document);
        return ((manager.GetMotionStudyNames() as object[]) ?? []).OfType<string>().FirstOrDefault(name => manager.GetMotionStudy(name) is IMotionStudy { IsActive: true });
    });

    public double MotionStudySeconds(string id, string study) => Call(() => StudyOf(Find(id), study).GetDuration());

    public IReadOnlyList<MotionSample> RunMotionStudy(string id, string study, int samples, double? hold = null) => Call<IReadOnlyList<MotionSample>>(() =>
    {
        var document = Find(id);
        var motion = StudyOf(document, study);
        if (!motion.Calculate())
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't calculate {study}", "Check for over-defined mates with mate_list");
        }

        var seconds = motion.GetDuration();
        var components = ComponentsOf(document, id);

        // SetTime(0) straight after calculating leaves the model at the study's end, so the start is read a moment in.
        Settle(document, motion, components, StartMoment);
        var start = components.Select(c => (double[])c.Transform2.ArrayData).ToList();
        var (last, total) = (start.ToList(), new double[components.Count]);
        var axes = new Point3?[components.Count];
        var found = new List<MotionSample>();

        // Stepping between the samples keeps each move under half a turn, so the total can't fold back on itself.
        for (var step = 1; step <= samples * StepsPerSample; step++)
        {
            var at = seconds * step / (samples * StepsPerSample);
            Settle(document, motion, components, at);
            for (var i = 0; i < components.Count; i++)
            {
                var now = (double[])components[i].Transform2.ArrayData;
                var (turn, axis) = TurnBetween(last[i], now);
                if (turn > MinimumTurn)
                {
                    axes[i] ??= PositiveAxis(axis);
                    total[i] += axis.Dot(axes[i]!.Value) < 0 ? -turn : turn;
                }

                last[i] = now;
                if (step % StepsPerSample == 0)
                {
                    found.Add(new MotionSample(at, components[i].Name2, total[i], DistanceBetween(start[i], now)));
                }
            }
        }

        Settle(document, motion, components, hold ?? StartMoment);
        return found;
    });

    public IReadOnlyList<Raster> RenderMotionStudy(string id, string study, StandardView view, int width, int height, int frames) => Call<IReadOnlyList<Raster>>(() =>
    {
        var document = Find(id);
        var motion = StudyOf(document, study);
        if (!App.Visible || App.FrameState == (int)swWindowState_e.swWindowMinimized)
        {
            throw new DatumException(ErrorCode.SolidworksError, "the SOLIDWORKS window is minimized or hidden", "Restore the SOLIDWORKS window; frames are rendered on screen");
        }

        var seconds = motion.GetDuration();
        var components = ComponentsOf(document, id);
        var file = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.bmp");
        var model = (IModelView)document.ActiveView;
        var (renderWidth, renderHeight) = RenderSize(model, width, height);
        var renders = new List<Raster>();
        try
        {
            return WithOption(swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance, (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain, () =>
                WithOption(swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground, White, () =>
                {
                    document.ShowNamedView2("", (int)view);
                    motion.SetTime(0);
                    document.ViewZoomtofit2();
                    model.Scale2 *= 0.75;
                    foreach (var at in Enumerable.Range(0, frames).Select(i => seconds * i / Math.Max(1, frames - 1)))
                    {
                        Settle(document, motion, components, at);
                        if (!document.SaveBMP(file, renderWidth, renderHeight))
                        {
                            throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't render a frame");
                        }

                        renders.Add(Bmp.Decode(File.ReadAllBytes(file)));
                    }

                    motion.SetTime(0);
                    return (IReadOnlyList<Raster>)Raster.FitTogether(renders, width, height);
                }));
        }
        finally
        {
            File.Delete(file);
        }
    });

    // On a big assembly SetTime returns before every component has reached that time, so parts locked together can
    // read different angles, and it can start late. It's done when several readings in a row agree.
    private static void Settle(IModelDoc2 document, IMotionStudy motion, IReadOnlyList<IComponent2> components, double seconds)
    {
        motion.SetTime(seconds);
        double[] Reading() => [.. components.SelectMany(c => (double[])c.Transform2.ArrayData)];
        var last = Reading();
        var steady = 0;
        for (var wait = 0; wait < SettleLimit && steady < SteadyReadings; wait++)
        {
            document.GraphicsRedraw2();
            Thread.Sleep(SettlePause);
            var now = Reading();
            steady = now.SequenceEqual(last) && Math.Abs(motion.GetTime() - seconds) < TimeTolerance ? steady + 1 : 0;
            last = now;
        }
    }

    public IReadOnlyList<Raster> RenderTurntable(string id, double tilt, int width, int height, int frames) => Call<IReadOnlyList<Raster>>(() =>
    {
        var document = Find(id);
        if (!App.Visible || App.FrameState == (int)swWindowState_e.swWindowMinimized)
        {
            throw new DatumException(ErrorCode.SolidworksError, "the SOLIDWORKS window is minimized or hidden", "Restore the SOLIDWORKS window; frames are rendered on screen");
        }

        var model = (IModelView)document.ActiveView;
        var (orientation, zoom, translation) = (model.Orientation3, model.Scale2, model.Translation3);
        var (renderWidth, renderHeight) = RenderSize(model, width, height);
        var file = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.bmp");
        var maths = (IMathUtility)App.GetMathUtility();
        var (sinTilt, cosTilt) = Math.SinCos(tilt * Math.PI / 180);
        var renders = new List<Raster>();
        try
        {
            return WithOption(swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance, (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain, () =>
                WithOption(swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground, White, () =>
                {
                    foreach (var step in Enumerable.Range(0, frames))
                    {
                        var (sin, cos) = Math.SinCos(step * 2 * Math.PI / frames);

                        // The camera's axes in model space: a turn about model Y, then a tilt about the camera's own X.
                        double[] camera =
                        [
                            cos, sin * sinTilt, -sin * cosTilt,
                            0, cosTilt, sinTilt,
                            sin, -cos * sinTilt, cos * cosTilt,
                            0, 0, 0, 1, 0, 0, 0,
                        ];
                        model.Orientation3 = (MathTransform)maths.CreateTransform(camera);
                        document.ViewZoomtofit2();
                        model.Scale2 *= 0.85;
                        document.GraphicsRedraw2();
                        if (!document.SaveBMP(file, renderWidth, renderHeight))
                        {
                            throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't render a frame");
                        }

                        renders.Add(Bmp.Decode(File.ReadAllBytes(file)));
                    }

                    return (IReadOnlyList<Raster>)Raster.FitTogether(renders, width, height);
                }));
        }
        finally
        {
            model.Orientation3 = orientation;
            model.Scale2 = zoom;
            model.Translation3 = translation;
            document.GraphicsRedraw2();
            File.Delete(file);
        }
    });

    private static IMotionStudyManager ManagerOf(IModelDoc2 document) => (IMotionStudyManager)document.Extension.GetMotionStudyManager();

    private static IMotionStudy StudyOf(IModelDoc2 document, string name)
    {
        var manager = ManagerOf(document);
        var study = manager.GetMotionStudy(name) as IMotionStudy
            ?? throw new DatumException(ErrorCode.NotFound, $"no motion study '{name}'", "Call motion_new");
        study.Activate();
        return study;
    }

    // Transform arrays hold the rotation first, then the translation in metres.
    private static double DistanceBetween(double[] from, double[] to) => new Point3(to[9] - from[9], to[10] - from[10], to[11] - from[11]).Length * Mm;

    private static (double Degrees, Point3 Axis) TurnBetween(double[] from, double[] to)
    {
        double Cell(int row, int column) => Enumerable.Range(0, 3).Sum(k => from[(k * 3) + row] * to[(k * 3) + column]);
        var degrees = Math.Acos(Math.Clamp((Cell(0, 0) + Cell(1, 1) + Cell(2, 2) - 1) / 2, -1, 1)) * 180 / Math.PI;
        var axis = new Point3(Cell(2, 1) - Cell(1, 2), Cell(0, 2) - Cell(2, 0), Cell(1, 0) - Cell(0, 1));
        return (degrees, axis.Length > 0 ? axis.Normalized() : axis);
    }

    private static Point3 PositiveAxis(Point3 axis)
    {
        var main = new[] { axis.X, axis.Y, axis.Z }.MaxBy(Math.Abs);
        return main < 0 ? new Point3(-axis.X, -axis.Y, -axis.Z) : axis;
    }
}
