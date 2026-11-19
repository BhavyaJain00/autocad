using Microsoft.Win32;
using AcadCopilot.Plugin.ViewModels;

namespace AcadCopilot.Plugin.Views;

/// <summary>Windows open-file dialog for attachments (shown only when the user clicks the paperclip).</summary>
public sealed class WpfFilePicker : IFilePicker
{
    /// <inheritdoc />
    public IReadOnlyList<string> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach images or PDF files",
            Filter = "Images and PDF files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.pdf|Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|PDF files|*.pdf|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
