using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TSCBr.OfficeInstaller.Controls;

public static class Motion
{
    public static void Reveal(FrameworkElement element, int delayMilliseconds = 0)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        var translation = new TranslateTransform();
        element.RenderTransform = translation;
        var movement = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        movement.KeyFrames.Add(new DiscreteDoubleKeyFrame(7, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMilliseconds > 0) movement.KeyFrames.Add(new DiscreteDoubleKeyFrame(7, KeyTime.FromTimeSpan(delay)));
        movement.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(280)))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        translation.BeginAnimation(TranslateTransform.YProperty, movement);

        // Start at zero immediately, including the stagger delay, then restore the base opacity.
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMilliseconds > 0) fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(240))));
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void PhaseChanged(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(150))
        { FillBehavior = FillBehavior.Stop });
    }
}
