using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SimpleUnlockerRemastered
{
    internal static class Program
    {
        private static readonly string LogDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "SimpleUnlockerRemastered");

        private static readonly string LogFile =
            Path.Combine(LogDirectory, "startup-error.log");

        [STAThread]
        private static void Main()
        {
            // Перехватываем ошибки UI-потока, чтобы приложение не закрывалось молча.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
                ShowStartupError("Ошибка UI-потока", e.Exception);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    WriteLog("Необработанная ошибка процесса", ex);
                else
                    WriteLog("Необработанная ошибка процесса", e.ExceptionObject?.ToString() ?? "Неизвестная ошибка");
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                WriteLog("Необработанная ошибка фоновой задачи", e.Exception);
                e.SetObserved();
            };

            try
            {
                ApplicationConfiguration.Initialize();

                // Ошибка внутри конструктора MainForm теперь попадёт в этот catch.
                using (var mainForm = new MainForm())
                {
                    Application.Run(mainForm);
                }
            }
            catch (Exception ex)
            {
                ShowStartupError("Не удалось запустить SimpleUnlocker Remastered", ex);
            }
        }

        private static void ShowStartupError(string title, Exception exception)
        {
            WriteLog(title, exception);

            string logPath = LogFile;
            string message =
                title + Environment.NewLine + Environment.NewLine +
                exception.GetType().FullName + Environment.NewLine +
                exception.Message + Environment.NewLine + Environment.NewLine +
                "Подробности записаны в файл:" + Environment.NewLine +
                logPath;

            try
            {
                MessageBox.Show(message, "SimpleUnlocker Remastered — ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // Не допускаем повторного падения при показе сообщения.
            }
        }

        private static void WriteLog(string title, Exception exception)
        {
            WriteLog(title, exception.ToString());
        }

        private static void WriteLog(string title, string details)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                File.AppendAllText(LogFile,
                    "==================================================" + Environment.NewLine +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    title + Environment.NewLine +
                    details + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Ошибка логирования не должна завершать приложение.
            }
        }
    }
}
