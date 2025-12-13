namespace SharpSFV
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            lvFiles = new ListView();
            columnHeader1 = new ColumnHeader();
            columnHeader3 = new ColumnHeader();
            columnHeader4 = new ColumnHeader();
            panel1 = new Panel();
            progressBarTotal = new ProgressBar();
            panel1.SuspendLayout();
            SuspendLayout();
            // 
            // lvFiles
            // 
            lvFiles.Columns.AddRange(new ColumnHeader[] { columnHeader1, columnHeader3, columnHeader4 });
            lvFiles.Dock = DockStyle.Fill;
            lvFiles.FullRowSelect = true;
            lvFiles.GridLines = true;
            lvFiles.Location = new Point(0, 0);
            lvFiles.Name = "lvFiles";
            lvFiles.Size = new Size(800, 450);
            lvFiles.TabIndex = 0;
            lvFiles.UseCompatibleStateImageBehavior = false;
            lvFiles.View = View.Details;
            // 
            // columnHeader1
            // 
            columnHeader1.Text = "File Name";
            columnHeader1.Width = 300;
            // 
            // columnHeader3
            // 
            columnHeader3.Text = "CRC/Hash";
            columnHeader3.Width = 220;
            // 
            // columnHeader4
            // 
            columnHeader4.Text = "Status";
            columnHeader4.Width = 100;
            // 
            // panel1
            // 
            panel1.Controls.Add(progressBarTotal);
            panel1.Dock = DockStyle.Bottom;
            panel1.Location = new Point(0, 410);
            panel1.Name = "panel1";
            panel1.Size = new Size(800, 40);
            panel1.TabIndex = 1;
            // 
            // progressBarTotal
            // 
            progressBarTotal.Dock = DockStyle.Fill;
            progressBarTotal.Location = new Point(0, 0);
            progressBarTotal.Name = "progressBarTotal";
            progressBarTotal.Size = new Size(800, 40);
            progressBarTotal.TabIndex = 0;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(panel1);
            Controls.Add(lvFiles);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "Form1";
            Text = "SharpSFV - By L33T";
            panel1.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private ListView lvFiles;
        private ColumnHeader columnHeader1;
        private ColumnHeader columnHeader3;
        private ColumnHeader columnHeader4;
        private Panel panel1;
        private ProgressBar progressBarTotal;
    }
}
