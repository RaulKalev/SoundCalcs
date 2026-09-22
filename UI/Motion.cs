using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SoundCalcs.UI
{
    /// <summary>
    /// Minimal, explanatory motion for panel changes only (never for list rows):
    ///  - <c>Motion.Reveal="True"</c>: when the element becomes visible it fades in and settles from a small offset
    ///    (<c>Motion.FromY</c>, default 6 DIP). Starting from the element's current animated value
    ///    (HandoffBehavior.SnapshotAndReplace) keeps it interruptible – a new reveal never jumps.
    ///  - Reduced motion ("Show animations in Windows" off): opacity only, no movement.
    /// WPF has no spring animation; a short critically damped-looking ease-out is used instead. Animations run
    /// only while they play – there is no continuous rendering loop.
    /// </summary>
    public static class Motion
    {
        public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
            "Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealChanged));

        public static readonly DependencyProperty FromYProperty = DependencyProperty.RegisterAttached(
            "FromY", typeof(double), typeof(Motion), new PropertyMetadata(6.0));

        public static bool GetReveal(DependencyObject d) => (bool)d.GetValue(RevealProperty);
        public static void SetReveal(DependencyObject d, bool value) => d.SetValue(RevealProperty, value);
        public static double GetFromY(DependencyObject d) => (double)d.GetValue(FromYProperty);
        public static void SetFromY(DependencyObject d, double value) => d.SetValue(FromYProperty, value);

        private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var el = d as FrameworkElement;
            if (el == null) return;
            if ((bool)e.NewValue)
            {
                el.IsVisibleChanged += OnVisibleChanged;
                el.Loaded += OnLoaded;
            }
            else
            {
                el.IsVisibleChanged -= OnVisibleChanged;
                el.Loaded -= OnLoaded;
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var el = (FrameworkElement)sender;
            if (el.IsVisible) Play(el);
        }

        private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue) Play((FrameworkElement)sender);
        }

        /// <summary>Plays the reveal on any element (e.g. sheets shown from code).</summary>
        public static void Play(FrameworkElement el, double? fromY = null)
        {
            var reduced = ThemeManager.ReducedMotion;
            var duration = TimeSpan.FromMilliseconds(reduced ? 100 : 180);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fade = new DoubleAnimation { From = el.Opacity < 0.99 ? (double?)null : 0.0, To = 1.0, Duration = duration, EasingFunction = ease };
            el.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

            if (reduced) return;
            var t = el.RenderTransform as TranslateTransform;
            if (t == null || t.IsFrozen)
            {
                t = new TranslateTransform();
                el.RenderTransform = t;
            }
            // Interrupted mid-slide: continue from the current (presentation) value instead of jumping back.
            var inFlight = Math.Abs(t.Y) > 0.01;
            var slide = new DoubleAnimation { From = inFlight ? (double?)null : (fromY ?? GetFromY(el)), To = 0, Duration = duration, EasingFunction = ease };
            t.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
