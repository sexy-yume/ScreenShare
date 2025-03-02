namespace ScreenShare.Client.Forms
{
    partial class LoginForm
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
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.lblClientNumber = new System.Windows.Forms.Label();
            this.txtClientNumber = new System.Windows.Forms.TextBox();
            this.lblHostIp = new System.Windows.Forms.Label();
            this.txtHostIp = new System.Windows.Forms.TextBox();
            this.lblHostPort = new System.Windows.Forms.Label();
            this.txtHostPort = new System.Windows.Forms.TextBox();
            this.btnSave = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();

            // lblClientNumber
            this.lblClientNumber.Location = new System.Drawing.Point(12, 15);
            this.lblClientNumber.Name = "lblClientNumber";
            this.lblClientNumber.Size = new System.Drawing.Size(100, 23);
            this.lblClientNumber.TabIndex = 0;
            this.lblClientNumber.Text = "클라이언트 번호:";
            this.lblClientNumber.TextAlign = System.Drawing.ContentAlignment.MiddleRight;

            // txtClientNumber
            this.txtClientNumber.Location = new System.Drawing.Point(118, 15);
            this.txtClientNumber.Name = "txtClientNumber";
            this.txtClientNumber.Size = new System.Drawing.Size(174, 23);
            this.txtClientNumber.TabIndex = 1;

            // lblHostIp
            this.lblHostIp.Location = new System.Drawing.Point(12, 44);
            this.lblHostIp.Name = "lblHostIp";
            this.lblHostIp.Size = new System.Drawing.Size(100, 23);
            this.lblHostIp.TabIndex = 2;
            this.lblHostIp.Text = "호스트 IP:";
            this.lblHostIp.TextAlign = System.Drawing.ContentAlignment.MiddleRight;

            // txtHostIp
            this.txtHostIp.Location = new System.Drawing.Point(118, 44);
            this.txtHostIp.Name = "txtHostIp";
            this.txtHostIp.Size = new System.Drawing.Size(174, 23);
            this.txtHostIp.TabIndex = 3;

            // lblHostPort
            this.lblHostPort.Location = new System.Drawing.Point(12, 73);
            this.lblHostPort.Name = "lblHostPort";
            this.lblHostPort.Size = new System.Drawing.Size(100, 23);
            this.lblHostPort.TabIndex = 4;
            this.lblHostPort.Text = "호스트 Port:";
            this.lblHostPort.TextAlign = System.Drawing.ContentAlignment.MiddleRight;

            // txtHostPort
            this.txtHostPort.Location = new System.Drawing.Point(118, 73);
            this.txtHostPort.Name = "txtHostPort";
            this.txtHostPort.Size = new System.Drawing.Size(174, 23);
            this.txtHostPort.TabIndex = 5;

            // btnSave
            this.btnSave.Location = new System.Drawing.Point(118, 112);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(75, 23);
            this.btnSave.TabIndex = 6;
            this.btnSave.Text = "저장";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.btnSave_Click);

            // btnCancel
            this.btnCancel.Location = new System.Drawing.Point(217, 112);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(75, 23);
            this.btnCancel.TabIndex = 7;
            this.btnCancel.Text = "취소";
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);

            // LoginForm
            this.AcceptButton = this.btnSave;
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.btnCancel;
            this.ClientSize = new System.Drawing.Size(304, 147);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnSave);
            this.Controls.Add(this.txtHostPort);
            this.Controls.Add(this.lblHostPort);
            this.Controls.Add(this.txtHostIp);
            this.Controls.Add(this.lblHostIp);
            this.Controls.Add(this.txtClientNumber);
            this.Controls.Add(this.lblClientNumber);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "LoginForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "ScreenShare 로그인";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label lblClientNumber;
        private System.Windows.Forms.TextBox txtClientNumber;
        private System.Windows.Forms.Label lblHostIp;
        private System.Windows.Forms.TextBox txtHostIp;
        private System.Windows.Forms.Label lblHostPort;
        private System.Windows.Forms.TextBox txtHostPort;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.Button btnCancel;
    }
}