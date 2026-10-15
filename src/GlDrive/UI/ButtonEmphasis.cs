using System.Windows;

namespace GlDrive.UI;

/// <summary>
/// Switches a button between the shared PrimaryButton and SecondaryButton styles by resource
/// reference. A local style BasedOn either of them would capture the theme that was active when
/// the view loaded, so an open view kept the old template after a theme switch.
/// Pair with Style="{DynamicResource SecondaryButton}" for the initial (false) state.
/// </summary>
public static class ButtonEmphasis
{
    public const string PrimaryKey = "PrimaryButton";
    public const string SecondaryKey = "SecondaryButton";

    public static readonly DependencyProperty IsPrimaryProperty = DependencyProperty.RegisterAttached(
        "IsPrimary", typeof(bool), typeof(ButtonEmphasis), new PropertyMetadata(false, OnIsPrimaryChanged));

    public static bool GetIsPrimary(DependencyObject element) => (bool)element.GetValue(IsPrimaryProperty);
    public static void SetIsPrimary(DependencyObject element, bool value) => element.SetValue(IsPrimaryProperty, value);

    private static void OnIsPrimaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
            element.SetResourceReference(FrameworkElement.StyleProperty, (bool)e.NewValue ? PrimaryKey : SecondaryKey);
    }
}
