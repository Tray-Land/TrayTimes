using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace TrayTimes.Views;

/// <summary>
/// The app's small, shared animations: things rise in, pop when they change, a bell rings.
/// Every one is skipped when the user has turned animations off in Windows.
/// </summary>
internal static class Motion
{
    private const int StaggerMilliseconds = 45;
    private const int MaxStaggerSteps = 8;
    private const float BellPivotY = 0.12f; // A bell swings from its top.

    private static readonly UISettings Settings = new();
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(160);

    private static bool IsEnabled => Settings.AnimationsEnabled;

    /// <summary>Fades the element in while it rises into place; <paramref name="step"/> delays it so a list can cascade.</summary>
    public static void Enter(UIElement element, int step = 0, float rise = 14)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = 1;
        if (!IsEnabled)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));
        TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(step, MaxStaggerSteps) * StaggerMilliseconds);

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, decelerate);
        Schedule(fade, delay, EnterDuration);
        visual.StartAnimation("Opacity", fade);

        if (rise != 0)
        {
            ScalarKeyFrameAnimation slide = compositor.CreateScalarKeyFrameAnimation();
            slide.InsertKeyFrame(0, rise);
            slide.InsertKeyFrame(1, 0, decelerate);
            Schedule(slide, delay, EnterDuration);
            visual.StartAnimation("Translation.Y", slide);
        }
    }

    /// <summary>
    /// Opens the element from no height to its full height, on the same cascade as <see cref="Enter"/>, so the rows
    /// below ease down to make room instead of jumping. Call once the element has been laid out.
    /// </summary>
    public static void Grow(FrameworkElement element, int step = 0)
    {
        double target = element.ActualHeight;
        if (!IsEnabled || target <= 0)
        {
            return;
        }

        RectangleGeometry clip = new();
        void Resize(object? sender, SizeChangedEventArgs e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        element.SizeChanged += Resize;
        element.Clip = clip;
        element.Height = 0;

        DoubleAnimation grow = new()
        {
            From = 0,
            To = target,
            Duration = EnterDuration,
            BeginTime = TimeSpan.FromMilliseconds(Math.Min(step, MaxStaggerSteps) * StaggerMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true, // Height is a layout property.
        };
        Storyboard.SetTarget(grow, element);
        Storyboard.SetTargetProperty(grow, "Height");
        Storyboard storyboard = new();
        storyboard.Children.Add(grow);
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            element.SizeChanged -= Resize;
            element.ClearValue(FrameworkElement.HeightProperty); // Back to sizing itself, so later changes still fit.
            element.Clip = null;
        };
        storyboard.Begin();
    }

    /// <summary>Fades the element out, then calls <paramref name="completed"/> (straight away when animations are off).</summary>
    public static void Exit(UIElement element, Action completed)
    {
        if (!IsEnabled)
        {
            completed();
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = ExitDuration;
        visual.Opacity = 0; // Where it rests once the animation ends.
        visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) => completed();
    }

    /// <summary>Springs the element from <paramref name="from"/> times its size back to normal: above 1 is a pop, below 1 a squash.</summary>
    public static void Pop(UIElement element, float from = 1.35f, float pivotY = 0.5f)
    {
        if (!IsEnabled)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(element.ActualSize.X / 2, element.ActualSize.Y * pivotY, 0);
        SpringVector3NaturalMotionAnimation spring = visual.Compositor.CreateSpringVector3Animation();
        spring.InitialValue = new Vector3(from, from, 1);
        spring.FinalValue = Vector3.One;
        spring.DampingRatio = 0.4f;
        spring.Period = TimeSpan.FromMilliseconds(55);
        visual.StartAnimation("Scale", spring);
    }

    /// <summary>Swings the element like a bell that has just been rung.</summary>
    public static void Ring(UIElement element)
    {
        if (!IsEnabled)
        {
            return;
        }

        Pop(element, 1.3f, BellPivotY);
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        CubicBezierEasingFunction ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0), new Vector2(0.63f, 1));
        ScalarKeyFrameAnimation swing = visual.Compositor.CreateScalarKeyFrameAnimation();
        swing.InsertKeyFrame(0, 0);
        swing.InsertKeyFrame(0.15f, -26, ease);
        swing.InsertKeyFrame(0.38f, 22, ease);
        swing.InsertKeyFrame(0.58f, -14, ease);
        swing.InsertKeyFrame(0.78f, 7, ease);
        swing.InsertKeyFrame(1, 0, ease);
        swing.Duration = TimeSpan.FromMilliseconds(750);
        visual.StartAnimation("RotationAngleInDegrees", swing);
    }

    /// <summary>Turns the element one full circle.</summary>
    public static void Spin(UIElement element)
    {
        if (!IsEnabled)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(element.ActualSize.X / 2, element.ActualSize.Y / 2, 0);
        CubicBezierEasingFunction ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0), new Vector2(0.1f, 1));
        ScalarKeyFrameAnimation turn = visual.Compositor.CreateScalarKeyFrameAnimation();
        turn.InsertKeyFrame(0, 0);
        turn.InsertKeyFrame(1, 360, ease);
        turn.Duration = TimeSpan.FromMilliseconds(600);
        visual.StartAnimation("RotationAngleInDegrees", turn);
    }

    private static void Schedule(KeyFrameAnimation animation, TimeSpan delay, TimeSpan duration)
    {
        animation.Duration = duration;
        animation.DelayTime = delay;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
    }
}
