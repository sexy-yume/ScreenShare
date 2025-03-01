// ScreenShare.Client/Forms/LoginForm.cs
using System;
using System.Windows.Forms;
using ScreenShare.Common.Settings;

namespace ScreenShare.Client.Forms
{
    public partial class LoginForm : Form
    {
        private ClientSettings _settings;

        public LoginForm()
        {
            InitializeComponent();
            _settings = ClientSettings.Load();

            txtClientNumber.Text = _settings.ClientNumber.ToString();
            txtHostIp.Text = _settings.HostIp;
            txtHostPort.Text = _settings.HostPort.ToString();
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (int.TryParse(txtClientNumber.Text, out int clientNumber) &&
                !string.IsNullOrEmpty(txtHostIp.Text) &&
                int.TryParse(txtHostPort.Text, out int hostPort))
            {
                _settings.ClientNumber = clientNumber;
                _settings.HostIp = txtHostIp.Text;
                _settings.HostPort = hostPort;
                _settings.Save();

                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show("모든 정보를 올바르게 입력해주세요.", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}