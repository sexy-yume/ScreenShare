// ScreenShare.Client/Program.cs
using System;
using System.Windows.Forms;
using System.Threading;
using ScreenShare.Client.Forms;
using ScreenShare.Common.Settings;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // �α� �ʱ�ȭ
            var logger = FileLogger.Instance;
            logger.WriteInfo("���ø����̼� ����");

            // ���� ���� ó��
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            try
            {
                var settings = ClientSettings.Load();
                logger.WriteInfo($"���� �ε� �Ϸ� - Ŭ���̾�Ʈ ��ȣ: {settings.ClientNumber}, ȣ��Ʈ: {settings.HostIp}:{settings.HostPort}");

                bool showLoginForm = true;

                // ���� Ȯ�� - ��� �ʼ� �׸��� �����Ǿ����� ����
                if (!string.IsNullOrEmpty(settings.HostIp) && settings.HostPort > 0 && settings.ClientNumber > 0)
                {
                    // ���� �������� �����Ϸ��� showLoginForm = false�� ����
                    showLoginForm = false; // �α��� â�� ǥ������ ����

                    // �׽�Ʈ ���� ���� �Ʒ� ���� �ּ��� �����Ͽ� �׻� �α��� â ǥ��
                    // showLoginForm = true;
                }

                if (showLoginForm)
                {
                    logger.WriteInfo("�α��� â ǥ��");
                    using (var loginForm = new LoginForm())
                    {
                        if (loginForm.ShowDialog() == DialogResult.Cancel)
                        {
                            logger.WriteInfo("����ڰ� �α��� �����");
                            return; // ��� ��ư�� ���� ��� ���α׷� ����
                        }
                        logger.WriteInfo($"�α��� �Ϸ� - Ŭ���̾�Ʈ ��ȣ: {settings.ClientNumber}, ȣ��Ʈ: {settings.HostIp}:{settings.HostPort}");
                    }
                }

                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                logger.WriteError("���α׷� ���� �� ���� �߻�", ex);
                MessageBox.Show($"������ �߻��߽��ϴ�: {ex.Message}", "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                logger.WriteInfo("���ø����̼� ����");
                logger.Close();
            }
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            FileLogger.Instance.WriteError("UI ������ ���� �߻�", e.Exception);
            MessageBox.Show($"ó������ ���� ������ �߻��߽��ϴ�: {e.Exception.Message}", "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            FileLogger.Instance.WriteError("ó������ ���� ���� �߻�", e.ExceptionObject as Exception);
            MessageBox.Show($"�ɰ��� ������ �߻��߽��ϴ�: {(e.ExceptionObject as Exception)?.Message}", "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}