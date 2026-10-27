using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TSCBr.OfficeInstaller.Controls;

public partial class BusyIndicator : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BusyIndicator), new PropertyMetadata(false, OnActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsAnimating => SpinnerRotation.HasAnimatedProperties;

    public BusyIndicator()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += SettingsChanged;
            UpdateAnimation();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SettingsChanged;
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private static void OnActiveChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var indicator = (BusyIndicator)target;
        indicator.SetCurrentValue(VisibilityProperty, (bool)args.NewValue ? Visibility.Visible : Visibility.Collapsed);
        indicator.UpdateAnimation();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (SpinnerRotation is null) return;
        var animate = IsActive && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation;
        if (animate == IsAnimating) return;
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SpinnerRotation.Angle = 0;
        if (animate)
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            { RepeatBehavior = RepeatBehavior.Forever });
    }
}
