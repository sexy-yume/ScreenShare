using System;
using System.Windows.Forms;
using ScreenShare.Host.Forms;
using ScreenShare.Common.Utils;

namespace ScreenShare.Host
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // ����� �ܼ� Ȱ��ȭ
            ConsoleHelper.ShowConsoleWindow();
            Console.WriteLine("ScreenShare ȣ��Ʈ ���ø����̼� ����");

            // �⺻ Windows Forms �ʱ�ȭ
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ���� ���� Ȱ��ȭ�ϱ� ���� ���� ó���� ���
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.ThreadException += Application_ThreadException;

            try
            {
                Console.WriteLine("���� �� ����");
                Application.Run(new MainForm());
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