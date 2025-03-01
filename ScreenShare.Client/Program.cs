using System;
using System.Windows.Forms;
using ScreenShare.Client.Forms;
using ScreenShare.Common.Utils;

namespace ScreenShare.Client
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // ����� �ܼ� Ȱ��ȭ
            ConsoleHelper.ShowConsoleWindow();
            Console.WriteLine("ScreenShare Ŭ���̾�Ʈ ���ø����̼� ����");

            // �⺻ Windows Forms �ʱ�ȭ
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ���� ���� Ȱ��ȭ�ϱ� ���� ���� ó���� ���
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.ThreadException += Application_ThreadException;

            try
            {
                // �α��� ���� ���� ǥ��
                var loginForm = new LoginForm();
                if (loginForm.ShowDialog() == DialogResult.OK)
                {
                    Console.WriteLine("�α��� ����, ���� Ŭ���̾�Ʈ �� ����");
                    Application.Run(new MainForm());
                }
                else
                {
                    Console.WriteLine("�α��� ��ҵ�");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"����ġ ���� ����: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"���ø����̼� ���� �� ������ �߻��߽��ϴ�: {ex.Message}",
                    "����", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            Console.WriteLine("���ø����̼� ����");
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Console.WriteLine($"ó������ ���� ����: {ex?.Message}\n{ex?.StackTrace}");
            FileLogger.Instance.WriteError("ó������ ���� ����", ex);
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Console.WriteLine($"UI ������ ����: {e.Exception.Message}\n{e.Exception.StackTrace}");
            FileLogger.Instance.WriteError("UI ������ ����", e.Exception);
        }
    }
}