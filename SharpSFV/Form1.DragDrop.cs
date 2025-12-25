using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SharpSFV
{
    public partial class Form1
    {
        private void SetupDragDrop()
        {
            this.AllowDrop = true;
            this.DragEnter += Form1_DragEnter;
            this.DragDrop += Form1_DragDrop;
        }

        private void Form1_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private async void Form1_DragDrop(object? sender, DragEventArgs e)
        {
            if (_isProcessing) return; // Ignore drops while working

            string[]? paths = (string[]?)e.Data!.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
            {
                await HandleDroppedPaths(paths);
            }
        }
    }
}