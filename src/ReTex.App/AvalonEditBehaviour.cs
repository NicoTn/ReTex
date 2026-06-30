using System.Windows;
using ICSharpCode.AvalonEdit;

namespace ReTex.App;

/// <summary>
/// Two-way binds a string to an AvalonEdit <see cref="TextEditor"/>, whose own <c>Text</c>
/// property is not a DependencyProperty and so can't be bound directly.
/// Usage: &lt;avalonedit:TextEditor local:AvalonEditBehaviour.BindableText="{Binding ConfigText}"/&gt;
/// </summary>
public static class AvalonEditBehaviour
{
    public static readonly DependencyProperty BindableTextProperty =
        DependencyProperty.RegisterAttached(
            "BindableText", typeof(string), typeof(AvalonEditBehaviour),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBindableTextChanged));

    public static string GetBindableText(DependencyObject o) => (string)o.GetValue(BindableTextProperty);
    public static void SetBindableText(DependencyObject o, string value) => o.SetValue(BindableTextProperty, value);

    // Editors we've already wired the TextChanged handler on (avoids double-subscribing).
    private static readonly HashSet<TextEditor> _hooked = new();

    private static void OnBindableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;

        if (_hooked.Add(editor))
            editor.TextChanged += (_, _) => SetBindableText(editor, editor.Text);

        var text = e.NewValue as string ?? string.Empty;
        // Guard against the echo from our own TextChanged push (and avoid resetting the caret
        // when the incoming value already matches what's on screen).
        if (editor.Text != text) editor.Text = text;
    }
}
