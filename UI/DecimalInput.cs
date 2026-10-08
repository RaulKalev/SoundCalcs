using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SoundCalcs.UI
{
    /// <summary>
    /// Number fields accept a decimal comma: a ',' typed or pasted becomes '.'. The bindings parse with a '.'
    /// decimal point and read ',' as a thousands separator, so "1,2" would silently become 12 (European keyboards
    /// and numpads type ','). Set <c>ui:DecimalInput.Enabled="True"</c> (the number field styles do).
    /// </summary>
    public static class DecimalInput
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(DecimalInput), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBox box)) return;
            box.PreviewTextInput -= OnPreviewTextInput;
            DataObject.RemovePastingHandler(box, OnPasting);
            if (!(bool)e.NewValue) return;
            box.PreviewTextInput += OnPreviewTextInput;
            DataObject.AddPastingHandler(box, OnPasting);
        }

        private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.Text != "," || !(sender is TextBox box)) return;
            e.Handled = true;
            int caret = box.SelectionStart;
            box.SelectedText = ".";
            box.SelectionLength = 0;
            box.CaretIndex = caret + 1;
        }

        private static void OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
            string text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (text == null || text.IndexOf(',') < 0) return;
            // "1 234,5" or "1.234,5": the last separator is the decimal point
            string cleaned = text.Trim().Replace(" ", "").Replace(" ", "");
            if (cleaned.IndexOf('.') >= 0 && cleaned.LastIndexOf(',') > cleaned.LastIndexOf('.')) cleaned = cleaned.Replace(".", "");
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, cleaned.Replace(',', '.'));
            e.DataObject = data;
        }
    }
}
