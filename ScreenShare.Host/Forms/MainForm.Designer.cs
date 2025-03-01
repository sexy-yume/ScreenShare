namespace ScreenShare.Host.Forms
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            if (disposing)
            {
                Cleanup();
            }

            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.btnStart = new System.Windows.Forms.Button();
            this.btnStop = new System.Windows.Forms.Button();
            this.tilePanel = new System.Windows.Forms.Panel();
            this.logListBox = new System.Windows.Forms.ListBox();
            this.splitContainer = new System.Windows.Forms.SplitContainer();

            // splitContainer
            this.splitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer.Orientation = System.Windows.Forms.Orientation.Horizontal;
            this.splitContainer.Panel1.Controls.Add(this.tilePanel);
            this.splitContainer.Panel2.Controls.Add(this.logListBox);
            this.splitContainer.SplitterDistance = 400;

            // btnStart
            this.btnStart.Location = new System.Drawing.Point(12, 12);
            this.btnStart.Size = new System.Drawing.Size(100, 30);
            this.btnStart.Text = "서버 시작";
            this.btnStart.Click += new System.EventHandler(this.OnStartButtonClick);

            // btnStop
            this.btnStop.Location = new System.Drawing.Point(118, 12);
            this.btnStop.Size = new System.Drawing.Size(100, 30);
            this.btnStop.Text = "서버 중지";
            this.btnStop.Enabled = false;
            this.btnStop.Click += new System.EventHandler(this.OnStopButtonClick);

            // tilePanel
            this.tilePanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tilePanel.AutoScroll = true;

            // logListBox
            this.logListBox.Dock = System.Windows.Forms.DockStyle.Fill;

            // MainForm
            this.Text = "ScreenShare Manager";
            this.Size = new System.Drawing.Size(1024, 768);
            this.Controls.Add(this.btnStart);
            this.Controls.Add(this.btnStop);
            this.Controls.Add(this.splitContainer);
            this.Padding = new System.Windows.Forms.Padding(12, 48, 12, 12);
        }

        #endregion

        private System.Windows.Forms.Button btnStart;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Panel tilePanel;
        private System.Windows.Forms.ListBox logListBox;
        private System.Windows.Forms.SplitContainer splitContainer;
    }
}