using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoundCalcs.UI
{
    /// <summary>
    /// Shows a field's validation error in place of the hint under its label, so the message is visible without
    /// hovering: <c>ui:FieldHint.For="{Binding ElementName=SpacingBox}"</c> on the hint TextBlock. The source may
    /// also be a panel (an octave-band table): then the first error of any text field in it is shown, prefixed
    /// with that field's accessible name. The hint's own text and colour come back once the error is fixed; a hint
    /// with no text of its own stays collapsed until there is an error.
    /// </summary>
    public static class FieldHint
    {
        public static readonly DependencyProperty ForProperty = DependencyProperty.RegisterAttached(
            "For", typeof(FrameworkElement), typeof(FieldHint), new PropertyMetadata(null, OnForChanged));

        public static FrameworkElement GetFor(DependencyObject d) => (FrameworkElement)d.GetValue(ForProperty);
        public static void SetFor(DependencyObject d, FrameworkElement value) => d.SetValue(ForProperty, value);

        private static readonly DependencyPropertyDescriptor HasErrorDescriptor =
            DependencyPropertyDescriptor.FromProperty(Validation.HasErrorProperty, typeof(TextBox));

        private sealed class State
        {
            public string OriginalText;
            public object OriginalForeground;
            public bool OriginallyEmpty;
            public readonly List<TextBox> Boxes = new List<TextBox>();
        }

        private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
            "State", typeof(State), typeof(FieldHint), new PropertyMetadata(null));

        private static void OnForChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var hint = d as TextBlock;
            if (hint == null) return;
            if (hint.IsLoaded) Attach(hint);
            else hint.Loaded += (s, args) => Attach(hint);
        }

        private static void Attach(TextBlock hint)
        {
            if (hint.GetValue(StateProperty) != null) return;
            FrameworkElement source = GetFor(hint);
            if (source == null) return;

            var state = new State
            {
                OriginalText = hint.Text,
                OriginalForeground = hint.ReadLocalValue(TextBlock.ForegroundProperty),
                OriginallyEmpty = string.IsNullOrEmpty(hint.Text)
            };
            if (source is TextBox single) state.Boxes.Add(single);
            else state.Boxes.AddRange(Descendants(source).OfType<TextBox>());
            hint.SetValue(StateProperty, state);

            EventHandler changed = (s, e) => Update(hint, state);
            foreach (TextBox box in state.Boxes)
                HasErrorDescriptor.AddValueChanged(box, changed);
            if (state.OriginallyEmpty) hint.Visibility = Visibility.Collapsed;
            Update(hint, state);
        }

        private static void Update(TextBlock hint, State state)
        {
            TextBox failing = state.Boxes.FirstOrDefault(Validation.GetHasError);
            if (failing == null)
            {
                hint.Text = state.OriginalText;
                if (state.OriginalForeground == DependencyProperty.UnsetValue) hint.ClearValue(TextBlock.ForegroundProperty);
                else hint.Foreground = (Brush)state.OriginalForeground;
                if (state.OriginallyEmpty) hint.Visibility = Visibility.Collapsed;
                return;
            }

            string message = Message(Validation.GetErrors(failing).FirstOrDefault()?.ErrorContent);
            if (state.Boxes.Count > 1)
            {
                string name = System.Windows.Automation.AutomationProperties.GetName(failing);
                if (!string.IsNullOrEmpty(name)) message = $"{name}: {message}";
            }
            hint.Text = message;
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Status.Error");
            hint.Visibility = Visibility.Visible;
        }

        /// <summary>WPF's conversion error ("Value 'x' could not be converted.") in plain words.</summary>
        public static string Message(object errorContent)
        {
            string text = errorContent as string ?? errorContent?.ToString() ?? "";
            if (text.Length == 0 || text.IndexOf("could not be converted", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Enter a number.";
            return text;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject d in Descendants(child)) yield return d;
            }
        }
    }
}
