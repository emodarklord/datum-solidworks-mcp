using System.Runtime.InteropServices;
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
    private const int StepsPerSample = 4;
    private const double MinimumTurn = 1e-4;
    private const double ReachedTolerance = 0.01;
    private const int FramingChecks = 6;
    private const int CheckShrink = 4;
    // How much of the window the model fills when a study is made, leaving room for parts to move.
    private const double StudyMargin = 0.7;
    private const int VideoPause = 1000;
    private const int VideoSteadyReadings = 3;
    private static readonly TimeSpan VideoLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CalculationLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StillCalculating = TimeSpan.FromSeconds(2);

    public string NewMotionStudy(string id, string? name, double seconds) => Call(() =>
    {
        var document = Find(id);
        AssemblyOf(document, id);
        // An animation study keeps the camera it was made with and puts it back at every time, including in the videos it
        // renders, so the model is framed with room to move first.
        document.ViewZoomtofit2();
        ((IModelView)document.ActiveView).Scale2 *= StudyMargin;
        document.GraphicsRedraw2();
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
        motor.LoadReferences = new[] { new DispatchWrapper(target) };
        if (spec.RelativeTo is { } relative)
        {
            motor.RelativeComponent = (Component2)ComponentOf(id, document, relative);
        }

        motor.ReverseDirection = spec.Reverse;
        if (spec.Swing is { } swing)
        {
            motor.OscillatingMotor(swing * Math.PI / 180, 1 / spec.Period!.Value);
        }
        else
        {
            motor.ConstantSpeedMotor(spec.Rpm!.Value * 2 * Math.PI / 60);
        }

        if (motion.CreateFeature(motor) is IFeature made)
        {
            return made.Name;
        }

        var owner = ((IEntity)target).GetComponent() as IComponent2;
        var nested = owner?.GetParent() is IComponent2 parent ? $" inside {parent.Name2}{(parent.Solving == (int)swComponentSolvingOption_e.swComponentFlexibleSolving ? ", which is flexible" : ", which is rigid")}" : "";
        throw new DatumException(
            ErrorCode.FeatureFailed,
            $"SOLIDWORKS couldn't add a motor on {spec.Face}, a {SurfaceOf(target).ToString().ToLowerInvariant()} on {owner?.Name2 ?? "the assembly"}{nested}",
            "Drive a part that sits directly in this assembly, on a cylinder or a flat face square to the axis, and try relative_to with the part it turns against");
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

    public IReadOnlyList<MotionSample> RunMotionStudy(string id, string study, IReadOnlyList<double> times, double? hold = null) => Call<IReadOnlyList<MotionSample>>(() =>
    {
        var document = Find(id);
        var motion = StudyOf(document, study);
        if (!motion.Calculate())
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't calculate {study}", "Check for over-defined mates with mate_list");
        }

        var seconds = motion.GetDuration();
        WaitForCalculation(motion, seconds, study);
        var components = ComponentsOf(document, id);

        Settle(document, motion, components, 0);
        var start = components.Select(c => (double[])c.Transform2.ArrayData).ToList();
        var (last, total) = (start.ToList(), new double[components.Count]);
        var axes = new Point3?[components.Count];
        var found = new List<MotionSample>();
        var previous = 0.0;
        foreach (var sample in times)
        {
            // Stepping up to each sample keeps every move under half a turn, so the total can't fold back on itself.
            var reached = sample;
            for (var step = 1; step <= StepsPerSample; step++)
            {
                var at = previous + ((sample - previous) * step / StepsPerSample);
                reached = Settle(document, motion, components, at);
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
                }
            }

            found.AddRange(components.Select((component, i) => new MotionSample(sample, component.Name2, total[i], DistanceBetween(start[i], last[i]))
            {
                Reached = Math.Abs(reached - sample) > ReachedTolerance ? reached : null,
            }));
            previous = sample;
        }

        Settle(document, motion, components, hold ?? 0);
        return found;
    });

    // Calculate returns before an animation has finished computing, so a pose read straight after can belong to another
    // time. It's done when the study's clock has run to its end, which is then stopped so SetTime takes over.
    private static void WaitForCalculation(IMotionStudy motion, double seconds, string study)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (time, moved) = (motion.GetTime(), clock.Elapsed);
        while (time < seconds - TimeTolerance && clock.Elapsed - moved < StillCalculating)
        {
            if (clock.Elapsed > CalculationLimit)
            {
                motion.Stop();
                throw new DatumException(ErrorCode.Timeout, $"{study} was still calculating after {CalculationLimit.TotalSeconds:0} s", "Shorten the study, or run it again");
            }

            Thread.Sleep(SettlePause);
            var now = motion.GetTime();
            (time, moved) = Math.Abs(now - time) > TimeTolerance ? (now, clock.Elapsed) : (time, moved);
        }

        motion.Stop();
    }

    // SOLIDWORKS renders the video itself; a few frames rendered the same way first show whether the model sits well in it.
    // SaveToAVI returns while it is still writing, so this waits for a finished file before the background setting is put back.
    public MotionVideo RecordMotionVideo(string id, string study, StandardView view, string path, int fps, int width, int height) => Call(() =>
    {
        var document = Find(id);
        var motion = StudyOf(document, study);
        if (!App.Visible || App.FrameState == (int)swWindowState_e.swWindowMinimized)
        {
            throw new DatumException(ErrorCode.SolidworksError, "the SOLIDWORKS window is minimized or hidden", "Restore the SOLIDWORKS window; the video is rendered on screen");
        }

        var seconds = motion.GetDuration();
        var components = ComponentsOf(document, id);
        var file = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.bmp");
        try
        {
            return WithOption(swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance, (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain, () =>
                WithOption(swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground, White, () =>
                {
                    document.ShowNamedView2("", (int)view);
                    var checks = new List<Raster>();
                    foreach (var at in Enumerable.Range(0, FramingChecks).Select(i => seconds * i / (FramingChecks - 1)))
                    {
                        Settle(document, motion, components, at);
                        if (!document.SaveBMP(file, width / CheckShrink, height / CheckShrink))
                        {
                            throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't render a frame");
                        }

                        checks.Add(Bmp.Decode(File.ReadAllBytes(file)));
                    }

                    motion.SetTime(0);
                    var settings = (IAVIParameter)ManagerOf(document).CreateAVIParameter();
                    settings.OutputType = (int)(Path.GetExtension(path).Equals(".avi", StringComparison.OrdinalIgnoreCase) ? swAnimationOutputType_e.swAnimationOutput_AVI : swAnimationOutputType_e.swAnimationOutput_Series_of_MP4);
                    settings.FramePerSecond = fps;
                    settings.SaveEntireAnimation = true;
                    settings.RendererType = (int)swRendererType_e.swRendererType_Solidworks_Screen;
                    settings.MotionBlur = false;
                    settings.ImageSize = (int)swSaveAVIImageSize_e.swImage_Custom;
                    settings.PreserveRatio = false;
                    settings.ScreenRatio = false;
                    settings.ScreenWidth = width;
                    settings.ScreenHeight = height;
                    if (!motion.SaveToAVI(path, (AVIParameter)settings))
                    {
                        throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS wouldn't start the video at {path}", "Check the folder exists and the file isn't open elsewhere");
                    }

                    return new MotionVideo(checks, WaitForVideo(path));
                }));
        }
        finally
        {
            File.Delete(file);
        }
    });

    // Finished means the file reads as a whole video and has stopped growing.
    private static VideoFile WaitForVideo(string path)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (size, steady) = (-1L, 0);
        while (clock.Elapsed < VideoLimit)
        {
            Thread.Sleep(VideoPause);
            var now = File.Exists(path) ? new FileInfo(path).Length : 0;
            steady = now > 0 && now == size ? steady + 1 : 0;
            size = now;
            if (steady >= VideoSteadyReadings && VideoFile.Read(path) is { Complete: true } finished)
            {
                return finished;
            }
        }

        throw new DatumException(ErrorCode.Timeout, $"SOLIDWORKS hadn't finished {path} after {VideoLimit.TotalMinutes:0} minutes", "Lower the size or fps, or shorten the study");
    }

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
    private static double Settle(IModelDoc2 document, IMotionStudy motion, IReadOnlyList<IComponent2> components, double seconds)
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

        return motion.GetTime();
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
